// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.Touch;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

[ComponentCategory("Hidden")]
public class DashSurfacePortal : Component, ILaserPointerTarget, ILaserAxisTarget, ITouchSurface
{
    private UserspaceDashboard? _dash;

    public int InteractionTargetPriority => 1000;

    public InteractionDescription GetInteractionDescription(InteractionLaser laser)
    {
        return new InteractionDescription
        {
            Name = Dash()?.HoveredTip(laser) ?? "Dashboard",
            Cursor = LaserCursor.Default,
        };
    }

    public bool TryGetLaserPointerHit(InteractionLaser laser, in float3 rayOrigin, in float3 rayDirection, float maxDistance, out LaserPointerHit hit)
    {
        hit = default;
        if (!TryProject(rayOrigin, rayDirection, out var local, out var world))
            return false;

        var size = SurfaceSize();
        if (MathF.Abs(local.x) > size.x * 0.5f || MathF.Abs(local.y) > size.y * 0.5f)
            return false;

        float distance = (world - rayOrigin).Length;
        if (distance > maxDistance)
            return false;

        hit = new LaserPointerHit(distance, world);
        return true;
    }

    public void UpdateLaserPointer(InteractionLaser laser, int pointerId, in float3 rayOrigin, in float3 rayDirection, bool isPressed)
    {
        var dash = Dash();
        if (dash == null)
            return;

        if (!TryProject(rayOrigin, rayDirection, out var local, out var world))
        {
            dash.ClearVrPointer(laser, pointerId);
            return;
        }

        var size = SurfaceSize();
        float u = local.x / size.x + 0.5f;
        float v = 0.5f - local.y / size.y;
        dash.UpdateVrPointer(laser, pointerId, new float2(u, v), isPressed, (world - rayOrigin).Length);
    }

    public void ClearLaserPointer(InteractionLaser laser, int pointerId)
    {
        Dash()?.ClearVrPointer(laser, pointerId);
    }

    public bool TryGetLaserDragPoint(InteractionLaser laser, in float3 rayOrigin, in float3 rayDirection, out float3 point)
        => TryProject(rayOrigin, rayDirection, out _, out point);

    // Headset only: on a monitor the window's own wheel feed already reaches the dash, and taking the
    // laser's wheel here as well would scroll every notch twice.
    public bool ProcessLaserAxis(InteractionLaser laser, int pointerId, in float2 axis)
        => InHeadset && Dash()?.ProcessVrAxis(laser, pointerId, axis) == true;

    public bool WantsLaserAxis(InteractionLaser laser, int pointerId)
        => InHeadset && Dash()?.WantsVrAxis(laser, pointerId) == true;

    // On a monitor too: the middle-button scroll drives the list itself and never goes through the wheel feed.
    public Helio.UI.ScrollRect? ScrollUnderLaser(InteractionLaser laser, int pointerId)
        => Dash()?.ScrollUnderVrPointer(laser, pointerId);

    private static bool InHeadset => Engine.Current?.InputInterface?.IsVRActive == true;

    // The grab and the release of a laser pointing at the surface, forwarded to the dash canvas under
    // the same pointer id its hover runs on: a widget lifts off a grid, a carried widget lands on one.
    public IGrabbable? TryGrab(InteractionLaser laser)
        => Dash()?.TryGrabVrPointer(laser, laser.PointerId);

    public bool TryReceive(IReadOnlyList<IGrabbable> items, InteractionLaser laser)
        => Dash()?.TryReceiveVrPointer(laser, laser.PointerId, items) == true;

    // A fingertip on the display surface goes to the dash canvas the same way the laser does: the hit's
    // unrolled coordinate divided by the surface size is the canvas u/v. The dash canvas is a render
    // target, so the poke can never reach it directly. -xlinka
    public bool AcceptsPhysicalTouch => true;

    public bool TryTouchProject(in float3 rayOrigin, in float3 rayDirection, float rayLength, out TouchSurfaceHit hit)
    {
        hit = default;
        if (Slot == null || !Slot.IsActive)
            return false;
        float dirLength = rayDirection.Length;
        if (dirLength <= 0.000001f)
            return false;
        var direction = rayDirection / dirLength;
        if (!TryProject(rayOrigin, direction, out var local, out var world))
            return false;

        var size = SurfaceSize();
        if (MathF.Abs(local.x) > size.x * 0.5f || MathF.Abs(local.y) > size.y * 0.5f)
            return false;

        float distance = (world - rayOrigin).Length;
        if (distance > rayLength)
            return false;

        float3 normal = Slot.Forward;
        float normalLength = normal.Length;
        normal = normalLength > 0.000001f ? normal / normalLength : float3.Forward;
        hit = new TouchSurfaceHit(distance, world, normal, local);
        return true;
    }

