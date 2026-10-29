// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Bone eyes turn in their head-relative frame; flat eyes get eyeLook blendshapes from the same gaze.
// Runs on every peer and the writes are local (no network churn), like the IK bone writes.
//
// An avatar can say which bones are its eyes, when the rig's are not the ones that look, and how far each eye
// may turn each way. Idle glances stay inside the cone and inside those limits; tracked eyes and the camera's
// pull go as far as the limits allow, and the look shapes reach full at them. -xlinka
[ComponentCategory("Users/Avatar")]
public sealed class EyeGazeDriver : Component
{
    public readonly Sync<float> MaxYawDegrees = new();
    public readonly Sync<float> MaxPitchDegrees = new();
    public readonly Sync<float> SaccadeMinInterval = new();
    public readonly Sync<float> SaccadeMaxInterval = new();
    public readonly Sync<float> Speed = new();

    public readonly SyncRef<Slot> LeftEye = new();
    public readonly SyncRef<Slot> RightEye = new();
    // Degrees each eye may turn: x up, y down, z toward the avatar's left, w toward its right. A zero leaves
    // that way to the cone.
    public readonly Sync<float4> LeftEyeLimits = new();
    public readonly Sync<float4> RightEyeLimits = new();
    // The eyes are the bones and nothing else: no look shapes found by name. Shapes made for face tracking
    // turn the eyeball a second time on top of the bone.
    public readonly Sync<bool> ExactEyes = new();

    // Brief looks the avatar gives now and then while nothing else holds its eyes: degrees toward its left (x) and
    // up (y), how often against the others, and how long each is held (x to y seconds). One is tried every
    // GlanceInterval seconds and taken with GlanceChance; it stays inside the cone and the eye limits, and the
    // idle saccades go on once it has been held.
    public readonly SyncFieldList<float2> GlanceAngles = new();
    public readonly SyncFieldList<float> GlanceWeights = new();
    public readonly SyncFieldList<float2> GlanceDurations = new();
    public readonly Sync<float> GlanceChance = new();
    public readonly Sync<float> GlanceInterval = new();

    // Where the eyes look up and down this frame, -1 as far down as they go to 1 as far up.
    public float LookPitch { get; private set; }

    private const float Deg2Rad = MathF.PI / 180f;

    private enum LookKind { Left, Right, Up, Down, In, Out }

    private Slot _head = null!, _leftEye = null!, _rightEye = null!;
    private floatQ _leftRestRel = floatQ.Identity, _rightRestRel = floatQ.Identity;
    private floatQ _headRestRot = floatQ.Identity;
    private float3 _restFront = float3.Backward;
    private bool _resolved;
    private EyeStreamManager? _eyeTracking;
    private readonly AvatarFaceWearer _wearer = new();

    // Flat-eye gaze: eyeLook blendshapes (side: -1 combined, 0 left, 1 right). Driven from the same gaze.
    private readonly List<(SkinnedMeshRenderer Renderer, int Index, LookKind Kind, int Side)> _lookTargets = new();

    private readonly Random _rng = new();
    private float _timer;
    private float _next = 1.5f;
    private float _curYaw, _curPitch, _tgtYaw, _tgtPitch; // radians
    private float _glanceClock;

