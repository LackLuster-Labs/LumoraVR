// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The box around what a slot and everything under it draws: each enabled mesh renderer's mesh bounds
// carried through its slot, a skinned mesh in its bind pose, inactive branches left out. Where nothing
// draws, the enabled colliders give the box instead. The box is in space's own space, or world space when
// space is empty or one the script cannot see. Include inactive also measures inactive branches and
// switched-off renderers and colliders; a tag measures only what sits on slots carrying exactly that tag.
// Found is false and the box empty when there is nothing to measure (a mesh still loading counts as
// nothing yet), or when the script cannot see the slot.
[Node("Transform/Bounds/Compute Bounds", Capability.WorldRead, Description = "The box around what a slot and everything under it draws: each enabled mesh renderer's mesh bounds carried through its slot, a skinned mesh in its bind pose, inactive branches left out.",
    Keywords = "bounding box, size, extents, dimensions, measure")]
public sealed class ComputeBounds : ReadNode
{
    public static void Run(ScriptContext ctx, Slot? slot, out BoundingBox bounds, out bool found, Slot? space = null,
        bool includeInactive = false, string? tag = null)
    {
        bounds = BoundingBox.Empty;
        found = false;
        if (!ScriptReads.Visible(ctx, slot))
            return;
        if (!ScriptSlotBounds.TryCompute(slot!, ScriptSpaces.Space(ctx, space), includeInactive, tag, out var measured))
            return;
        bounds = measured;
        found = true;
    }
}
