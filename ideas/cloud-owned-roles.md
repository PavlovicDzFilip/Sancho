# Cloud-Owned Roles (idea — not implemented)

## Problem
Sancho's Orchestrator wires the pipeline together but also owns several
responsibilities itself: transcript buffering and flush gating, Claude-ready
state tracking, reconnect handling, and sentence-level "turn" semantics. Some of
these are roles Sancho cannot orchestrate reliably on its own, and
re-implementing them locally duplicates behaviour the cloud services already
provide.

## Principle
Roles that cannot be orchestrated by Sancho should stay in the cloud service
itself. Sancho's job is the orchestration shell — microphone capture, wiring,
and display — and it should delegate, not re-implement:

- **Turn detection / VAD** — stays server-side (`server_vad` in the OpenAI
  Realtime session config). Sancho never derives speech boundaries from raw audio.
- **Transcription** — the Realtime API transcribes; Sancho only forwards PCM.
- **The agent loop** — tool calling, permissions, and reasoning all run inside
  the Claude CLI subprocess; Sancho just relays messages and renders events.

## Open question
How far to push this? A full voice-agent role exists in the Realtime API itself
(instructions, tools, audio output). If Sancho ever moves past the
"microphone → transcription → Claude CLI" pipeline, the conversation role itself
could live in the cloud rather than in a local subprocess. The current transcript
buffer / flush logic in `Orchestrator` is the local piece most worth revisiting
under this principle.
