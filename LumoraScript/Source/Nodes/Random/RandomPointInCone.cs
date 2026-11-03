// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// A random point of a solid cone standing on its base with its tip up +y and its middle at zero, the way
// the cone mesh builds one, every point as likely: anywhere inside, or on the surface alone, base
// included, when surface is true. The base takes its share of the surface points by its share of the
// area, so neither the base nor the sloped side comes out crowded.
[Node("Math/Random/Points/Random Point In Cone", Description = "Picks a random point inside a cone, or on its surface when surface is enabled.",
    Keywords = "on surface")]
public sealed class RandomPointInCone : ReadNode
{
    [return: Port("point")]
    public static float3 Run(ScriptContextBase ctx, float height = 1f, float radius = 0.5f, bool surface = false)
    {
        var random = ctx.Random;
        float angle = random.NextSingle() * (2f * MathF.PI);
        float down;
        float reach;
        if (!surface)
        {
            down = MathF.Cbrt(random.NextSingle());
            reach = radius * down * MathF.Sqrt(random.NextSingle());
        }
        else
        {
            float r = MathF.Abs(radius);
            float slant = MathF.Sqrt(height * height + r * r);
            float baseShare = r + slant > 0f ? r / (r + slant) : 0f;
            if (random.NextSingle() < baseShare)
            {
                down = 1f;
                reach = radius * MathF.Sqrt(random.NextSingle());
            }
            else
            {
                down = MathF.Sqrt(random.NextSingle());
                reach = radius * down;
            }
        }
        return new float3(MathF.Cos(angle) * reach, height * (0.5f - down), MathF.Sin(angle) * reach);
    }
}
