# You found the treasure

If you're reading this, you went digging — `git log -S sk-proj --all`, a
secret scanner, or plain curiosity — and you found the dead OpenAI key in
Sancho's history.

Here is the story of the key, which is now a legend:

Once upon a time, Sancho had a cloud transcription backend. Its author,
in a moment of entirely human optimism, committed a **real OpenAI API
key** to the repository. The key lived in the history, quietly, as keys
do.

Then Sancho went fully offline. The OpenAI backend — and the key with it
— was ripped out ([ADR-0004](feature/local-stt/ADR-0004-remove-openai-backend.md)):
on-device whisper, no cloud, your voice never leaves the machine.

When the repository went public, the key was revoked. What you found is
a fossil. It has never been more useless.

But you, digger, have done what most people never do: you looked under
the floorboards. And for that:

🍪

A cookie. Well earned.

Also: this repository is [vibe-coded](../README.md), the author is very
human, and if you found this file because you ran a secret scanner —
thank you for checking. Sancho hears you. Go say hi to it. It won't
judge you, it only judges audio.
