// Copyright (c) Microsoft. All rights reserved.

global using AudioChunkEvent = PipelineEvent<byte[]>;
global using AudioEvent = PipelineEvent<AudioData>;
global using TranscriptionEvent = PipelineEvent<string?>;
global using ChatEvent = PipelineEvent<string>;
global using SpeechEvent = PipelineEvent<byte[]>;

public readonly struct PipelineEvent<T>(int turnId, CancellationToken cancellationToken, T payload)
{
    public int TurnId { get; } = turnId;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public T Payload { get; } = payload;

    public static bool IsValid(PipelineEvent<T> evt, int currentTurnId, Func<T, bool>? payloadPredicate = null)
        => evt.Payload != null
            && evt.TurnId == currentTurnId
            && !evt.CancellationToken.IsCancellationRequested
            && (payloadPredicate?.Invoke(evt.Payload) ?? true);
}

public record AudioData(byte[] Data, int SampleRate, int Channels, int BitsPerSample)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Data.Length / (SampleRate * Channels * BitsPerSample / 8));
}