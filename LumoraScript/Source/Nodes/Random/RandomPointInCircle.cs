// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// A random point of a circle about zero, every point as likely: anywhere inside, or on the edge alone
// when edge is true.
[Node("Math/Random/Points/Random Point In Circle", Description = "A random point of a circle about zero, every point as likely: anywhere inside, or on the edge alone when edge is true.",
    Keywords = "disk, ring, on edge")]
public sealed class RandomPointInCircle : ReadNode
{
    [return: Port("point")]
    public static float2 Run(ScriptContextBase ctx, float radius = 1f, bool edge = false)
    {
        var random = ctx.Random;
        float angle = random.NextSingle() * (2f * MathF.PI);
        float reach = edge ? radius : radius * MathF.Sqrt(random.NextSingle());
        return new float2(MathF.Cos(angle) * reach, MathF.Sin(angle) * reach);
    }
}
