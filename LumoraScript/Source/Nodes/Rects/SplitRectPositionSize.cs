// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

[Node("Math/Rects/Split Rect Position Size", Description = "Separates a rectangle into its position and size.",
    Keywords = "rectangle")]
public sealed class SplitRectPositionSize : ValueNode
{
    public static void Run(Rect rect, out float2 position, out float2 size)
    {
        position = rect.Position;
        size = rect.Size;
    }
}
