// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Carries a point from one slot's space into another's: with only from set, from that slot's own space into
// world space; with only to set, from world space into that slot's. An empty slot, or one the script cannot
// see, stands for world space. A slot flattened on an axis counts that axis as unscaled, so the answer
// stays a finite number.
[Node("Transform/Conversion/Convert Point", Capability.WorldRead, Description = "Carries a point from one slot's space into another's: with only from set, from that slot's own space into world space; with only to set, from world space into that slot's.",
    Keywords = "local to global, global to local, world to local, local to world")]
public sealed class ConvertPoint : ReadNode
{
    [return: Port("converted")]
    public static float3 Run(ScriptContext ctx, Slot? from, Slot? to, float3 point)
        => ScriptSpaces.Point(ScriptSpaces.Space(ctx, from), ScriptSpaces.Space(ctx, to), point);
}
