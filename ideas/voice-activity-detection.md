# Voice Activity Detection (idea — not implemented)

## Problem
Currently audio is streamed from the microphone to the transcription service
continuously — even during silence. This wastes API bandwidth and can produce
spurious transcriptions from ambient noise.

## Proposed: pause mic stream during silence
Insert a VAD stage between `MicrophoneAudioSource` and the transcription WebSocket.
When no speech is detected for a configurable duration, pause sending audio chunks
and resume when speech returns.

```
mic → VAD gate → Channel<byte[]> → RealtimeTranscriptionService → Channel<string> → Claude
```

## Options

### Option A: Client-side VAD library
- **WebRTC VAD** (C# wrapper or P/Invoke) — lightweight, well-known algorithm
- **Silero VAD** via ONNX runtime — neural, more accurate, heavier
- **NAudio built-in** — `AudioFileReader` + amplitude threshold (simplest, least accurate)

### Option B: Server-side (OpenAI turn detection)
- OpenAI Realtime API has server-side VAD with configurable `turn_detection`
- Set `turn_detection.type: "server_vad"` in the session config
- Server sends `input_audio_buffer.speech_started` / `speech_stopped` events
- No local processing needed, but less control over sensitivity

## Considerations
- **False positives**: sudden noises (keyboard, door) triggering resume — needs a
  minimum speech duration threshold
- **False negatives**: quiet speech dropped — adjustable sensitivity/VAD threshold
- **Latency**: VAD adds a few ms of buffering before decide-or-drop
- **API cost**: reduces OpenAI transcription minutes billed (only pay for speech)
