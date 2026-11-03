// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

[Node("Color/Lerp HDR Color", Description = "Blends between two HDR colours, keeping brightness values above 1.",
    Keywords = "mix, blend colors, gradient, interpolate")]
public sealed class LerpHdrColor : ValueNode
{
    [return: Port("color")]
    public static colorHDR Run([Default(0, 0, 0, 1)] colorHDR a, [Default(1)] colorHDR b, float t) => colorHDR.Lerp(a, b, t);
}
