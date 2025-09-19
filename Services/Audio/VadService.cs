// Copyright (c) Microsoft. All rights reserved.

using WebRtcVadSharp;
using System.Linq;

public class VadService : IDisposable
{
    // Voice Activity Detection Constants
    private const int MaxPrerollFrames = 10; // Maximum number of frames to keep before speech detection
    private const int SilenceThresholdFrames = 20; // Number of consecutive silent frames to end speech segment
    private const double MinSpeechDurationSeconds = 0.8; // Minimum duration in seconds for valid speech utterance
    private static readonly int ExpectedFrameBytes = (AudioOptions.SampleRate * AudioOptions.Channels * AudioOptions.BitsPerSample * AudioOptions.BufferMilliseconds) / (8 * 1000);

    private readonly WebRtcVad _vad = new() { OperatingMode = OperatingMode.VeryAggressive };
    private readonly TurnManager _turnManager;

    // State for pipeline processing
    private readonly Queue<byte[]> _frames = new();
    private int _silenceFrames = 0;

    private enum VadState { Idle, InSpeech }
    private VadState _state = VadState.Idle;

    public VadService(PipelineControlPlane controlPlane)
    {
        this._turnManager = controlPlane.TurnManager;
    }

    /// <summary>
    /// Pipeline integration method for processing audio chunk events into speech segments.
    /// This method handles the pipeline event creation and processing.
    /// </summary>
    /// <param name="audioChunkEvent">Audio chunk event from the pipeline.</param>
    /// <returns>Audio events when speech segments are detected.</returns>
    public IEnumerable<AudioEvent> Transform(AudioChunkEvent audioChunkEvent)
    {
        foreach (var audioEvent in this.ProcessAudioChunk(audioChunkEvent.Payload))
        {
            yield return audioEvent;
        }
    }

    /// <summary>
    /// Legacy pipeline integration method for processing raw audio chunks into speech segments.
    /// </summary>
    /// <param name="audioChunk">Raw audio chunk from microphone.</param>
    /// <returns>Audio events when speech segments are detected.</returns>
    public IEnumerable<AudioEvent> Transform(byte[] audioChunk)
    {
        foreach (var audioEvent in this.ProcessAudioChunk(audioChunk))
        {
            yield return audioEvent;
        }
    }

    /// <summary>
    /// Core audio processing logic for speech detection and segmentation.
    /// </summary>
    /// <param name="audioChunk">Raw audio chunk to process.</param>
    /// <returns>Audio events when speech segments are detected.</returns>
    private IEnumerable<AudioEvent> ProcessAudioChunk(byte[] audioChunk)
    {
        if (audioChunk is null || audioChunk.Length != ExpectedFrameBytes)
        {
            // Ignore invalid frames per CON-001
            yield break;
        }

        var frame = audioChunk.ToArray(); // Need to copy, since upstream may reuse this buffers
        bool voiced = this.HasSpeech(frame); // audioChunk expected to be in 20ms chunks

        switch (this._state)
        {
            case VadState.Idle:
                // Accumulate preroll frames up to cap
                this._frames.Enqueue(frame);
                while (this._frames.Count > MaxPrerollFrames)
                {
                    this._frames.Dequeue();
                }

                if (voiced)
                {
                    // Transition to InSpeech (queue already contains preroll + current frame)
                    this._state = VadState.InSpeech;
                    this._silenceFrames = 0;
                }
                break;

            case VadState.InSpeech:
                this._frames.Enqueue(frame);
                this._silenceFrames = voiced ? 0 : this._silenceFrames + 1;

                if (this._silenceFrames >= SilenceThresholdFrames)
                {
                    var merged = _frames.SelectMany(f => f).ToArray();
                    var audio = new AudioData(merged, AudioOptions.SampleRate, AudioOptions.Channels, AudioOptions.BitsPerSample);
                    if (audio.Duration.TotalSeconds > MinSpeechDurationSeconds)
                    {
                        this._turnManager.Interrupt();
                        yield return new AudioEvent(this._turnManager.CurrentTurnId, this._turnManager.CurrentToken, audio);
                    }
                    this.ResetToIdle();
                }
                break;
        }
    }

    private bool HasSpeech(byte[] frame20ms) => this._vad.HasSpeech(frame20ms, SampleRate.Is16kHz, FrameLength.Is20ms);

    public void Dispose() => this._vad.Dispose();

    private void ResetToIdle()
    {
        this._frames.Clear();
        this._silenceFrames = 0;
        this._state = VadState.Idle;
    }
}
