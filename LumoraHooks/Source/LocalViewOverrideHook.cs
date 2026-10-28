// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Applies a slot's per-view transform override while the matching context is the one rendering.
//
// Only the local client runs it, so the override is local by construction: everyone else holds their
// own copy of the same slot and IsLocalUser() is false there. Position, rotation and scale go through
// SlotHook.SetLocalTransformOverride so the slot stays the single writer of its own transform.
//
// A scale override of zero is the one case that does NOT go through the transform: it means "hide this
// from myself". A zero scale would take the shadow with it, and ShadowsOnly (what this used to do) takes
// the object out of every local render including mirrors and in-world cameras - you end up headless in
// your own reflection. So it is done with render layers instead, two ways depending on where the
// geometry actually lives:
//
//   - Renderers UNDER the slot (a placeholder head, hair parented to the head bone) move onto
//     RenderHelper.LOCAL_HEAD_LAYER. The eye cameras are the only ones masking that bit off, lights
//     carry no cull mask, so the piece keeps its shadow, still shows in mirrors and on camera, and only
//     your own eyes skip it.
//   - A skinned body has no geometry under the head bone at all; its mesh hangs off the skeleton and
//     the head is just vertices weighted to that bone. For every SkinnedMeshRenderer whose bone set
//     reaches this slot (found on the engine side, LocalViewOverride.CollectSkinnedRenderers) the
//     SkinnedMeshHook keeps an EYE-VIEW TWIN: a second instance on RenderHelper.EYES_ONLY_LAYER, bound
//     to a mirrored skeleton with this bone collapsed to zero scale, while the real instance moves onto
//     LOCAL_HEAD_LAYER. Only the two eye cameras draw the twin, everything else draws the real one, so
//     the wearer is headless to themselves and whole to everyone and everything else, shadow included.
//
// Both are gated on the first-person view: third person and free cam show the whole avatar. This is
// what keeps the head out of the view now that the near plane is millimetres. -xlinka
[ImplementableHook(typeof(LocalViewOverride))]
public class LocalViewOverrideHook : ComponentHook<LocalViewOverride>
{
    // Frames we will keep coming back looking for something to hide before accepting that this slot
    // simply has none. Bounded so a LocalViewOverride on a slot with no geometry cannot re-dirty forever.
    private const int HideRetryLimit = 240;

    private int _hideRetries = HideRetryLimit;
    private bool _hiddenFromView;
    private bool _transformOverridden;
    // What each instance's layer mask was before we moved it, so a restore puts back what was actually
    // there rather than a guessed default (a renderer on the private/hidden band, or under a
    // RenderLayerOverride, would otherwise come back wearing the public bit it never had).
    private readonly System.Collections.Generic.Dictionary<Node3D, int> _priorLayers = new();

    // The skinned hooks currently collapsing this bone for the wearer. Registered on the activation
    // edge and re-collected during the retry window so a renderer whose hook lands late still joins.
    private readonly System.Collections.Generic.List<SkinnedMeshHook> _collapsedRenderers = new();
    private readonly System.Collections.Generic.List<SkinnedMeshRenderer> _rendererScratch = new();

    public static IHook<LocalViewOverride> Constructor() => new LocalViewOverrideHook();

    // The override reaches the node through SlotHook.SetLocalTransformOverride; nothing is hung here.
    protected override bool ReadsNodePose => false;

    public override void Initialize()
    {
        base.Initialize();
        LumoraLogger.Debug($"LocalViewOverrideHook: Initialized on '{Owner.Slot.SlotName.Value}'");
    }

