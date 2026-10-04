# Sancho

**Talk to your coding agent.** Sancho listens to your microphone, transcribes your speech entirely on-device, and hands your words to a coding agent as if you'd typed them — no typing, no cloud round-trip for speech.

Speech-to-text runs fully locally (sherpa-onnx whisper + silero VAD), so your voice never leaves the machine and there's no API key or subscription for transcription. The agent it talks to (Claude Code, Cursor, Hermes, or Codex — whichever you have installed) is the same CLI you'd type into — Sancho just replaces the keyboard.

## What can you use it for?

- **Hands-free agent sessions** — run `sancho` in a project and talk to your agent (Claude Code, Cursor, Hermes, or Codex) like a coworker: ask questions, request changes, talk through code while you keep your hands on the keyboard or away from it.
- **Dictation** — `sancho --notes` skips the agent entirely and appends everything you say to a dated markdown file in the current directory (`sancho-notes-YYYY-MM-DD.md`). Good for docs, emails, commit messages, standup notes.
- **Meeting transcripts** — `sancho --meeting` transcribes both your microphone and the system audio (the other participants on a call), labeling each line `Me:` / `Others:` (Windows and Linux).
- **Private by design** — the transcription engine is fully offline and on-device. Audio is processed locally and discarded; nothing is uploaded for speech-to-text.

## How it works

```
mic → on-device whisper transcription → your agent CLI
```

- You speak; each utterance is transcribed when you pause (~1 s), not word-by-word.
- The transcript is sent to the agent CLI running in the current directory, exactly as if you'd typed it.
- The agent's system prompt comes from `.sancho.md` in that directory — created with a sensible default on first run, edit it to steer the agent for your project.
- Sessions persist per agent, so `sancho --continue` picks up where you left off.

## How it was built

Sancho is a vibe-coded project, built with [Claude Code](https://claude.com/claude-code) — features were talked through rather than specced, and not much time went into designing exactly how it all fits together. There's a reasonable separation of concerns (audio capture, transcription, agent integration each live in their own place), but it wasn't whiteboarded first. The code quality is okay — not perfect, but good enough for what the tool does — and it's honest about being that. If a bit of it looks like it grew organically: it did.

## Why "Sancho"?

Named after [Sancho Panza](https://en.wikipedia.org/wiki/Sancho_Panza), Don Quixote's squire. Quixote was the dreamer, forever charging at windmills; Sancho was the practical one — grounded, plain-spoken, the companion who turned his master's grand schemes into words that worked in the real world. That's the job here. Your coding agent is the knight errant, and Sancho is the faithful squire carrying your voice to it: plainly, reliably, both feet on the ground.

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

Requirements: an installed and authenticated CLI for your selected backend (`claude`, `codex`, `cursor-agent` or `agent`, or `hermes`), plus ffmpeg on macOS/Linux. Linux microphone selection and meeting mode also use `pactl` (Ubuntu package `pulseaudio-utils`); the installer installs it. Unset backend selection is auto-detected on first run. Sancho checks CLI availability before starting the assistant.

## Usage

```bash
sancho               # talk to your agent in the current directory
sancho --continue    # resume a previous session
sancho --notes       # dictation: append to sancho-notes-YYYY-MM-DD.md, no agent
sancho --meeting     # transcribe mic + system audio, labeled Me:/Others: (Windows/Linux)
sancho --agent codex # use a different agent for this run
sancho --model base  # smaller/faster transcription model for this run
sancho --help
```

Agents: `claude`, `cursor`, `hermes`, `codex` — on a fresh install Sancho auto-detects whichever are on your PATH (picking one automatically, or asking if there are several) and saves your choice to the config. The first run of each model size downloads it, so give it a minute.

> **Note:** Linux microphone selection and meeting capture require PulseAudio/PipeWire and `pactl`. macOS meeting capture is not supported yet.

## Ubuntu transcription test

Test the real microphone and local speech recognition without installing an AI CLI:

```bash
dotnet run --project Sancho.Console -- --agent dummy --model tiny --log
```

The dummy assistant repeats the recognized text through the normal display. It needs no login or API key and does not execute commands. Use an interactive desktop terminal and select your microphone in the startup picker. Setup, expected results, and troubleshooting are in [the Ubuntu test guide](docs/ubuntu-transcription-test.md).

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

Config keys: `agent` (claude | cursor | hermes | codex | dummy; auto-detected from your PATH when unset), `model` (tiny | base | small | medium). Precedence: defaults < config file < flags.

The system prompt is read from `.sancho.md` in the directory you run Sancho from. If the file is missing, Sancho creates it with a default prompt and tells you — edit it to customize how the agent behaves on your project.

Speech spoken while the assistant is busy is queued and joined into the next turn when it is ready. Each backend reports replies, tools, and errors through the same display. Ctrl+C stops capture and owned assistant processes. If the backend stops unexpectedly, Sancho stops capture and keeps unsent speech visible.

All four backends run in **YOLO mode**, allowing commands and edits without approval prompts. Run Sancho only in a directory where you authorize those actions. Authentication, available models, tools, and MCP configuration remain specific to each native CLI.

`.sancho.md` is appended to Claude's native system prompt, passed as Codex developer instructions, and prepended to each Cursor/Hermes turn. Native instruction precedence and context limits can differ. Session IDs belong to their backend: changing `--agent` starts or resumes that backend's own conversation, and does not transfer history between providers. Cursor's session picker combines native CLI chat metadata and local IDE transcripts. Native CLI conversations resume by ID, but their binary history is not displayed by Sancho; IDE JSONL transcripts can be displayed. Hermes session summaries and exports depend on its CLI output.

See [backend verification](docs/backend-verification.md) for tests and current live-check limits.

## Known issues

- **Clipped microphone input:** excessive capture gain or microphone boost can saturate audio before Sancho receives it. The warning measures sustained full-scale samples and does not identify a CPU or driver. Lower input gain/boost or select another microphone.

## Building & Releasing

Requires the .NET 10 SDK.

- `scripts/publish.ps1` (Windows/Linux) / `scripts/publish.sh` (macOS/Linux) build all platforms: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`.
- Output: `artifacts/publish/<platform>/`, with release-ready assets staged in `artifacts/release/`.
- To release: create a GitHub release and attach everything from `artifacts/release/` (`sancho.exe`, `sancho-linux-x64`, `sancho-osx-arm64`, …). The installers download from `releases/latest/download`.

---

*psst: `git log -S sk-proj --all` — every good project has a story under the floorboards.*
