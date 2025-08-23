# Voice Chat

This example demonstrates a voice chat application using Semantic Kernel and OpenAI's API for speech-to-text, chat, and text-to-speech functionalities. The application captures audio from the microphone, processes it through a pipeline, and plays back the AI-generated responses with the following flow:
```
Microphone → VAD → STT → Chat → TTS → Speaker
```

## API Key 
Use .NET user-secrets to securely store your API key:

```bash
dotnet user-secrets set "OpenAI:ApiKey" "your-openai-api-key"
```

## Extending the Sample

The sample can be further extended by improving VAD, STT and other components. Some suggestions include:
 - Use local CPU ML model based Voice Activity Detector, such as [Silero VAD](https://github.com/snakers4/silero-vad)
 - Use audio streaming, such as supported by 
 [Azure AI Speech](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/how-to-use-audio-input-streams), 
 [Deepgram](https://developers.deepgram.com/docs/live-streaming-audio) and other providers.
 - Connect Semantic Kernel plugins or tools for richer, task-oriented conversations.