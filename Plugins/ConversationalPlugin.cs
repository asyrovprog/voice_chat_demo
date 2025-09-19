using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using OpenAI.Realtime;
using System.ComponentModel;
using System.Runtime.InteropServices;

#pragma warning disable OPENAI002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

public class ConversationalPlugin
{
    private Random _rnd = new ();

    public RealtimeAudioService? RealtimeService { get; set; }
    public ILogger Logger { get; set; }

    [KernelFunction,
     Description("Based on provided probability that you need to respond to participant speech this function returns true if response starts.")]
    public bool UpdateConversationState(
        [Description("Probability value between 0.0 and 1.0 that you are addressed and needs to respond.")] 
        float probability,

        [Description("Reason for probability. Such as 'I called by name' or 'Participant asked to wait'")]
        string? reason = null
        )
    {
        Logger?.LogInformation($"{nameof(UpdateConversationState)} called with probability to respond {probability}. Reason: {reason}.");

        var shouldRespond = _rnd.NextSingle() <= probability;
        if (shouldRespond && RealtimeService?.Session is not null)
        {
            Logger?.LogInformation($"{nameof(UpdateConversationState)} decided to start response.");
            _ = RealtimeService.Session.StartResponseAsync(default).ConfigureAwait(false);
            return true;
        }
        else
        {
            Logger?.LogInformation("After probability sampling decided not to respond.");
        }
        return false;
    }
}

