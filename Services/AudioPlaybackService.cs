// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Logging;
using NAudio.Wave;

public class AudioPlaybackService(ILogger<AudioPlaybackService> logger) : IDisposable
{
    private readonly ILogger<AudioPlaybackService> _logger = logger;
    private WaveOutEvent? _waveOut;
    private bool _isPlaying;

    public Task PipelineActionAsync(SpeechEvent evt) => PlayAudioAsync(evt.Payload, evt.CancellationToken);

    public void Dispose() => _waveOut?.Dispose();

    private async Task PlayAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (_isPlaying)
        {
            _logger.LogError("Ignoring audio playback. Already playing.");
            return;
        }

        _logger.LogInformation("Starting audio playback...");

        try
        {
            using var audioStream = new MemoryStream(audioData);
            using var audioFileReader = new Mp3FileReader(audioStream);

            _waveOut = new WaveOutEvent();
            var tcs = new TaskCompletionSource();

            _waveOut.PlaybackStopped += (sender, e) =>
            {
                _isPlaying = false;
                tcs.TrySetResult();
                if (e.Exception != null)
                {
                    _logger.LogWarning($"Playback error occurred: {e.Exception.Message}");
                }
            };

            _waveOut.Init(audioFileReader);
            _isPlaying = true;
            _waveOut.Play();

            _logger.LogInformation("Audio chunk playback started. You can speak to interrupt.");

            // Wait for playback to complete or cancellation
            await using (cancellationToken.Register(() =>
            {
                _logger.LogInterrupted();
                _waveOut.Stop();
                tcs.TrySetCanceled();
            }))
            {
                await tcs.Task;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInterrupted();
            _isPlaying = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during audio playback");
            _isPlaying = false;
            throw;
        }
        finally
        {
            _waveOut?.Dispose();
            _waveOut = null;
        }
    }
}