// Copyright (c) Microsoft. All rights reserved.

using System.ClientModel;
using System.Text;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Realtime;

#pragma warning disable OPENAI002 // 'OpenAI.Realtime.RealtimeClient' is for evaluation purposes only and is subject to change or removal in future updates.

public class RealtimeAudioService : IDisposable
{
    private readonly ILogger<RealtimeAudioService> _logger;
    private readonly RealtimeClient _realtimeClient;
    private readonly OpenAIOptions _audioOptions;
    private RealtimeSession? _realtimeSession;

    private ActionBlock<AudioEvent>? _sendAudioBlock;
    private BroadcastBlock<AudioEvent>? _audioBus;
    private BroadcastBlock<TranscriptionEvent>? _transcriptBus;
    private CancellationTokenSource? _cancellationTokenSource;

    private TurnManager _turnManager;

    public RealtimeAudioService(
        PipelineControlPlane controlPlane,
        ILogger<RealtimeAudioService> logger, 
        IOptions<OpenAIOptions> openAIOptions
    )
    {
        _turnManager = controlPlane.TurnManager;
        this._logger = logger;
        _audioOptions = openAIOptions.Value;

        var apiKeyCredential = new ApiKeyCredential(_audioOptions.ApiKey);
        _realtimeClient = new RealtimeClient(apiKeyCredential);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_realtimeSession != null)
        {
            _logger.LogError("RealtimeAudioService is already started. Ignoring duplicate StartAsync() call.");
            return;
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _realtimeSession = await this._realtimeClient.StartConversationSessionAsync(_audioOptions.RealtimeApiModelId, cancellationToken: cancellationToken).ConfigureAwait(false);

        var config = new ConversationSessionOptions()
        {
            Instructions = 
            "You are a voice assistant. Keep your responses concise, conversational yet short, up to 4-5 sentences.",
            InputAudioFormat = RealtimeAudioFormat.Pcm16,
            OutputAudioFormat = RealtimeAudioFormat.Pcm16,
            InputTranscriptionOptions = new InputTranscriptionOptions
            {
                Model = "whisper-1",
                Prompt = "The user is speaking to a voice assistant.",
            },
            TurnDetectionOptions = TurnDetectionOptions.CreateSemanticVoiceActivityTurnDetectionOptions(),
            ContentModalities = RealtimeContentModalities.Text | RealtimeContentModalities.Audio,
            Temperature = 0.8f,
        };

        await _realtimeSession.ConfigureConversationSessionAsync(config, cancellationToken).ConfigureAwait(false);

        _sendAudioBlock = new ActionBlock<AudioEvent>(async audioEvent =>
        {
            await SendInputAudioAsync(audioEvent, audioEvent.CancellationToken).ConfigureAwait(false);
        }, new ExecutionDataflowBlockOptions
        {
            BoundedCapacity = 256,
            MaxDegreeOfParallelism = 1,
            CancellationToken = _cancellationTokenSource.Token
        });

        _audioBus = new BroadcastBlock<AudioEvent>(e => e, new DataflowBlockOptions
        {
            BoundedCapacity = 256,
            CancellationToken = _cancellationTokenSource.Token
        });

        _transcriptBus = new BroadcastBlock<TranscriptionEvent>(e => e, new DataflowBlockOptions
        {
            BoundedCapacity = 256,
            CancellationToken = _cancellationTokenSource.Token
        });

        _ = _sendAudioBlock.Completion.ContinueWith(t =>
        {
            _cancellationTokenSource.Cancel();
        }, TaskContinuationOptions.ExecuteSynchronously);

        _ = Task.Run(async () => await ReceiveUpdatesAsync(_cancellationTokenSource.Token), cancellationToken).ConfigureAwait(false);
    }

    public ITargetBlock<AudioEvent> AudioInput => _sendAudioBlock ?? throw new InvalidOperationException();

    public ISourceBlock<AudioEvent> AudioOutput => _audioBus ?? throw new InvalidOperationException();

