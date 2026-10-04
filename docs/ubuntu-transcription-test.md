# Ubuntu microphone and transcription test

Use branch `codex/ubuntu-transcription-test`. It is based on the reviewed backend work in `codex/backend-parity` and adds an in-process dummy assistant. No Claude, Codex, Cursor, Hermes, login, or API key is needed. Speech still goes through the real microphone capture, local Whisper model, and normal Sancho display. The dummy assistant repeats the recognized text; it does not generate answers or execute commands.

## Prepare and run

Install the .NET 10 SDK using [Microsoft's instructions for your Ubuntu version](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install). Package availability varies with Ubuntu version. Install Git and ffmpeg if needed:

```bash
sudo apt update
sudo apt install git ffmpeg
git clone --branch codex/ubuntu-transcription-test https://github.com/PavlovicDzFilip/Sancho.git
cd Sancho
dotnet --list-sdks
ffmpeg -version
dotnet run --project Sancho.Console -- --agent dummy --model tiny --log
```

Use an ordinary interactive desktop terminal, with your intended microphone selected as the default input in Ubuntu Sound settings. The first run restores dependencies and downloads the tiny English Whisper model and VAD model. Wait for that to finish before speaking. Tiny is a fast initial check; repeat with `--model small` if recognition accuracy is poor. Both models are English-only. Audio decoding and transcription run locally after the downloads.

If you already cloned the repository:

```bash
git fetch origin
git switch --track origin/codex/ubuntu-transcription-test
dotnet run --project Sancho.Console -- --agent dummy --model tiny --log
```

If the local branch already exists, use `git switch codex/ubuntu-transcription-test` instead of creating it again. Use the source checkout for this test; the normal release installer downloads the released version rather than this branch.

## What to check

1. The microphone meter moves while speaking and settles during silence.
2. Say “the quick brown fox jumps over the lazy dog,” then pause for about one second. The recognized sentence should appear as your input and be repeated by the dummy assistant.
3. Say two or three different sentences, pausing between them. Each should appear once as an input and once as the dummy reply, in order. There should be no invented replies during silence.
4. Press Ctrl+C. Sancho should exit cleanly and stop microphone capture.

The important result is real speech appearing correctly. A dummy reply is only a check that the recognized text reaches the normal assistant display; it does not validate a real AI backend on Ubuntu. `--continue` is unavailable for the dummy assistant because it has no persistent sessions. The explicit `--agent dummy` overrides any saved backend selection without changing it.

As a second check without the assistant display, run transcription-only notes mode:

```bash
dotnet run --project Sancho.Console -- --notes --model tiny --log
```

Recognized utterances are appended to `sancho-notes-YYYY-MM-DD.md` in the current directory.

## If it fails

Send the Ubuntu version (`cat /etc/os-release`), architecture (`uname -m`), the command used, the visible error, and the relevant portion of `~/.sancho/sancho.log`. The log contains recognized speech; review it before sharing. `SANCHO_CONFIG_DIR` can change the log directory.

Linux capture first uses the default PulseAudio/PipeWire microphone, with an ALSA fallback. Check the default input and mute state in Sound settings. If Sancho reports a clipped built-in microphone on an AMD Ryzen AI 300 laptop, try a USB or 3.5 mm headset microphone.

The deterministic regression suite can also be run with:

```bash
dotnet test Sancho.slnx -m:1
```

Some recorded-audio tests require Windows Media Foundation and are skipped on Linux; the manual microphone check above is the Ubuntu acceptance test. Ubuntu 26.04 x64 verification on 2026-10-04: 150 automated tests passed and one Windows-only audio test was skipped. A separate ffmpeg-based check passed the stored recording through the real small-model Whisper/VAD pipeline and recognized the expected phrase. Startup, default microphone opening, model loading, and cancellation also passed. A live spoken-sentence check is still needed in the desktop session. The lifecycle test now allows up to five seconds for descendants to finish terminating and being reaped; surviving processes still fail.
