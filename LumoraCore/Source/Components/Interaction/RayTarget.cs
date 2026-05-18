// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

/// <summary>
/// Marks a slot as a target for controller ray interaction.
/// Attach this component to any object that should respond to hover and activation
/// events produced by a ControllerRayBeam. Hit detection uses a sphere centred on
/// the slot's world position with radius HoverRadius.
/// </summary>
[ComponentCategory("Interaction")]
public sealed class RayTarget : Component
{
    /// <summary>
    /// Radius of the hit-detection sphere in metres.
    /// </summary>
    public readonly Sync<float> HoverRadius = new();

    /// <summary>
    /// Whether this target can currently be activated by a trigger press.
    /// </summary>
    public readonly Sync<bool> AllowActivation = new();

    /// <summary>
    /// Whether a ControllerRayBeam ray is currently hovering over this target.
    /// Set automatically by the beam; treat this as read-only from outside.
    /// </summary>
    public readonly Sync<bool> IsHovered = new();

    /// <summary>
    /// Fired once when a ray first enters the hover sphere.
    /// </summary>
    public event Action HoverEntered;

    /// <summary>
    /// Fired once when the hovering ray exits the sphere or the beam is destroyed.
    /// </summary>
    public event Action HoverExited;

    /// <summary>
    /// Fired when this target is activated (trigger pressed while hovered).
    /// The float3 parameter is the world-space intersection point on the hover sphere.
    /// </summary>
    public event Action<float3> Activated;

    public override void OnInit()
    {
        base.OnInit();
        HoverRadius.Value = 0.05f;
        AllowActivation.Value = true;
        // IsHovered = false (C# default, skip)
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
