// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Min is the corner at x and y; max is x plus width and y plus height.
[Node("Math/Rects/Split Rect Min Max", Description = "Min is the corner at x and y; max is x plus width and y plus height.",
    Keywords = "rectangle")]
public sealed class SplitRectMinMax : ValueNode
{
    public static void Run(Rect rect, out float2 min, out float2 max)
    {
        min = rect.Min;
        max = rect.Max;
    }
}
