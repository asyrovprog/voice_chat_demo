using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;

using Microsoft.Extensions.Logging;

using NAudio.Wave;
using NAudio.Wave.SampleProviders;

public sealed class AudioMixerService : IAsyncDisposable
{
    private const string MixerNotStartedError = "AudioMixerService not started";
    private const int MaxParticipants = 32;

    public readonly record struct AudioInputChunk(byte[] Pcm16, int ActiveMask);

    private readonly WaveFormat _pcm16 = new WaveFormat(24000, 16, 1);
    private readonly MixingSampleProvider _mixer; // float32 mono
    private readonly ConcurrentDictionary<int, BufferedWaveProvider> _sources = new();

    private readonly int _frameMs;
    private readonly int _frameSamples;
    private readonly int _warmupMs;
    private readonly float _baseHeadroom;          // static headroom
    private readonly float _activityThreshold;     // peak threshold in float domain
    private readonly int _activityHangFrames;      // frames to keep activity after last detection
    private readonly float _targetPeak;            // adaptive gain target peak
    private readonly float _minHeadroom;           // lower bound for scaling (when already quiet)

    private readonly int[] _lastActiveFrame;       // per participant last active frame index
    private volatile int _frameActiveMask;
    private long _frameIndex;

    private CancellationTokenSource? _cts;
    private ActionBlock<AudioEvent>? _audioIn;
    private BroadcastBlock<AudioEvent>? _audioOut;
    private readonly ILogger<AudioMixerService> _logger;
    private readonly PipelineControlPlane _controlPlane;

    public AudioMixerService(
        ILogger<AudioMixerService> logger,
        PipelineControlPlane controlPlane,
        int frameMs = 20,
        int warmupMs = 40,
        float baseHeadroom = 0.70f,
        float activityThreshold = 0.015f,
        int activityHangFrames = 2,
        float targetPeak = 0.92f,
        float minHeadroom = 0.55f)
    {
        _logger = logger;
        _controlPlane = controlPlane;
        _frameMs = frameMs;
        _warmupMs = warmupMs;
        _baseHeadroom = baseHeadroom;
        _activityThreshold = activityThreshold;
        _activityHangFrames = activityHangFrames;
        _targetPeak = targetPeak;
        _minHeadroom = minHeadroom;

        _frameSamples = _pcm16.SampleRate * _frameMs / 1000;

        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(_pcm16.SampleRate, 1))
        {
            ReadFully = true,
        };

