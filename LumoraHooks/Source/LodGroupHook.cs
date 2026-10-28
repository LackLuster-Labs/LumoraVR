// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components;

namespace Lumora.Godot.Hooks;

// Hands each level's renderers the distance band it should be visible in.
//
// There is no frame loop here and there is nothing to tick. The renderer already measures the
// distance to every instance for culling, so telling it a band means the LOD switch rides along on a
// test it was doing anyway; a hook that computed the current level itself would be doing the same
// work a second time, on the CPU, for every group in the world.
//
// The interesting part is the bookkeeping. A renderer that leaves a level - the level is removed, the
// slot is reparented, the group is destroyed - has to be handed an unbounded band back, or it keeps
// the band from a group that no longer applies to it and disappears at a distance for no visible
// reason. So the set of renderers touched last time is kept, and anything not in the new set is
// released. -xlinka
[ImplementableHook(typeof(LodGroup))]
public sealed class LodGroupHook : ComponentHook<LodGroup>
{
    private readonly HashSet<ILodRangeTarget> _applied = new();
    private readonly HashSet<ILodRangeTarget> _current = new();
    private readonly List<Component> _scratch = new();
    private bool _subscribed;

    public static IHook<LodGroup> Constructor() => new LodGroupHook();

    protected override bool ReadsNodePose => false;

    public override void Initialize()
    {
        base.Initialize();
        Owner.BandsInvalidated += OnBandsInvalidated;
        _subscribed = true;
        ApplyChanges();
    }

    public override void ApplyChanges()
    {
        _current.Clear();

        bool enabled = Owner.Enabled && Owner.Slot != null && Owner.Slot.IsActive;
        float fade = Owner.ScaledCrossfade;

        for (int i = 0; i < Owner.Levels.Count; i++)
        {
            var (begin, end) = Owner.BandFor(i);

            // The nearest level has no inner edge to fade across, and fading the outer edge of an
            // unbounded last level would fade it into nothing at no particular distance.
            var range = enabled
                ? new LodVisibilityRange(
                    begin,
                    begin > 0f ? fade : 0f,
                    end,
                    end > 0f ? fade : 0f,
                    fade > 0f)
                : LodVisibilityRange.Unbounded;

            foreach (var target in TargetsOf(Owner.Levels[i]))
            {
                target.SetLodVisibilityRange(in range);
                _current.Add(target);
            }
        }

        ReleaseDropped();
    }

    private IEnumerable<ILodRangeTarget> TargetsOf(LodLevel level)
    {
        foreach (var root in level.Renderers)
        {
            if (root == null || root.IsDestroyed)
                continue;

            // Any hook that can take a band participates, not just the plain mesh renderer: skinned
            // meshes carry one too, and asking the hook rather than the component type means a future
            // renderer gets LOD by implementing the interface instead of by being added to a list.
            _scratch.Clear();
            root.GetComponentsInChildren(_scratch);
            foreach (var component in _scratch)
            {
                if (component == null || component.IsDestroyed)
                    continue;
                if (component is IImplementable implementable && implementable.Hook is ILodRangeTarget target)
                    yield return target;
            }
        }
    }

    private void ReleaseDropped()
    {
        if (_applied.Count > 0)
        {
            foreach (var target in _applied)
            {
                if (!_current.Contains(target))
                    target.SetLodVisibilityRange(LodVisibilityRange.Unbounded);
            }
        }

        _applied.Clear();
        foreach (var target in _current)
            _applied.Add(target);
    }

    private void OnBandsInvalidated()
    {
        // Marking the component dirty rather than applying inline: this arrives from a structure or
        // transform event, and the hook contract is that ApplyChanges runs on the drain with a
        // consistent snapshot of the component's state.
        if (Owner is { IsDestroyed: false })
            Owner.MarkChangeDirty();
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (_subscribed && Owner != null)
        {
            Owner.BandsInvalidated -= OnBandsInvalidated;
            _subscribed = false;
        }

        // A world being torn down takes its renderers with it, so releasing bands there is work with
        // nobody left to see it.
        if (!destroyingWorld)
        {
            foreach (var target in _applied)
                target.SetLodVisibilityRange(LodVisibilityRange.Unbounded);
        }
        _applied.Clear();
        _current.Clear();

        base.Destroy(destroyingWorld);
    }
}
