// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Channels from 0 up; past 1 is brighter than white. Alpha 0 to 1.
[Node("Color/Packing/Make HDR Color", Description = "Combines red, green, blue and alpha into an HDR colour, allowing brightness above 1.",
    Keywords = "rgb, rgba, compose color, construct color")]
public sealed class MakeHdrColor : ValueNode
{
    [return: Port("color")]
    public static colorHDR Run(float r, float g, float b, float a = 1f) => new(r, g, b, a);
}
