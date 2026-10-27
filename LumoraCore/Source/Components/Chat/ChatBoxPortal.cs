// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Chat;

// The chat box as something the desktop cursor can point at. The box is a picture the platform lays over the
// window, out of reach of any ray, so this answers the userspace pointer's ray with where that ray crosses the
// window: inside the box's corner it is a hit just in front of the camera (the box draws over everything), and
// the point goes on to the box's canvas as a pointer there. Only with the cursor loose; the crosshair never
// lands in that corner, and a press there with the cursor locked stays the world's. -xlinka
[ComponentCategory("Hidden")]
[UserspaceOnly]
public sealed class ChatBoxPortal : Component, ILaserPointerTarget, ILaserAxisTarget
{
    private const float HitDistance = 0.05f;

    private DesktopChatBox? _box;

    public int InteractionTargetPriority => 1000;

    // No name: the beam's label is drawn in the room, under the box. The box shows its own tip.
    public InteractionDescription GetInteractionDescription(InteractionLaser laser)
        => new() { Cursor = LaserCursor.Default };

    public bool TryGetLaserPointerHit(InteractionLaser laser, in float3 rayOrigin, in float3 rayDirection, float maxDistance, out LaserPointerHit hit)
    {
        hit = default;
        var box = Box();
        if (box == null || !box.TakesPointer || HitDistance > maxDistance)
            return false;
        if (!box.TryCanvasPoint(rayDirection.Normalized, out _, out bool inside) || !inside)
            return false;
        hit = new LaserPointerHit(HitDistance, rayOrigin + rayDirection.Normalized * HitDistance);
        return true;
    }

    // A press held here keeps coming, wherever the cursor goes, until it lets go: off the box the point is
    // still on the canvas's plane, so a press that slides off a name lets go without opening anything.
    public void UpdateLaserPointer(InteractionLaser laser, int pointerId, in float3 rayOrigin, in float3 rayDirection, bool isPressed)
    {
        var box = Box();
        if (box == null)
            return;
        if (!box.TakesPointer || !box.TryCanvasPoint(rayDirection.Normalized, out var point, out _))
        {
            box.ClearPoint(pointerId);
            return;
        }
        box.Point(pointerId, point, isPressed);
    }

    public void ClearLaserPointer(InteractionLaser laser, int pointerId) => Box()?.ClearPoint(pointerId);

    public bool ProcessLaserAxis(InteractionLaser laser, int pointerId, in float2 axis)
    {
        var box = Box();
        if (box == null || !box.TakesPointer || axis.y == 0f)
            return false;
        box.Wheel(axis.y);
        return true;
    }

    private DesktopChatBox? Box()
    {
        if (_box == null || _box.IsDestroyed)
            _box = Slot?.GetComponentInParents<DesktopChatBox>();
        return _box;
    }
}
