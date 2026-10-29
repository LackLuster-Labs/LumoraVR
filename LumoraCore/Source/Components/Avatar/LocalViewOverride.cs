// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Replaces part of a slot's rendered transform while a specific rendering context is the one drawing.
//
// It is applied on the LOCAL client only, so it never touches what anyone else sees: every peer holds
// its own copy of the slot and the context test fails there. Position, rotation and scale each have
// their own has-override flag; anything left off falls through to the slot's real transform.
//
// A scale override of zero is special-cased into shadows-only rendering rather than an actual zero
// scale, because the reason you hide your own head is to stop seeing it, not to lose its shadow. On a
// slot that is a BONE of a skinned mesh the geometry lives in the body's renderer, not under the bone,
// so the hook collapses that bone for the wearer's eyes instead; CollectSkinnedRenderers below is how
// it finds which renderers that touches. -xlinka
[ComponentCategory("Users/Avatar")]
public class LocalViewOverride : ImplementableComponent<IHook>
{
    private const float HideScaleEpsilon = 0.0001f;

    // which rendering context activates this override
    public readonly Sync<ViewContext> Context = new();

    public readonly Sync<bool>   HasPositionOverride = new();
    public readonly Sync<float3> PositionOverride    = new();

    public readonly Sync<bool>   HasRotationOverride = new();
    public readonly Sync<floatQ> RotationOverride    = new();

    // float3.Zero here means "hide from this context" - the mesh stops drawing but keeps casting
    public readonly Sync<bool>   HasScaleOverride = new();
    public readonly Sync<float3> ScaleOverride    = new();

    private readonly UserRootRegistrationTracker _userRootReg;

    public LocalViewOverride()
    {
        _userRootReg = new UserRootRegistrationTracker(this);
    }

    // True when this override hides the slot from the wearer's own eyes.
    public bool HidesFromOwnView
        => Context.Value == ViewContext.UserView && HasScaleOverride.Value && IsEffectivelyZero(ScaleOverride.Value);

    public static bool IsEffectivelyZero(in float3 v)
    {
        return System.Math.Abs(v.x) < HideScaleEpsilon
            && System.Math.Abs(v.y) < HideScaleEpsilon
            && System.Math.Abs(v.z) < HideScaleEpsilon;
    }

    public void HideFromOwnView()
    {
        Context.Value = ViewContext.UserView;
        HasScaleOverride.Value = true;
        ScaleOverride.Value = float3.Zero;
    }

    // The skinned renderers whose bone set reaches this slot or anything under it: collapsing this
    // bone changes what they draw, nothing else is affected. Searched from the avatar root when there
    // is one, else the user root, else the top of whatever object this slot sits in.
    public void CollectSkinnedRenderers(List<SkinnedMeshRenderer> output)
    {
        var slot = Slot;
        if (slot == null || slot.IsDestroyed)
            return;
        var root = slot.GetComponentInParents<AvatarForm>()?.Slot
                   ?? slot.ActiveUserRoot?.Slot
                   ?? TopmostUnderWorldRoot(slot);
        if (root == null || root.IsDestroyed)
            return;
        FindSkinnedRenderersBoundUnder(root, slot, output);
    }

    public static void FindSkinnedRenderersBoundUnder(Slot searchRoot, Slot bone, List<SkinnedMeshRenderer> output)
    {
        if (searchRoot == null || bone == null)
            return;
        foreach (var renderer in searchRoot.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (renderer.IsDestroyed || output.Contains(renderer))
                continue;
            if (renderer.BindsBoneUnder(bone))
                output.Add(renderer);
        }
    }

    private static Slot? TopmostUnderWorldRoot(Slot slot)
    {
        var worldRoot = slot.World?.RootSlot;
        var walk = slot;
        while (walk.Parent != null && walk.Parent != worldRoot)
            walk = walk.Parent;
        return walk;
    }

    public override void OnAwake()
    {
        base.OnAwake();
        _userRootReg.Attach();
        // Equip reparents the avatar under the wearer; the hook gates on "is this the local user's",
        // and nothing else re-runs it when that answer changes.
        if (Slot != null)
            Slot.ActiveUserRootChanged += OnActiveUserRootChanged;
    }

    private void OnActiveUserRootChanged(Slot _) => RunApplyChanges();

    public override void OnInit()
    {
        base.OnInit();
        Context.Value          = ViewContext.UserView;
        // HasPositionOverride = false (C# default, skip)
        // PositionOverride = float3.Zero (C# default, skip)
        // HasRotationOverride = false (C# default, skip)
        RotationOverride.Value = floatQ.Identity;
        // HasScaleOverride = false (C# default, skip)
        ScaleOverride.Value    = float3.One;
    }

    // The hook also gates on the external-camera state (third-person/free-cam
    // must show the full avatar); poke it when that flips since no sync field
    // changes.
    private bool _lastExternalCamera;

    // An external view whose lens sits right at your own head (a wall pulled the orbit in, a focused panel
    // zoomed back through you, a free camera parked in your face) keeps the first-person head hide, or the
    // inside of your own head fills the view. In the user's own metres, with a gap so it cannot flicker. -xlinka
    private const float LensAtHeadIn = 0.3f;
    private const float LensAtHeadOut = 0.4f;
    private static bool _lensAtHead;

    public static bool ExternalViewShowsHead
    {
        get
        {
            if (!UserInputState.FocusedExternalCameraActive)
                return false;
            var input = Engine.Current?.InputInterface;
            var root = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root;
            var head = root?.HeadSlot;
            if (input == null || !input.DesktopCameraPoseValid || root == null || head == null || head.IsDestroyed)
                return true;
            float scale = System.MathF.Max(root.GlobalScale, 0.01f);
            float distance = float3.Distance(input.DesktopCameraPosition, head.GlobalPosition) / scale;
            _lensAtHead = distance < (_lensAtHead ? LensAtHeadOut : LensAtHeadIn);
            return !_lensAtHead;
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        bool external = ExternalViewShowsHead;
        if (external != _lastExternalCamera)
        {
            _lastExternalCamera = external;
            RunApplyChanges();
        }
    }

    public override void OnDestroy()
    {
        if (Slot != null)
            Slot.ActiveUserRootChanged -= OnActiveUserRootChanged;
        _userRootReg.Detach();
        base.OnDestroy();
    }
}
