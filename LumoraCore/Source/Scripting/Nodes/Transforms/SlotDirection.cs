// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Where one of the slot's own axes points in world space, as a unit direction: forward is its +z, up its
// +y, right its +x, carried through its rotation and every parent's as drawn. A slot the script cannot see
// reads as unturned.
[Node("Transform/Directions/Direction", Capability.WorldRead, Description = "Where one of the slot's own axes points in world space, as a unit direction: forward is its +z, up its +y, right its +x, carried through its rotation and every parent's as drawn.",
    Keywords = "forward, up, right, facing, heading, axis")]
public sealed class SlotDirection : ReadNode
{
    [return: Port("direction")]
    public static float3 Run(ScriptContext ctx, Slot? slot, SlotAxis axis)
        => ScriptSpaces.Direction(ScriptSpaces.Space(ctx, slot), null, ScriptSpaces.Axis(axis));
}
