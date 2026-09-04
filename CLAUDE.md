# Sancho Project Instructions

## Logging Rules
- **Never use `Console.Write` or `Console.WriteLine` for output.** Always use `ILogger<T>` via dependency injection.
- The project uses a custom `RawConsoleFormatter` (`Logging\RawConsoleFormatter.cs`, formatter name `"raw"`) that outputs just the message with no prefix — ANSI escape codes in messages are preserved for colour.
- Log levels:
  - `Information` — normal output (transcription text, Claude responses, tool status, startup/shutdown)
  - `Warning` — timeouts, unexpected process exits, claude stderr
  - `Error` — connection failures, process crashes
  - `Debug` — diagnostics hidden at default verbosity
- The only exceptions to the no-Console rule are `Console.ReadKey` for user input, the interactive microphone selector in `MicrophoneAudioSourceFactory.cs`, and `AnsiConsole` output from the CLI surface (`--help`, `--version`, `config get/set`, usage errors) which runs before the DI container exists.

## Cross-Platform Requirement
- Everything added from now on must be cross-platform (Windows, macOS, Linux): code, scripts, tooling, installers.
- **NAudio is the one remaining Windows-only exception** — it is the capture backend on Windows only (bundled package, no external install). macOS/Linux capture is `FfmpegAudioSource` (ffmpeg subprocess); ffmpeg is a runtime dependency there and `install.sh` installs it when missing. Windows needs no ffmpeg.
- Shell tooling must come in Windows + Unix pairs (`scripts\install.ps1` + `scripts\install.sh`) or run on a cross-platform runtime; never Windows-only tooling.
- Publishing targets all RIDs: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`. Release asset naming: `sancho.exe` (Windows), `sancho-<os>-<arch>` (others).

## Project Structure
- .NET 10 console app (`Sancho.Console.csproj`, exe name `sancho`)
- Entry point: `Program.cs` — CLI parsing (`Cli\CliArgs.cs`) and the early-exit commands (`--help`, `--version`, `config`) run before any services are built; the run path resolves config, then builds a plain `ServiceCollection` (no Generic Host, no ConfigurationBinder).
- Config: `Config\SanchoPaths.cs` + `Config\ConfigStore.cs` — user config in `~/.sancho/config.json`, read via source-generated System.Text.Json (`SanchoConfigJsonContext`).
- Audio capture: `IAudioSource` → `MicrophoneAudioSource` (NAudio, Windows) / `FfmpegAudioSource` (ffmpeg subprocess: avfoundation on macOS, pulse with ALSA fallback on Linux) / `LoopbackAudioSource` (WASAPI loopback, Windows — `--meeting` mode)
- Transcription: `LocalTranscriptionService` (the only backend — on-device sherpa-onnx offline whisper small.en int8 + silero VAD; utterances arrive as `Completed` events at utterance end, no live deltas; model files downloaded to `~/.sancho/models/` on first use; `LocalSttModels` handles provisioning). The OpenAI and record backends were removed — sancho is fully offline (ADR-0004). Engine choice: `docs/feature/local-stt/ADR-0003-offline-whisper-vad.md` (supersedes ADR-0002; model size may be revisited).
- Agent integration: `Services\Agents\` — `AgentService` abstraction over the selected CLI, all backends emit the shared `AgentEvent` stream; `ClaudeCodeAgentService` drives any Claude-Code-protocol CLI (subprocess via `<agent> --print --input-format stream-json --output-format stream-json`); the `agent` config key (or `--agent` flag) selects the backend (claude only for now; cursor/codex/hermes planned). The agent factory in Program.cs verifies the executable exists and is logged in before the orchestrator starts.
- Pipeline: mic → Channel<byte[]> → `LocalTranscriptionService` → Channel<string> → Claude CLI

## Configuration
- File: `~/.sancho/config.json` (`SANCHO_CONFIG_DIR` env var overrides the directory). Keys: `agent` (default `claude`; cursor/codex/hermes planned).
- Precedence: defaults < config file < flags. `--log` is a one-off override and is never persisted; only `sancho config set` persists.
- System prompt: `.sancho.md` in the current directory (the directory sancho is run from). If the file is missing, sancho creates it with a default prompt and prints a note that it can be edited.

## CLI Surface
- `sancho` — start listening in the current directory
- `sancho --notes` — dictation mode: transcriptions append to `sancho-notes-YYYY-MM-DD.md` in the current directory; Claude is never involved (no CLI check, no session, no `.sancho.md`)
- `sancho --meeting` — meeting mode: transcribes the mic and the system output (other participants) as two whisper streams sharing one recognizer; output lines are labeled `Me:`/`Others:`; Windows-only for now (WASAPI loopback), Linux/macOS later
- `sancho --agent <name>` — one-off agent override (default `claude`); persisted via `sancho config set agent <name>`
- `sancho --continue` / `-c` — session picker, resumes a prior Claude session
- `sancho config get [key]` / `sancho config set <key> <value>` — view/persist config
- `sancho --help`, `sancho --version`
- Arg parsing is hand-rolled in `Cli\CliArgs.cs` — keep it that way: small, explicit surface, no reflection-based parsers. Usage errors exit 2.

## Publishing
- `scripts\publish.ps1` (Windows) and `scripts\publish.sh` (macOS/Linux) publish framework-dependent single-file builds for all RIDs: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64` (`artifacts/` is gitignored).
- Output: `artifacts\publish\<rid>\` per RID, plus release-ready assets staged in `artifacts\release\` named per convention (`sancho.exe`, `sancho-<os>-<arch>`) — these are what `scripts\install.ps1` / `scripts\install.sh` download.
- Framework-dependent single-file (`PublishSingleFile=true`, `--self-contained false`): one executable per platform, requires the .NET runtime — both installers install the SDK when missing.
- Runtime dependencies handled by the installers: .NET 10 SDK on all platforms, plus ffmpeg on macOS/Linux (mic capture; apt/dnf/pacman/zypper/apk/brew).
- No AOT, no native toolchain required. `InvariantGlobalization=true` stays (safe: the app is English-only).

## Build Convention (Dogfooding)
`sancho.exe` is locked while running. To verify compilation without stopping:
```
dotnet build -o bin/staging
```
`bin/` is gitignored so staging builds won't be tracked. Restart Sancho from
staging when ready to test the new build.
