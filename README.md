# Sancho

**Talk to your coding agent.** Sancho listens to your microphone, transcribes your speech entirely on-device, and hands your words to a coding agent as if you'd typed them — no typing, no cloud round-trip for speech.

Speech-to-text runs fully locally (sherpa-onnx whisper + silero VAD), so your voice never leaves the machine and there's no API key or subscription for transcription. The agent it talks to (Claude Code by default) is the same CLI you'd type into — Sancho just replaces the keyboard.

## What can you use it for?

- **Hands-free agent sessions** — run `sancho` in a project and talk to your agent (Claude Code, Cursor, Hermes, or Codex) like a coworker: ask questions, request changes, talk through code while you keep your hands on the keyboard or away from it.
- **Dictation** — `sancho --notes` skips the agent entirely and appends everything you say to a dated markdown file in the current directory (`sancho-notes-YYYY-MM-DD.md`). Good for docs, emails, commit messages, standup notes.
- **Meeting transcripts** — `sancho --meeting` transcribes both your microphone and the system audio (the other participants on a call), labeling each line `Me:` / `Others:` (Windows only for now).
- **Private by design** — the transcription engine is fully offline and on-device. Audio is processed locally and discarded; nothing is uploaded for speech-to-text.

## How it works

```
mic → on-device whisper transcription → your agent CLI
```

- You speak; each utterance is transcribed when you pause (~1 s), not word-by-word.
- The transcript is sent to the agent CLI running in the current directory, exactly as if you'd typed it.
- The agent's system prompt comes from `.sancho.md` in that directory — created with a sensible default on first run, edit it to steer the agent for your project.
- Sessions persist per agent, so `sancho --continue` picks up where you left off.

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

- install the .NET 10 SDK only if a working `dotnet` 10 isn't already present,
- install ffmpeg on macOS/Linux if missing (needed for microphone capture; Windows uses the bundled NAudio package and needs nothing extra),
- download the latest build for your OS/architecture from GitHub releases,
- install it for all users (`C:\Program Files\Sancho` on Windows, `/usr/local/bin` on macOS/Linux),
- prompt for administrator/sudo rights.

Requirements: the CLI for your chosen agent — the [Claude CLI](https://docs.anthropic.com/en/docs/claude-code/setup) by default — plus ffmpeg on macOS/Linux.

## Usage

```bash
sancho               # talk to your agent in the current directory
sancho --continue    # resume a previous session
sancho --notes       # dictation: append to sancho-notes-YYYY-MM-DD.md, no agent
sancho --meeting     # transcribe mic + system audio, labeled Me:/Others: (Windows)
sancho --agent codex # use a different agent for this run
sancho --model base  # smaller/faster transcription model for this run
sancho --help
```

Agents: `claude` (default), `cursor`, `hermes`, `codex`. The first run of each model size downloads it, so give it a minute.

## Transcription

Sancho transcribes on-device with [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (offline whisper .en int8 — `tiny`, `base`, `small` (default) or `medium`, segmented by silero VAD):

```bash
sancho
```

Utterances arrive when you stop speaking (~1 s after), not word-by-word. The first run downloads the selected model (tiny ~105 MB, base ~140 MB, small ~380 MB, medium ~945 MB) into `~/.sancho/models/`; each size keeps its own directory. See `docs/feature/local-stt/` for how the engine was chosen.

## Configuration

```bash
sancho config get [key]
sancho config set model small
sancho config set agent claude
```

Config keys: `agent` (claude | cursor | hermes | codex), `model` (tiny | base | small | medium). Precedence: defaults < config file < flags.

The system prompt is read from `.sancho.md` in the directory you run Sancho from. If the file is missing, Sancho creates it with a default prompt and tells you — edit it to customize how the agent behaves on your project.

## Known issues

- **Built-in microphone on AMD Ryzen AI 300 laptops (kernel ≥ 6.16):** the `snd_acp_pdm` driver feeds a clipped, full-scale signal instead of real audio — an upstream driver bug ([Framework Community thread](https://community.frame.work/t/laptop13-ryzen-ai-340-internal-mic-in-fedora42-doesnt-work/75748), [sof-project#5714](https://github.com/thesofproject/linux/issues/5714)). Sancho detects the clipped signal and warns; use a USB or 3.5mm headset mic until a kernel/driver fix lands.

## Building & Releasing

Requires the .NET 10 SDK.

- `scripts/publish.ps1` (Windows) / `scripts/publish.sh` (macOS/Linux) build all platforms: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`.
- Output: `artifacts/publish/<platform>/`, with release-ready assets staged in `artifacts/release/`.
- To release: create a GitHub release and attach everything from `artifacts/release/` (`sancho.exe`, `sancho-linux-x64`, `sancho-osx-arm64`, …). The installers download from `releases/latest/download`.
