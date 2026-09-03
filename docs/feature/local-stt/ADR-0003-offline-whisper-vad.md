# ADR-0003: Offline whisper small.en + silero VAD for local STT

- Status: accepted (implemented 2026-09-03)
- Feature: local-stt
- Supersedes: ADR-0002 (offline zipformer English int8)

## Context

ADR-0002 shipped offline zipformer English int8 + silero VAD. It solved the
CPU problem (0.16 cores idle) but zipformer-en's accuracy is only moderate
and the model is English-only. The Whisper-family approach used by
[handy](https://github.com/cjpais/handy) (whisper.cpp GGML models) was
requested as the reference. sherpa-onnx — already the native dependency —
serves Whisper models through the same `OfflineRecognizer` API, so the swap
keeps the whole ADR-0002 pipeline (VAD segmentation, pending buffer,
utterance-end `Completed` events, `.part`-safe model provisioning) and adds
no new native library.

## Decision

Keep sherpa-onnx and the ADR-0002 pipeline; swap the recognizer:

- **Whisper small.en int8** (`sherpa-onnx-whisper-small.en`, csukuangfj HF
  mirror): `small.en-encoder.int8.onnx` + `small.en-decoder.int8.onnx` +
  `small.en-tokens.txt`. Same 16 kHz / 80-dim features, greedy search, CPU
  provider.
- English-only `.en` model; `Whisper.Language` left empty (auto-detect).
- `NumThreads` raised 2 → 4: whisper decode is heavier than zipformer was.

## Trade-offs

- **Accuracy**: whisper small.en is a meaningful step up from zipformer-en
  on conversational dictation (the whole point of the swap).
- **Download**: ~380 MB vs ~70 MB for zipformer.
- **Decode latency**: roughly real-time on a desktop CPU (~2–4 s per
  utterance after the speaker stops). There are no live deltas, so text
  lands later than zipformer's — acceptable for utterance-at-a-time use.
- **CPU**: decode is bursty multi-core work at utterance end; idle cost is
  unchanged (the VAD path is untouched).

## Status note

**The model size may need revisiting.** small.en was chosen for accuracy;
if decode latency or the ~460 MB download bothers users, `base.en`
(~140 MB, ~1.5–2× faster decode) is a file-and-config swap. Non-English
speakers would want multilingual `base` (~150 MB, auto language detection).
GPU variants exist, but CPU keeps the cross-platform, no-driver story.

## Consequences

- `LocalTranscriptionService.CreateRecognizer` points at the whisper config;
  everything else (VAD, pending buffer, resampling, events) is unchanged.
- `LocalSttModels` downloads the whisper set and deletes the superseded
  zipformer directory after a successful download.
- Since offline whisper has no live deltas, the status bar compensates with
  `SpeechDetected`/`Decoding` events: "● hearing…" while the user speaks and
  "⏳ transcribing…" during the decode, resetting on `Completed`.
- VAD tuning: `MinSilenceDuration` raised 0.5 → 1.5 s and the minimum
  segment floor 250 → 500 ms — with 0.5 s every mid-sentence pause split
  the utterance and whisper hallucinated on the short leftover chunks.
  The tradeoff is ~1 s more latency after the speaker stops.
- `Program.cs` sets `Console.OutputEncoding = UTF8` so emoji/box-drawing
  UI renders on legacy Windows consoles instead of '?', and sets
  `SHERPA_ONNX_LOG_LEVEL=WARNING` to quiet the native logger's INFO noise.
- The recognizer is fed its native 16 kHz (linear 3:2 resample of the
  24 kHz capture) rather than 24 kHz: whisper's log-mel features only span
  0-8 kHz so the resampler's aliasing is invisible to it, and sherpa never
  builds its own resampler — avoiding a LOGE "Creating a resampler" message
  on stderr per decode.
