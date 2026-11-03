// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// x and y are the corner the width and height are measured from.
[Node("Math/Rects/Rect From XYWH", Description = "X and y are the corner the width and height are measured from.",
    Keywords = "rectangle")]
public sealed class RectFromXYWH : ValueNode
{
    [return: Port("rect")]
    public static Rect Run(float x, float y, float width, float height) => new(x, y, width, height);
}
