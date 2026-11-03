// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Carries a rotation from one slot's space into another's: the same turn, as the other slot sees it. From
// and to work as they do on Convert Point.
[Node("Transform/Conversion/Convert Rotation", Capability.WorldRead, Description = "Carries a rotation from one slot's space into another's: the same turn, as the other slot sees it.",
    Keywords = "local to global, global to local, rotation to world, rotation to local")]
public sealed class ConvertRotation : ReadNode
{
    [return: Port("converted")]
    public static floatQ Run(ScriptContext ctx, Slot? from, Slot? to, [Default(0, 0, 0, 1)] floatQ rotation)
        => ScriptSpaces.Rotation(ScriptSpaces.Space(ctx, from), ScriptSpaces.Space(ctx, to), rotation);
}
