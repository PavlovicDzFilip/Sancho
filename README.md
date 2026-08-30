# Sancho

A live voice assistant for the terminal: microphone → OpenAI Realtime transcription → Claude CLI.

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
- download the latest build for your OS/architecture from GitHub releases,
- install it for all users (`C:\Program Files\Sancho` on Windows, `/usr/local/bin` on macOS/Linux),
- prompt for administrator/sudo rights.

Requirements: the [Claude CLI](https://docs.anthropic.com/en/docs/claude-code/setup) and an OpenAI API key (Sancho prompts for one on first run and stores it in `~/.sancho/config.json`).

## Usage

```bash
sancho               # start listening in the current directory
sancho --continue    # resume a previous session
sancho --help
```

## Configuration

```bash
sancho config get [key]   # apiKey
sancho config set apiKey sk-...
```

The system prompt is read from `.sancho.md` in the directory you run Sancho from.

Precedence: defaults < config file < `OPENAI_API_KEY` env var < `--api-key` flag.

## Building & Releasing

Requires the .NET 10 SDK.

- `scripts/publish.ps1` (Windows) / `scripts/publish.sh` (macOS/Linux) build all platforms: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`.
- Output: `artifacts/publish/<platform>/`, with release-ready assets staged in `artifacts/release/`.
- To release: create a GitHub release and attach everything from `artifacts/release/` (`sancho.exe`, `sancho-linux-x64`, `sancho-osx-arm64`, …). The installers download from `releases/latest/download`.
