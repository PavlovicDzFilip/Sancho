# Ubuntu microphone and transcription test

The Ubuntu audio support and in-process dummy assistant are available on `master`. No Claude, Codex, Cursor, Hermes, login, or API key is needed. Speech still goes through the real microphone capture, local Whisper model, and normal Sancho display. The dummy assistant repeats the recognized text; it does not generate answers or execute commands.

## Prepare and run

Install the .NET 10 SDK using [Microsoft's instructions for your Ubuntu version](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install). Package availability varies with Ubuntu version. Install Git and ffmpeg if needed:

```bash
sudo apt update
sudo apt install git ffmpeg pulseaudio-utils
git clone --branch master https://github.com/PavlovicDzFilip/Sancho.git
cd Sancho
dotnet --list-sdks
ffmpeg -version
dotnet run --project Sancho.Console -- --agent dummy --model tiny --log
```

Use an ordinary interactive desktop terminal. When multiple inputs are available, Sancho offers a microphone picker, including connected headset microphones. Select your intended microphone and check the displayed device name. Disconnected analog ports and system-output monitors are excluded. The first run restores dependencies and downloads the tiny English Whisper model and VAD model. Wait for that to finish before speaking. Tiny is a fast initial check; repeat with `--model small` if recognition accuracy is poor. Both models are English-only. Audio decoding and transcription run locally after the downloads.

If you already cloned the repository:

```bash
git fetch origin
git switch master
git pull --ff-only
dotnet run --project Sancho.Console -- --agent dummy --model tiny --log
```

Use the source checkout for the latest changes; the normal release installer downloads the released version, which may lag behind master.

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

## Test meeting mode after microphone selection

Run `sancho-transcription-test --meeting` on the configured laptop, or add `--meeting` to the source command above. Select your microphone, speak, and confirm your words are labeled `Me`. Play spoken audio through the desktop's default output (headphones recommended) and confirm those words are labeled `Others`. Stop with Ctrl+C and check capture stops. Linux meeting mode captures the default output's PulseAudio/PipeWire monitor; it does not substitute a microphone if no output monitor exists. Restart after changing the default output device.

## If it fails

Send the Ubuntu version (`cat /etc/os-release`), architecture (`uname -m`), the command used, the visible error, and the relevant portion of `~/.sancho/sancho.log`. The log contains recognized speech; review it before sharing. `SANCHO_CONFIG_DIR` can change the log directory.

Linux capture uses the selected PulseAudio/PipeWire source. If device listing is unavailable, Sancho announces default input capture with an ALSA fallback; install `pulseaudio-utils` to enable selection. Explicit selections never silently fall back to another microphone. Check the selected input and mute state in Sound settings. If Sancho reports repeated full-scale microphone samples, lower input volume or microphone boost, or try another microphone. The warning is based on the audio signal and does not identify a CPU or driver. A high average volume alone does not count as clipping.

The deterministic regression suite can also be run with:

```bash
dotnet test Sancho.slnx -m:1
```

The recorded-audio regression uses Windows Media Foundation on Windows and FFmpeg on Linux/macOS. It checks the stored recording through the real small-model Whisper/VAD pipeline when the model is installed. Live microphone and meeting checks above remain separate acceptance tests. The lifecycle test allows up to five seconds for descendants to finish terminating and being reaped; surviving processes still fail.

Verification on Ubuntu 26.04 x64 on 2026-10-04: all 166 tests passed without skips. The microphone picker listed internal and Soundcore headset inputs, and the selected headset capture opened and stopped cleanly. An isolated output-monitor integration check transcribed the stored phrase as `Others` through the production capture and meeting pipeline. Live user acceptance of both inputs and simultaneous meeting speech remains pending.
