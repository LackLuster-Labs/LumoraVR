// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Restarts this script's random numbers from a seed, so the same seed gives the same run.
[Node("Math/Random/Seed Random", Description = "Restarts this script's random numbers from a seed, so the same seed gives the same run.")]
public sealed class SeedRandom : ActionNode
{
    public static void Run(ScriptContextBase ctx, int seed) => ctx.SeedRandom(seed);
}
