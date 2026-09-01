# ADR-0001: Local speech-to-text via sherpa-onnx (streaming zipformer en, int8)

- Status: accepted (implemented 2026-09-01, spike-validated on Linux x64)
- Feature: local-stt
- Supersedes: the "interim" framing in `ideas/local-stt.md`

## Context

Sancho's voice pipeline (mic → 24 kHz PCM16 mono → transcription → Claude CLI)
sent audio to OpenAI Realtime; the `record` mode was an interim WAV-only
replacement. The goal: transcribe on-device so no audio leaves the machine,
on the existing `ITranscriptionService` seam, across all five publish RIDs
(win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64).

## Decision

Use **sherpa-onnx**, integrated **in-process via the official NuGet package**
(`org.k2fsa.sherpa.onnx` 1.13.5, which pulls per-RID native runtime packages
for all five Sancho RIDs), with the **streaming zipformer English int8 model**
(`sherpa-onnx-streaming-zipformer-en-2023-06-26`) on CPU, greedy search,
and the engine's built-in **endpoint detection** for utterance segmentation
(rule1 2.4 s / rule2 1.2 s / rule3 20 s — no separate VAD model needed).

## Alternatives considered

- **whisper.cpp / faster-whisper (subprocess)**: subprocess style matches the
  ffmpeg capture backend, but requires shipping a per-OS binary and managing
  its lifecycle; whisper.cpp is batch-oriented (no cheap streaming partials).
- **whisper tiny.en via sherpa-onnx offline recognizer**: also spike-tested —
  accurate, but batch-only (no live deltas) and its int8 files are larger
  (~99 MB vs ~68 MB).
- **whisper.cpp via Whisper.net NuGet**: in-process, but streaming partials
  are limited; model files are larger for comparable accuracy.
- **sherpa-onnx CLI binaries (subprocess)**: works (validated in the spike)
  but the prebuilt CLI bundle is not published for every release version and
  would complicate the installers; the NuGet package needs nothing from the
  installers at all.

## Spike evidence (2026-09-01, Linux x64, CPU, int8)

- Streaming zipformer en via `sherpa-onnx-vad-with-online-asr`: both bundled
  LibriSpeech test wavs transcribed **exactly** matching `trans.txt`;
  RTF 0.075 (~13× faster than real time) on 4 threads.
- whisper tiny.en int8 offline: near-exact (US vs UK spelling variance only),
  ~1.4 s per file.
- In-process C# path: model download (Hugging Face mirror, individual files,
  ~68 MB total), recognizer setup, 24 kHz→16 kHz internal resampling
  (`OnlineStream.AcceptWaveform` creates a low-pass `LinearResample` when the
  input rate differs — confirmed in `features.cc` v1.13.5), live deltas, and
  two-utterance endpointing all exercised through the real
  `LocalTranscriptionService` via a demo-sound harness (no audio playback).

## Consequences

- `transcription: local` selects `LocalTranscriptionService`; `openai` stays
  the default so existing behavior is unchanged.
- First local run downloads 4 model files into `~/.sancho/models/`
  (`SANCHO_CONFIG_DIR` overrides); downloads use `.part` files and atomic
  moves, reuse existing files, and use a dedicated HttpClient (30 min
  timeout — the app-wide 45 s client would kill the 67 MB encoder download).
- The encoder keeps ~0.6 s of lookahead; `FinalizeUtterance` feeds trailing
  silence before `InputFinished()` or the last words are silently dropped.
- Utterance endpointing lives inside the engine; Sancho's capture remains
  utterance-agnostic and both `openai` and `local` modes present the same
  event shape (`Delta`/`Completed`), so the Orchestrator needed no changes.
- Local mode does not record WAVs; `record` mode remains for that.

## Open questions (unchanged from ideas/local-stt.md)

- Whether to keep a parallel WAV recording alongside local STT for
  review/debugging.
- Model download size on first run (~68 MB) — acceptable for now; a smaller
  model (e.g. mobile zipformer) could shrink it later.
