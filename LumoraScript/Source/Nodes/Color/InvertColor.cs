// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// One minus red, green and blue; alpha kept. An HDR colour is not clamped first, so a channel past one goes
// below zero.
[Node("Color/Invert", Description = "One minus red, green and blue; alpha kept.",
    Keywords = "negative, complement")]
public sealed class InvertColor : ValueNode
{
    [return: Port("color")]
    public static color Run(color value) => new(1f - value.r, 1f - value.g, 1f - value.b, value.a);

    [return: Port("color")]
    public static colorHDR Run(colorHDR value) => new(1f - value.r, 1f - value.g, 1f - value.b, value.a);
}
