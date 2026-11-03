// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

[Node("Math/Rects/Split Rect XYWH", Description = "Separates a rectangle into X, Y, width and height.",
    Keywords = "rectangle")]
public sealed class SplitRectXYWH : ValueNode
{
    public static void Run(Rect rect, out float x, out float y, out float width, out float height)
    {
        x = rect.x;
        y = rect.y;
        width = rect.width;
        height = rect.height;
    }
}