    public override void ApplyChanges()
    {
        // UserView hiding only applies while the first-person view renders:
        // third-person/free-cam must show the full avatar, unless their lens is
        // sitting right at the head.
        bool shouldApply = Owner.Enabled
            && Owner.Context.Value == ViewContext.UserView
            && IsLocalUser()
            && !LocalViewOverride.ExternalViewShowsHead;

        if (!shouldApply)
        {
            ClearAll();
            return;
        }

        bool hasScale = Owner.HasScaleOverride.Value;
        float3 scale = Owner.ScaleOverride.Value;
        bool hide = hasScale && LocalViewOverride.IsEffectivelyZero(scale);

        SetHiddenFromView(hide);
        SetSkinnedCollapse(hide);
        ScheduleRetry(hide);

        float3? position = Owner.HasPositionOverride.Value ? Owner.PositionOverride.Value : null;
        floatQ? rotation = Owner.HasRotationOverride.Value ? Owner.RotationOverride.Value : null;
        // A zero scale is served by the layer swap above, so it must not also reach the transform - the two
        // together would collapse the geometry as well as hide it, and take the shadow with it.
        float3? scaleOverride = hasScale && !hide ? scale : null;

        PushTransform(position, rotation, scaleOverride);
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
            ClearAll();
        else
            _collapsedRenderers.Clear();

        base.Destroy(destroyingWorld);
    }

    // Helpers

    private void ClearAll()
    {
        SetHiddenFromView(false);
        SetSkinnedCollapse(false);
        _hideRetries = HideRetryLimit;
        PushTransform(null, null, null);
    }

    private void PushTransform(float3? position, floatQ? rotation, float3? scale)
    {
        bool wanted = position.HasValue || rotation.HasValue || scale.HasValue;
        if (!wanted && !_transformOverridden)
            return;

        if (Owner.Slot?.Hook is not SlotHook slotHook)
            return;

        slotHook.SetLocalTransformOverride(position, rotation, scale);
        _transformOverridden = wanted;
    }

    private void SetHiddenFromView(bool hidden)
    {
        // Re-applied every pass while active to catch MeshInstance3Ds created after us; the restore
        // only runs on the edge so a slot that never hid never pays for the walk.
        if (!hidden && !_hiddenFromView)
            return;

        var slotNode = (Owner.Slot?.Hook as SlotHook)?.GeneratedNode3D;
        if (slotNode == null || !GodotObject.IsInstanceValid(slotNode))
        {
            _hiddenFromView = hidden && _hiddenFromView;
            return;
        }

        if (hidden)
        {
            Helpers.RenderHelper.SetHierarchyLayer(slotNode, Helpers.RenderHelper.LOCAL_HEAD_LAYER, _priorLayers);
        }
        else
        {
            Helpers.RenderHelper.RestoreHierarchyLayer(slotNode, _priorLayers);
            _priorLayers.Clear();
        }

        _hiddenFromView = hidden;
    }

    // Register this bone with every skinned hook whose mesh it can move, or pull it back out of all of
    // them. Re-collected on every active pass: the engine-side list is the authority, and a hook found
    // this time that was missing last time simply joins.
    private void SetSkinnedCollapse(bool hidden)
    {
        if (!hidden)
        {
            if (_collapsedRenderers.Count == 0)
                return;
            foreach (var hook in _collapsedRenderers)
                hook.RemoveEyeViewCollapse(Owner);
            _collapsedRenderers.Clear();
            return;
        }

        _rendererScratch.Clear();
        Owner.CollectSkinnedRenderers(_rendererScratch);
        foreach (var renderer in _rendererScratch)
        {
            if (renderer.Hook is not SkinnedMeshHook hook || _collapsedRenderers.Contains(hook))
                continue;
            hook.AddEyeViewCollapse(Owner);
            _collapsedRenderers.Add(hook);
        }
        _rendererScratch.Clear();
    }

    // The renderer hooks build their instances on their own schedule, and on a fresh avatar they
    // usually land AFTER this. Nothing re-fires us when they do, so a one-shot pass would leave the
    // head on the public layer and in your eyes - which the old 25 cm near plane used to cover up.
    // Come back for a bounded window so late pieces (a face mesh whose hook is a frame behind the
    // body's) still get picked up, then stop. -xlinka
    private void ScheduleRetry(bool hidden)
    {
        if (!hidden)
        {
            _hideRetries = HideRetryLimit;
            return;
        }

        if (_hideRetries-- > 0)
            Owner?.MarkChangeDirty();
    }

    private bool IsLocalUser()
    {
        var slot = Owner.Slot;
        while (slot != null)
        {
            var userRoot = slot.GetComponent<UserRoot>();
            if (userRoot != null)
                return userRoot.ActiveUser?.IsLocal ?? false;
            slot = slot.Parent;
        }
        return false;
    }
}
