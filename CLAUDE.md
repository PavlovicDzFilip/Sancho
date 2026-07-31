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

## Project Structure
- .NET 10 console app (`Sancho.Console.csproj`)
- Audio capture: `IAudioSource` → `MicrophoneAudioSource` (NAudio)
- Transcription: `RealtimeTranscriptionService` (OpenAI Realtime WebSocket)
- Claude integration: `ClaudeService` (subprocess via `claude --print --input-format stream-json --output-format stream-json`)
- Pipeline: mic → Channel<byte[]> → transcription → Channel<string> → Claude CLI

## Build Convention (Dogfooding)
Sancho.exe is locked while running. To verify compilation without stopping:
```
dotnet build -o bin/staging
```
`bin/` is gitignored so staging builds won't be tracked. Restart Sancho from
staging when ready to test the new build.

