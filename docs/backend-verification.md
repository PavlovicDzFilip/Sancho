# Backend verification

Build and run the deterministic suite with the .NET 10 SDK:

```powershell
dotnet test Sancho.slnx -m:1
```

Single-node builds avoid concurrent native-runtime packaging collisions on Windows. Tests start the real cross-platform `Sancho.ProcessTestHost` executable with representative CLI output. They verify arguments, working directories, instructions, session continuation, text/tool/error events, turn admission, process cancellation, disposal, and stream closure for all four adapters without paid model calls or credentials.

`AgentInputQueueTests` sends two speech turns through each real adapter and the process test host, and exercises the queue used by the voice orchestrator: speech arriving while a turn is busy stays visible, utterances are batched on the next `Ready`, a rejected send retains pending speech, and a stopped backend cannot receive another batch. Adapter protocol tests separately verify the `Ready` handshake and two-turn continuation across the actual process boundary.

`DecodeTests` uses the stored `Sancho.Tests/fixtures/Recording.m4a` audio and expects “the quick brown fox jumps over the lazy dog.” It uses Windows Media Foundation on Windows and FFmpeg on Linux/macOS, with the locally downloaded small English Whisper model. It checks speech recognition independently of backend behavior; it does not validate CLI conversation continuation. Missing fixtures or models skip that check; missing FFmpeg on Linux/macOS fails explicitly.

During this compatibility work, installed Claude Code and Codex passed isolated two-turn live conversation checks, including recall of the first turn. Both also passed native tool checks that wrote a file in a disposable repository and recalled its contents on the next turn. One initial Claude tool run did not complete; its repeat passed. These checks did not exercise microphone capture.

Hermes was temporarily installed in an isolated environment and passed a DeepSeek-backed two-turn check, native file writing, workspace session listing, and transcript export. The live run exposed plain diagnostic lines mixed into its JSON stream; the adapter now tolerates those lines while still rejecting malformed structured events and incomplete turns. The temporary installation, runtime, settings, sessions, caches, and owned Python launcher were removed after testing.

The installed Cursor Agent also passed native file writing and two-turn recall on Windows. Its command wrappers required a discovery/launch fix: Sancho checks `cursor-agent`, `agent`, and agent-capable `cursor`, reads the user PATH as well as the running process PATH, and launches the official bundled Node entry point directly to preserve literal prompts. The Cursor editor is not accepted as an agent CLI. A further live check discovered the native CLI session by workspace, recreated the adapter, resumed it, and recalled the earlier token. Native CLI binary history is not displayed by Sancho; the session picker uses metadata, and the CLI restores conversation context itself.

For a live voice check, authenticate the selected CLI first, enter a disposable project directory, and run `sancho --agent claude` (or `codex`, `cursor`, `hermes`). Ask for a short response, speak two follow-up sentences during the active turn, and check that they appear together as the next input. Then stop with Ctrl+C and confirm the CLI child process exits. `sancho --continue --agent <backend>` resumes only that backend's native session. All these runs use YOLO mode.

Use current CLIs supporting structured streaming: Claude `stream-json`, Codex `exec --json`, Cursor Agent `--output-format stream-json`, and Hermes `--format stream-json`. Unsupported older protocol versions may fail explicitly. Native models, account authentication, tools, instruction precedence, and session discovery differ by backend; Sancho normalizes the conversational event flow rather than those native features.


Latest Windows and Ubuntu 26.04 x64 verification on 2026-10-04: 166 automated tests passed on each platform with no skips, including recorded speech decoding. The Ubuntu microphone picker displayed the laptop's internal mic and Soundcore Space Q45; selecting the headset opened its named capture source and Ctrl+C stopped the test session cleanly. A separate Ubuntu integration check played the stored recording into a temporary null sink, captured its monitor using the production FFmpeg audio source, and transcribed the expected phrase through the real meeting pipeline tagged `Others`. The temporary sink was removed; normal audio routing was unchanged. Live user checks of microphone selection and simultaneous `Me`/`Others` remain acceptance steps.

Native Claude, Codex, and Cursor checks on Windows passed file writing and second-turn recall; Cursor also passed discovery and recall after recreating the adapter. Hermes passed its DeepSeek-backed native checks before removal. Actual authenticated backend calls on Ubuntu remain unverified; the automated protocol/process tests passed there.
