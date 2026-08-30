# Sancho Project Instructions

## Logging Rules
- **Never use `Console.Write` or `Console.WriteLine` for output.** Always use `ILogger<T>` via dependency injection.
- The project uses a custom `RawConsoleFormatter` (formatter name: `"raw"`) that outputs just the message with no prefix — ANSI escape codes in messages are preserved for colour.
- Log levels:
  - `Information` — normal output (transcription text, Claude responses, tool status, startup/shutdown)
  - `Warning` — timeouts, unexpected process exits, claude stderr
  - `Error` — connection failures, process crashes
  - `Debug` — diagnostics hidden at default verbosity
- The only exception to the no-Console rule is `Console.ReadKey` for user input and the interactive microphone selector in `MicrophoneAudioSource.cs` (which uses cursor positioning).

## Cross-Platform Requirement
- Everything added from now on must be cross-platform (Windows, macOS, Linux): code, scripts, tooling, installers.
- No new Windows-only dependencies. Known exception to retire: **NAudio** (Windows-only mic capture) — when the audio layer is next touched, replace it with a cross-platform capture backend.
- Shell tooling must come in Windows + Unix pairs (`scripts\install.ps1` + `scripts\install.sh`) or run on a cross-platform runtime; never Windows-only tooling.
- Publishing targets all RIDs: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`. Release asset naming: `sancho.exe` (Windows), `sancho-<os>-<arch>` (others).

## Project Structure
- .NET 10 console app (`Sancho.Console.csproj`)
- Audio capture: `IAudioSource` → `MicrophoneAudioSource` (NAudio)
- Transcription: `RealtimeTranscriptionService` (OpenAI Realtime WebSocket)
- Claude integration: `ClaudeService` (subprocess via `claude --print --input-format stream-json --output-format stream-json`)
- Pipeline: mic → Channel<byte[]> → transcription → Channel<string> → Claude CLI

## Publishing
- `scripts\publish.ps1` (Windows) and `scripts\publish.sh` (macOS/Linux) publish framework-dependent single-file builds for all RIDs: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64` (`artifacts/` is gitignored).
- Output: `artifacts\publish\<rid>\` per RID, plus release-ready assets staged in `artifacts\release\` named per convention (`sancho.exe`, `sancho-<os>-<arch>`) — these are what `scripts\install.ps1` / `scripts\install.sh` download.
- Framework-dependent single-file (`PublishSingleFile=true`, `--self-contained false`): one executable per platform, requires the .NET runtime — both installers install the SDK when missing.
- No AOT, no native toolchain required. `InvariantGlobalization=true` stays (safe: the app is English-only).

## Build Convention (Dogfooding)
Sancho.exe is locked while running. To verify compilation without stopping:
```
dotnet build -o bin/staging
```
`bin/` is gitignored so staging builds won't be tracked. Restart Sancho from
staging when ready to test the new build.

