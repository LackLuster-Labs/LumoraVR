// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot's local position, rotation and scale as one matrix, placing it in its parent's space. A slot
// the script cannot see reads as the identity.
[Node("Transform/Matrices/Local Matrix", Capability.WorldRead, Description = "The slot's local position, rotation and scale as one matrix, placing it in its parent's space.")]
public sealed class SlotLocalMatrix : ReadNode
{
    [return: Port("matrix")]
    public static float4x4 Run(ScriptContext ctx, Slot? slot)
        => ScriptReads.Visible(ctx, slot) ? slot!.LocalTransform : float4x4.Identity;
}
