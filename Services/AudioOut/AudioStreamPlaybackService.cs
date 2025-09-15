// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Logging;
using NAudio.Wave;

public sealed class AudioStreamPlaybackService : IDisposable
{
    // Realtime API default
    private static readonly WaveFormat Format = new WaveFormat(24000, 16, 1);
    private static readonly TimeSpan MaxBuffer = TimeSpan.FromMilliseconds(2000);

    private readonly ILogger<AudioStreamPlaybackService> _logger;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _buffer;
    private readonly object _gate = new();

    private int _currentTurnId = -1;
    private TurnManager _turnManager;

    public AudioStreamPlaybackService(PipelineControlPlane controlPlane, ILogger<AudioStreamPlaybackService> logger)
    {
        _turnManager = controlPlane.TurnManager;
        _turnManager.OnTurnInterrupted += (turn) =>
        {
            if (turn >= _currentTurnId)
            {
                Interrupt();
            }
        };

        _logger = logger;
    }

    public async Task PipelineActionAsync(AudioEvent evt)
    {
        if (evt.TurnId > _currentTurnId)
        {
            Interrupt();
            _currentTurnId = evt.TurnId;
        }
        await AppendAsync(evt.Payload.Data, evt.CancellationToken);
    }

    public Task AppendAsync(byte[] pcm16, CancellationToken ct = default)
    {
        if (pcm16 is null || pcm16.Length == 0)
        {
            _logger.LogWarning($"{nameof(AudioStreamPlaybackService)}: No audio data to play for TurnId {_currentTurnId}");
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            EnsureInitialized();

            var duration = AudioData.GetAudioDurationMs(pcm16.Length, Format.SampleRate, Format.Channels, Format.BitsPerSample);
            _logger.LogDebug($"Playing {duration} ms audio for TurnId {_currentTurnId}");

            _buffer!.AddSamples(pcm16, 0, pcm16.Length);
            if (_waveOut!.PlaybackState != PlaybackState.Playing)
            {
                _waveOut.Play();
            }

            var dropBytes = _buffer.BufferedBytes - BytesForDuration(MaxBuffer);
            if (dropBytes > 0)
            {
                _logger.LogWarning($"{nameof(AudioStreamPlaybackService)}: Buffer exceeded {MaxBuffer.TotalSeconds} seconds by {dropBytes} bytes, dropping excess audio for TurnId {_currentTurnId}");
                _ = _buffer.Read(new byte[dropBytes], 0, dropBytes);
            }
        }

        return Task.CompletedTask;
    }

    /// Instant stop + clear (for barge-in).
    public void Interrupt() => StopPlaybackNoThrow();

    public void Stop() => StopPlaybackNoThrow();

    public void Dispose()
    {
        Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _buffer = null;
    }

    private void EnsureInitialized()
    {
        if (_waveOut is not null)
        {
            return;
        }

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 5
        };

        _buffer = new BufferedWaveProvider(Format)
        {
            BufferDuration = MaxBuffer
        };

        _waveOut.Init(_buffer);
    }

    private bool StopPlaybackNoThrow()
    {
        lock (_gate)
        {
            bool interrupted = false;
            try
            {
                _waveOut?.Stop();
            }
            catch
            {
            }
            finally
            {
                interrupted = _buffer is not null && _buffer.BufferDuration > TimeSpan.Zero;
                _buffer?.ClearBuffer();
            }
            return interrupted;
        }
    }

    private static int BytesForDuration(TimeSpan duration)
    {
        var bytes = (int)Math.Floor(duration.TotalSeconds * Format.AverageBytesPerSecond);
        return bytes - (bytes % Format.BlockAlign);
    }
}
