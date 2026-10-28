// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Touch;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The quad a PanelSurface shows, as something a laser or a fingertip can point at: the hit on the quad's
// plane, divided by its size, is the point on the offscreen canvas, and every pointer, scroll and secondary
// press goes on to that canvas under the same source and id it would have had there. -xlinka
[ComponentCategory("Hidden")]
public sealed class PanelSurfacePortal : Component, ILaserPointerTarget, ILaserAxisTarget, ILaserSecondaryTarget, ITouchSurface
{
    private PanelSurface? _surface;

    public int InteractionTargetPriority => 1000;

    public InteractionDescription GetInteractionDescription(InteractionLaser laser)
        => new() { Name = Surface?.Canvas?.HoveredTip(SourceOf(laser), laser.PointerId) ?? "Panel", Cursor = LaserCursor.Default };

    public bool TryGetLaserDragPoint(InteractionLaser laser, in float3 rayOrigin, in float3 rayDirection, out float3 point)
        => TryProjectPlane(rayOrigin, rayDirection, out _, out point);

    private PanelSurface? Surface
    {
        get
        {
            if (_surface == null || _surface.IsDestroyed)
                _surface = Slot?.GetComponent<PanelSurface>();
            return _surface;
        }
    }

    private static UIInteractionSource SourceOf(InteractionLaser laser)
        => laser?.ControllerSide.Value == Chirality.Left ? UIInteractionSource.VRLeft : UIInteractionSource.VRRight;

    public bool TryGetLaserPointerHit(InteractionLaser laser, in float3 rayOrigin, in float3 rayDirection, float maxDistance, out LaserPointerHit hit)
    {
        hit = default;
        if (Surface is not { Shown: true } || !TryProject(rayOrigin, rayDirection, out var local, out var world))
            return false;
        float distance = (world - rayOrigin).Length;
        if (distance > maxDistance)
            return false;
        hit = new LaserPointerHit(distance, world);
        return true;
    }

    public void UpdateLaserPointer(InteractionLaser laser, int pointerId, in float3 rayOrigin, in float3 rayDirection, bool isPressed)
    {
        var surface = Surface;
        if (surface == null)
            return;
        // Off the edge while pressed still lands: a drag that leaves the quad keeps its press.
        if (!surface.Shown || !Slot.IsActive || !TryProjectPlane(rayOrigin, rayDirection, out var local, out var world))
        {
            surface.ClearPoint(SourceOf(laser), pointerId, World?.LocalUser);
            return;
        }
        surface.Point(SourceOf(laser), pointerId, surface.CanvasPointOf(local), isPressed, World?.LocalUser, (world - rayOrigin).Length);
    }

    public void ClearLaserPointer(InteractionLaser laser, int pointerId)
        => Surface?.ClearPoint(SourceOf(laser), pointerId, World?.LocalUser);

    public bool ProcessLaserAxis(InteractionLaser laser, int pointerId, in float2 axis)
        => Surface?.Canvas?.ProcessAxis(SourceOf(laser), pointerId, axis) == true;

    public bool WantsLaserAxis(InteractionLaser laser, int pointerId)
        => Surface?.Canvas?.WantsAxis(SourceOf(laser), pointerId) == true;

    public Helio.UI.ScrollRect? ScrollUnderLaser(InteractionLaser laser, int pointerId)
        => Surface?.Canvas?.ScrollUnder(SourceOf(laser), pointerId);

    public bool TriggerLaserSecondary(InteractionLaser laser, int pointerId)
        => Surface?.Canvas?.TriggerSecondary(SourceOf(laser), pointerId) == true;

    public bool AcceptsPhysicalTouch => true;

    public bool TryTouchProject(in float3 rayOrigin, in float3 rayDirection, float rayLength, out TouchSurfaceHit hit)
    {
        hit = default;
        if (Slot == null || !Slot.IsActive || Surface is not { Shown: true })
            return false;
        float length = rayDirection.Length;
        if (length <= 0.000001f)
            return false;
        if (!TryProject(rayOrigin, rayDirection / length, out var local, out var world))
            return false;
        float distance = (world - rayOrigin).Length;
        if (distance > rayLength)
            return false;
        var normal = Slot.Forward;
        float normalLength = normal.Length;
        normal = normalLength > 0.000001f ? normal / normalLength : float3.Forward;
        hit = new TouchSurfaceHit(distance, world, normal, local);
        return true;
    }

    public void UpdateTouchPointer(int pointerId, in TouchSurfaceHit hit, bool pressed, User? actor)
    {
        var surface = Surface;
        surface?.Point(UIInteractionSource.Touch, pointerId, surface.CanvasPointOf(hit.Local), pressed, actor);
    }

    public void ClearTouchPointer(int pointerId, User? actor)
        => Surface?.ClearPoint(UIInteractionSource.Touch, pointerId, actor);

    private float2 SurfaceSize()
    {
        return Surface?.Size * PanelSurface.UnitMeters ?? float2.Zero;
    }

    // On the quad's plane and inside its edges.
    private bool TryProject(in float3 origin, in float3 direction, out float2 local, out float3 world)
    {
        if (!TryProjectPlane(origin, direction, out local, out world))
            return false;
        var size = SurfaceSize();
        var half = size * 0.5f;
        if (MathF.Abs(local.x) > half.x || MathF.Abs(local.y) > half.y)
            return false;
        float radius = MathF.Min(DashTheme.RadiusPanel * PanelSurface.UnitMeters, MathF.Min(half.x, half.y));
        float x = MathF.Max(0f, MathF.Abs(local.x) - (half.x - radius));
        float y = MathF.Max(0f, MathF.Abs(local.y) - (half.y - radius));
        return x * x + y * y <= radius * radius;
    }

    private bool TryProjectPlane(in float3 origin, in float3 direction, out float2 local, out float3 world)
    {
        local = default;
        world = default;
        var localOrigin = Slot.GlobalPointToLocal(origin);
        var localDir = Slot.GlobalDirectionToLocal(direction.Normalized);
        // The rear is a backing, not a second set of controls. Reject grazing/invalid rays too.
        if (!float.IsFinite(localOrigin.z) || !float.IsFinite(localDir.z) || localOrigin.z < 0f || localDir.z >= -1e-6f)
            return false;
        float t = -localOrigin.z / localDir.z;
        if (!float.IsFinite(t) || t < 0f)
            return false;
        var point = localOrigin + localDir * t;
        if (!float.IsFinite(point.x) || !float.IsFinite(point.y))
            return false;
        local = new float2(point.x, point.y);
        world = Slot.LocalPointToGlobal(point);
        return true;
    }
}
