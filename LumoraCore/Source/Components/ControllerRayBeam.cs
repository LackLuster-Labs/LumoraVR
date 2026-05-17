// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Projects a visual ray beam from a tracked VR controller slot and delivers hover
/// and activation events to RayTarget components in the world.
///
/// Each frame, the beam casts from the slot's world position along the slot's forward
/// axis. It performs ray-sphere intersection against RayTarget components and resolves
/// a stable hovered target. The beam cylinder is repositioned and resized to match the
/// selected hit distance, changing color to indicate hover state.
///
/// Trigger press while a target is hovered fires RayTarget.Activated with the
/// world-space hit point on the hover sphere.
///
/// Intended usage:
///   Attach to the same slot as a TrackedDevicePositioner.
///   Set ControllerSide to match that positioner's hand side.
/// </summary>
[ComponentCategory("XR/Interaction")]
public sealed class ControllerRayBeam : Component
{
    // ===== SYNC FIELDS =====

    /// <summary>Maximum cast distance in meters.</summary>
    public readonly Sync<float> MaxDistance;

    /// <summary>Visual radius of the beam cylinder in meters.</summary>
    public readonly Sync<float> BeamRadius;

    /// <summary>Forward offset from controller origin where the beam starts, in meters.</summary>
    public readonly Sync<float> BeamStartOffset;

    /// <summary>
    /// Which controller to poll for trigger state.
    /// Must match the hand side of the TrackedDevicePositioner on this slot.
    /// </summary>
    public readonly Sync<Chirality> ControllerSide;

    /// <summary>Beam color when no RayTarget is within range.</summary>
    public readonly Sync<colorHDR> IdleColor;

    /// <summary>Beam color when a RayTarget is hovered.</summary>
    public readonly Sync<colorHDR> HoverColor;

    /// <summary>
    /// Distance tolerance for hover switching in meters.
    /// If the current target remains within (nearest hit + tolerance), hover stays on it.
    /// </summary>
    public readonly Sync<float> HoverSwitchTolerance;

    // ===== PRIVATE STATE =====

    private readonly List<TargetHit> _hitBuffer = new List<TargetHit>(64);
    private Slot _beamSlot;
    private CylinderMesh _beamMesh;
    private UnlitMaterial _beamMaterial;
    private RayTarget _currentHovered;
    private bool _prevTriggerState;

    private readonly struct TargetHit
    {
        public readonly RayTarget Target;
        public readonly float Distance;
        public readonly float3 HitPoint;

        public TargetHit(RayTarget target, float distance, float3 hitPoint)
        {
            Target = target;
            Distance = distance;
            HitPoint = hitPoint;
        }
    }

    // ===== LIFECYCLE =====

    public override void OnAwake()
    {
        base.OnAwake();
    }

    public override void OnInit()
    {
        base.OnInit();
        MaxDistance.Value = 8f;
        BeamRadius.Value = 0.003f;
        BeamStartOffset.Value = 0.020f;
        ControllerSide.Value = Chirality.Right;
        IdleColor.Value = new colorHDR(0.30f, 0.85f, 1.00f, 0.55f);
        HoverColor.Value = new colorHDR(1.00f, 1.00f, 1.00f, 0.85f);
        HoverSwitchTolerance.Value = 0.05f;
    }

    public override void OnStart()
    {
        base.OnStart();
        BuildBeamVisual();
        LumoraLogger.Log($"ControllerRayBeam: Started on '{Slot.SlotName.Value}' side={ControllerSide.Value}");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        CastAndUpdate();
    }

    public override void OnDestroy()
    {
        _currentHovered?.NotifyHoverExited();
        _currentHovered = null;
        base.OnDestroy();
    }

    // ===== BEAM VISUAL CONSTRUCTION =====

    private void BuildBeamVisual()
    {
        _beamSlot = Slot.AddSlot("RayBeamVisual");

        _beamMesh = _beamSlot.AttachComponent<CylinderMesh>();
        _beamMesh.Radius.Value = BeamRadius.Value;
        _beamMesh.Height.Value = MaxDistance.Value;
        _beamMesh.Segments.Value = 6;

        var matSlot = _beamSlot.AddSlot("RayBeamMaterial");
        _beamMaterial = matSlot.AttachComponent<UnlitMaterial>();
        _beamMaterial.TintColor.Value = IdleColor.Value;
        _beamMaterial.BlendMode.Value = BlendMode.Transparent;
        _beamMaterial.RenderQueue.Value = 85;

        var renderer = _beamSlot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = _beamMesh;
        renderer.Material.Target = _beamMaterial;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        renderer.SortingOrder.Value = 85;
    }

