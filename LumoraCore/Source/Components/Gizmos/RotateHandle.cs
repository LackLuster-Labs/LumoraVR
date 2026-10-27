// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// Rotation ring: dragging spins the target around the handle's axis by the angle the laser's
// intersection with the ring plane sweeps around the center.
//
// The ring's shape is a circle, not a box, so it overrides the base handle's pick box with a
// ray-vs-ring test: a plane intersection plus one radius compare. There is no torus collision shape
// to fall back on anyway - approximating one meant a fence of trigger boxes around the
// circumference, eight slots per ring, and it was still a polygon. -xlinka
[ComponentCategory("Hidden")]
public class RotateHandle : TransformHandle
{
    // local units, matching the torus visual
    public readonly Sync<float> Radius;

    // Half-width of the band around the circle that counts as a hit, in local units. Wider than the
    // 0.004 tube the eye sees: a ring is a thin thing to point at, and the old collider fence was
    // 0.03 across for the same reason.
    private const float PickBand = 0.022f;

    private float3 _axis;
    private float3 _center;
    private float3 _basisU;
    private float3 _basisV;
    private floatQ _startRotation;
    private float _startAngle;
    private bool _valid;

    public RotateHandle()
    {
        Radius = new Sync<float>(this, 0.25f);
    }

    protected override Localization.LocaleText DragDescription => UndoLocale.Rotate;

    protected override bool PosesTarget => true;

    protected override void BeginDrag(Slot target, float3 rayOrigin, float3 rayDirection)
    {
        _axis = AxisWorld;
        _center = CenterWorld;
        _startRotation = target.GlobalRotation;

        // Plane basis perpendicular to the axis; pick the reference axis farthest from parallel.
        var reference = MathF.Abs(float3.Dot(_axis, float3.Up)) > 0.9f ? float3.Right : float3.Up;
        _basisU = float3.Cross(_axis, reference).Normalized;
        _basisV = float3.Cross(_axis, _basisU);

        _valid = TryReadAngle(rayOrigin, rayDirection, out _startAngle);
    }

    protected override void UpdateDrag(Slot target, float3 rayOrigin, float3 rayDirection)
    {
        if (!_valid)
            return;
        if (!TryReadAngle(rayOrigin, rayDirection, out float angle))
            return; // beam parallel to the ring plane this frame - hold rotation
        TurnTarget(target, floatQ.AxisAngleRad(_axis, angle - _startAngle) * _startRotation);
    }

    private bool TryReadAngle(float3 rayOrigin, float3 rayDirection, out float angle)
    {
        angle = 0f;
        if (!RayPlane(rayOrigin, rayDirection, _center, _axis, out var hit))
            return false;
        var dir = hit - _center;
        if (dir.LengthSquared < 1e-8f)
            return false;
        angle = MathF.Atan2(float3.Dot(dir, _basisV), float3.Dot(dir, _basisU));
        return true;
    }

    // LASER HIT: the ring lies in this slot's local XZ plane, so the plane normal is local +Y. Hit =
    // the ray crosses that plane inside a band around the ring radius. A beam that runs along the
    // plane misses, which is what you want: edge-on there is nothing to aim at.
    public override bool TryGetLaserPointerHit(InteractionLaser laser, in float3 rayOrigin, in float3 rayDirection,
        float maxDistance, out LaserPointerHit hit)
    {
        hit = default;
        var slot = Slot;
        if (slot == null || slot.IsDestroyed || slot.IsLocallyHidden || !DevToolItem.CanManipulateGizmos(laser) || !TargetIsEditable)
            return false;

        var normal = (slot.GlobalRotation * float3.Up).Normalized;
        float denom = float3.Dot(rayDirection, normal);
        if (MathF.Abs(denom) < 1e-5f)
            return false;

        var center = slot.GlobalPosition;
        float distance = float3.Dot(center - rayOrigin, normal) / denom;
        if (distance < 0f || distance > maxDistance)
            return false;

        var point = rayOrigin + rayDirection * distance;
        var offset = point - center;
        float radiusAtHit = MathF.Sqrt(offset.LengthSquared);

        // The whole handle rig is distance-scaled, so local units have to be carried into world units
        // through the slot's own scale or the pick band shrinks as the gizmo grows.
        var scale = slot.GlobalScale;
        float worldScale = (MathF.Abs(scale.x) + MathF.Abs(scale.y) + MathF.Abs(scale.z)) / 3f;
        if (worldScale <= 0f)
            return false;

        if (MathF.Abs(radiusAtHit - Radius.Value * worldScale) > PickBand * worldScale)
            return false;

        hit = new LaserPointerHit(distance, point);
        return true;
    }
}
