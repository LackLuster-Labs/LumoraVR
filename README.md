# Lumora VR Project Summary
Lumora VR is a feature-rich Social VR Platform designed for mid-range PC users with VR capabilities built using Godot Engine and written in C#.

## Build requirements

Use Godot **4.8 beta1 .NET**, its matching **4.8.beta1.mono export templates**, and .NET 10.
The local Steam build script's `GODOT` path must point to that editor too. Updating the C# SDK
alone does not update the native runtime shipped in an export. Godot 4.8 dev6/dev7 have a
[Steam Deck controller regression](https://github.com/godotengine/godot/issues/123704) in Linux Gaming Mode.

## Editor Mode
- Real-time collaborative environment for building and sharing Avatars, Worlds, and Gadgets.
- Simplified interfaces for each asset category, focusing on essential features.
- Specialized editors optimized for accessibility and efficiency.
- Node-based and text-based scripting options for content creation.

## Social Mode
- Optimized for social interaction, minimizing network overhead.
- Enhanced security measures, restricting user actions where appropriate.
- First-party implementation of all features, eliminating mod dependency.

## Key Features
- Simplified asset creation process.
- Optimized networking for social interactions.
- Enhanced security measures.
- Node-based and text-based scripting options.

## Support
For questions or issues, please open an issue or make a PR.


## Licence

LumoraVR is source-available under the [LumoraVR Source Available License v3.0](./LICENSE.md).

**Non-commercial use is free.** Build, fork, host community servers, experiment, share — as long as no money changes hands.

**Commercial use requires a paid licence.** Contact **legal@lumoravr.com**.

**What you can do for free:**
- View, study, run, and modify the source code for non-commercial purposes.
- Host community or public servers, provided access is free and no revenue is generated.
- Share or distribute non-commercial builds and modifications, clearly labelled as unofficial.
- Fork for personal non-commercial experimentation or to submit pull requests.
- Use the source code for non-commercial AI/ML research.

**What requires a commercial licence:**
- Running a paid or monetised service.
- Distributing as part of a commercial product or selling access.
- Publishing on commercial storefronts (Steam, Meta Store, App Store, Google Play, etc.).
- Building a commercial social VR or shared-presence product with this code.
- Training AI/ML models for commercial purposes.

**Forks and modifications must:**
- Use a different name — you may not call your fork "LumoraVR" or anything confusingly similar.
- Include attribution — e.g. "Based on LumoraVR" in your readme or about screen.
- Not use the LumoraVR name, logo, or branding to promote or represent your project.

**Contributions:** By submitting a pull request you grant LUMORAVR LTD a perpetual, royalty-free licence to use your contribution for any purpose, including commercially. You retain ownership subject to this licence. See [CLA](.github/CLA.md).

See [LICENSE.md](./LICENSE.md) for full terms.
