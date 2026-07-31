# Split Console Layout (idea — not implemented)

## Problem
Currently all output flows through a single log stream — transcription text, Claude
responses, tool status, and system messages all interleaved with no visual separation.
It's hard to distinguish "what I said" from "what Claude said" in real time.

## Proposed: two-panel Spectre.Console layout

```
┌────────────────────────────────────────────────────────┐
│  TOP PANEL — History                                   │
│                                                        │
│  Scrollable backlog of everything that happened:        │
│  past transcription, past Claude responses,             │
│  tool calls, results. Scrolls up as new lines arrive.   │
│                                                        │
│  [User]  what are some of the ideas we have...          │
│  [Sancho] Here's what we have...                        │
│  [Tool]   git add ideas/console-split-layout.md         │
│  [User]  I want to use Spectre.Console with panels...   │
│  [Sancho] Got it...                                     │
│                                                        │
├────────────────────────────────────────────────────────┤
│  BOTTOM PANEL — Live                                   │
│                                                        │
│  ▶ You: I want to use Spectre.Console with panels...    │  ← live transcription
│  ◀ Sancho: Got it. A two-panel layout...                │  ← current Claude response
│                                                        │
├────────────────────────────────────────────────────────┤
│  Status: 🟢 Connected  |  Model: gpt-live-transcribe    │
└────────────────────────────────────────────────────────┘
```

- **Top panel**: scrollable history — everything that's been said and done.
- **Bottom panel**: the "now" — live transcription as you speak, plus whatever
  Claude is currently streaming back.
- **Status bar**: connection, model, mic level, latency — minimal, always visible.

## Framework: Spectre.Console
Use `Spectre.Console`'s `Layout` with named regions. Spectre handles panel
borders, scrolling regions, live updates, and ANSI colour — all in the box.

## Data sources (already separate)
```
mic → Channel<byte[]> → RealtimeTranscriptionService (cloud) → "You: ..."  → bottom panel
                                                                               ↓
                                                                     ClaudeService (subprocess)
                                                                               ↓
                                                                        "Sancho: ..." → bottom panel
                                                                               ↓
                                                                        (after turn) → top panel (history)
```

## Relationship to other ideas
- **Pairs with streaming-claude-response.md** — bottom panel shows Claude's
  response as it streams in; after the turn ends, everything moves to the
  history panel.
- **Independent of transcription-models.md** — works with any transcription
  backend.
