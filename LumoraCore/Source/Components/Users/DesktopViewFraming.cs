// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core.Components.Magnets;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// Where a screen camera goes to look at something properly. A panel or a plane is met square on, from far
// enough that the whole of it fits the view, and the frame is kept in the space of whatever carries the panel
// so a panel on a moving thing stays framed while one being dragged about does not drag the view with it. An
// object is met from the side the view already looks at it from, a few of its own sizes away. Nothing here
// touches a slot or the view: the camera asks, and the headless check asks the same questions. -xlinka
public static class DesktopViewFraming
{
    // The fit leaves a little air round the panel's edges.
    public const float PanelFitMargin = 1.05f;
    // Never nearer than this to a small panel, in the user's own metres.
    public const float PanelMinDistance = 0.5f;
    // How far zoom may take the view from a framed panel, in the user's own metres.
    public const float PanelZoomMin = 0.2f;
    public const float PanelZoomMax = 100f;
    // An object is framed this many of its bounding radii from its centre.
    public const float ObjectRadii = 2.5f;
    // An object with no bounds of its own is framed as if it were this big.
    public const float ObjectFallbackRadius = 0.25f;

    public struct PanelFrame
    {
        // The frame is kept in this slot's space; the fields below are in it.
        public Slot Space;
        public float3 Center;
        // From the panel toward whoever reads it.
        public float3 Facing;
        public float3 Up;
        // Global metres from the centre along the facing.
        public float Distance;
        // Global metres.
        public float2 Size;
    }

    public struct ObjectFrame
    {
        public Slot Target;
        public float3 Center;
        public float Radius;
        public float3 ViewPosition;
    }

    // A panel the pointer rests on: a canvas out in the world (not a menu drawn over the view) or a magnet plane.
    public static Slot? FindPanel(Slot? hit)
    {
        for (var slot = hit; slot != null && !slot.IsDestroyed; slot = slot.Parent)
        {
            var canvas = slot.GetComponent<Canvas>();
            if (canvas != null && !canvas.IsDestroyed && !canvas.Overlay.Value)
                return slot;
            if (slot.GetComponent<MagnetPlane>() is { IsDestroyed: false })
                return slot;
        }
        return null;
    }

    // The distance at which a rectangle of the given size, met square on, just fits a view of this vertical
    // field and aspect.
    public static float FitDistance(float2 size, float fovYDegrees, float aspect)
    {
        float halfY = MathF.Max(fovYDegrees, 1f) * 0.5f * (MathF.PI / 180f);
        float tanY = MathF.Tan(halfY);
        float tanX = tanY * MathF.Max(aspect, 0.1f);
        float byHeight = size.y * 0.5f / tanY;
        float byWidth = size.x * 0.5f / tanX;
        return MathF.Max(byHeight, byWidth) * PanelFitMargin;
    }

