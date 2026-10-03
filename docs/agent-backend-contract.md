# Agent backend contract

Implementation and review checklist (each item receives a separate reviewed commit):

- [x] Shared backend contract and cross-platform process testing seam.
- [x] Process lifecycle: shutdown, cancellation, unexpected exits, and output draining.
- [x] Claude correctness and regression coverage.
- [x] Codex compatibility and two-turn coverage.
- [ ] Cursor compatibility and protocol coverage.
- [ ] Hermes compatibility and protocol coverage.
- [ ] Shared voice flow verification and setup/capability documentation.

Every backend uses the same `AgentEvent` stream. Emit `Ready` after initialization and when the previous turn has finished. `Send` accepts exactly one outstanding sentence and reserves that turn synchronously; a second `Send` before the next `Ready` throws `InvalidOperationException`. Input must not silently disappear.

For accepted input, emit `TurnStart`, then assistant text and tool activity as supported by the CLI. Emit `TurnComplete` exactly once when the turn ends, including a failed turn after reporting `Error`. Return to `Ready` only if the backend remains usable. Fatal startup or process errors emit `Error` and close the stream. Cancellation and disposal terminate owned processes, drain or stop background readers, and close the stream; cancellation need not produce normal turn-completion events.

`AgentLaunchOptions` supplies the target working directory, optional instructions override, and executable/prefix arguments. Adapters should resolve `.sancho.md` from the target directory and map instructions into their native protocol without replacing native tool guidance. All backends use the authorized YOLO permission mode. Resume IDs remain native to each backend.

The test-only `Sancho.ProcessTestHost` executable provides `echo`, `wait`, `exit <code>`, and `flood` modes. `script <fixture-path>` executes JSON lines containing `kind` and string `value`: `stdout`/`stderr` write protocol lines, `read` waits for a stdin line (and appends it to the file in `value` when nonempty), `capture` records backend arguments and cwd to a file, `delay` pauses for milliseconds, and `exit` sets the exit code. Invoke it through `dotnet` using launch prefix arguments to exercise real stdin/stdout/stderr and process lifecycle without authentication, network, or model calls. It is built and copied by `Sancho.Tests` automatically.




