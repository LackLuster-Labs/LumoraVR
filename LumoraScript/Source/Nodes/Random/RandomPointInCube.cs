// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A random point of a cube of side size centred on zero, every point as likely: anywhere inside, or on
// its surface alone when surface is true, each face as likely as the others.
[Node("Math/Random/Points/Random Point In Cube", Description = "A random point of a cube of side size centred on zero, every point as likely: anywhere inside, or on its surface alone when surface is true, each face as likely as the others.",
    Keywords = "box, on surface")]
public sealed class RandomPointInCube : ReadNode
{
    [return: Port("point")]
    public static float3 Run(ScriptContextBase ctx, float size = 1f, bool surface = false)
    {
        var random = ctx.Random;
        if (!surface)
            return new float3((random.NextSingle() - 0.5f) * size, (random.NextSingle() - 0.5f) * size, (random.NextSingle() - 0.5f) * size);

        int face = random.Next(6);
        float a = (random.NextSingle() - 0.5f) * size;
        float b = (random.NextSingle() - 0.5f) * size;
        float side = (face & 1) == 0 ? size * 0.5f : size * -0.5f;
        return (face >> 1) switch
        {
            0 => new float3(side, a, b),
            1 => new float3(a, side, b),
            _ => new float3(a, b, side),
        };
    }
}
