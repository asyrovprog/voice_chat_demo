using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;

using Microsoft.Extensions.Logging;

public sealed class AudioMixerService : IAsyncDisposable
{
    private const string MixerNotStartedError = "AudioMixerService not started";
    private const int MaxParticipants = 32;

    public readonly record struct AudioInputChunk(byte[] Pcm16, int ActiveMask);

    // ---- Config ----
    private readonly int _sampleRate = 24000; // PCM16 mono, 24 kHz
    private readonly int _channels = 1;
    private readonly int _bits = 16;
    private readonly int _frameMs;
    private readonly int _frameSamples;      // per frame (per channel)
    private readonly int _warmupMs;
    private readonly float _baseHeadroom;    // e.g., 0.7
    private readonly float _targetPeak;      // e.g., 0.92
    private readonly short _activityThresh;  // e.g., ~600 i16

    // ---- IO blocks ----
    private ActionBlock<AudioEvent>? _audioIn;
    private BroadcastBlock<AudioEvent>? _audioOut;

    // ---- Sources ----
    private readonly ConcurrentDictionary<int, Pcm16RingBuffer> _sources = new();
    //private readonly int[] _lastActiveFrame = new int[MaxParticipants];
    private long _frameIndex;

    private readonly ILogger<AudioMixerService> _logger;
    private readonly PipelineControlPlane _controlPlane;

    private CancellationTokenSource? _cts;

    // ---- API ----
    public ActionBlock<AudioEvent> In => _audioIn ?? throw new InvalidOperationException(MixerNotStartedError);

    public BroadcastBlock<AudioEvent> Out => _audioOut ?? throw new InvalidOperationException(MixerNotStartedError);

    public bool IsStarted => _audioIn is not null;

    public AudioMixerService(
        ILogger<AudioMixerService> logger,
        PipelineControlPlane controlPlane,
        int frameMs = 20,
        int warmupMs = 40,
        float baseHeadroom = 0.70f,
        float targetPeak = 0.92f,
        int activityThresholdI16 = 600)
    {
        _logger = logger;
        _controlPlane = controlPlane;

        _frameMs = frameMs;
        _warmupMs = warmupMs;
        _baseHeadroom = baseHeadroom;
        _targetPeak = targetPeak;
        _activityThresh = (short)Math.Clamp(activityThresholdI16, 1, short.MaxValue);

        _frameSamples = _sampleRate * _frameMs / 1000;

        //Array.Fill(_lastActiveFrame, -10_000);
    }

