// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// A random point of a ball about zero, every point as likely: anywhere inside, or on the surface alone
// when surface is true, which makes it a random direction at radius 1. Three random lanes normalized
// would crowd toward the corners of the cube they fill; this draws the height and the turn about it
// evenly instead.
[Node("Math/Random/Points/Random Point In Sphere", Description = "A random point of a ball about zero, every point as likely: anywhere inside, or on the surface alone when surface is true, which makes it a random direction at radius 1.",
    Keywords = "random direction, random unit vector, on surface")]
public sealed class RandomPointInSphere : ReadNode
{
    [return: Port("point")]
    public static float3 Run(ScriptContextBase ctx, float radius = 1f, bool surface = false)
    {
        var random = ctx.Random;
        float height = random.NextSingle() * 2f - 1f;
        float angle = random.NextSingle() * (2f * MathF.PI);
        float ring = MathF.Sqrt(MathF.Max(0f, 1f - height * height));
        float reach = surface ? radius : radius * MathF.Cbrt(random.NextSingle());
        return new float3(MathF.Cos(angle) * ring * reach, height * reach, MathF.Sin(angle) * ring * reach);
    }
}
