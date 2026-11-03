// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Moved by offset, its size unchanged.
[Node("Math/Rects/Translate", Description = "Moved by offset, its size unchanged.",
    Keywords = "rectangle, move, offset")]
public sealed class TranslateRect : ValueNode
{
    [return: Port("moved")]
    public static Rect Run(Rect rect, float2 offset) => new(rect.x + offset.x, rect.y + offset.y, rect.width, rect.height);
}
