# Transcription Model Options (explored 2026-07-31)

## Current
`gpt-realtime-whisper` — older Realtime API model, moderate accuracy.

## Upgrade candidate: gpt-live-transcribe
Released July 2026. Drop-in replacement in the Realtime WebSocket session config.
Lower word error rate, better noise immunity than gpt-realtime-whisper.

### Config
```json
"audio.input.transcription": {
    "model": "gpt-live-transcribe",
    "delay": "high",
    "prompt": "Technical discussion about software development",
    "languages": ["en"]
}
```

- `delay`: minimal | low | medium | high | xhigh (no published ms numbers — benchmark needed)
- `prompt`: context hints for domain vocabulary
- `languages`: plural array format (not `language` singular)
- No word timestamps, no speaker labels, no confidence scores

### Pricing
$0.017/min — same as gpt-realtime-whisper.

## Non-OpenAI alternative
**Deepgram Nova-3** — lower claimed WER (6.84%), half the price ($0.0077/min streaming), sub-300ms latency. Would require replacing the transcription layer (IAudioSource → Deepgram SDK). Has Flux end-of-turn detection.
