// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Social;

namespace Lumora.Core.Components.Avatar;

// THE NAME PLATE AS THE WAY TO SOMEBODY'S PROFILE
//
// Point at a name plate and press, beam or mouse, and that person's profile comes up in your People window.
// The plate gets no collider for it. A collider over every head would stop hands, beams and anything else
// that casts for something to land on. The laser asks this instead: a flat test against the panel as it is
// drawn, which only a beam ever makes. It answers only while there is an account to open, so a guest's
// plate, your own plate (third person and mirrors show it to you), and a hand holding a tool all go straight
// through to whatever is behind, exactly as before.
//
// Local like the rest of the plate: every viewer has their own, and only their own beams reach it, so the
// press opens the presser's window and nobody else's. A press counts where it lands; one already held as
// the beam swept across the plate presses nothing. -xlinka
[ComponentCategory("Hidden")]
public sealed class NameplatePress : Component, ILaserPointerTarget
{
    // On a par with a panel, so nothing an avatar carries further up the tree takes the hover off the plate.
    public const int Priority = 1000;

    internal NameplateManager? Manager;
    internal Meshes.RoundedQuadMesh? Panel;

    private readonly Dictionary<int, bool> _down = new();

    public int InteractionTargetPriority => Priority;

    // The account a press here opens, or empty when nothing would open.
    public string Account
    {
        get
        {
            var root = Slot?.ActiveUserRoot;
            if (root == null || root.IsDestroyed || root.IsLocalUserRoot)
                return string.Empty;
            var user = root.ActiveUser;
            return user == null || user.IsDestroyed || user.IsGuest ? string.Empty : user.AccountId.Value ?? string.Empty;
        }
    }

    public bool IsHovered => _down.Count > 0;

    public InteractionDescription GetInteractionDescription(InteractionLaser laser) => new()
    {
        Name = ProfileLink.ViewProfile.Resolve(),
        Cursor = LaserCursor.Default,
    };

    public bool TryGetLaserPointerHit(InteractionLaser laser, in float3 rayOrigin, in float3 rayDirection, float maxDistance,
        out LaserPointerHit hit)
    {
        hit = default;
        var panel = Panel;
        if (panel == null || panel.IsDestroyed || panel.Slot == null || !Answers(laser) || Account.Length == 0)
            return false;
        var size = panel.Size.Value;
        if (size.x <= 0f || size.y <= 0f)
            return false;

        var space = panel.Slot;
        var from = space.GlobalPointToLocal(rayOrigin);
        var along = space.GlobalPointToLocal(rayOrigin + rayDirection) - from;
        if (MathF.Abs(along.z) < 1e-6f)
            return false;
        float t = -from.z / along.z;
        if (t <= 0f)
            return false;
        var local = from + along * t;
        if (MathF.Abs(local.x) > size.x * 0.5f || MathF.Abs(local.y) > size.y * 0.5f)
            return false;

        var point = space.LocalPointToGlobal(local);
        float distance = (point - rayOrigin).Length;
        float reach = Manager is { IsDestroyed: false } manager ? MathF.Min(maxDistance, manager.MaxViewDistance.Value) : maxDistance;
        if (distance > reach)
            return false;
        hit = new LaserPointerHit(distance, point);
        return true;
    }

    public void UpdateLaserPointer(InteractionLaser laser, int pointerId, in float3 rayOrigin, in float3 rayDirection, bool isPressed)
    {
        bool wasDown = _down.TryGetValue(pointerId, out bool down) && down;
        bool first = !_down.ContainsKey(pointerId);
        _down[pointerId] = isPressed;
        if (first)
            Manager?.SetPressHovered(true);
        if (!isPressed || wasDown || !Answers(laser) || !PressBeganNow(laser))
            return;
        string account = Account;
        if (account.Length > 0)
            Profiles.Open(account);
    }

    public void ClearLaserPointer(InteractionLaser laser, int pointerId)
    {
        if (_down.Remove(pointerId) && _down.Count == 0)
            Manager?.SetPressHovered(false);
    }

    public override void OnDestroy()
    {
        _down.Clear();
        base.OnDestroy();
    }

    // This machine's own beam, from a hand with nothing in it.
    private bool Answers(InteractionLaser laser)
    {
        if (laser == null || laser.IsDestroyed)
            return false;
        var owner = laser.Slot?.ActiveUser;
        if (owner != null && !ReferenceEquals(owner, World?.LocalUser))
            return false;
        return laser.Slot?.GetComponentInParent<HandTool>() is not { ActiveToolItem.Target: { IsDestroyed: false } };
    }

    // The press went down this frame, rather than arriving already held as the beam came over the plate.
    private static bool PressBeganNow(InteractionLaser laser)
    {
        var actions = Engine.Current?.InputInterface?.Actions;
        if (actions == null || !actions.HasEvaluated)
            return true;
        var side = laser.ControllerSide.Value == Chirality.Left ? Chirality.Left : Chirality.Right;
        return actions.Interaction(side).Primary.Pressed;
    }
}
