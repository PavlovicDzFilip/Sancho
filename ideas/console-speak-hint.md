# Console Speak Hint

Status: **implemented** (2026-09-03) — the transcript panel shows a cue on the
bottom row where the next sentence will appear: "🎤 Start talking…" (idle) →
"🎤 Listening…" (speech detected, green) → "⏳ Transcribing…" (pause) → the
sentence itself. OpenAI mode gets its hearing/transcribing states from the
server-VAD `speech_started`/`speech_stopped` events; local mode already had
them from silero. Hidden while reconnecting or after transcription fails, and
absent in record mode.

## Problem
There is no indication of when the user should talk. Transcribed text only
appears after a pause, so while speaking the console shows nothing — the user
may think the text isn't being captured.

## Proposed: hint at the point where text appears
Show a hint at the very end of the console output — exactly where the next
transcribed sentence will be written — with a state machine like:

1. **Idle / prompt to speak** — show a cue such as "start talking…"
2. **Listening (speech in progress)** — cue changes to something like
   "listening…" so the user knows audio is being captured before the pause
3. **Text appears on pause** — the sentence lands where the cue was

## Considerations
- Cue needs to be a single terminal line that gets replaced in place
  (ANSI clear-line + carriage return) so it doesn't litter the output.
- Works best with voice activity signals (see `voice-activity-detection.md`):
  without VAD, "listening" would just mean "audio streaming", which is always on.
- Colour: use ANSI colours consistent with existing raw console output.
- Likely interacts with `console-split-layout.md` — a status region versus
  inline cue at the write position.
