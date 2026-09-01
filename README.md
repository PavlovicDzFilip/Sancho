# Sancho

A voice assistant for the terminal: microphone → transcription → Claude CLI. Local speech-to-text is the next milestone — until it lands, there is an interim `record` mode that saves your voice to a WAV file instead of sending it to OpenAI.

## Install

**Windows** (PowerShell):

```powershell
irm https://raw.githubusercontent.com/PavlovicDzFilip/Sancho/master/scripts/install.ps1 | iex
```

**macOS / Linux**:

```bash
curl -fsSL https://raw.githubusercontent.com/PavlovicDzFilip/Sancho/master/scripts/install.sh | bash
```

Both installers:

- install the .NET 10 SDK only if `dotnet` is not already present,
- install ffmpeg on macOS/Linux if missing (needed for microphone capture; Windows uses the bundled NAudio package and needs nothing extra),
- download the latest build for your OS/architecture from GitHub releases,
- install it for all users (`C:\Program Files\Sancho` on Windows, `/usr/local/bin` on macOS/Linux),
- prompt for administrator/sudo rights.

Requirements: the [Claude CLI](https://docs.anthropic.com/en/docs/claude-code/setup), plus ffmpeg on macOS/Linux.

## Usage

```bash
sancho               # start listening in the current directory
sancho --continue    # resume a previous session
sancho --help
```

## Recording mode

By default Sancho transcribes with OpenAI Realtime. During the transition to local speech-to-text, `transcription: record` makes it save your microphone to a WAV file in `~/.sancho/recordings/` instead (`SANCHO_CONFIG_DIR` overrides the directory):

```bash
sancho config set transcription record   # persist the interim mode
sancho --transcription record            # one run only
```

Stop with Ctrl+C to finalize the file. In record mode, Claude won't hear your voice.

## Configuration

```bash
sancho config get [key]   # apiKey
sancho config set apiKey sk-...
```

The system prompt is read from `.sancho.md` in the directory you run Sancho from. If the file is missing, Sancho creates it with a default prompt and tells you — edit it to customize.

Keys: `apiKey` (prompted on first run in `openai` mode) and `transcription` (`openai` | `record`, default `openai`).

Precedence: defaults < config file < `OPENAI_API_KEY` env var < flags (`--api-key`, `--transcription`).

## Building & Releasing

Requires the .NET 10 SDK.

- `scripts/publish.ps1` (Windows) / `scripts/publish.sh` (macOS/Linux) build all platforms: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`.
- Output: `artifacts/publish/<platform>/`, with release-ready assets staged in `artifacts/release/`.
- To release: create a GitHub release and attach everything from `artifacts/release/` (`sancho.exe`, `sancho-linux-x64`, `sancho-osx-arm64`, …). The installers download from `releases/latest/download`.
