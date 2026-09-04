# ADR-0004: Remove the OpenAI and record backends — sancho is fully offline

- Status: accepted (implemented 2026-09-03)
- Feature: local-stt
- Supersedes: the `openai` and `record` transcription modes (ADR-0001..0003 context)

## Context

Sancho shipped three transcription backends: `openai` (Realtime WebSocket,
the default), `local` (offline whisper per ADR-0003), and `record` (local
WAV). In practice the offline path won: it is free, private, and — after
ADR-0003 — accurate enough that the cloud mode is no longer the better
default. The OpenAI backend carried extra surface (API key config,
interactive key prompt, Realtime protocol handling, reconnect logic) and
the OpenAI dependency also powered session-title generation. Once OpenAI
was gone, `record` (raw WAV capture with no transcription) had no remaining
purpose either, and with a single backend the `transcription` config key
and `--transcription` flag had nothing left to select.

## Decision

Remove both extra backends and all mode-selection surface:

- Delete `RealtimeTranscriptionService` (the WebSocket backend),
  `SessionTitleService` (the only other OpenAI API call), and
  `RecordingTranscriptionService` (the record backend).
- Drop `apiKey` and `transcription` from the config model, CLI flags,
  env-var precedence, and docs. Existing config files that still contain
  them load fine — the source-generated deserializer ignores unknown
  properties.
- `LocalTranscriptionService` (whisper small.en + silero VAD) becomes the
  only transcription path — sancho is fully offline.
- Session titles stop being generated. Existing `.title` files on disk are
  still read by `ClaudeService` for the session picker, so previously named
  sessions keep their names.
- The `config get/set` plumbing stays (zero keys) as a home for future
  settings; `SANCHO_CONFIG_DIR` still relocates models/logs.

## Consequences

- One engine to maintain (`LocalTranscriptionService` + whisper), no mode
  dispatch in the orchestrator, no secrets in config, no network calls —
  sancho is fully offline.
- New sessions have no generated title; the `--continue` picker falls back
  to message previews (already the behavior when a title is absent).
