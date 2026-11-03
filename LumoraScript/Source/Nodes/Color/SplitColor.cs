// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

[Node("Color/Packing/Split Color", Description = "Separates a colour into its red, green, blue and alpha channels.",
    Keywords = "rgba, decompose color, channels")]
public sealed class SplitColor : ValueNode
{
    public static void Run(color value, out float r, out float g, out float b, out float a)
    {
        r = value.r;
        g = value.g;
        b = value.b;
        a = value.a;
    }
}
