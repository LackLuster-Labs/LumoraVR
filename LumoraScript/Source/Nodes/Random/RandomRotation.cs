// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// A rotation with every orientation as likely as any other. Four random parts scaled to length one would
// favour the turns toward the corners of the box they fill; here one draw shares the length between two
// pairs of parts by a square root and two more set the angle within each pair, which spreads evenly.
// -xlinka
[Node("Math/Random/Random Rotation", Description = "A rotation with every orientation as likely as any other.",
    Keywords = "random orientation, random quaternion, random floatQ")]
public sealed class RandomRotation : ReadNode
{
    [return: Port("rotation")]
    public static floatQ Run(ScriptContextBase ctx)
    {
        var random = ctx.Random;
        float share = random.NextSingle();
        float first = random.NextSingle() * (2f * MathF.PI);
        float second = random.NextSingle() * (2f * MathF.PI);
        float a = MathF.Sqrt(1f - share), b = MathF.Sqrt(share);
        return new floatQ(a * MathF.Sin(first), a * MathF.Cos(first), b * MathF.Sin(second), b * MathF.Cos(second));
    }
}