    public ISourceBlock<TranscriptionEvent> TranscriptOutput => _transcriptBus ?? throw new InvalidOperationException();

    private async Task SendInputAudioAsync(AudioEvent audioEvent, CancellationToken cancellationToken = default)
    {
        if (_sendAudioBlock == null || _realtimeSession == null)
        {
            _logger.LogError("Attempted to send input audio before the RealtimeAudioService was started. Please call StartAsync() before sending audio.");
            return;
        }
        await _realtimeSession.SendInputAudioAsync(new BinaryData(audioEvent.Payload.Data), cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (_realtimeSession == null || _audioBus == null || _transcriptBus == null)
        {
            _logger.LogError("Attempted to receive updates before the RealtimeAudioService was started. Please call StartAsync() before receiving updates.");
            return;
        }

        StringBuilder responseTranscript = new StringBuilder();
        int audioEndMs = 0;

        try
        {
            await foreach (var update in _realtimeSession.ReceiveUpdatesAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (update)
                {
                    case InputAudioTranscriptionDeltaUpdate transcriptDelta:
                        _logger.LogInformation($"Transcript Delta: {transcriptDelta.Delta}");
                        break;

                    case InputAudioTranscriptionFinishedUpdate transcriptFinished:
                        _logger.LogInformation($"Transcript Finished: {transcriptFinished.Transcript}");
                        break;

                    case OutputStreamingStartedUpdate streamingStartedUpdate:
                        responseTranscript.Clear();
                        audioEndMs = 0;
                        _logger.LogInformation("Output streaming started.");
                        _turnManager.Interrupt();
                        break;

                    case OutputDeltaUpdate delta:
                        var common = $"EventId: {delta.EventId}, ResponseId: {delta.ResponseId}, ItemId: {delta.ItemId}, ItemIndex: {delta.ItemIndex}, Part: {delta.ContentPartIndex}";

                        if (!string.IsNullOrEmpty(delta.AudioTranscript))
                        {
                            _logger.LogDebug($"{nameof(RealtimeAudioService)} Transcript Delta: {delta.AudioTranscript}, {common}");
                            responseTranscript.Append(delta.AudioTranscript);

                            await _transcriptBus.SendAsync(new TranscriptionEvent(
                                _turnManager.CurrentTurnId,
                                _turnManager.CurrentToken,
                                delta.AudioTranscript), cancellationToken).ConfigureAwait(false);
                        }

                        if (delta.AudioBytes != null)
                        {
                            _logger.LogDebug($"{nameof(RealtimeAudioService)} Audio Delta Length: {delta.AudioBytes.Length}, {common}");

                            var transcript = responseTranscript.ToString();
                            responseTranscript.Clear();

                            var audioEndTime = audioEndMs + AudioData.GetAudioDurationMs(delta.AudioBytes.Length, 24000, 1, 16);

                            await _audioBus.SendAsync(new AudioEvent(
                                _turnManager.CurrentTurnId,
                                _turnManager.CurrentToken,
                                new AudioData(delta.AudioBytes.ToArray(), 24000, 1, 16, transcript, audioEndTime)), cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case OutputAudioTranscriptionFinishedUpdate finishedUpdate:
                        _logger.LogInformation($"Output audio transcript finished: {finishedUpdate.Transcript}");
                        break;

                    case OutputStreamingFinishedUpdate finishedUpdate:
                        _logger.LogInformation("Output streaming finished.");
                        break;

                    case RealtimeErrorUpdate err:
                        _logger.LogWarning("[Realtime] {Message}", err.Message);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ReceiveUpdatesAsync operation was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while receiving updates from Realtime session.");
        }
        finally
        {
            _audioBus.Complete();
            _transcriptBus.Complete();
            _logger.LogInformation("RealtimeAudioService has completed receiving updates and closed the audio and transcript");
        }
    }

    public void Dispose()
    {
        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch
        {
        }

        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        _realtimeSession?.Dispose();
        _realtimeSession = null;
    }
}