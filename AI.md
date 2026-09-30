# AI Disclosure

LumoraVR is built with help from AI tools. We'd rather say that up front than have people guess, so this covers how AI is used to make LumoraVR, what actually ships in it, and what we ask of contributors who use AI.

## How LumoraVR is made

We use AI coding assistants, mainly Claude Code (and GitHub Copilot in the past), for:

- documentation and code comments, which is where we lean on it the most
- writing and running test harnesses, and tracking down bugs (more on that below)
- sorting out GitHub workflow scripts
- helping write and refactor code

How much AI is involved varies a lot from change to change. Sometimes it's only used for feedback on code a person wrote. Sometimes a person writes the code first and AI helps clean it up or refactor it, which doesn't make that code AI-generated. And some code is written with AI from the start.

AI gets code wrong a lot, so nothing it produces goes in on trust. It gets checked, built and tested like any other change, and whoever merges it owns it. The design and direction come from the maintainers and contributors, and a lot of the codebase is contributors' own work.

We don't mark individual commits or files as AI-assisted. This document is the project-wide disclosure instead.

### Test harnesses

A lot of the testing for the audio engine and the nodes in Loom, the in-world scripting system, is done with test harnesses that AI writes and runs. A harness is a small program that runs the real engine code without the game window. It starts a real world (and for anything networked, a real session with a host and clients joining it), plays through scenarios and checks the results. Nodes get run with lots of different inputs, audio effects get checked against what they should produce, a clip edited on one machine has to come out right on another, and so on.

We use AI for this because of how much there is to test. Loom has over 700 nodes, and the audio engine has effects, analysis and clip editing on top of voice chat. Testing all of that by hand, setting up each case in game, joining with a second client and checking the result, would take weeks to months. AI can keep writing and running scenarios nonstop and rerun the whole lot after every change, so the same testing gets done in about two days. It has already caught real bugs, like the microphone shutting off while something was still using it, and duplicated objects losing the values in their lists.

It doesn't replace trying things in game. A harness only checks what it was written to check, so audio still has to be listened to and nodes still have to be used in a real world. Fixes for anything a harness finds go through the same checks as any other change.

## Art and assets

We don't use AI image, audio, video or 3D generators to make LumoraVR's art or assets.

## What ships in LumoraVR

LumoraVR doesn't include generative AI. The client and server don't call LLMs or any other generative AI service, and the engine doesn't send your voice, movement, text or anything else to AI services, or collect it to train AI. There are no generative AI features in LumoraVR and none are planned.

The one machine learning model that ships is RNNoise, a small neural network that removes background noise from your own microphone. It runs on your device and sends nothing anywhere.

Scripts in worlds (Loom) can make web requests, but only to sites the person on the sending machine has approved.

## Using AI in your contributions

AI-assisted contributions are welcome. The usual PR rules still apply: it builds (`dotnet build LumoraGodot/Lumora.sln`), it's formatted (`dotnet format LumoraGodot/Lumora.sln`), you've tested it, and you've signed the [CLA](/.github/CLA.md). On top of that:

### You own it

- **You're the author.** Whatever the AI wrote, you're the one submitting it. Understand it well enough to explain any part of it in review. If a reviewer asks why something works the way it does, "the AI did it" isn't an answer.
- **Stay within what you could write yourself.** Use AI to go faster, not to go past what you understand. If you couldn't have written it by hand given enough time, you won't be able to maintain it either.
- **Bugs it brings in are yours.** If it breaks something, you'll be asked to fix it.

### Check it properly

- **Read the whole diff before opening the PR.** AI code often looks right and isn't: calls to methods that don't exist, made-up settings, half-finished edits, leftover chat text in comments.
- **Run it in the game, not just the build.** A clean build proves very little. Try the thing you changed, in the mode it's meant for (desktop, VR, or both).
- **Test with more than one person.** LumoraVR is multiplayer, so anything that touches the world has to work for everyone in a session, not just on your machine. Host a session and join it from a second client.
- **Watch for the usual traps:**
  - skipping or working around permission checks
  - touching Godot or world state from a background thread
  - using Godot types in the engine projects (LumoraCore and below stay Godot-free)
  - "fixing" a failing test by changing what it expects

### Keep it reviewable

- **Small, focused PRs.** AI makes it easy to change 50 files at once. Keep to one problem per PR and split big work up. Don't reformat or rename code you didn't need to touch.
- **Short comments.** Say why, not what, and match the code around it. No walls of generated doc comments.
- **Docs people can maintain.** A page of generated prose is more to review and keep up to date, not less.
- **Tests that test something.** AI is good at writing tests, and they're welcome, but a test has to check real behaviour and fail when that behaviour breaks.

### Keep it honest and clean

- **Real reports only.** AI can help write up a bug report or feature request, but the logs, repro steps and what you saw have to be real, never made up or "reconstructed".
- **Only submit code you have the right to.** AI tools can reproduce code from other projects, including licenses that can't be used here, like GPL. If something looks lifted, leave it out.
- **Keep private stuff out of AI tools.** Don't paste tokens, keys or other people's data into them.
- **No AI-generated assets.** Don't submit AI-generated art, images, audio, video or 3D models.

### Saying you used AI

If a big part of a PR was written with AI, a quick mention under "What does this PR do?" is appreciated. Using AI for feedback, or to tidy up code you wrote yourself, doesn't need a mention. It's never required and won't count against you. Maintainers may ask you to explain or simplify parts of a PR, and if it can't be explained, it won't be merged.
