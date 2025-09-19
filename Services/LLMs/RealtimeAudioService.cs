// Copyright (c) Microsoft. All rights reserved.

using System.ClientModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

using OpenAI.Realtime;

#pragma warning disable OPENAI002 // 'OpenAI.Realtime.RealtimeClient' is for evaluation purposes only and is subject to change or removal in future updates.

public class RealtimeAudioService : IDisposable
{
    private readonly ILogger<RealtimeAudioService> _logger;
    private readonly RealtimeClient _realtimeClient;
    private readonly RealtimeModelsOptions.Template _options;
    private RealtimeSession? _realtimeSession;

    private ActionBlock<AudioEvent>? _sendAudioBlock;
    private BroadcastBlock<AudioEvent>? _audioBus;
    private BroadcastBlock<TranscriptionEvent>? _transcriptBus;
    private CancellationTokenSource? _cancellationTokenSource;

    private TurnManager _turnManager;
    private PipelineControlPlane _controlPlane;
    private string _name = "";
    private bool _automaticResponse;
    private Kernel? _kernel;
    private KernelPlugin? _plugin;
    private TimeSpan _engagementRequestStartTime = TimeSpan.Zero;
    private List<ConversationTool> _conversationTools = new List<ConversationTool>();
    private string? _currentSpeakerName = null;

    // private Queue<>

    public RealtimeAudioService(
        PipelineControlPlane controlPlane,
        ILogger<RealtimeAudioService> logger,
        IOptions<OpenAIOptions> openAIOptions,
        IOptions<RealtimeModelsOptions.Template> options
    )
    {
        _turnManager = controlPlane.TurnManager;
        this._logger = logger;
        _options = options.Value;
        _controlPlane = controlPlane;

        var apiKeyCredential = new ApiKeyCredential(openAIOptions.Value.ApiKey);
        _realtimeClient = new RealtimeClient(apiKeyCredential);
    }

    public RealtimeSession? Session => _realtimeSession;

    public int ParticipantId { get; set; } = 0;