    public override void OnInit()
    {
        base.OnInit();
        MaxYawDegrees.Value = 18f;
        MaxPitchDegrees.Value = 12f;
        SaccadeMinInterval.Value = 1.2f;
        SaccadeMaxInterval.Value = 4f;
        Speed.Value = 8f;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (_wearer.Update(Slot))
            Release();
        var wearer = _wearer.Current;
        if (wearer == null)
            return;

        if (!_resolved)
            Resolve();

        bool hasBones = _head != null && !_head.IsDestroyed && (_leftEye != null || _rightEye != null);
        bool hasLook = _lookTargets.Count > 0;
        if (!hasBones && !hasLook)
            return;

        // Target gaze comes from real eye tracking when the head device reports it; otherwise the wearer's
        // own camera when it asks for their eyes; otherwise an idle saccade picks a new target on a
        // randomized cadence. Either way we smooth toward the target.
        if (TryRealGaze(out float realYaw, out float realPitch))
        {
            _tgtYaw = realYaw;
            _tgtPitch = realPitch;
        }
        else if (TryCameraGaze(wearer, out float cameraYaw, out float cameraPitch))
        {
            // Held on the lens; the saccade clock waits until the camera lets go.
            _tgtYaw = cameraYaw;
            _tgtPitch = cameraPitch;
            _timer = 0f;
        }
        else
        {
            _timer += delta;
            if (TryGlance(delta))
            {
            }
            else if (_timer >= _next)
            {
                _timer = 0f;
                float span = MathF.Max(0f, SaccadeMaxInterval.Value - SaccadeMinInterval.Value);
                _next = SaccadeMinInterval.Value + (float)_rng.NextDouble() * span;
                _tgtYaw = ClampYaw(((float)_rng.NextDouble() * 2f - 1f) * MaxYawDegrees.Value * Deg2Rad);
                _tgtPitch = ClampPitch(((float)_rng.NextDouble() * 2f - 1f) * MaxPitchDegrees.Value * Deg2Rad);
            }
        }

        // Frame-rate-independent smoothing toward the target gaze.
        float k = 1f - MathF.Exp(-MathF.Max(0.01f, Speed.Value) * delta);
        _curYaw += (_tgtYaw - _curYaw) * k;
        _curPitch += (_tgtPitch - _curPitch) * k;

        var left = LeftEyeLimits.Value;
        var right = RightEyeLimits.Value;
        if (hasBones)
        {
            ApplyEye(_leftEye, _leftRestRel, EyeYaw(left, _curYaw), EyePitch(left, _curPitch));
            ApplyEye(_rightEye, _rightRestRel, EyeYaw(right, _curYaw), EyePitch(right, _curPitch));
        }

        LookPitch = (Reached(left, _curPitch) + Reached(right, _curPitch)) * 0.5f;

        if (hasLook)
            DriveLook(_curYaw, _curPitch, left, right);
    }

    // A glance when one is due and the dice say so, weighted among the avatar's own; true when one starts.
    private bool TryGlance(float delta)
    {
        int count = System.Math.Min(GlanceAngles.Count, GlanceWeights.Count);
        if (count == 0 || GlanceInterval.Value <= 0f)
            return false;
        _glanceClock -= delta;
        if (_glanceClock > 0f)
            return false;
        _glanceClock += GlanceInterval.Value;
        if (_rng.NextDouble() >= GlanceChance.Value)
            return false;

        float total = 0f;
        for (int i = 0; i < count; i++)
            total += MathF.Max(GlanceWeights[i], 0f);
        if (total <= 0f)
            return false;
        float pick = (float)_rng.NextDouble() * total;
        int chosen = count - 1;
        for (int i = 0; i < count; i++)
        {
            pick -= MathF.Max(GlanceWeights[i], 0f);
            if (pick <= 0f)
            {
                chosen = i;
                break;
            }
        }

        var angle = GlanceAngles[chosen];
        var hold = chosen < GlanceDurations.Count ? GlanceDurations[chosen] : new float2(0.5f, 2f);
        _tgtYaw = ClampYaw(angle.x * Deg2Rad);
        _tgtPitch = ClampPitch(angle.y * Deg2Rad);
        _timer = 0f;
        _next = hold.x + (float)_rng.NextDouble() * MathF.Max(0f, hold.y - hold.x);
        _glanceClock = _next;
        return true;
    }

    // Radians one eye may turn one way: 0 up, 1 down, 2 left, 3 right.
    private float Limit(float4 limits, int way)
    {
        float set = way switch { 0 => limits.x, 1 => limits.y, 2 => limits.z, _ => limits.w };
        float cone = way < 2 ? MaxPitchDegrees.Value : MaxYawDegrees.Value;
        return MathF.Max(set > 0f ? set : cone, 0f) * Deg2Rad;
    }

    // As far as the freer eye goes, which is as far as the gaze itself goes.
    private float Reach(int way) => MathF.Max(Limit(LeftEyeLimits.Value, way), Limit(RightEyeLimits.Value, way));

    private float ClampYaw(float yaw) => System.Math.Clamp(yaw, -Reach(3), Reach(2));

    private float ClampPitch(float pitch) => System.Math.Clamp(pitch, -Reach(1), Reach(0));

    private float EyeYaw(float4 limits, float yaw) => System.Math.Clamp(yaw, -Limit(limits, 3), Limit(limits, 2));

    private float EyePitch(float4 limits, float pitch) => System.Math.Clamp(pitch, -Limit(limits, 1), Limit(limits, 0));

    // How much of one eye's way up (+) or down (-) the pitch has used, -1..1.
    private float Reached(float4 limits, float pitch)
    {
        float way = Limit(limits, pitch >= 0f ? 0 : 1);
        return way < 1e-4f ? 0f : System.Math.Clamp(pitch / way, -1f, 1f);
    }

