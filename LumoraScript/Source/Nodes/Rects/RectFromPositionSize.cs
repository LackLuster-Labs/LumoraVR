// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Position is the corner the size is measured from.
[Node("Math/Rects/Rect From Position Size", Description = "Creates a rectangle from a corner position and its width and height.",
    Keywords = "rectangle")]
public sealed class RectFromPositionSize : ValueNode
{
    [return: Port("rect")]
    public static Rect Run(float2 position, float2 size) => new(position, size);
}
