// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Carries a box from one slot's space into another's: the box around its eight corners carried through,
// so a turn between the spaces makes it grow. An empty box stays empty. From and to work as they do on
// Convert Point.
[Node("Transform/Conversion/Convert Bounds", Capability.WorldRead, Description = "Carries a box from one slot's space into another's: the box around its eight corners carried through, so a turn between the spaces makes it grow.")]
public sealed class ConvertBounds : ReadNode
{
    [return: Port("converted")]
    public static BoundingBox Run(ScriptContext ctx, Slot? from, Slot? to, BoundingBox bounds)
        => ScriptSpaces.Bounds(ScriptSpaces.Space(ctx, from), ScriptSpaces.Space(ctx, to), bounds);
}