        _lastActiveFrame = new int[MaxParticipants];
        Array.Fill(_lastActiveFrame, -10_000);
    }

    public bool IsStarted => _audioIn is not null;

    public async Task StartAsync(int participantCount = 2, CancellationToken token = default)
    {
        if (participantCount < 1 || participantCount > MaxParticipants)
            throw new ArgumentOutOfRangeException(nameof(participantCount));

        if (_audioIn != null)
            return;

        for (int i = 0; i < participantCount; i++)
            Register(i);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        _audioIn = new ActionBlock<AudioEvent>(evt =>
        {
            var id = evt.Payload.Participant;
            if (id < 0) return Task.CompletedTask;
            Write(id, evt.Payload.Data, 0, evt.Payload.Data.Length);
            return Task.CompletedTask;
        }, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            BoundedCapacity = 512,
            EnsureOrdered = true
        });

        _ = _audioIn.Completion.ContinueWith(_ => _cts!.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _audioOut = new BroadcastBlock<AudioEvent>(e => e, new DataflowBlockOptions
        {
            BoundedCapacity = 256,
            EnsureOrdered = true
        });

        _ = Task.Run(() => RunMixerLoopAsync(_cts.Token), _cts.Token);
        await Task.CompletedTask;
    }

    public ActionBlock<AudioEvent> AudioIn =>
        _audioIn ?? throw new InvalidOperationException(MixerNotStartedError);

    public BroadcastBlock<AudioEvent> AudioOut =>
        _audioOut ?? throw new InvalidOperationException(MixerNotStartedError);

    public async ValueTask DisposeAsync()
    {
        try
        {
            _audioIn?.Complete();
            _cts?.Cancel();
        }
        catch { }

        try
        {
            if (_audioOut != null)
                await _audioOut.Completion.ConfigureAwait(false);
        }
        catch { }

        _cts?.Dispose();
        _cts = null;
        _sources.Clear();
    }

    #region Internals

    private void Register(int id)
    {
        if (id < 0 || id >= MaxParticipants)
            throw new ArgumentOutOfRangeException(nameof(id), $"Participant id must be 0..{MaxParticipants - 1}");
        if (_sources.ContainsKey(id))
            return;

        var buffer = new BufferedWaveProvider(_pcm16)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true, // <-- supply silence when empty so mixer never sees a 0-length read
            BufferDuration = TimeSpan.FromMilliseconds(1500)
        };

        if (_sources.TryAdd(id, buffer))
        {
            // PCM16 -> float
            var f32 = buffer.ToSampleProvider();

            // Activity metering per frame (frameSamples)
            var meter = new MeteringSampleProvider(f32, _frameSamples);
            meter.StreamVolume += (_, e) =>
            {
                var peak = (e.MaxSampleValues is { Length: > 0 } mv) ? Math.Abs(mv[0]) : 0f;
                if (peak >= _activityThreshold)
                {
                    _lastActiveFrame[id] = (int)_frameIndex;
                    _frameActiveMask |= (1 << id);
                }
            };

            _mixer.AddMixerInput(meter);
        }
    }

    private void Write(int id, byte[] pcm16, int offset, int count)
    {
        if (id < 0 || id >= MaxParticipants)
        {
            _logger.LogWarning("AudioMixerService: write for invalid participant {Id}", id);
            return;
        }

        if (_sources.TryGetValue(id, out var bp))
        {
            bp.AddSamples(pcm16, offset, count);
        }
        else
        {
            _logger.LogWarning("AudioMixerService: write for unknown participant {Id}", id);
        }
    }

    private async Task RunMixerLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var chunk in GetMixedAudioChunksAsync(token).ConfigureAwait(false))
            {
                var data = chunk.Pcm16;
                var activeMask = chunk.ActiveMask;
                var audioData = new AudioData(data, _pcm16.SampleRate, _pcm16.Channels, _pcm16.BitsPerSample, null, 0, activeMask);
                var evt = new AudioEvent(_controlPlane.TurnManager.CurrentTurnId, _controlPlane.TurnManager.CurrentToken, audioData);

                var max = data.Max();
                await _audioOut!.SendAsync(evt, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"AudioMixerService loop error: {ex}");
        }
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

        var floatFrame = new float[_frameSamples];
        var pcmFrame = new byte[_frameSamples * 2];

        while (!token.IsCancellationRequested)
        {
            var wait = next - sw.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, token).ConfigureAwait(false); }
                catch { yield break; }
            }
            next += frameDur;

            _frameActiveMask = 0;

            int read = _mixer.Read(floatFrame, 0, _frameSamples);
            if (read < _frameSamples)
                Array.Clear(floatFrame, read, _frameSamples - read);

            // Track peak for adaptive scaling
            float peak = 0f;
            for (int i = 0; i < _frameSamples; i++)
            {
                var abs = Math.Abs(floatFrame[i]);
                if (abs > peak) peak = abs;
            }

            // Adaptive scale
            float dynamicScale = 1f;
            if (peak > 0f)
            {
                var targetScale = _targetPeak / peak;
                dynamicScale = Math.Min(1f, targetScale);
            }

            float scale = _baseHeadroom * dynamicScale;
            if (scale < _minHeadroom) scale = _minHeadroom;

            // Convert to PCM16 (with scaling)
            for (int i = 0, j = 0; i < _frameSamples; i++, j += 2)
            {
                int v = (int)(floatFrame[i] * scale * 32767f);
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                pcmFrame[j] = (byte)v;
                pcmFrame[j + 1] = (byte)(v >> 8);
            }

            // Apply hang-based activity augmentation
            var maskWithHang = _frameActiveMask;
            var currentFrame = (int)_frameIndex;
            if (_activityHangFrames > 0)
            {
                foreach (var pid in _sources.Keys)
                {
                    if (currentFrame - _lastActiveFrame[pid] <= _activityHangFrames)
                        maskWithHang |= (1 << pid);
                }
            }

            var payload = new byte[pcmFrame.Length];
            Buffer.BlockCopy(pcmFrame, 0, payload, 0, payload.Length);

            yield return new AudioInputChunk(payload, maskWithHang);

            _frameIndex++;
        }
    }

    #endregion
}
