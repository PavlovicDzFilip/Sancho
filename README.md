# Sancho

A voice assistant for the terminal: microphone → transcription → Claude Code, Codex CLI, Cursor Agent, or Hermes Agent. Speech-to-text runs fully on-device (sherpa-onnx whisper + silero VAD) — no cloud round-trip, your voice never leaves the machine.

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

Requirements: an installed and authenticated CLI for your selected backend (`claude`, `codex`, `cursor-agent` or `agent`, or `hermes`), plus ffmpeg on macOS/Linux. Linux microphone selection and meeting mode also use `pactl` (Ubuntu package `pulseaudio-utils`); the installer installs it. Claude is the default. Sancho checks CLI availability before starting the assistant.

## Usage

```bash
sancho               # start listening in the current directory
sancho --continue    # resume a previous session
sancho --agent codex # swap the assistant; microphone and transcription stay the same
sancho --agent cursor
sancho --agent hermes
sancho --help
```

## Ubuntu transcription test

On branch `codex/ubuntu-transcription-test`, test the real microphone and local speech recognition without installing an AI CLI:

```bash
dotnet run --project Sancho.Console -- --agent dummy --model tiny --log
```

The dummy assistant repeats the recognized text through the normal display. It needs no login or API key and does not execute commands. Use an interactive desktop terminal with your default microphone selected in Ubuntu Sound settings. Setup, expected results, and troubleshooting are in [the Ubuntu test guide](docs/ubuntu-transcription-test.md).

## Transcription

Sancho transcribes on-device with [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (offline whisper .en int8 — `tiny`, `base`, `small` (default) or `medium`, segmented by silero VAD) — no network, no API key, and the audio never leaves your machine:

```bash
sancho
```

Utterances arrive when you stop speaking (~1 s after), not word-by-word. The first run downloads the selected model (tiny ~105 MB, base ~140 MB, small ~380 MB, medium ~945 MB) into `~/.sancho/models/`; each size keeps its own directory. See `docs/feature/local-stt/` for how the engine was chosen.

## Configuration

```bash
sancho config get [key]
sancho config set model small
sancho config set agent codex
```

The system prompt is read from `.sancho.md` in the directory you run Sancho from. If the file is missing, Sancho creates it with a default prompt and tells you — edit it to customize.

Speech spoken while the assistant is busy is queued and joined into the next turn when it is ready. Each backend reports replies, tools, and errors through the same display. Ctrl+C stops capture and owned assistant processes. If the backend stops unexpectedly, Sancho stops capture and keeps unsent speech visible.

All four backends run in **YOLO mode**, allowing commands and edits without approval prompts. Run Sancho only in a directory where you authorize those actions. Authentication, available models, tools, and MCP configuration remain specific to each native CLI.

`.sancho.md` is appended to Claude's native system prompt, passed as Codex developer instructions, and prepended to each Cursor/Hermes turn. Native instruction precedence and context limits can differ. Session IDs belong to their backend: changing `--agent` starts or resumes that backend's own conversation, and does not transfer history between providers. Cursor's session picker combines native CLI chat metadata and local IDE transcripts. Native CLI conversations resume by ID, but their binary history is not displayed by Sancho; IDE JSONL transcripts can be displayed. Hermes session summaries and exports depend on its CLI output.

See [backend verification](docs/backend-verification.md) for tests and current live-check limits.

Config keys: `agent` (claude | cursor | hermes | codex | dummy), `model` (tiny | base | small | medium).

## Known issues

- **Clipped microphone input:** excessive capture gain or microphone boost can saturate audio before Sancho receives it. The warning measures sustained full-scale samples and does not identify a CPU or driver. Lower input gain/boost or select another microphone.

## Building & Releasing

Requires the .NET 10 SDK.

- `scripts/publish.ps1` (Windows) / `scripts/publish.sh` (macOS/Linux) build all platforms: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`.
- Output: `artifacts/publish/<platform>/`, with release-ready assets staged in `artifacts/release/`.
- To release: create a GitHub release and attach everything from `artifacts/release/` (`sancho.exe`, `sancho-linux-x64`, `sancho-osx-arm64`, …). The installers download from `releases/latest/download`.
