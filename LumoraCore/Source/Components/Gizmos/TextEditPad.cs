// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// The pencil beside a text's gizmo. Pointing and pressing it starts typing into the text, pressing it again
// keeps what was typed. It answers any laser, dev tool or not: the person pressing is the one asked whether
// they may edit, on their own machine, and the card that opens is theirs alone.
//
// A press that lands on the pad while that text is being edited only ends the edit. That press already took
// the keys off the field on its way down, and acting again on the release would open a fresh edit straight
// after the old one closed. -xlinka
[ComponentCategory("Utility/Gizmos")]
public sealed class TextEditPad : Component, ILaserPointerTarget, ILaserPreferredTarget
{
    public readonly SyncRef<TextRenderer> Text = new();
    public readonly SyncRef<OverlayUnlitMaterial> Face = new();

    private readonly Dictionary<int, (bool Pressed, bool Armed, bool Ends)> _pointers = new();
    private bool _wasHot;
    private bool _painted;

    public int InteractionTargetPriority => 11000;

    private TextRenderer? Target => Text.Target is { IsDestroyed: false } text ? text : null;

    private bool CanUse => Enabled.Value && Slot.IsActive && Target != null
        && Slot.GetComponentInParents<TextGizmo>() is { IsVisible: true };

    public InteractionDescription GetInteractionDescription(InteractionLaser laser) => new()
    {
        Name = TextEditSession.IsEditing(Target)
            ? "Gizmo.Text.Pad.Finish".AsLocale("Finish editing").Resolve()
            : "Gizmo.Text.Pad.Edit".AsLocale("Edit text").Resolve(),
        Cursor = LaserCursor.Default,
    };

    public bool PreferLaserHit(InteractionLaser laser) => CanUse;

    public bool TryGetLaserPointerHit(InteractionLaser laser, in float3 origin, in float3 direction,
        float maxDistance, out LaserPointerHit hit)
    {
        hit = default;
        if (!CanUse)
            return false;
        var localOrigin = Slot.GlobalPointToLocal(origin);
        var localDirection = Slot.GlobalDirectionToLocal(direction);
        var half = new float3(0.6f, 0.6f, 0.25f);
        if (localDirection.LengthSquared < 1e-12f
            || !TransformHandle.RayBox(localOrigin, localDirection, -half, half, out float along))
            return false;
        var point = Slot.LocalPointToGlobal(localOrigin + localDirection * along);
        float distance = (point - origin).Length;
        if (distance > maxDistance)
            return false;
        hit = new LaserPointerHit(distance, point);
        return true;
    }

    public void UpdateLaserPointer(InteractionLaser laser, int pointerId, in float3 origin,
        in float3 direction, bool isPressed)
    {
        if (!TryGetLaserPointerHit(laser, origin, direction, float.MaxValue, out _))
        {
            ClearLaserPointer(laser, pointerId);
            return;
        }
        // Entering with the trigger already held does not press it.
        bool existed = _pointers.TryGetValue(pointerId, out var previous);
        bool pressedNow = existed && !previous.Pressed && isPressed;
        bool armed = previous.Armed || pressedNow;
        bool ends = pressedNow ? TextEditSession.IsEditing(Target) : previous.Ends;
        if (pressedNow && ends)
            TextEditSession.Current?.Done();
        if (previous.Pressed && !isPressed && armed)
        {
            if (!ends)
                Activate();
            armed = false;
            ends = false;
        }
        _pointers[pointerId] = (isPressed, armed, ends);
        RefreshLook();
    }

    public void ClearLaserPointer(InteractionLaser laser, int pointerId)
    {
        _pointers.Remove(pointerId);
        RefreshLook();
    }

    // Opens the edit, or finishes the one already open on this text.
    public void Activate()
    {
        var text = Target;
        if (text == null)
            return;
        if (TextEditSession.IsEditing(text))
        {
            TextEditSession.Current?.Done();
            return;
        }
        TextEditSession.Begin(text, World?.LocalUser);
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        RefreshLook();
    }

    private void RefreshLook()
    {
        bool hot = _pointers.Count != 0 || TextEditSession.IsEditing(Target);
        if (_painted && hot == _wasHot)
            return;
        var material = Face.Target;
        if (material == null || material.IsDestroyed)
            return;
        // The plate belongs to whoever put the gizmo up. Anyone else writing its colour would be refused by
        // the session, and every refusal counts against them.
        if (Slot.GetComponentInParents<TextGizmo>() is not { } gizmo || !ReferenceEquals(gizmo.Owner.Target, World?.LocalUser))
            return;
        _painted = true;
        _wasHot = hot;
        var tint = hot ? new colorHDR(0.95f, 0.5f, 0.95f, 0.85f) : new colorHDR(0.16f, 0.12f, 0.22f, 0.7f);
        material.FrontTintColor.Value = tint;
        material.BehindTintColor.Value = new colorHDR(tint.r, tint.g, tint.b, tint.a * 0.6f);
    }
}
