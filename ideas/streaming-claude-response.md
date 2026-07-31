# Streaming Claude Response (idea — not implemented)

## Problem
Currently Claude's response is fully buffered in `_turnBuffer` and only displayed
at end-of-turn (when `type=result` arrives). During long tool executions or complex
reasoning, the user sees nothing — no indication Claude is working.

## Proposed: stream content blocks in real-time
Instead of buffering text and tool_use blocks in `HandleAssistantMessage`, log them
immediately as they arrive. This gives live feedback: text appears word-by-word,
tool calls show as they happen, tool results appear inline.

## Challenge: three-mode detection
The current FlushTurn detection (… / 💡 / direct / tool-use) requires knowing the
full response before deciding how to render it. With streaming, we don't know the
mode until the turn is complete.

### Option A: Stream in neutral, re-color at end
- Stream all text in default/dim colour as it arrives
- At end-of-turn, if the full response was `…`, erase or dim what we streamed
- If it was 💡, leave it dimmed
- If it was direct/tool-use, it's already visible

### Option B: Stream in orange, retroactively suppress
- Stream all text in orange as it arrives
- At end-of-turn, if it was `…` or `💡`, change the display retroactively
- Requires Spectre.Console or similar for content updating

### Option C: First-chunk detection
- Stream the first content block immediately
- If the first chunk starts with `…` or `💡`, we know the mode early
- If not, stream in orange (direct response mode)
- Edge case: what if first chunk is just "…" but more follows? (prompt would prevent this)

### Option D: Hybrid — stream tool-use, buffer text
- Tool use and results stream immediately (they're always "real work")
- Text is buffered as now, flushed at end-of-turn
- This solves the "is Claude working?" problem without the mode-detection issue
- Trade-off: text responses still appear all at once

## Recommendation
Option D is the pragmatic first step — it solves the actual user pain (not knowing
if Claude is working) without requiring mode-detection gymnastics. Tool activity
streaming is unambiguous, and text can stay buffered.