    // ===== PER-FRAME RAY LOGIC =====

    private void CastAndUpdate()
    {
        if (_beamSlot == null || World?.RootSlot == null)
        {
            return;
        }

        // In desktop the reticle is the cursor, so hide the world-space beam entirely so
        // it can't appear to diverge from the reticle (origin is the hand, not the camera).
        var inputCheck = Engine.Current?.InputInterface;
        if (inputCheck != null && !inputCheck.IsVRActive)
        {
            _beamSlot.ActiveSelf.Value = false;
            return;
        }
        _beamSlot.ActiveSelf.Value = true;

        float maxDist = MathF.Max(MaxDistance.Value, 0.01f);
        ResolveRayPose(out float3 origin, out float3 direction);

        float blockingDistance = maxDist;
        bool hasBlockingHit = TryFindNearestColliderHitDistance(origin, direction, maxDist, out float colliderHitDistance);
        if (hasBlockingHit)
        {
            blockingDistance = colliderHitDistance;
        }

        _hitBuffer.Clear();

        foreach (var target in World.RootSlot.GetComponentsInChildren<RayTarget>())
        {
            if (target == null || !target.Enabled.Value || target.Slot == null || !target.Slot.IsActive)
            {
                continue;
            }

            if (IsSlotOnThisHierarchy(target.Slot))
            {
                continue;
            }

            float3 center = target.Slot.GlobalPosition;
            float radius = MathF.Max(target.HoverRadius.Value, 0.001f);
            if (!RaySphereIntersect(origin, direction, center, radius, out float distance) || distance > maxDist)
            {
                continue;
            }
            if (distance > blockingDistance)
            {
                continue;
            }

            var hitPoint = new float3(
                origin.x + direction.x * distance,
                origin.y + direction.y * distance,
                origin.z + direction.z * distance);

            _hitBuffer.Add(new TargetHit(target, distance, hitPoint));
        }

        RayTarget hoveredTarget = null;
        float hoveredDistance = maxDist;
        float3 hoveredHitPoint = float3.Zero;

        if (_hitBuffer.Count > 0)
        {
            int nearestIndex = 0;
            float nearestDistance = _hitBuffer[0].Distance;
            for (int i = 1; i < _hitBuffer.Count; i++)
            {
                if (_hitBuffer[i].Distance < nearestDistance)
                {
                    nearestDistance = _hitBuffer[i].Distance;
                    nearestIndex = i;
                }
            }

            int selectedIndex = nearestIndex;
            float keepThreshold = nearestDistance + MathF.Max(HoverSwitchTolerance.Value, 0f);
            if (_currentHovered != null)
            {
                for (int i = 0; i < _hitBuffer.Count; i++)
                {
                    var hit = _hitBuffer[i];
                    if (ReferenceEquals(hit.Target, _currentHovered) && hit.Distance <= keepThreshold)
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            var selected = _hitBuffer[selectedIndex];
            hoveredTarget = selected.Target;
            hoveredDistance = selected.Distance;
            hoveredHitPoint = selected.HitPoint;
        }

        if (!ReferenceEquals(hoveredTarget, _currentHovered))
        {
            _currentHovered?.NotifyHoverExited();
            _currentHovered = hoveredTarget;
            _currentHovered?.NotifyHoverEntered();
        }

        bool triggerNow = ReadTriggerPressed();
        if (triggerNow && !_prevTriggerState && _currentHovered != null)
        {
            _currentHovered.NotifyActivated(hoveredHitPoint);
        }
        _prevTriggerState = triggerNow;

        float beamLength = hoveredTarget != null ? hoveredDistance : blockingDistance;
        PositionBeam(origin, direction, beamLength);

        colorHDR wantedColor = hoveredTarget != null ? HoverColor.Value : IdleColor.Value;
        colorHDR haveColor = _beamMaterial.TintColor.Value;
        if (haveColor.r != wantedColor.r || haveColor.g != wantedColor.g ||
            haveColor.b != wantedColor.b || haveColor.a != wantedColor.a)
        {
            _beamMaterial.TintColor.Value = wantedColor;
        }

        _beamMesh.Radius.Value = BeamRadius.Value;
        _beamMesh.Height.Value = beamLength;
    }

    private void ResolveRayPose(out float3 origin, out float3 direction)
    {
        float startOffset = MathF.Max(BeamStartOffset.Value, 0f);
        var input = Engine.Current?.InputInterface;

        // Desktop mode: the hand slot rotation is a rest pose (fingers down) which is
        // not useful as an aim direction. Instead, use the head/camera forward so the
        // beam follows where the user is looking, while the hand visual stays at the side.
        if (input != null && !input.IsVRActive)
        {
            // Desktop: aim from the head/camera so the ray passes through the reticle.
            // Origin is the head position so visuals (when shown) align with the cursor dot.
            var headSlot = FindHeadSlot();
            floatQ headRot = headSlot != null ? headSlot.GlobalRotation : floatQ.Identity;
            float3 headPos = headSlot != null ? headSlot.GlobalPosition : Slot.GlobalPosition;

            direction = headRot * new float3(0f, 0f, -1f);
            float dLen = direction.Length;
            if (dLen > 0.0001f) direction /= dLen;
            else direction = float3.Backward;

            origin = new float3(
                headPos.x + direction.x * startOffset,
                headPos.y + direction.y * startOffset,
                headPos.z + direction.z * startOffset);
            return;
        }

        // VR mode: controller slot rotation is driven by hardware tracking, use it directly.
        direction = -Slot.Forward; // controller faces -Z local → world forward when tracked
        float dirLen = direction.Length;
        if (dirLen < 0.0001f)
            direction = float3.Backward;
        else
            direction /= dirLen;

        origin = new float3(
            Slot.GlobalPosition.x + direction.x * startOffset,
            Slot.GlobalPosition.y + direction.y * startOffset,
            Slot.GlobalPosition.z + direction.z * startOffset);

        if (input == null) return;

        // VR hand tracking: refine to index fingertip direction when available.
        BodyNode tipNode = ControllerSide.Value == Chirality.Left
            ? BodyNode.LeftIndexFinger_Tip
            : BodyNode.RightIndexFinger_Tip;
        BodyNode distalNode = ControllerSide.Value == Chirality.Left
            ? BodyNode.LeftIndexFinger_Distal
            : BodyNode.RightIndexFinger_Distal;

        var tip = input.GetBodyNode(tipNode);
        if (tip == null || !tip.IsTracking) return;

        float3 resolvedOrigin = tip.Position;

        var distal = input.GetBodyNode(distalNode);
        if (distal != null && distal.IsTracking)
        {
            float3 fingertipDirection = new float3(
                tip.Position.x - distal.Position.x,
                tip.Position.y - distal.Position.y,
                tip.Position.z - distal.Position.z);
            float fingertipDirectionLength = fingertipDirection.Length;
            if (fingertipDirectionLength > 0.0001f)
                direction = fingertipDirection / fingertipDirectionLength;
        }

        origin = new float3(
            resolvedOrigin.x + direction.x * startOffset,
            resolvedOrigin.y + direction.y * startOffset,
            resolvedOrigin.z + direction.z * startOffset);
    }

    private bool TryFindNearestColliderHitDistance(float3 origin, float3 direction, float maxDistance, out float hitDistance)
    {
        hitDistance = maxDistance;
        bool hasHit = false;

        foreach (var collider in World.RootSlot.GetComponentsInChildren<Collider>())
        {
            if (!IsColliderRaycastCandidate(collider))
            {
                continue;
            }

            if (!TryIntersectCollider(collider, origin, direction, maxDistance, out float candidateDistance))
            {
                continue;
            }

            if (candidateDistance < hitDistance)
            {
                hitDistance = candidateDistance;
                hasHit = true;
            }
        }

        return hasHit;
    }

    private bool IsColliderRaycastCandidate(Collider collider)
    {
        if (collider == null || collider.Slot == null)
        {
            return false;
        }

        if (!collider.Enabled.Value || !collider.Slot.IsActive)
        {
            return false;
        }

        if (collider.IgnoreRaycasts.Value || collider.Type.Value == ColliderType.NoCollision)
        {
            return false;
        }

        if (IsSlotOnThisHierarchy(collider.Slot))
        {
            return false;
        }

        return true;
    }

    private static bool TryIntersectCollider(Collider collider, float3 origin, float3 direction, float maxDistance, out float hitDistance)
    {
        hitDistance = 0f;

        if (!TryGetColliderLocalBounds(collider, out float3 localMin, out float3 localMax))
        {
            return false;
        }

        float3 localOrigin = collider.Slot.GlobalPointToLocal(origin);
        float3 localDirection = collider.Slot.GlobalDirectionToLocal(direction);
        if (localDirection.Length < 0.0001f)
        {
            return false;
        }

        if (!RayAabbIntersect(localOrigin, localDirection, localMin, localMax, out float localHitT))
        {
            return false;
        }

        float3 localHitPoint = new float3(
            localOrigin.x + localDirection.x * localHitT,
            localOrigin.y + localDirection.y * localHitT,
            localOrigin.z + localDirection.z * localHitT);
        float3 worldHitPoint = collider.Slot.LocalPointToGlobal(localHitPoint);

        float3 worldDelta = new float3(
            worldHitPoint.x - origin.x,
            worldHitPoint.y - origin.y,
            worldHitPoint.z - origin.z);
        float forwardDistance = float3.Dot(worldDelta, direction);
        if (forwardDistance <= 0f)
        {
            return false;
        }

        float distance = worldDelta.Length;
        if (distance <= 0.0001f || distance > maxDistance)
        {
            return false;
        }

        hitDistance = distance;
        return true;
    }

    private static bool TryGetColliderLocalBounds(Collider collider, out float3 min, out float3 max)
    {
        switch (collider)
        {
            case BoxCollider box:
            {
                float3 size = AbsVector(box.Size.Value);
                float3 half = size * 0.5f;
                float3 offset = box.Offset.Value;
                min = new float3(offset.x - half.x, offset.y - half.y, offset.z - half.z);
                max = new float3(offset.x + half.x, offset.y + half.y, offset.z + half.z);
                return true;
            }
            case SphereCollider sphere:
            {
                float radius = MathF.Max(MathF.Abs(sphere.Radius.Value), 0.0005f);
                float3 offset = sphere.Offset.Value;
                min = new float3(offset.x - radius, offset.y - radius, offset.z - radius);
                max = new float3(offset.x + radius, offset.y + radius, offset.z + radius);
                return true;
            }
            case CapsuleCollider capsule:
            {
                float radius = MathF.Max(MathF.Abs(capsule.Radius.Value), 0.0005f);
                float halfHeight = MathF.Max(MathF.Abs(capsule.Height.Value) * 0.5f, radius);
                float3 offset = capsule.Offset.Value;
                min = new float3(offset.x - radius, offset.y - halfHeight, offset.z - radius);
                max = new float3(offset.x + radius, offset.y + halfHeight, offset.z + radius);
                return true;
            }
            case CylinderCollider cylinder:
            {
                float radius = MathF.Max(MathF.Abs(cylinder.Radius.Value), 0.0005f);
                float halfHeight = MathF.Max(MathF.Abs(cylinder.Height.Value) * 0.5f, 0.0005f);
                float3 offset = cylinder.Offset.Value;
                min = new float3(offset.x - radius, offset.y - halfHeight, offset.z - radius);
                max = new float3(offset.x + radius, offset.y + halfHeight, offset.z + radius);
                return true;
            }
            default:
            {
                min = float3.Zero;
                max = float3.Zero;
                return false;
            }
        }
    }

    private static bool RayAabbIntersect(float3 origin, float3 direction, float3 min, float3 max, out float t)
    {
        const float epsilon = 0.000001f;
        float tMin = 0f;
        float tMax = float.MaxValue;

        if (!RayAabbAxis(origin.x, direction.x, min.x, max.x, ref tMin, ref tMax, epsilon) ||
            !RayAabbAxis(origin.y, direction.y, min.y, max.y, ref tMin, ref tMax, epsilon) ||
            !RayAabbAxis(origin.z, direction.z, min.z, max.z, ref tMin, ref tMax, epsilon))
        {
            t = 0f;
            return false;
        }

        if (tMax < 0f)
        {
            t = 0f;
            return false;
        }

        t = tMin >= 0f ? tMin : tMax;
        return t >= 0f;
    }

    private static bool RayAabbAxis(
        float origin,
        float direction,
        float min,
        float max,
        ref float tMin,
        ref float tMax,
        float epsilon)
    {
        if (MathF.Abs(direction) <= epsilon)
        {
            return origin >= min && origin <= max;
        }

        float inverse = 1f / direction;
        float t0 = (min - origin) * inverse;
        float t1 = (max - origin) * inverse;
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        if (t0 > tMin)
        {
            tMin = t0;
        }
        if (t1 < tMax)
        {
            tMax = t1;
        }

        return tMin <= tMax;
    }

    private static float3 AbsVector(float3 value)
    {
        return new float3(MathF.Abs(value.x), MathF.Abs(value.y), MathF.Abs(value.z));
    }

    /// <summary>
    /// Moves the beam slot so the cylinder spans from <paramref name="origin"/> to
    /// <c>origin + direction * length</c>, with its Y-axis aligned to the ray.
    /// </summary>
    private void PositionBeam(float3 origin, float3 direction, float length)
    {
        _beamSlot.GlobalPosition = new float3(
            origin.x + direction.x * length * 0.5f,
            origin.y + direction.y * length * 0.5f,
            origin.z + direction.z * length * 0.5f);
        _beamSlot.GlobalRotation = AlignYToDirection(direction);
    }

    /// <summary>
    /// Finds the head slot for desktop aim direction by walking the parent hierarchy.
    /// Tries two strategies: via UserRoot.HeadSlot, then by directly searching a sibling
    /// named "Head" inside the nearest "Body Nodes" ancestor.
    /// </summary>
    private Slot FindHeadSlot()
    {
        // Strategy 1: walk to UserRoot and use its HeadSlot accessor
        var current = Slot.Parent;
        while (current != null)
        {
            var userRoot = current.GetComponent<UserRoot>();
            if (userRoot != null)
            {
                var hs = userRoot.HeadSlot;
                if (hs != null) return hs;
                break; // found UserRoot but HeadSlot is null; try strategy 2
            }
            current = current.Parent;
        }

        // Strategy 2: find the "Body Nodes" ancestor, then get its "Head" child directly
        current = Slot.Parent;
        while (current != null)
        {
            if (current.Name.Value == "Body Nodes")
            {
                foreach (var child in current.Children)
                {
                    if (child.Name.Value == "Head")
                        return child;
                }
            }
            current = current.Parent;
        }

        return null;
    }

    private bool ReadTriggerPressed()
    {
        var input = Engine.Current?.InputInterface;
        if (input == null)
        {
            return false;
        }

        bool vrTrigger = ControllerSide.Value == Chirality.Left
            ? input.LeftController.TriggerPressed
            : input.RightController.TriggerPressed;
        if (vrTrigger)
        {
            return true;
        }

        if (!input.IsVRActive && input.Mouse != null)
        {
            return ControllerSide.Value == Chirality.Left
                ? input.Mouse.RightButton.Held
                : input.Mouse.LeftButton.Held;
        }

        return false;
    }

    // ===== STATIC HELPERS =====

    /// <summary>
    /// Tests whether a ray intersects a sphere and returns the entry distance.
    ///
    /// Solves |origin + t*dir - center|^2 = radius^2 analytically.
    /// Returns false when there is no intersection or when the intersection is
    /// behind the ray origin (t <= 0).
    /// </summary>
    private static bool RaySphereIntersect(float3 origin, float3 direction, float3 center, float radius, out float t)
    {
        float3 oc = new float3(origin.x - center.x, origin.y - center.y, origin.z - center.z);
        float b = float3.Dot(oc, direction);
        float c = float3.Dot(oc, oc) - radius * radius;
        float disc = b * b - c;

        if (disc < 0f)
        {
            t = 0f;
            return false;
        }

        float sqrtDisc = MathF.Sqrt(disc);
        float nearT = -b - sqrtDisc;
        float farT = -b + sqrtDisc;

        if (nearT > 0f)
        {
            t = nearT;
            return true;
        }

        if (farT > 0f)
        {
            t = farT;
            return true;
        }

        t = 0f;
        return false;
    }

    private bool IsSlotOnThisHierarchy(Slot candidate)
    {
        var current = candidate;
        while (current != null)
        {
            if (ReferenceEquals(current, Slot))
            {
                return true;
            }
            current = current.Parent;
        }

        return false;
    }

    /// <summary>
    /// Returns a rotation whose local Y-axis points along <paramref name="direction"/>.
    /// CylinderMesh extends along the local Y-axis, so this aligns the beam cylinder
    /// with any arbitrary world-space direction.
    /// </summary>
    private static floatQ AlignYToDirection(float3 direction)
    {
        float len = direction.Length;
        if (len < 0.0001f)
        {
            return floatQ.Identity;
        }

        float3 dir = new float3(direction.x / len, direction.y / len, direction.z / len);
        float3 axis = float3.Cross(float3.Up, dir);
        float axisLen = axis.Length;

        if (axisLen < 0.001f)
        {
            return float3.Dot(float3.Up, dir) > 0f
                ? floatQ.Identity
                : floatQ.AxisAngle(float3.Forward, MathF.PI);
        }

        float3 normAxis = new float3(axis.x / axisLen, axis.y / axisLen, axis.z / axisLen);
        float angle = MathF.Acos(System.Math.Clamp(float3.Dot(float3.Up, dir), -1f, 1f));
        return floatQ.AxisAngle(normAxis, angle);
    }
}
