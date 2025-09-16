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

public record AudioData(byte[] Data, int SampleRate, int Channels, int BitsPerSample, string? Transcript = null, double AudioEndMs = 0, int Participant = 0)
{
    public int BytesPerFrame => checked(Channels * (BitsPerSample / 8));
    public int Samples => Data.Length / BytesPerFrame;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples / SampleRate);

    public static double GetAudioDurationMs(int sizeInBytes, int sampleRate, int channels, int bitsPerSample)
    {
        var bytesPerFrame = checked(channels * (bitsPerSample / 8));
        var samples = sizeInBytes / bytesPerFrame;
        return (double) samples / sampleRate;
    }

    public static AudioData Concat(AudioData first, AudioData second)
    {
        var combinedData = new byte[first.Data.Length + second.Data.Length];
        Buffer.BlockCopy(first.Data, 0, combinedData, 0, first.Data.Length);
        Buffer.BlockCopy(second.Data, 0, combinedData, first.Data.Length, second.Data.Length);
        var endMs = first.AudioEndMs + GetAudioDurationMs(second.Data.Length, first.SampleRate, first.Channels, first.BitsPerSample);

        return new AudioData(
            combinedData,
            first.SampleRate,
            first.Channels,
            first.BitsPerSample,
            first.Transcript ?? string.Empty + second.Transcript ?? string.Empty,
            endMs,
            second.Participant);
    }
}
