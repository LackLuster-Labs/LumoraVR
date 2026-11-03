// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Every channel clamped to 0 to 1, alpha included.
[Node("Color/Conversion/To Color", Description = "Every channel clamped to 0 to 1, alpha included.",
    Keywords = "clamp color, ldr, hdr to color, saturate")]
public sealed class HdrToColor : ValueNode
{
    [return: Port("color")]
    public static color Run(colorHDR value) => new(Unit(value.r), Unit(value.g), Unit(value.b), Unit(value.a));

    private static float Unit(float channel) => MathF.Max(0f, MathF.Min(1f, channel));
}
