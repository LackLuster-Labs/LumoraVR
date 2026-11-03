// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The matrix that carries the slot's local space into world space: every parent's local matrix times the
// next, down to this slot's own. A slot the script cannot see reads as the identity.
[Node("Transform/Matrices/World Matrix", Capability.WorldRead, Description = "The matrix that carries the slot's local space into world space: every parent's local matrix times the next, down to this slot's own.",
    Keywords = "transform matrix, local to world, model matrix")]
public sealed class SlotWorldMatrix : ReadNode
{
    [return: Port("matrix")]
    public static float4x4 Run(ScriptContext ctx, Slot? slot)
        => ScriptReads.Visible(ctx, slot) ? slot!.LocalToWorld : float4x4.Identity;
}