    public async Task StartAsync(
        Kernel? kernel,
        KernelPlugin? plugin,
        PipelineControlPlane? controlPlane,
        string name = "",
        bool automaticResponse = true,
        CancellationToken cancellationToken = default)
    {
        if (controlPlane != null)
        {
            _controlPlane = controlPlane;
            _turnManager = controlPlane.TurnManager;
            controlPlane.Subscribe<PipelineControlPlane.ActiveSpeaker>(e =>
            {
                _ = UpdateCurrentSpeakerAsync(e.Name, cancellationToken).ConfigureAwait(false);
            });
        }

        if (_realtimeSession != null)
        {
            _logger.LogError("RealtimeAudioService is already started. Ignoring duplicate StartAsync() call.");
            return;
        }

        _kernel = kernel;
        _plugin = plugin;
        _automaticResponse = automaticResponse;
        _name = name;
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _realtimeSession = await this._realtimeClient.StartConversationSessionAsync(_options.ModelId, cancellationToken: cancellationToken).ConfigureAwait(false);

        var turnOptions = 
            TurnDetectionOptions.CreateSemanticVoiceActivityTurnDetectionOptions(
                SemanticEagernessLevel.High,
                enableAutomaticResponseCreation: _automaticResponse,
                enableResponseInterruption: true);

        var config = new ConversationSessionOptions()
        {
            Instructions = File.ReadAllText(_options.Instructions),
            InputAudioFormat = RealtimeAudioFormat.Pcm16,
            OutputAudioFormat = RealtimeAudioFormat.Pcm16,
            InputTranscriptionOptions = new InputTranscriptionOptions
            {
                Model = "whisper-1",
                Prompt = File.ReadAllText(_options.TranscriptInstructions),
                Language = _options.Language,
            },
            TurnDetectionOptions = turnOptions,
            ContentModalities = RealtimeContentModalities.Text | RealtimeContentModalities.Audio,
            Temperature = (float) _options.Temperature,
            Voice = _options.Voice,
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

    public ITargetBlock<AudioEvent> In => _sendAudioBlock ?? throw new InvalidOperationException();

    public ISourceBlock<AudioEvent> Out => _audioBus ?? throw new InvalidOperationException();

    public ISourceBlock<TranscriptionEvent> TranscriptOutput => _transcriptBus ?? throw new InvalidOperationException();

    public async Task TriggerResponseAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (_realtimeSession == null)
        {
            _logger.LogError("Attempted to trigger response before the RealtimeAudioService was started. Please call StartAsync() before triggering a response.");
            return;
        }
        await _realtimeSession.AddItemAsync(RealtimeItem.CreateAssistantMessage([ConversationContentPart.CreateOutputTextPart(prompt)])).ConfigureAwait(false);
        await _realtimeSession.StartResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateCurrentSpeakerAsync(string? speakerName, CancellationToken cancellationToken = default)
    {
        if (_currentSpeakerName == speakerName)
        {
            return;
        }

        if (speakerName == _name)
        {
            speakerName = null; // Don't send own name as current speaker
        }

        if (_realtimeSession == null)
        {
            _logger.LogError("Attempted to update current speaker before the RealtimeAudioService was started. Please call StartAsync() before updating current speaker.");
            return;
        }

        _logger.LogInformation($"[{_name}] - Updating current speaker to: {speakerName}");
        await _realtimeSession.AddItemAsync(
            RealtimeItem.CreateUserMessage([ConversationContentPart.CreateInputTextPart($"[current_speaker_name={speakerName}]")])).ConfigureAwait(false);

        _currentSpeakerName = speakerName;
    }

    private async Task SendInputAudioAsync(AudioEvent audioEvent, CancellationToken cancellationToken = default)
    {
        if (_sendAudioBlock == null || _realtimeSession == null)
        {
            _logger.LogError("Attempted to send input audio before the RealtimeAudioService was started. Please call StartAsync() before sending audio.");
            return;
        }
        await _realtimeSession.SendInputAudioAsync(new BinaryData(audioEvent.Payload.Data), cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessInputSpeechFinishedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_realtimeSession != null && _automaticResponse == false)
            {
                _engagementRequestStartTime = PipelineControlPlane.Timestamp;

                var req = new ConversationResponseOptions
                {
                    ToolChoice = ConversationToolChoice.CreateRequiredToolChoice(),
                    ContentModalities = RealtimeContentModalities.Text,
                    ConversationSelection = ResponseConversationSelection.None,
                };

                if (_conversationTools.Count == 0 && _plugin != null)
                {
                    foreach (var func in _plugin)
                    {
                        if (func is null) continue;
                        _conversationTools.Add(func.ToRealtimeTool());
                    }
                }

                foreach (var func in _conversationTools)
                {
                    req.Tools.Add(func);
                }

                _logger.LogInformation("Starting intelligent response evaluation based on context.");
                await _realtimeSession.StartResponseAsync(req, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while processing input speech finished.");
        }
        finally
        {
            _logger.LogInformation("Finished intelligent response evaluation based on context.");
        }
    }

    private async Task ReceiveUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (_realtimeSession == null || _audioBus == null || _transcriptBus == null)
        {
            _logger.LogError("Attempted to receive updates before the RealtimeAudioService was started. Please call StartAsync() before receiving updates.");
            return;
        }

        StringBuilder responseTranscript = new StringBuilder();
        double audioEndMs = 0;
        TimeSpan speechFinishedTime = TimeSpan.Zero;

        try
        {
            await foreach (var update in _realtimeSession.ReceiveUpdatesAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (update)
                {
                    case InputAudioSpeechStartedUpdate speechStarted:
                        _logger.LogInformation($"{_name} - Input speech started.");
                        _turnManager.Interrupt();
                        responseTranscript.Clear();
                        audioEndMs = 0;
                        break;

                    case ConversationSessionConfiguredUpdate configUpdate:
                        _logger.LogInformation($"{_name} - ConversationSessionConfiguredUpdate");
                        break;

                    case ConversationSessionStartedUpdate configUpdate:
                        _logger.LogInformation($"{_name} - ConversationSessionStartedUpdate");
                        break;

                    case InputAudioSpeechFinishedUpdate speechFinished:
                        _logger.LogInformation($"{_name} - Input speech finished.");
                        speechFinishedTime = PipelineControlPlane.Timestamp;
                        _ = ProcessInputSpeechFinishedAsync(cancellationToken);
                        break;

                    case InputAudioTranscriptionDeltaUpdate transcriptDelta:
                        _logger.LogInformation($"{_name} - Transcript Delta: {transcriptDelta.Delta}");
                        break;

                    case InputAudioTranscriptionFinishedUpdate transcriptFinished:
                        _logger.LogInformation($"{_name} - Transcript Finished: {transcriptFinished.Transcript}");
                        break;

                    case OutputStreamingStartedUpdate streamingStartedUpdate:
                        _logger.LogInformation($"[{_name}] - Output streaming started.");
                        _turnManager.Interrupt();
                        responseTranscript.Clear();
                        audioEndMs = 0;
                        break;

                    case OutputDeltaUpdate delta:
                        var common = $"EventId: {delta.EventId}, ResponseId: {delta.ResponseId}, ItemId: {delta.ItemId}, ItemIndex: {delta.ItemIndex}, Part: {delta.ContentPartIndex}";

                        if (delta.Text != null)
                        {
                            _logger.LogDebug($"[{_name}] - {nameof(RealtimeAudioService)} Text Delta: {delta.Text}, {common}");
                        }

                        if (!string.IsNullOrEmpty(delta.AudioTranscript))
                        {
                            _logger.LogDebug($"[{_name}] - {nameof(RealtimeAudioService)} Transcript Delta: {delta.AudioTranscript}, {common}");
                            responseTranscript.Append(delta.AudioTranscript);

                            _ = _transcriptBus.Post(new TranscriptionEvent(
                                _turnManager.CurrentTurnId,
                                _turnManager.CurrentToken,
                                delta.AudioTranscript));
                        }

                        if (delta.AudioBytes != null)
                        {
                            if (speechFinishedTime != TimeSpan.Zero)
                            {
                                var latency = PipelineControlPlane.Timestamp - speechFinishedTime;
                                _logger.LogInformation($"[{_name}] - LATENCY: Input speech finished to audio response started: {latency.TotalMilliseconds} ms");
                                speechFinishedTime = TimeSpan.Zero;
                            }

                            _logger.LogDebug($"[{_name}] - {nameof(RealtimeAudioService)} Audio Length: {delta.AudioBytes.Length}, Audio Max: {delta.AudioBytes.ToArray().Max()}, {common}");

                            var transcript = responseTranscript.ToString();
                            responseTranscript.Clear();

                            var audioEndTime = audioEndMs + AudioData.GetAudioDurationMs(delta.AudioBytes.Length, 24000, 1, 16);
                            audioEndMs = audioEndTime;

                            _ = _audioBus.Post(new AudioEvent(
                                _turnManager.CurrentTurnId,
                                _turnManager.CurrentToken,
                                new AudioData(delta.AudioBytes.ToArray(), 24000, 1, 16, transcript, audioEndTime, ParticipantId)));
                        }
                        break;

                    case OutputAudioTranscriptionFinishedUpdate finishedUpdate:
                        _logger.LogInformation($"[{_name}] - Output audio transcript finished: {finishedUpdate.Transcript}");
                        break;

                    case OutputStreamingFinishedUpdate finishedUpdate:
                        if (!string.IsNullOrEmpty(finishedUpdate.FunctionName))
                        {
                            _logger.LogInformation($"[{_name}] - Tool call: {finishedUpdate.FunctionName}. LATENCY: {PipelineControlPlane.Timestamp - _engagementRequestStartTime}");
                            _ = PluginUtilities.TryRunAsync(_kernel!, _plugin!, _realtimeSession!, finishedUpdate, logger: _logger, cancellationToken).ConfigureAwait(false);
                        }
                        else if (audioEndMs > 0)
                        {
                            _controlPlane.Publish(new PipelineControlPlane.ActiveSpeaker(this._name));
                            SendSilence(TimeSpan.FromMilliseconds(audioEndMs));
                            audioEndMs = 0;
                            _logger.LogInformation($"[{_name}] - Output streaming finished.");
                            break;
                        }
                        break;

                    case OutputTextFinishedUpdate finishedText:
                        _logger.LogInformation($"[{_name}] - Output text finished: {finishedText.Text}");
                        break;

                    case RealtimeErrorUpdate err:
                        _logger.LogWarning($"[{_name}] - [Realtime] {err.Message}");
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

    private void SendSilence(TimeSpan audioEndMs)
    {
        var data = new AudioData(new byte[24000], 24000, 1, 16, "", audioEndMs.TotalMilliseconds, ParticipantId);
        audioEndMs += data.Duration;
        for (int i = 0; i < 6; i++)
        {
            _audioBus?.Post(new AudioEvent(_turnManager.CurrentTurnId, _turnManager.CurrentToken, data));
        }
    }
}