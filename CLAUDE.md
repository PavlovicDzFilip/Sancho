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
- Audio capture: `IAudioSource` → `MicrophoneAudioSource` (NAudio, Windows) / `FfmpegAudioSource` (ffmpeg subprocess: avfoundation on macOS, pulse with ALSA fallback on Linux)
- Transcription: two `ITranscriptionService` implementations selected by the `transcription` config key (`openai` is the default): `RealtimeTranscriptionService` (OpenAI Realtime WebSocket) and `RecordingTranscriptionService` (writes the PCM stream to a WAV in `~/.sancho/recordings/`, pure managed — the interim mode). Local STT will replace both on the same seam (see `ideas/local-stt.md`).
- Claude integration: `ClaudeService` (subprocess via `claude --print --input-format stream-json --output-format stream-json`)
- Pipeline: mic → Channel<byte[]> → transcription (`openai` → Channel<string> → Claude CLI; `record` → WAV file)

## Configuration
- File: `~/.sancho/config.json` (`SANCHO_CONFIG_DIR` env var overrides the directory). Keys: `apiKey`, `transcription` (`openai` | `record`; default `openai`).
- Precedence: defaults < config file < env var < flags. Flags (`--api-key`, `--transcription`) are one-off overrides and are never persisted; only `sancho config set` persists.
- In `openai` mode a missing key prompts for it interactively on first run and stores it. In `record` mode the key is only used for OpenAI-generated session titles.
- System prompt: `.sancho.md` in the current directory (the directory sancho is run from). If the file is missing, sancho creates it with a default prompt and prints a note that it can be edited.

## CLI Surface
- `sancho` — start listening in the current directory
- `sancho --continue` / `-c` — session picker, resumes a prior Claude session
- `sancho config get [key]` / `sancho config set <key> <value>` — view/persist config
- `sancho --help`, `sancho --version`, one-off `--api-key`
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
