// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

public sealed class AudioStreamPlaybackService : IDisposable
{
    private static readonly WaveFormat Format = new WaveFormat(24000, 16, 1);
    private static readonly TimeSpan MaxBuffer = TimeSpan.FromSeconds(2);

    // anti-click
    private const int PrebufferMs = 200; // minimum buffered before first Play in a turn
    private const int FadeMs = 20; // fade-in on the start of a turn

    private readonly ILogger<AudioStreamPlaybackService> _logger;
    private readonly object _gate = new();

    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _buffer;
    private FadeInOutSampleProvider? _fader;
    private bool _pendingFadeIn;
    private TurnManager _turnManager;
    private int _currentTurnId = -1;

    public AudioStreamPlaybackService(PipelineControlPlane controlPlane, ILogger<AudioStreamPlaybackService> logger)
    {
        _logger = logger;

        Initialize();
        _turnManager = controlPlane.TurnManager;
        _turnManager.OnTurnInterrupted += InterruptHandler;
    }

    public void ResetControlPlane(PipelineControlPlane controlPlane)
    {
        _turnManager.OnTurnInterrupted -= InterruptHandler;
        _turnManager = controlPlane.TurnManager;
        _turnManager.OnTurnInterrupted += InterruptHandler;
    }

    public void PipelineAction(AudioEvent evt) => Append(evt.Payload.Data, evt.CancellationToken);

    public string Name { get; set; } = "";

    private void InterruptHandler(int turnId)
    {
        if (turnId >= _currentTurnId && _currentTurnId != -1)
        {
            _logger.LogWarning($"[{Name}] - Audio playback turn interrupted. New turn {turnId}, current turn {_currentTurnId}.");
            Interrupt();
            _currentTurnId = turnId;
        }
    }

    private void Append(byte[] pcm16, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _buffer!.AddSamples(pcm16, 0, pcm16.Length);

            if (_waveOut!.PlaybackState != PlaybackState.Playing)
            {
                if (GetBufferedDuration(_buffer, Format) >= TimeSpan.FromMilliseconds(PrebufferMs))
                {
                    if (_pendingFadeIn)
                    {
                        _fader!.BeginFadeIn(FadeMs);
                        _pendingFadeIn = false;
                    }
                    _waveOut.Play();
                }
            }
        }
    }

    private void Interrupt()
    {
        lock (_gate)
        {
            _pendingFadeIn = StopPlaybackNoThrow();
        }
    }

    private void Stop() => StopPlaybackNoThrow();

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _waveOut?.Dispose();
            _waveOut = null;
            _buffer = null;
        }
    }

    private void Initialize()
    {
        if (_waveOut is not null) return;

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100, // moderate latency reduces device underruns
            NumberOfBuffers = 5
        };

        _buffer = new BufferedWaveProvider(Format)
        {
            BufferDuration = MaxBuffer,
            ReadFully = true,  // supply silence when empty (prevents underrun pops)
            DiscardOnBufferOverflow = true
        };

        var sample = _buffer.ToSampleProvider();
        _fader = new FadeInOutSampleProvider(sample);

        _pendingFadeIn = true;
        _waveOut.Init(_fader);
    }

    private bool StopPlaybackNoThrow()
    {
        bool interrupted = false;
        lock (_gate)
        {
            if (_waveOut is null || _buffer is null || _fader is null) return interrupted;
            try
            {
                if (_waveOut.PlaybackState == PlaybackState.Playing)
                {
                    _logger.LogWarning($"[{Name}] - Audio playback interrupted. Stopped.");
                    _waveOut.Stop();
                    interrupted = true;
                }

                if (_buffer.BufferedBytes > 0)
                {
                    _logger.LogWarning($"[{Name}] - Audio playback interrupted. Buffer cleared.");
                    _buffer.ClearBuffer();
                    interrupted = true;
                }
            }
            catch 
            {
                _logger.LogError($"[{Name}] - Audio playback interrupted. Stop failed.");
            }
            return interrupted;
        }
    }

    private static TimeSpan GetBufferedDuration(BufferedWaveProvider buffer, WaveFormat fmt) => TimeSpan.FromSeconds(buffer.BufferedBytes / (double)fmt.AverageBytesPerSecond);
}
