// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

// Copies what you point at and stands the copy beside it.
//
// The copy comes off Slot.Duplicate, which is the three-phase clone: every reference that pointed
// inside the source tree is rebound to the clone, so drives and colour drivers follow the copy
// instead of both objects sharing one. That is what makes the copy independent rather than a second
// view of the original.
//
// Permissions: duplication is a CREATE, not a destroy, so the held-object destroy rule is not what
// governs it - but it is still gated. The world-editing floor stops it outright in Social/Event
// worlds; the datamodel gate refuses the write when the actor's role cannot create under the chosen
// parent, and that refusal is caught rather than thrown through the hand's input pass. Users and
// their avatars are off limits entirely: nobody gets to mint copies of somebody else's body. -xlinka
[ComponentCategory("Interaction/Tools")]
public sealed class DuplicatorTool : EquippableTool
{
    private const float MinOffset = 0.05f;
    private const float MaxOffset = 5f;
    private const float DefaultOffset = 0.25f;

    // Off puts the copy exactly on the original, which is what you want when the next thing you do is
    // drag it with a gizmo anyway.
    public readonly Sync<bool> OffsetCopy = new();

    protected override colorHDR VisualTint => new(0.55f, 1f, 0.75f, 1f);

    public override void OnInit()
    {
        base.OnInit();
        EquipName.Value = "Duplicator";
        OffsetCopy.Value = true;
    }

    // Two beads, because "this makes a second one" is the only thing the shape has to say.
    protected override void BuildVisualExtras(Slot visual)
    {
        EnsureBead(visual, "Bead A", new float3(-0.012f, 0.05f, 0f), 0.009f, VisualTint);
        EnsureBead(visual, "Bead B", new float3(0.012f, 0.05f, 0f), 0.009f, new colorHDR(1f, 0.95f, 0.4f, 1f));
    }

    public override bool OnPrimaryPress()
    {
        if (EditingBlocked || !TryGetAim(out var aim))
            return false;

        var source = ResolveObjectRoot(aim.HitSlot);
        if (source == null || IsOffLimits(source) || Security.ItemSeals.RefusesHere(source, Security.SealRule.SaveCopy))
            return false;

        var parent = source.Parent ?? World?.RootSlot;
        if (parent == null)
            return false;

        var copy = TryDuplicate(source, parent);
        if (copy == null)
            return false;

        if (OffsetCopy.Value)
        {
            // Step the copy off the original along the surface normal by the source's own size, so a
            // duplicated crate lands next to the crate rather than inside it.
            float offset = OffsetFor(source, aim.Normal);
            var placed = copy;
            Guarded(() => placed.GlobalPosition += aim.Normal * offset);
        }

        RecordUndo(SlotExistenceUndoBatch.Created(World, new[] { copy }, UndoLocale.Duplicate));
        return true;
    }

    private static float OffsetFor(Slot source, float3 normal)
    {
        if (!SlotBoundsHelper.TryComputeWorldBounds(source, out var bounds))
            return DefaultOffset;

        var size = bounds.Size;
        float span = MathF.Abs(size.x * normal.x) + MathF.Abs(size.y * normal.y) + MathF.Abs(size.z * normal.z);
        return System.Math.Clamp(span, MinOffset, MaxOffset);
    }

    public override void PopulateToolActions(ContextMenuPage page, ContextMenuContext context)
    {
        page.AddItem(new ContextMenuItem
        {
            Label = OffsetCopy.Value ? "Place: Beside" : "Place: In Place",
            FillColor = OffsetCopy.Value ? ActiveFill : ItemFill,
            OnPressed = _ => { if (!IsDestroyed) OffsetCopy.Value = !OffsetCopy.Value; },
        });
    }
}