    // The same for left (+) and right (-).
    private float ReachedYaw(float4 limits, float yaw)
    {
        float way = Limit(limits, yaw >= 0f ? 2 : 3);
        return way < 1e-4f ? 0f : System.Math.Clamp(yaw / way, -1f, 1f);
    }

    // Map the current gaze (yaw/pitch radians) onto eyeLook blendshapes. +yaw rotates about Up = looking
    // LEFT (right-handed); +pitch about Right rotates local -Z upward. In/Out are per-eye
    // (In = toward the nose). Each eye's shapes reach full at that eye's limit; a shape for both eyes
    // takes the two eyes' average.
    private void DriveLook(float yaw, float pitch, float4 leftLimits, float4 rightLimits)
    {
        float leftYaw = ReachedYaw(leftLimits, yaw), rightYaw = ReachedYaw(rightLimits, yaw);
        float leftPitch = Reached(leftLimits, pitch), rightPitch = Reached(rightLimits, pitch);

        for (int i = 0; i < _lookTargets.Count; i++)
        {
            var (renderer, index, kind, side) = _lookTargets[i];
            if (renderer == null || renderer.IsDestroyed || !renderer.OwnsBlendShape(index, this))
            {
                _resolved = false; // mesh went away or our claim was lost - rescan next frame
                return;
            }

            float y = side == 0 ? leftYaw : side == 1 ? rightYaw : (leftYaw + rightYaw) * 0.5f;
            float p = side == 0 ? leftPitch : side == 1 ? rightPitch : (leftPitch + rightPitch) * 0.5f;
            float lookLeft = MathF.Max(y, 0f), lookRight = MathF.Max(-y, 0f);
            float w = kind switch
            {
                LookKind.Left => lookLeft,
                LookKind.Right => lookRight,
                LookKind.Up => MathF.Max(p, 0f),
                LookKind.Down => MathF.Max(-p, 0f),
                LookKind.In => side == 1 ? lookLeft : lookRight,   // left eye in = look right; right eye in = look left
                LookKind.Out => side == 1 ? lookRight : lookLeft,
                _ => 0f,
            };
            renderer.DriveBlendShapeWeight(index, w);
        }
    }

    // Eye = the authored eye pose carried by the live head, with the gaze applied as a WORLD-frame yaw
    // (about up) + pitch (about the gaze's right axis) around the head's current facing. The gaze must
    // never be composed in the eye BONE's local frame - its axes are arbitrary per rig, and rolling the
    // gaze through them sent eyes the wrong way (same trap as the head/feet aiming). -xlinka
    private void ApplyEye(Slot? eye, in floatQ restRelToHead, float yaw, float pitch)
    {
        if (eye == null || eye.IsDestroyed)
            return;

        floatQ baseRot = _head.GlobalRotation * restRelToHead;

        // Where the head currently faces: the rest body front carried by the head's rotation since rest.
        float3 front = (_head.GlobalRotation * _headRestRot.Inverse) * _restFront;
        float3 frontFlat = front;
        frontFlat.y = 0f;
        frontFlat = frontFlat.LengthSquared > 1e-6f ? frontFlat.Normalized : float3.Backward;

        floatQ gaze = floatQ.AxisAngleRad(float3.Up, yaw);
        float3 right = float3.Cross(frontFlat, float3.Up);
        if (right.LengthSquared > 1e-8f && MathF.Abs(pitch) > 1e-5f)
            gaze = floatQ.AxisAngleRad(right.Normalized, pitch) * gaze;

        floatQ eyeRot = gaze * baseRot;
        var parent = eye.Parent;
        var local = parent != null ? parent.GlobalRotationToLocal(eyeRot) : eyeRot;
        eye.LocalRotation.SetValueSilently(local, change: true);
    }

    // Real eye gaze from the user's eye-tracking stream, as yaw/pitch (radians) clamped to the cone.
    // Returns false when no eye tracking is active, so the caller falls back to the idle saccade. The
    // combined gaze is treated as a head-local direction (-Z forward); exact axis signs depend on the
    // device hook feeding the head device.
    private bool TryRealGaze(out float yaw, out float pitch)
    {
        yaw = 0f;
        pitch = 0f;

        if (_eyeTracking != null && _eyeTracking.IsDestroyed)
            _eyeTracking = null;
        _eyeTracking ??= _wearer.Current?.GetRegisteredComponent<EyeStreamManager>();
        if (_eyeTracking == null || !_eyeTracking.IsTracking)
            return false;
        if (!_eyeTracking.TryGetCombinedGaze(out var dir))
            return false;

        float len = MathF.Sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
        if (len < 1e-4f)
            return false;
        dir /= len;

        // Lumora view forward is local -Z. Positive yaw is a leftward rotation around +Y, so +X
        // (viewer right) is negative yaw and -X (viewer left) is positive yaw.
        yaw = ClampYaw(MathF.Atan2(-dir.x, -dir.z));
        pitch = ClampPitch(MathF.Asin(System.Math.Clamp(dir.y, -1f, 1f)));
        return true;
    }