    public static bool TryFramePanel(Slot? hit, float fovYDegrees, float aspect, float userScale, out PanelFrame frame)
    {
        frame = default;
        var surface = FindPanel(hit);
        if (surface == null || surface.IsUnderLocalUser)
            return false;

        float3 center;
        float3 facing;
        float2 size;
        var canvas = surface.GetComponent<Canvas>();
        if (canvas != null && !canvas.IsDestroyed)
        {
            var rect = canvas.RootRectTransform?.LocalComputeRect;
            if (rect == null || rect.Value.IsEmpty)
                return false;
            var r = rect.Value;
            center = surface.LocalPointToGlobal(canvas.CanvasToLocal(r.Center));
            // A canvas is read from its forward side, or from behind when it is turned to face backward.
            facing = surface.GlobalRotation * new float3(0f, 0f, canvas.FaceBackward.Value ? -1f : 1f);
            var scale = surface.GlobalScale;
            size = new float2(MathF.Abs(r.width * scale.x), MathF.Abs(r.height * scale.y));
        }
        else
        {
            var plane = surface.GetComponent<MagnetPlane>();
            var normal = plane?.Normal.Value ?? float3.Up;
            if (normal.LengthSquared < 1e-8f)
                return false;
            facing = surface.GlobalRotation * normal.Normalized;
            if (SlotBoundsHelper.TryComputeWorldBounds(surface, out var bounds) && !bounds.IsEmpty)
            {
                center = bounds.Center;
                float side = MathF.Max(MathF.Max(bounds.Size.x, bounds.Size.y), bounds.Size.z);
                size = new float2(side, side);
            }
            else
            {
                center = surface.GlobalPosition;
                size = new float2(1f, 1f) * MathF.Max(surface.GlobalScale.x, 0.01f);
            }
        }
        if (facing.LengthSquared < 1e-8f)
            return false;
        facing = facing.Normalized;

        // Upright when the panel nearly is, so a panel a few degrees off level does not roll the view; along
        // the panel's own up otherwise, which is what a panel lying flat on a table needs.
        var up = surface.GlobalRotation * float3.Up;
        up = (up - facing * float3.Dot(up, facing));
        if (up.LengthSquared < 1e-8f)
            up = float3.Up - facing * float3.Dot(float3.Up, facing);
        if (up.LengthSquared < 1e-8f)
            return false;
        up = up.Normalized;
        var level = float3.Up - facing * float3.Dot(float3.Up, facing);
        if (level.LengthSquared > 1e-8f && float3.Dot(level.Normalized, up) > 0.9f)
            up = level.Normalized;

        // Held by the space of whatever carries the panel, never the panel itself, and never anything on the
        // local user: a panel worn on your own body (refused above) has no place for the view to stand still in.
        var space = surface.ObjectRootSlot().Parent ?? surface.World?.RootSlot;
        if (space == null || space.IsUnderLocalUser)
            return false;

        float scaleOfUser = MathF.Max(userScale, 0.01f);
        float distance = MathF.Max(FitDistance(size, fovYDegrees, aspect), PanelMinDistance * scaleOfUser);

        frame = new PanelFrame
        {
            Space = space,
            Center = space.GlobalPointToLocal(center),
            Facing = SpaceDirection(space, facing),
            Up = SpaceDirection(space, up),
            Distance = distance,
            Size = size,
        };
        return true;
    }

    // The frame in global terms: where the view stands, the point it looks at, the way back from the panel
    // toward the view, and up.
    public static void PanelPose(in PanelFrame frame, float distance, out float3 position, out float3 center, out float3 facing, out float3 up)
    {
        var space = frame.Space;
        center = space.LocalPointToGlobal(frame.Center);
        facing = GlobalDirection(space, frame.Facing);
        up = GlobalDirection(space, frame.Up);
        position = center + facing * distance;
    }

    // The object the pointer rests on, framed from where the view is now.
    public static bool TryFrameObject(Slot? hit, in float3 viewPosition, out ObjectFrame frame)
    {
        frame = default;
        if (hit == null || hit.IsDestroyed)
            return false;
        var target = hit.ObjectRootSlot();
        if (target.IsRootSlot)
            return false;

        float3 center;
        float radius;
        if (SlotBoundsHelper.TryComputeWorldBounds(target, out var bounds) && !bounds.IsEmpty)
        {
            center = bounds.Center;
            radius = MathF.Max(bounds.Extents.Length, 0.01f);
        }
        else
        {
            center = target.GlobalPosition;
            radius = ObjectFallbackRadius * MathF.Max(target.GlobalScale.x, 0.01f);
        }

        var away = viewPosition - center;
        if (away.LengthSquared < 1e-8f)
            away = new float3(0f, 0f, 1f);
        frame = new ObjectFrame
        {
            Target = target,
            Center = center,
            Radius = radius,
            ViewPosition = center + away.Normalized * (radius * ObjectRadii),
        };
        return true;
    }

    // The thing a pointer on this slot means as a whole: the nearest marked object root above it, else the
    // topmost slot under the world root (a user's whole rig, a loose prop with its parts).
    private static Slot ObjectRootSlot(this Slot slot)
    {
        Slot top = slot;
        for (var current = slot; current != null && !current.IsDestroyed; current = current.Parent)
        {
            if (current.GetComponent<ObjectRoot>() != null)
                return current;
            if (current.Parent == null)
                break;
            top = current;
        }
        return top;
    }

    private static float3 SpaceDirection(Slot space, float3 global)
    {
        var local = space.GlobalDirectionToLocal(global);
        return local.LengthSquared > 1e-12f ? local.Normalized : global;
    }

    private static float3 GlobalDirection(Slot space, float3 local)
    {
        var global = space.LocalDirectionToGlobal(local);
        return global.LengthSquared > 1e-12f ? global.Normalized : local;
    }
}
