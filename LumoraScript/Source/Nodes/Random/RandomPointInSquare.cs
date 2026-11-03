// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// A random point of a square of side size centred on zero, every point as likely: anywhere inside, or
// on its edge alone when edge is true.
[Node("Math/Random/Points/Random Point In Square", Description = "A random point of a square of side size centred on zero, every point as likely: anywhere inside, or on its edge alone when edge is true.",
    Keywords = "on edge")]
public sealed class RandomPointInSquare : ReadNode
{
    [return: Port("point")]
    public static float2 Run(ScriptContextBase ctx, float size = 1f, bool edge = false)
    {
        var random = ctx.Random;
        if (!edge)
            return new float2((random.NextSingle() - 0.5f) * size, (random.NextSingle() - 0.5f) * size);

        // The four sides laid end to end, one draw along all of them.
        float around = random.NextSingle() * 4f;
        int side = Math.Min(3, (int)around);
        float along = (around - side - 0.5f) * size;
        float half = size * 0.5f;
        return side switch
        {
            0 => new float2(along, -half),
            1 => new float2(half, along),
            2 => new float2(-along, half),
            _ => new float2(-half, -along),
        };
    }
}
