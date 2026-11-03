// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The same channels as an HDR color, free to go past 1 from here on.
[Node("Color/Conversion/To HDR", Description = "The same channels as an HDR color, free to go past 1 from here on.",
    Keywords = "color to hdr, overbright")]
public sealed class ColorToHdr : ValueNode
{
    [return: Port("color")]
    public static colorHDR Run(color value) => colorHDR.FromLDR(value);
}
