# Voice Chat

This example demonstrates a voice chat application using Semantic Kernel and OpenAI's API for speech-to-text, chat, and text-to-speech functionalities. The application captures audio from the microphone, processes it through a pipeline, and plays back the AI-generated responses with the following flow:
```
Microphone → VAD → STT → Chat (SK) → TTS → Speaker
```

## API Key 
Use .NET user-secrets to securely store your API key:

```bash
dotnet user-secrets set "OpenAI:ApiKey" "your-openai-api-key"
```
