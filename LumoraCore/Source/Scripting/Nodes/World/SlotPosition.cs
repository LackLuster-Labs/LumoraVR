// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// World space.
[Node("Transform/Global/Position", Capability.WorldRead, Description = "Reads a slot's world-space position.",
    Keywords = "world position, global position, location, where, get position")]
public sealed class SlotPosition : ReadNode
{
    [return: Port("position")]
    public static float3 Run(ScriptContext ctx, Slot? slot)
        => ScriptReads.Visible(ctx, slot) ? slot!.GlobalPosition : default;
}