    // The wearer's own camera, when it asks for their eyes and sits within reach of where they face: the gaze
    // laid on its lens as the same head-relative yaw and pitch the saccades use, clamped to the cone. Every
    // machine works this out for itself from the camera and head it already has, like the saccades.
    private bool TryCameraGaze(UserRoot wearer, out float yaw, out float pitch)
    {
        yaw = 0f;
        pitch = 0f;
        var user = wearer.ActiveUser;
        if (user == null || user.IsDestroyed)
            return false;
        var facing = wearer.HeadRotation * float3.Backward;
        if (!PhotoCamera.TryEyeTarget(user, wearer.HeadPosition, facing, out var lens))
            return false;

        var toward = lens - EyeCentre(wearer);
        if (toward.LengthSquared < 1e-8f)
            return false;
        toward = toward.Normalized;

        // From where the eyes rest, which is where yaw and pitch count from: the rest front carried by the
        // head, or the view when the rig has no head bone and only look shapes.
        var front = _head != null && !_head.IsDestroyed ? (_head.GlobalRotation * _headRestRot.Inverse) * _restFront : facing;
        front = front.LengthSquared > 1e-8f ? front.Normalized : facing;

        var frontFlat = new float3(front.x, 0f, front.z);
        var towardFlat = new float3(toward.x, 0f, toward.z);
        if (frontFlat.LengthSquared > 1e-8f && towardFlat.LengthSquared > 1e-8f)
        {
            frontFlat = frontFlat.Normalized;
            towardFlat = towardFlat.Normalized;
            yaw = MathF.Atan2(float3.Dot(float3.Up, float3.Cross(frontFlat, towardFlat)), float3.Dot(frontFlat, towardFlat));
        }
        pitch = MathF.Asin(System.Math.Clamp(toward.y, -1f, 1f)) - MathF.Asin(System.Math.Clamp(front.y, -1f, 1f));

        yaw = ClampYaw(yaw);
        pitch = ClampPitch(pitch);
        return true;
    }

    // Between the eyes when the rig has both, else the one it has, else the head.
    private float3 EyeCentre(UserRoot wearer)
    {
        Slot? left = _leftEye is { IsDestroyed: false } ? _leftEye : null;
        Slot? right = _rightEye is { IsDestroyed: false } ? _rightEye : null;
        if (left != null && right != null)
            return (left.GlobalPosition + right.GlobalPosition) * 0.5f;
        if (left != null)
            return left.GlobalPosition;
        if (right != null)
            return right.GlobalPosition;
        return _head != null && !_head.IsDestroyed ? _head.GlobalPosition : wearer.HeadPosition;
    }

    // Eyes back to their authored pose and every look shape released. The bone writes are local and silent,
    // so nothing else would ever put them back.
    private void Release()
    {
        if (_resolved && _head != null && !_head.IsDestroyed)
        {
            ApplyEye(_leftEye, _leftRestRel, 0f, 0f);
            ApplyEye(_rightEye, _rightRestRel, 0f, 0f);
        }
        foreach (var (renderer, index, _, _) in _lookTargets)
        {
            if (renderer != null && !renderer.IsDestroyed)
                renderer.ReleaseBlendShape(index, this);
        }
        _lookTargets.Clear();
        _resolved = false;
        _eyeTracking = null;
        _curYaw = _curPitch = _tgtYaw = _tgtPitch = 0f;
        _timer = 0f;
        LookPitch = 0f;
    }

    public override void OnAwake()
    {
        base.OnAwake();
        if (Slot != null)
            Slot.ActiveChanged += OnSlotActiveChanged;
    }

    // Switched off, it stops updating and would never see anything else change, so it lets go now.
    private void OnSlotActiveChanged(Slot slot)
    {
        if (Slot != null && !Slot.IsActive)
            Release();
    }

    public override void OnDisabled()
    {
        base.OnDisabled();
        Release();
    }

    public override void OnDestroy()
    {
        if (Slot != null)
            Slot.ActiveChanged -= OnSlotActiveChanged;
        Release();
        base.OnDestroy();
    }

