# Sancho

A voice assistant for the terminal: microphone → transcription → Claude CLI. Speech-to-text runs on-device in `local` mode (sherpa-onnx) — no cloud round-trip, your voice never leaves the machine. OpenAI Realtime remains the default backend, and `record` mode saves your voice to a WAV file without transcribing.

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

## Local mode

`transcription: local` transcribes on-device with [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (offline zipformer, English, int8, segmented by silero VAD) — no network, no API key, and the audio never leaves your machine:

```bash
sancho config set transcription local    # persist the local backend
sancho --transcription local             # one run only
```

Utterances arrive when you stop speaking (~1 s after), not word-by-word. The first run downloads the speech model (~70 MB) into `~/.sancho/models/`. See `docs/feature/local-stt/` for how the engine was chosen.

## Recording mode

`transcription: record` saves your microphone to a WAV file in `~/.sancho/recordings/` instead of transcribing (`SANCHO_CONFIG_DIR` overrides the directory):

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

Keys: `apiKey` (prompted on first run in `openai` mode) and `transcription` (`openai` | `record` | `local`, default `openai`).

Precedence: defaults < config file < `OPENAI_API_KEY` env var < flags (`--api-key`, `--transcription`).

## Known issues

- **Built-in microphone on AMD Ryzen AI 300 laptops (kernel ≥ 6.16):** the `snd_acp_pdm` driver feeds a clipped, full-scale signal instead of real audio — an upstream driver bug ([Framework Community thread](https://community.frame.work/t/laptop13-ryzen-ai-340-internal-mic-in-fedora42-doesnt-work/75748), [sof-project#5714](https://github.com/thesofproject/linux/issues/5714)). Sancho detects the clipped signal and warns; use a USB or 3.5mm headset mic until a kernel/driver fix lands.

## Building & Releasing

Requires the .NET 10 SDK.

- `scripts/publish.ps1` (Windows) / `scripts/publish.sh` (macOS/Linux) build all platforms: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`.
- Output: `artifacts/publish/<platform>/`, with release-ready assets staged in `artifacts/release/`.
- To release: create a GitHub release and attach everything from `artifacts/release/` (`sancho.exe`, `sancho-linux-x64`, `sancho-osx-arm64`, …). The installers download from `releases/latest/download`.
