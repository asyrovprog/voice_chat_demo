// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Logging;
using NAudio.Wave;

public sealed class AudioPlaybackService(ILogger<AudioPlaybackService> logger) : IDisposable
{
    private readonly ILogger<AudioPlaybackService> _logger = logger;
    private bool _isPlaying;

    public Task PipelineActionAsync(SpeechEvent evt) => this.PlayAudioAsync(evt.Payload, evt.CancellationToken);

    public void Dispose() { }

    private async Task PlayAudioAsync(byte[] audioData, CancellationToken token = default)
    {
        if (this._isPlaying)
        {
            this._logger.LogError("Ignoring audio playback. Already playing.");
            return;
        }

        _logger.LogInformation("Audio chunk playback started. You can speak to interrupt.");

        _isPlaying = true;
        var tcs = new TaskCompletionSource();
        using var audioStream = new MemoryStream(audioData);
        using var audioFileReader = new Mp3FileReader(audioStream);
        using var waveOut = new WaveOutEvent();
        var finished = tcs.Task;

        try
        {
            waveOut.PlaybackStopped += (sender, e) => tcs.TrySetResult();
            waveOut.Init(audioFileReader);
            waveOut.Play();

            await finished.WaitAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInterrupted();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Playback failed.");
        }
        finally
        {
            if (waveOut?.PlaybackState != PlaybackState.Stopped)
            {
                waveOut?.Stop();
            }
            _isPlaying = false;
            _logger.LogInformation("Audio chunk playback stopped.");
        }
    }
}