    public void UpdateTouchPointer(int pointerId, in TouchSurfaceHit hit, bool pressed, User? actor)
    {
        var dash = Dash();
        if (dash == null)
            return;
        var size = SurfaceSize();
        float u = hit.Local.x / size.x + 0.5f;
        float v = 0.5f - hit.Local.y / size.y;
        dash.UpdateTouchPointer(pointerId, new float2(u, v), pressed);
    }

    public void ClearTouchPointer(int pointerId, User? actor)
    {
        Dash()?.ClearTouchPointer(pointerId);
    }

    private UserspaceDashboard? Dash() => _dash ??= Slot.GetComponentInParents<UserspaceDashboard>();

    private float2 SurfaceSize()
    {
        var mesh = Slot.GetComponent<CurvedPlaneMesh>();
        return mesh != null ? mesh.Size.Value : new float2(1f, 0.5625f);
    }

    // Intersect the surface the wearer actually sees. In VR the display mesh is a CurvedPlaneMesh
    // bent toward the viewer (curvature 0.5 is a 90 degree arc); this used to hit the flat z=0 plane
    // of the slot instead, so the cursor's world point and the canvas coordinate it was fed were the
    // flat plane's, and the laser visibly landed somewhere else on the curve, worst at the edges.
    // `local.x` stays the unrolled coordinate the canvas mapping expects: the arc length from the
    // centre, scaled so the full width still spans [-w/2, w/2]. -xlinka
    private bool TryProject(in float3 origin, in float3 direction, out float2 local, out float3 world)
    {
        local = default;
        world = default;

        var localOrigin = Slot.GlobalPointToLocal(origin);
        var localDir = Slot.GlobalDirectionToLocal(direction.Normalized);

        var mesh = Slot.GetComponent<CurvedPlaneMesh>();
        float curvature = mesh != null ? System.Math.Clamp(mesh.Curvature.Value, 0f, 1f) : 0f;
        if (curvature < 0.01f)
            return ProjectFlat(localOrigin, localDir, out local, out world);

        // Same numbers CurvedPlaneMesh.BuildPlane uses, so the hit lands on the drawn vertices.
        var size = SurfaceSize();
        float width = size.x;
        float radius = width * 0.5f;
        float totalAngle = MathF.PI * curvature;
        float startAngle = (MathF.PI - totalAngle) * 0.5f;
        float globalOffset = MathF.Sin(startAngle) * radius;
        float widthAdjust = 1f / MathF.Cos(startAngle);
        float rx = radius * widthAdjust;

        // Points satisfy (x / rx)^2 + ((g - z) / r)^2 = 1: an ellipse in XZ centred at (0, g).
        float ox = localOrigin.x / rx, oz = (globalOffset - localOrigin.z) / radius;
        float dx = localDir.x / rx, dz = -localDir.z / radius;
        float a = dx * dx + dz * dz;
        float b = 2f * (ox * dx + oz * dz);
        float c = ox * ox + oz * oz - 1f;
        if (a < 1e-9f)
            return false;
        float disc = b * b - 4f * a * c;
        if (disc < 0f)
            return false;
        float sq = MathF.Sqrt(disc);

        // Nearest root in front of the ray that lands on the drawn arc.
        float tNear = (-b - sq) / (2f * a);
        float tFar = (-b + sq) / (2f * a);
        for (int i = 0; i < 2; i++)
        {
            float t = i == 0 ? tNear : tFar;
            if (t < 0f)
                continue;
            var point = localOrigin + localDir * t;
            float px = point.x / rx;
            float pz = (globalOffset - point.z) / radius;
            // BuildPlane: x = -cos(angle) * rx, z = g - sin(angle) * r.
            float angle = MathF.Atan2(pz, -px);
            if (angle < startAngle - 1e-4f || angle > startAngle + totalAngle + 1e-4f)
                continue;

            float u = (angle - startAngle) / totalAngle;
            local = new float2((u - 0.5f) * width, point.y);
            world = Slot.LocalPointToGlobal(point);
            return true;
        }
        return false;
    }

    private bool ProjectFlat(in float3 localOrigin, in float3 localDir, out float2 local, out float3 world)
    {
        local = default;
        world = default;
        if (MathF.Abs(localDir.z) < 1e-6f)
            return false;

        float t = -localOrigin.z / localDir.z;
        if (t < 0f)
            return false;

        var point = localOrigin + localDir * t;
        local = new float2(point.x, point.y);
        world = Slot.LocalPointToGlobal(point);
        return true;
    }
}
