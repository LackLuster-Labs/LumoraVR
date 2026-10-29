// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

// sphere-based laser target with hover/activation events. now implements
// IInteractionTarget so the new InteractionLaser picks it up alongside grabbables. - xlinka
[ComponentCategory("Interaction")]
public sealed class RayTarget : Component, IInteractionTarget
{
    public readonly Sync<float> HoverRadius = new();
    public readonly Sync<bool> AllowActivation = new();
    public readonly Sync<int> InteractionPriority = new();

    // set automatically by the laser; treat as read-only from outside. - xlinka
    public readonly Sync<bool> IsHovered = new();

    public event Action HoverEntered = null!;

    // also fires when the beam is destroyed while hovering, not just on exiting the sphere
    public event Action HoverExited = null!;

    // float3 param is the world-space intersection point on the hover sphere
    public event Action<float3> Activated = null!;

    public int InteractionTargetPriority => InteractionPriority.Value;

    public InteractionDescription GetInteractionDescription(InteractionLaser laser)
    {
        return new InteractionDescription
        {
            Name = Slot?.SlotName.Value,
            Cursor = AllowActivation.Value ? LaserCursor.Default : LaserCursor.Disabled,
            ForceActivate = false,
        };
    }

    // A beam resting on somebody's held tool is pointing at it, not changing it.
    public override void OnAwake()
    {
        base.OnAwake();
        IsHovered.MarkUiState();
    }

    public override void OnInit()
    {
        base.OnInit();
        HoverRadius.Value = 0.05f;
        AllowActivation.Value = true;
        InteractionPriority.Value = 0;
    }

    internal void NotifyHoverEntered()
    {
        if (IsHovered.Value) return;
        IsHovered.Value = true;
        HoverEntered?.Invoke();
    }

    internal void NotifyHoverExited()
    {
        if (!IsHovered.Value) return;
        IsHovered.Value = false;
        HoverExited?.Invoke();
    }

    internal void NotifyActivated(float3 hitPoint)
    {
        if (AllowActivation.Value)
            Activated?.Invoke(hitPoint);
    }

    public override void OnDestroy()
    {
        // Deliver a final HoverExited so listeners always see a balanced enter/exit pair.
        if (IsHovered.Value)
        {
            IsHovered.Value = false;
            HoverExited?.Invoke();
        }
        base.OnDestroy();
    }
}
