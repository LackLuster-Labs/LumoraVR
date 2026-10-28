// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components;

namespace Lumora.Godot.Hooks;

// One band, applied to every renderer under this slot. Same mechanism as the LOD group hook, minus
// the levels: the renderer is told the distance to stop drawing at and does the test itself. -xlinka
[ImplementableHook(typeof(LodDistanceCull))]
public sealed class LodDistanceCullHook : ComponentHook<LodDistanceCull>
{
    private HashSet<ILodRangeTarget> _applied = new();
    // Swapped with _applied after every pass instead of allocating a fresh set per apply; a scale
    // change on a moving cull root lands here every frame.
    private HashSet<ILodRangeTarget> _current = new();
    private readonly List<Component> _scratch = new();
    private bool _subscribed;

    public static IHook<LodDistanceCull> Constructor() => new LodDistanceCullHook();

    protected override bool ReadsNodePose => false;

    public override void Initialize()
    {
        base.Initialize();
        Owner.BandInvalidated += OnBandInvalidated;
        _subscribed = true;
        ApplyChanges();
    }

    public override void ApplyChanges()
    {
        var slot = Owner.Slot;
        if (slot == null)
            return;

        bool enabled = Owner.Enabled && slot.IsActive && Owner.ScaledDistance > 0f;
        float fade = Owner.ScaledFade;
        bool sizeBased = enabled && Owner.SizeBased.Value;
        var range = enabled
            ? new LodVisibilityRange(0f, 0f, Owner.ScaledDistance, fade, fade > 0f)
            : LodVisibilityRange.Unbounded;

        var current = _current;
        current.Clear();
        _scratch.Clear();
        slot.GetComponentsInChildren(_scratch);
        foreach (var component in _scratch)
        {
            if (component == null || component.IsDestroyed)
                continue;
            if (component is not IImplementable implementable || implementable.Hook is not ILodRangeTarget target)
                continue;

            if (sizeBased)
            {
                // Per-renderer distance from its own bounds; unmeasurable or big enough = never band.
                if (!target.TryGetLargestDimension(out float dimension)
                    || dimension >= Owner.SizeExempt.Value)
                {
                    target.SetLodVisibilityRange(LodVisibilityRange.Unbounded);
                    current.Add(target);
                    continue;
                }

                float distance = System.Math.Max(Owner.ScaledDistance, Owner.SizeMultiplier.Value * dimension);
                var sized = new LodVisibilityRange(0f, 0f, distance, fade, fade > 0f);
                target.SetLodVisibilityRange(in sized);
                current.Add(target);
                continue;
            }

            target.SetLodVisibilityRange(in range);
            current.Add(target);
        }

        // Anything that was under this slot last time and is not now gets its band back, or it stays
        // culled by a component that no longer covers it.
        foreach (var target in _applied)
        {
            if (!current.Contains(target))
                target.SetLodVisibilityRange(LodVisibilityRange.Unbounded);
        }

        _applied.Clear();
        _current = _applied;
        _applied = current;
    }

    private void OnBandInvalidated()
    {
        if (Owner is { IsDestroyed: false })
            Owner.MarkChangeDirty();
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_subscribed && Owner != null)
        {
            Owner.BandInvalidated -= OnBandInvalidated;
            _subscribed = false;
        }

        if (!destroyingWorld)
        {
            foreach (var target in _applied)
                target.SetLodVisibilityRange(LodVisibilityRange.Unbounded);
        }
        _applied.Clear();

        base.Destroy(destroyingWorld);
    }
}
