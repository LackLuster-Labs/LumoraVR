// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// From min up to but not including max.
[Node("Math/Random/Random Float", Description = "From min up to but not including max.")]
public sealed class RandomFloat : ReadNode
{
    public static float Run(ScriptContextBase ctx, float min = 0f, float max = 1f)
        => min + (max - min) * ctx.Random.NextSingle();
}