    public async Task StartAsync(CancellationToken token = default)
    {
        if (_audioIn is not null) return;

        // Pre-register a fixed set (optional; writes also auto-register lazily)
        //for (int i = 0; i < Math.Clamp(participantCount, 0, MaxParticipants); i++)
        //    Register(i);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        _audioIn = new ActionBlock<AudioEvent>(evt =>
        {
            var id = evt.Payload.Participant; // expecting 0..31 as "participant id"
            if ((uint) id < MaxParticipants)
                Write(id, evt.Payload.Data, 0, evt.Payload.Data.Length);
            else
                _logger.LogWarning("AudioMixerService: write for invalid participant {Id}", id);

            return Task.CompletedTask;
        }, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            EnsureOrdered = true,
            BoundedCapacity = 512
        });

        _audioOut = new BroadcastBlock<AudioEvent>(e => e, new DataflowBlockOptions
        {
            EnsureOrdered = true
        });

        _ = Task.Run(() => RunMixerLoopAsync(_cts.Token), _cts.Token);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        try { _audioIn?.Complete(); } catch { }
        try { _cts?.Cancel(); } catch { }

        try { if (_audioOut is not null) await _audioOut.Completion.ConfigureAwait(false); } catch { }

        _cts?.Dispose();
        _sources.Clear();
    }

    public void Register(int id)
    {
        if ((uint) id >= MaxParticipants) return;
        _sources.GetOrAdd(id, _ => new Pcm16RingBuffer(capacityBytes: 1 << 16)); // 64KB
    }

    private void Write(int id, byte[] pcm16, int offset, int count)
    {
        if ((uint)id >= MaxParticipants) return;
        if (!_sources.TryGetValue(id, out var buf))
        {
            buf = new Pcm16RingBuffer(1 << 16);
            _sources[id] = buf;
        }
        buf.Write(pcm16.AsSpan(offset, count));
    }

    private async Task RunMixerLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var chunk in GetMixedAudioChunksAsync(token).ConfigureAwait(false))
            {
                var data = chunk.Pcm16;
                var activeMask = chunk.ActiveMask;

                // Reuse AudioData.Participant to carry the bitmask (your class supports this).
                var audioData = new AudioData(data, _sampleRate, _channels, _bits, null, 0, activeMask);
                var evt = new AudioEvent(_controlPlane.TurnManager.CurrentTurnId,
                                         _controlPlane.TurnManager.CurrentToken,
                                         audioData);

                await _audioOut!.SendAsync(evt, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        finally
        {
            _audioOut?.Complete();
        }
    }

    private async IAsyncEnumerable<AudioInputChunk> GetMixedAudioChunksAsync(
        [EnumeratorCancellation] CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        var next = sw.Elapsed + TimeSpan.FromMilliseconds(_warmupMs);
        var frameDur = TimeSpan.FromMilliseconds(_frameMs);

        var mix = new int[_frameSamples];
        var tmp = ArrayPool<short>.Shared.Rent(_frameSamples);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var wait = next - sw.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { yield break; }
                }
                next += frameDur;

                Array.Clear(mix, 0, _frameSamples);
                int activeMask = 0;

                foreach (var kv in _sources)
                {
                    int id = kv.Key;
                    var buf = kv.Value;

                    int popped = buf.PopSamples(tmp.AsSpan(0, _frameSamples)); // 0..N samples
                    bool active = false;

                    // Mix whatever we popped; fill rest with 0s already
                    for (int i = 0; i < popped; i++)
                    {
                        short s = tmp[i];
                        mix[i] += s;
                        if (!active && (s >= _activityThresh || s <= -_activityThresh))
                            active = true;
                    }

                    if (active)
                    {
                        activeMask |= (1 << id);
                        //_lastActiveFrame[id] = (int)_frameIndex;
                    }
                }

                // Peak detect
                int peakAbs = 0;
                for (int i = 0; i < _frameSamples; i++)
                {
                    int a = mix[i];
                    int abs = a >= 0 ? a : -a;
                    if (abs > peakAbs) peakAbs = abs;
                }

                // Scale
                float scale = _baseHeadroom;
                if (peakAbs > 0)
                {
                    float target = _targetPeak * 32767f;
                    float s = target / peakAbs;
                    if (s < 1f) scale *= s; // reduce when hot; otherwise keep headroom
                }

                // Write PCM16
                byte[] outBytes = new byte[_frameSamples * 2];
                for (int i = 0, j = 0; i < _frameSamples; i++, j += 2)
                {
                    int v = (int)(mix[i] * scale);
                    if (v > short.MaxValue) v = short.MaxValue;
                    else if (v < short.MinValue) v = short.MinValue;
                    unchecked
                    {
                        outBytes[j] = (byte)v;
                        outBytes[j + 1] = (byte)(v >> 8);
                    }
                }

                yield return new AudioInputChunk(outBytes, activeMask);
                _frameIndex++;
            }
        }
        finally
        {
            ArrayPool<short>.Shared.Return(tmp);
        }
    }

    private sealed class Pcm16RingBuffer
    {
        private byte[] _buf;
        private int _head; // read
        private int _tail; // write
        private int _count;
        private readonly object _lock = new();

        public Pcm16RingBuffer(int capacityBytes)
        {
            if (capacityBytes < 2) capacityBytes = 2;
            capacityBytes = NextPow2(capacityBytes);
            _buf = new byte[capacityBytes];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int NextPow2(int x)
        {
            x--;
            x |= x >> 1; x |= x >> 2; x |= x >> 4; x |= x >> 8; x |= x >> 16;
            return x + 1;
        }

        public void Write(ReadOnlySpan<byte> src)
        {
            if (src.Length == 0) return;
            lock (_lock)
            {
                EnsureCapacity(_count + src.Length);
                int first = Math.Min(src.Length, _buf.Length - _tail);
                src.Slice(0, first).CopyTo(_buf.AsSpan(_tail, first));
                int rem = src.Length - first;
                if (rem > 0)
                    src.Slice(first, rem).CopyTo(_buf.AsSpan(0, rem));
                _tail = (_tail + src.Length) & (_buf.Length - 1);
                _count += src.Length;
            }
        }

        public int PopSamples(Span<short> dstSamples)
        {
            int wantBytes = dstSamples.Length * 2;
            int gotBytes;

            lock (_lock)
            {
                gotBytes = Math.Min(wantBytes, _count);
                if (gotBytes == 0)
                {
                    dstSamples.Clear();
                    return 0;
                }

                int first = Math.Min(gotBytes, _buf.Length - _head);
                BytesToInt16(_buf.AsSpan(_head, first), dstSamples.Slice(0, first / 2));
                int doneBytes = first;

                if (gotBytes > first)
                {
                    int rem = gotBytes - first;
                    BytesToInt16(_buf.AsSpan(0, rem), dstSamples.Slice(first / 2, rem / 2));
                    doneBytes += rem;
                }

                _head = (_head + doneBytes) & (_buf.Length - 1);
                _count -= doneBytes;
            }

            int gotSamples = gotBytes / 2;
            if (gotSamples < dstSamples.Length)
                dstSamples.Slice(gotSamples).Clear();

            return gotSamples;
        }

        private static void BytesToInt16(ReadOnlySpan<byte> src, Span<short> dst)
        {
            int n = Math.Min(dst.Length, src.Length / 2);
            for (int i = 0, j = 0; i < n; i++, j += 2)
                dst[i] = (short)(src[j] | (src[j + 1] << 8));
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buf.Length) return;

            int newCap = _buf.Length;
            while (newCap < required) newCap <<= 1;

            var newBuf = new byte[newCap];

            int first = Math.Min(_count, _buf.Length - _head);
            Array.Copy(_buf, _head, newBuf, 0, first);
            if (_count > first)
                Array.Copy(_buf, 0, newBuf, first, _count - first);

            _buf = newBuf;
            _head = 0;
            _tail = _count;
        }
    }
}
