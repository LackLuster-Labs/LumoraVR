// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// True with the given chance: 0 never, 1 always, 0.25 one time in four.
[Node("Math/Random/Random Bool", Description = "True with the given chance: 0 never, 1 always, 0.25 one time in four.",
    Keywords = "coin flip, chance, probability")]
public sealed class RandomBool : ReadNode
{
    public static bool Run(ScriptContextBase ctx, float chance = 0.5f) => ctx.Random.NextSingle() < chance;
}
