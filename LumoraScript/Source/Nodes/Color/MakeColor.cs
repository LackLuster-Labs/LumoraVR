// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Channels 0 to 1.
[Node("Color/Packing/Make Color", Description = "Combines red, green, blue and alpha channels into a colour, using values from 0 to 1.",
    Keywords = "rgb, rgba, compose color, construct color")]
public sealed class MakeColor : ValueNode
{
    [return: Port("color")]
    public static color Run(float r, float g, float b, float a = 1f) => new(r, g, b, a);
}
