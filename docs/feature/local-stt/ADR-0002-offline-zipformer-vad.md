# ADR-0002: Offline zipformer + silero VAD for local STT (replaces streaming)

- Status: accepted (implemented 2026-09-01, spike-validated on Linux x64)
- Feature: local-stt
- Supersedes: ADR-0001 (streaming zipformer via engine endpointing)

## Context

ADR-0001 shipped local STT on the streaming zipformer English int8 model with
the engine's built-in endpoint detection. Real-world feedback said quality was
low, and measurement showed the streaming decoder costs ~1.9 CPU-seconds per
audio-second (1.9 cores sustained, 10-core bursts). The alternatives on the
table were offline zipformer + VAD, whisper tiny/base, the older 20M streaming
zipformer, and VOSK.

## Decision

Keep **sherpa-onnx** (same NuGet, same seam) but switch the engine:

- **Offline (non-streaming) zipformer English int8**
  (`sherpa-onnx-zipformer-en-2023-06-26`) for decode. The offline encoder sees
  the whole utterance instead of a 16-chunk streaming window — better accuracy
  (the streaming lookahead is the classic streaming tax) and far cheaper CPU.
- **Silero VAD** (`silero_vad.onnx`) for utterance segmentation instead of the
  engine's endpoint rules.
- No live deltas: utterances arrive as `Completed` events at utterance end
  (~1 s after the speaker stops: 0.5 s min-silence + 0.5 s VAD feed cadence).
  The orchestrator only used deltas for the live display; all processing runs
  on `Completed`, so nothing functional is lost.
- int8, not fp32: identical demo accuracy in the spike, ~4% more CPU for fp32
  with 2× the RAM (820 MB vs 410 MB) and 3.6× the download (248 MB vs 68 MB).

## Spike evidence (2026-09-01, Linux x64, CPU, same 24.8 s demo audio as ADR-0001)

Measured with the same methodology as ADR-0001 (files only — no sound played):

| Config | Total CPU | Avg cores | Peak | RSS |
|---|---|---|---|---|
| streaming int8 (ADR-0001, 4 thr) | 46.7 s | 1.9 | ~10 | 211 MB |
| offline int8, VAD 1thr/500ms, rec 4thr | 5.3 s | 0.21 | 5.1 | 411 MB |
| **offline int8, VAD 1thr/500ms, rec 2thr (shipped)** | **3.9 s** | **0.16** | **2.3** | **410 MB** |
| offline fp32, VAD 1thr/500ms, rec 4thr | 6.1 s | 0.24 | 6.7 | 820 MB |
| idle baseline (25 s pure silence) | 3.2 s | 0.12 | 2.4 | 292 MB |

That is **~12× less CPU** than the streaming engine at equal-or-better
accuracy — both demo utterances word-for-word against the reference
transcripts in every config above.

### Findings that shaped the implementation

- **VAD idle spin**: feeding silero every 100 ms chunk (10 ORT Run()/s) burns
  ~0.5 idle cores — each Run() leaves ORT intra-op threads spinning. Feeding
  the VAD in 500 ms batches with `NumThreads=1` drops idle burn to ~0.1 cores.
  The recognizer contributes ~0.1 cores of its own (4 threads → shipped 2).
- **Segment boundary design**: silero's `SpeechSegment.Start`/`Samples` lag
  speech onset by a window or two, so decoding the VAD's own segment clips the
  first word. Instead the service accumulates a pending buffer since the last
  utterance and decodes *that* on each pop (capped at 2 s of pre-roll during
  silence). Carrying the previous segment's tail into the next decode
  hallucinated a leading word ("THREE GOD…"), so the buffer resets fully after
  each decode.
- **Exact-zero hallucination**: long runs of exact digital zeros before speech
  (a test-file artifact from concatenation) make both int8 and fp32
  hallucinate a leading token. Real microphone noise floors avoid this; noted
  as a known edge rather than guarded (a guard would mean distorting audio).

## Consequences

- `LocalTranscriptionService` rewritten: VAD + offline recognizer + pending
  buffer + 24 kHz→16 kHz linear resample for the VAD path only (the recognizer
  keeps sherpa's internal windowed-sinc resampler on 24 kHz input).
- `LocalSttModels` downloads the offline model set + silero VAD (~70 MB total)
  and deletes the superseded streaming-model directory.
- Quality ceiling if ever needed: fp32 offline zipformer (file swap; costs in
  the table above). Bigger models (whisper-large class) were ruled out: GB
  downloads and multi-core decode for diminishing returns on dictation.
