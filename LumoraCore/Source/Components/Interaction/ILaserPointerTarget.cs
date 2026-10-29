// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

public readonly struct LaserPointerHit
{
    public readonly float Distance;
    public readonly float3 Point;

    public LaserPointerHit(float distance, in float3 point)
    {
        Distance = distance;
        Point = point;
    }
}

// implemented by engine UI roots that want the laser to drive pointer state. - xlinka
public interface ILaserPointerTarget : IInteractionTarget
{
    bool TryGetLaserPointerHit(
        InteractionLaser laser,
        in float3 rayOrigin,
        in float3 rayDirection,
        float maxDistance,
        out LaserPointerHit hit);

    void UpdateLaserPointer(
        InteractionLaser laser,
        int pointerId,
        in float3 rayOrigin,
        in float3 rayDirection,
        bool isPressed);

    void ClearLaserPointer(InteractionLaser laser, int pointerId);

    // Where a press held on this target is being dragged to while the ray is off it: the point the drag
    // itself reads, on the target's surface carried on past its edge. False leaves the beam ending on the
    // ray at the depth the press went down at.
    bool TryGetLaserDragPoint(InteractionLaser laser, in float3 rayOrigin, in float3 rayDirection, out float3 point)
    {
        point = default;
        return false;
    }
}

public interface ILaserAxisTarget
{
    bool ProcessLaserAxis(InteractionLaser laser, int pointerId, in float2 axis);

    // True while what this laser points at would scroll with the stick.
    bool WantsLaserAxis(InteractionLaser laser, int pointerId) => false;

    // The scroll this laser is over, for something that drives it by itself (the desktop's middle-button
    // scroll, DesktopAutoscroll). Null where nothing under the laser scrolls.
    Helio.UI.ScrollRect? ScrollUnderLaser(InteractionLaser laser, int pointerId) => null;
}

public interface ILaserSecondaryTarget
{
    bool TriggerLaserSecondary(InteractionLaser laser, int pointerId);
}
