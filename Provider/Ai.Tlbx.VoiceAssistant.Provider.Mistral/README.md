# Mistral Voxtral 3

Voxtral Mini/Small Transcribe 3 and TTS 3 via OpenRouter (default) or native Mistral.
This is a half-duplex speech pipeline: local silence detection → file transcription →
application-owned reasoning/tools → MP3 synthesis → decoded 24 kHz PCM16 playback.
Microphone input is ignored while a reply is processing or playing. Explicit interruption
cancels the turn. Disconnect cancels pending work and discards unfinished microphone audio.
This provider does not implement Mistral's separate realtime WebSocket APIs.

```csharp
var audio = new MistralAudioClient(httpClient, apiKey); // useOpenRouter: false for Mistral
var provider = new MistralVoiceProvider(audio);
var settings = new MistralVoiceSettings {
    Voice = "voice_481", // Fiona, German
    RespondAsync = async (history, ct) => await yourBackend(history, ct)
};
// Use provider/settings with the normal VoiceAssistant orchestrator and audio hardware.
```

The backend callback owns instructions, tool execution, and token accounting; register those
in your application. `TranscriptionOnly = true` needs no backend callback. Supported model
IDs are pinned to `voxtral-mini-transcribe-3-0`, `voxtral-small-transcribe-3-0`, and
`voxtral-mini-tts-3-0`. Native credentials and OpenRouter credentials are distinct.
Voxtral pace/emotion tags can be embedded in spoken text. Numeric TalkingSpeed is unsupported.

Sources: [OpenRouter STT](https://openrouter.ai/docs/guides/overview/multimodal/stt),
[OpenRouter TTS](https://openrouter.ai/docs/guides/overview/multimodal/tts),
[Mistral voices](https://docs.mistral.ai/studio/audio/text-to-speech/voices).
