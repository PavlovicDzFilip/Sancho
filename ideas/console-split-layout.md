# Split Console Layout (idea — not implemented)

## Problem
Currently all output flows through a single log stream — transcription text, Claude
responses, tool status, and system messages all interleaved with no visual separation.
It's hard to distinguish "what I said" from "what Claude said" in real time.

## Proposed: split console layout
Divide the console into visually distinct regions:

- **Transcription pane** (bottom or left) — live transcription from the cloud
  (OpenAI Realtime API / `RealtimeTranscriptionService`). Shows what the user is
  saying as it's being transcribed.
- **Claude response pane** (top or right) — streaming responses from the Claude
  CLI subprocess (`ClaudeService`). Tool calls, results, and final text.
- **Status bar** — connection state, model name, latency, microphone level.

## Architecture implications
The two data sources are already separate in the pipeline:

```
mic → Channel<byte[]> → RealtimeTranscriptionService (cloud) → Channel<string> → ???
                                                                                    ↓
                                                                          ClaudeService (subprocess)
```

- Transcription text arrives via `RealtimeTranscriptionService` events
- Claude responses arrive via `ClaudeService` parsing of `stream-json` output

This idea requires a proper console framework for regions/panels (e.g. Spectre.Console,
Terminal.Gui, or raw ANSI escape sequences for split regions).

## Relationship to other ideas
- Pairs well with **streaming-claude-response.md** — if Claude responses stream in
  real-time, the split layout shows them side-by-side with live transcription.
- Console framework choice affects both features.
