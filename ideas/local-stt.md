# Local speech-to-text

Status: **implemented** (2026-09-01) — `transcription: local` ships on the
`ITranscriptionService` seam. Engine choice, spike evidence, and trade-offs
are locked in
[ADR-0001](../docs/feature/local-stt/ADR-0001-local-stt-via-sherpa-onnx.md).

## What landed

- `LocalTranscriptionService` — sherpa-onnx streaming zipformer (English,
  int8) in-process via NuGet, live `Delta` events, utterance segmentation by
  the engine's endpoint detection, `Completed` per utterance.
- `LocalSttModels` — first-run download of the model files (~68 MB) into
  `~/.sancho/models/`, atomic `.part` downloads, reuse on later runs.
- `transcription: local` config key + `--transcription local` flag.

## Goal (original)

Replace the cloud transcription that `RecordingTranscriptionService` stood in
for with local STT, so voice reaches Claude without any OpenAI dependency.

## Seams (all used)

- `ITranscriptionService` — same PCM stream (24 kHz mono 16-bit, 100 ms
  chunks); the engine resamples to 16 kHz internally.
- `TranscriptionEvent.Delta` / `Completed` — streaming local transcripts
  drop straight in; the Orchestrator needed no changes.
- The PCM channel writer is completed when capture ends, so the service
  finalizes the last utterance on channel completion.

## Candidates (resolved)

- **sherpa-onnx** — chosen: streaming, low latency, endpointing built in,
  per-RID NuGet native runtimes covering all five Sancho publish targets.
- whisper.cpp / faster-whisper — rejected: batch-oriented, per-OS binary
  shipping, or larger models for comparable accuracy (see ADR).

## Open questions (still open)

- Streaming vs per-utterance batching — resolved: streaming with engine
  endpointing; no local VAD needed.
- Whether to keep recording the WAV alongside STT (nice for review/debugging)
  or drop it — still open; `record` mode remains for that.
- Whether `RecordingTranscriptionService` becomes "record + transcribe" or a
  second service runs in parallel — resolved: separate `local` mode, no
  parallel recording for now.
