// Copyright (c) Microsoft. All rights reserved.

global using AudioChunkEvent = PipelineEvent<byte[]>;
global using AudioEvent = PipelineEvent<AudioData>;
global using ChatEvent = PipelineEvent<string>;
global using SpeechEvent = PipelineEvent<byte[]>;
global using TranscriptionEvent = PipelineEvent<string?>;

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

public record AudioData(byte[] Data, int SampleRate, int Channels, int BitsPerSample, string? Transcript = null, int AudioEndMs = 0)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)this.Data.Length / (this.SampleRate * this.Channels * this.BitsPerSample / 8));

    public static int GetAudioDurationMs(int sizeInBytes, int sampleRate, int channels, int bitsPerSample) => (int) (1000.0 * sizeInBytes / (sampleRate * channels * bitsPerSample / 8));
}
