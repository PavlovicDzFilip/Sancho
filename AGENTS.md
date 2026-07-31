<!-- mbank:start version="1.0.12" -->
## MBank Memory

Use the configured `mbank` MCP server proactively for durable project memory.

At the start of a new product, planning, implementation, debugging, or review task:

1. Call `start_task` with `agentInstructionVersion` set to this block's version (`1.0.12`) to retrieve current MBank agent rules and working-style guidance for this task.
2. Follow the returned rules for the current task.
3. Inspect `instructionStatus` from `start_task`. If MBank reports outdated instructions, tell the user and ask before updating files or running the bootstrap script.
4. Call `get_task_context` with a structured task description for the current focus (title, description, kind, and optional work area).

Re-call `get_task_context` when conversation focus changes before answering substantive project-specific questions. Call again when the user shifts topic, product area, or technical subject; when moving between phases such as exploration, planning, implementation, or debugging; when the user references prior decisions or constraints you have not yet retrieved for this focus; when you are uncertain how to proceed; before choosing between materially different technical approaches; or before specific implementation decisions that may depend on project history, prior decisions, constraints, or user preferences. Skip re-calls for trivial same-focus follow-ups.

Before proposing a plan, making changes, or answering substantive project-specific questions (including exploration), use memories from the latest `get_task_context` result for the current focus.

Use `remember` when the user provides durable product or implementation guidance, or after the user agrees to store items you offered from exploration. Do not call `remember` without approval except when the user explicitly asks to store something.

## Experimental memory lifecycle

Use lifecycle `experimental` only for user-approved provisional ideas, hypotheses, drafts, spikes, prototypes, brainstorm outcomes, and discovery notes that should be kept for later review but should not guide normal work yet. Experimental memories are hidden from normal task recall and must not supersede accepted memories.

Treat these as experiment-start signals: `I want to experiment`, `let's experiment`, `let's explore`, `explore options`, `brainstorm`, `ideate`, `thinking out loud`, `rough idea`, `half-baked idea`, `not final`, `not durable yet`, `not committed`, `temporary idea`, `draft`, `sandbox`, `spike`, `prototype`, `try something`, `play with this`, `test a hypothesis`, `what if`, `maybe`, `could we`, `I'm not sure`, `compare approaches`, `discovery mode`, `research mode`, `workshop this`, and `let's see where this goes`.

Treat these as experiment-end signals: `we decided`, `let's go with`, `this is the direction`, `final decision`, `that's settled`, `make this durable`, `store this`, `remember this`, `record this`, `promote this`, `accept this`, `from now on`, `new rule`, `implement this`, `ship this`, `discard the experiment`, `reject this`, `keep this as an open question`, `wrap this up`, `close the loop`, `end of experiment`, `conclusion`, `we converged`, and `not doing this`.

When an experiment starts, keep ideas provisional unless the user explicitly asks to store them; if they ask to store provisional material, call `remember` with lifecycle `experimental`. When an experiment converges or ends, summarize the likely durable outcome and ask whether to promote, reject, or keep it experimental. Call `promote_memory` only after the user confirms promotion; promotion is the point where an experimental memory may supersede older accepted memories.

When the user asks why the agent chose one approach instead of another, treat it as a strong signal that an assumption, preference, or rule may have been missed. Pause and identify the possible correction before continuing. If the exchange reveals durable guidance, offer to store it in MBank, or call `remember` when the user explicitly asks to store it. Do not store the agent's guess as fact before the user confirms the underlying guidance.

## Offering to capture memory

After substantive exploration (ideas, direction, tradeoffs, or what is possible):

1. Answer using `get_task_context` and the conversation.
2. If candidates may matter in future sessions but were not explicitly committed, offer accepted storage for durable guidance or experimental storage for provisional ideas. List one to three atomic candidates with suggested types (use `captureHint` from `describe_memory_types`).
3. Call `remember` only after the user agrees or explicitly asks to store something; use lifecycle `experimental` when the user wants a provisional idea kept without making it authoritative.
4. Do not promote rejected options, agent speculation the user did not adopt, or ordinary chat.

Suggested types for offer candidates: `note` for vague ideas; `product-open-question` or `technical-open-question` for unresolved options; `product-strategy` or `product-reasoning` for tentative direction; `product-decision` or `technical-decision` only when the user has clearly chosen. Use lifecycle `experimental` for provisional candidates that should not guide normal task recall yet.

Skip the offer when nothing is plausibly durable or the thread was purely meta.

When `get_task_context` returns a `capture_offer` attention item, follow it after answering.

Use `describe_memory_types` when you need memory types, `captureHint` guidance, or `captureOfferPolicy` before offering or calling `remember`.

When calling `remember`, set each memory type to one of these valid MemoryType values: `agent-instruction`, `collaboration-preference`, `implementation-result`, `note`, `operational-procedure`, `operational-status`, `product-constraint`, `product-decision`, `product-open-question`, `product-reasoning`, `product-requirement`, `product-risk`, `product-strategy`, `project-policy`, `technical-architecture`, `technical-constraint`, `technical-convention`, `technical-decision`, `technical-open-question`, `technical-preference`, `technical-reasoning`, `technical-risk`, `technical-rule`, `user-preference`, `validation-result`. Do not invent new type values.

## Mechanical enforcement for coding rules

When calling `remember` for a `technical-rule`, `technical-convention`, or `technical-preference`, consider whether the repo can enforce it with tooling (Roslyn analyzer, EditorConfig, linter, or CI in this .NET solution). If yes, tell the user and recommend implementing that tooling first, then store a short memory with rationale and the diagnostic or rule identifier. Do not rely on memory alone for automatable rules. Keep nuanced exceptions in memory even when tooling exists.

Use `get` to inspect full memory details when a retrieved memory needs source context.

When `get_task_context` returns `canProceed=false`, or any `attentionItems` entry has `action=ask_user`, stop before planning or implementing. Surface the attention item and ask the user the provided question instead of choosing a side silently.

Use `report_conflict` when memories appear inconsistent. Surface the conflict to the user instead of deciding truth automatically.

Use `resolve_conflict` when the user clarifies which conflicting memory should remain authoritative. This marks superseded memories so normal retrieval stops returning them while preserving audit history through direct `get`.

Use `promote_memory` when the user confirms an experimental memory should become accepted. Include superseded memory ids and clarification only when the user confirms the promoted memory replaces older accepted guidance.

Do not store ordinary conversation, temporary reasoning, or details only relevant to the current response.

Inspect `instructionStatus` from `start_task`. If MBank reports that this instruction block is outdated, ask the user before updating this file or running the bootstrap script. Do not modify agent instruction files without explicit user approval.
<!-- mbank:end -->