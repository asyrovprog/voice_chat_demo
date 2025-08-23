// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.CompilerServices;
using WebRtcVadSharp;

public class VadService : IDisposable
{
    // Voice Activity Detection Constants
    private const int MaxPrerollFrames = 10;             // Maximum number of frames to keep before speech detection
    private const int SilenceThresholdFrames = 20;       // Number of consecutive silent frames to end speech segment
    private const double MinSpeechDurationSeconds = 0.8; // Minimum duration in seconds for valid speech utterance
    
    private readonly WebRtcVad _vad = new() { OperatingMode = OperatingMode.VeryAggressive };
    private readonly TurnManager _turnManager;

    // State for pipeline processing
    private readonly Queue<byte[]> _preroll = new();
    private readonly List<byte> _speech = new();
    private int _silenceFrames = 0;
    private bool _inSpeech = false;

    public VadService(TurnManager turnManager)
    {
        _turnManager = turnManager;
    }

    /// <summary>
    /// Pipeline integration method for processing audio chunk events into speech segments.
    /// This method handles the pipeline event creation and processing.
    /// </summary>
    /// <param name="audioChunkEvent">Audio chunk event from the pipeline.</param>
    /// <returns>Audio events when speech segments are detected.</returns>
    public IEnumerable<AudioEvent> Transform(AudioChunkEvent audioChunkEvent)
    {
        foreach (var audioEvent in ProcessAudioChunk(audioChunkEvent.Payload))
        {
            yield return audioEvent;
        }
    }

    /// <summary>
    /// Creates an AudioChunkEvent from raw audio data for pipeline processing.
    /// </summary>
    /// <param name="audioChunk">Raw audio chunk from microphone.</param>
    /// <returns>AudioChunkEvent ready for pipeline processing.</returns>
    public AudioChunkEvent CreateAudioChunkEvent(byte[] audioChunk)
    {
        return new AudioChunkEvent(_turnManager.CurrentTurnId, _turnManager.CurrentToken, audioChunk);
    }

    /// <summary>
    /// Legacy pipeline integration method for processing raw audio chunks into speech segments.
    /// </summary>
    /// <param name="audioChunk">Raw audio chunk from microphone.</param>
    /// <returns>Audio events when speech segments are detected.</returns>
    public IEnumerable<AudioEvent> Transform(byte[] audioChunk)
    {
        foreach (var audioEvent in ProcessAudioChunk(audioChunk))
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
        bool voiced = HasSpeech(audioChunk); // audioChunk expected to be in 20ms chunks
        
        if (!_inSpeech)
        {
            _preroll.Enqueue(audioChunk);
            while (_preroll.Count > MaxPrerollFrames)
            { 
                _preroll.Dequeue();
            }

            if (voiced)
            { 
                _inSpeech = true;
                while (_preroll.Count > 0)
                {
                    _speech.AddRange(_preroll.Dequeue()); 
                    _silenceFrames = 0;
                }
            }
        }
        else
        {
            _speech.AddRange(audioChunk);
            _silenceFrames = voiced ? 0 : _silenceFrames + 1;

            if (_silenceFrames >= SilenceThresholdFrames)
            {
                var audio = new AudioData(_speech.ToArray(), AudioOptions.SampleRate, AudioOptions.Channels, AudioOptions.BitsPerSample);
                if (audio.Duration.TotalSeconds > MinSpeechDurationSeconds)
                {
                    _turnManager.Interrupt();
                    yield return new AudioEvent(_turnManager.CurrentTurnId, _turnManager.CurrentToken, audio);
                }
                _speech.Clear(); 
                _inSpeech = false; 
                _silenceFrames = 0;
            }
        }
    }

    public bool HasSpeech(byte[] frame20ms) => _vad.HasSpeech(frame20ms, SampleRate.Is16kHz, FrameLength.Is20ms);


    public void Dispose() => _vad.Dispose();
}
