# Local speech-to-text (planned)

Status: idea — both backends exist today (OpenAI Realtime as the default, local WAV recording via `transcription: record`); STT is the next step.

## Goal

Replace the cloud transcription that `RecordingTranscriptionService` stands in for with local STT, so voice reaches Claude without any OpenAI dependency.

## Seams already ready

- `ITranscriptionService` — `RecordingTranscriptionService` consumes the same PCM stream (24 kHz mono 16-bit, 100 ms chunks) a local STT engine needs. Swap or extend on this seam.
- `TranscriptionEvent.Delta` / `Completed` and the Orchestrator handling for them are intact and unused — streaming local transcripts drop straight in.
- The PCM channel writer is completed when capture ends, so an STT service can finalize on channel completion the way the recorder does.

## Candidates

- whisper.cpp / faster-whisper — small models, CPU-friendly, subprocess like ffmpeg (fits the codebase style).
- sherpa-onnx — streaming, low latency, includes its own VAD.

## Open questions

- Streaming vs per-utterance batching; local VAD segmentation (see `voice-activity-detection.md`) vs endpointing inside the engine.
- Whether to keep recording the WAV alongside STT (nice for review/debugging; WAV is lossless so STT can also consume the recorded file directly) or drop it.
- Whether `RecordingTranscriptionService` becomes "record + transcribe" or a second service runs in parallel.