    private void Resolve()
    {
        var rig = Slot.GetComponentInChildren<HumanoidRig>() ?? Slot.GetComponent<HumanoidRig>();
        if (rig == null)
            return; // keep trying until a rig appears

        _head = rig.TryGetBone(BodyNode.Head);
        _leftEye = LeftEye.Target is { IsDestroyed: false } left ? left : rig.TryGetBone(BodyNode.LeftEye);
        _rightEye = RightEye.Target is { IsDestroyed: false } right ? right : rig.TryGetBone(BodyNode.RightEye);

        if (_head != null && !_head.IsDestroyed)
        {
            var headInv = _head.GlobalRotation.Inverse;
            if (_leftEye != null && !_leftEye.IsDestroyed)
                _leftRestRel = headInv * _leftEye.GlobalRotation;
            if (_rightEye != null && !_rightEye.IsDestroyed)
                _rightRestRel = headInv * _rightEye.GlobalRotation;

            _headRestRot = _head.GlobalRotation;
            var front = rig.GuessForwardAxis();
            _restFront = front.HasValue && front.Value.LengthSquared > 1e-6f
                ? front.Value.Normalized
                : float3.Backward;
        }

        if (ExactEyes.Value)
            ReleaseLookTargets();
        else
            ResolveLookTargets();
        _resolved = true; // rig resolved; bone eyes and/or eyeLook blendshapes drive from here, else no-op
    }

    private void ReleaseLookTargets()
    {
        foreach (var (renderer, index, _, _) in _lookTargets)
        {
            if (renderer != null && !renderer.IsDestroyed)
                renderer.ReleaseBlendShape(index, this);
        }
        _lookTargets.Clear();
    }

    // Find eyeLook blendshapes (flat/texture eyes) and claim them so gaze can drive them. Disjoint from the
    // pupil/widen/squeeze/frown shapes claimed by EyeExpressionDriver.
    private void ResolveLookTargets()
    {
        _lookTargets.Clear();
        foreach (var renderer in Slot.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            for (int i = 0; i < renderer.BlendShapeCount; i++)
            {
                var name = renderer.BlendShapeName(i);
                if (string.IsNullOrEmpty(name))
                    continue;
                var classified = ClassifyLook(name.ToLowerInvariant());
                if (classified == null)
                    continue;
                if (renderer.ClaimBlendShape(i, this, SkinnedMeshRenderer.BlendShapePriorityEye))
                    _lookTargets.Add((renderer, i, classified.Value.Kind, classified.Value.Side));
            }
        }
    }

    private static (LookKind Kind, int Side)? ClassifyLook(string lower)
    {
        if (!lower.Contains("look"))
            return ClassifyEyeDirection(lower);
        int side = SideOf(lower);
        if (lower.Contains("lookin")) return (LookKind.In, side);
        if (lower.Contains("lookout")) return (LookKind.Out, side);
        if (lower.Contains("lookup")) return (LookKind.Up, side);
        if (lower.Contains("lookdown")) return (LookKind.Down, side);
        if (lower.Contains("lookleft")) return (LookKind.Left, side);
        if (lower.Contains("lookright")) return (LookKind.Right, side);
        return null;
    }

    // The vendor eye shape set names a look by eye then direction, with no "look" in it: Eye_Left_Left,
    // Eye_Right_Up. Only that exact three-part shape is taken, so Eye_Left_Wide and Eye_Left_Blink stay
    // with the drivers that own them, and the side comes from its position: Eye_Right_Left is the right
    // eye looking left, which a search for "left" would hand to the wrong eye. -xlinka
    private static (LookKind Kind, int Side)? ClassifyEyeDirection(string lower)
    {
        var parts = lower.Split('_');
        if (parts.Length != 3 || parts[0] != "eye")
            return null;
        int side = parts[1] == "left" ? 0 : parts[1] == "right" ? 1 : -1;
        if (side < 0)
            return null;
        return parts[2] switch
        {
            "left" => (LookKind.Left, side),
            "right" => (LookKind.Right, side),
            "up" => (LookKind.Up, side),
            "down" => (LookKind.Down, side),
            _ => null,
        };
    }

    private static int SideOf(string lower)
    {
        if (lower.Contains("left")) return 0;
        if (lower.Contains("right")) return 1;
        if (EndsWithSideToken(lower, 'l')) return 0;
        if (EndsWithSideToken(lower, 'r')) return 1;
        return -1;
    }

    private static bool EndsWithSideToken(string s, char side)
    {
        if (s.Length < 2 || s[s.Length - 1] != side)
            return false;
        char sep = s[s.Length - 2];
        return sep == '_' || sep == '.' || sep == '-' || sep == ' ';
    }
}
