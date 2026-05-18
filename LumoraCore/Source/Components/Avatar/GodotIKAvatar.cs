// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

/// <summary>
/// VR IK component that uses GodotIK (libik) for inverse kinematics.
/// Creates and manages GodotIK nodes on the Godot side via hooks.
/// </summary>
[ComponentCategory("Avatar")]
public class GodotIKAvatar : ImplementableComponent
{
    // ===== TRACKING TARGETS =====

    public readonly SyncRef<Slot> HeadTarget = new();
    public readonly SyncRef<Slot> LeftHandTarget = new();
    public readonly SyncRef<Slot> RightHandTarget = new();
    public readonly SyncRef<Slot> LeftFootTarget = new();
    public readonly SyncRef<Slot> RightFootTarget = new();

    // ===== SKELETON =====

    public readonly SyncRef<SkeletonBuilder> Skeleton = new();
    public readonly SyncRef<UserRoot> UserRoot = new();

    // ===== BODY POSITIONING SETTINGS =====

    public readonly Sync<float> HipHeight = new();
    public readonly Sync<bool> Enabled = new();

    /// <summary>Forward lean of spine in degrees.</summary>
    public readonly Sync<float> SpineTilt = new();

    /// <summary>How far hips sit behind the head center.</summary>
    public readonly Sync<float> HipsBackOffset = new();

    // ===== FOOT ZONE SETTINGS =====

    /// <summary>Where the foot zone sits relative to the head (x=side, y=down, z=back).</summary>
    public readonly Sync<float3> FootZoneOffset = new();

    /// <summary>Left/right foot spread distance.</summary>
    public readonly Sync<float> FootSeparation = new();

    /// <summary>Hover distance above detected ground.</summary>
    public readonly Sync<float> FootHoverHeight = new();

    /// <summary>Length of downward ground detection raycast.</summary>
    public readonly Sync<float> GroundRaycastRange = new();

    // ===== GROUND DETECTION FEEDBACK (written by GodotIKAvatarHook) =====

    /// <summary>Ground Y under left foot — written by the Godot-side hook, read by ProceduralLegs.</summary>
    public readonly Sync<float> LeftFootGroundY = new();

    /// <summary>Ground Y under right foot — written by the Godot-side hook, read by ProceduralLegs.</summary>
    public readonly Sync<float> RightFootGroundY = new();

    // ===== BONE REFERENCES =====

    private Slot _hips;
    private Slot _spine;
    private Slot _head;
    private Slot _leftUpperArm;
    private Slot _leftLowerArm;
    private Slot _leftHand;
    private Slot _rightUpperArm;
    private Slot _rightLowerArm;
    private Slot _rightHand;
    private Slot _leftUpperLeg;
    private Slot _leftLowerLeg;
    private Slot _leftFoot;
    private Slot _rightUpperLeg;
    private Slot _rightLowerLeg;
    private Slot _rightFoot;

    private bool _initialized;

    private const float Deg2Rad = MathF.PI / 180f;

    public override void OnInit()
    {
        base.OnInit();
        HipHeight.Value = 0.95f;
        Enabled.Value = true;
        SpineTilt.Value = 20f;
        HipsBackOffset.Value = 0.2f;
        FootZoneOffset.Value = new float3(0f, -1.4f, -0.25f);
        FootSeparation.Value = 0.3f;
        FootHoverHeight.Value = 0.15f;
        GroundRaycastRange.Value = 0.65f;
        // LeftFootGroundY = 0f (C# default, skip)
        // RightFootGroundY = 0f (C# default, skip)
    }

    public override void OnStart()
    {
        base.OnStart();
        TryInitialize();
    }

    private void TryInitialize()
    {
        if (_initialized) return;
        if (Skeleton.Target == null || !Skeleton.Target.IsBuilt.Value) return;

        // Get bone references
        var skel = Skeleton.Target;
        _hips = skel.GetBoneSlot("Hips");
        _spine = skel.GetBoneSlot("Spine");
        _head = skel.GetBoneSlot("Head");

        _leftUpperArm = skel.GetBoneSlot("LeftUpperArm");
        _leftLowerArm = skel.GetBoneSlot("LeftLowerArm");
        _leftHand = skel.GetBoneSlot("LeftHand");

        _rightUpperArm = skel.GetBoneSlot("RightUpperArm");
        _rightLowerArm = skel.GetBoneSlot("RightLowerArm");
        _rightHand = skel.GetBoneSlot("RightHand");

        _leftUpperLeg = skel.GetBoneSlot("LeftUpperLeg");
        _leftLowerLeg = skel.GetBoneSlot("LeftLowerLeg");
        _leftFoot = skel.GetBoneSlot("LeftFoot");

        _rightUpperLeg = skel.GetBoneSlot("RightUpperLeg");
        _rightLowerLeg = skel.GetBoneSlot("RightLowerLeg");
        _rightFoot = skel.GetBoneSlot("RightFoot");

        _initialized = _hips != null;

        if (_initialized)
            LumoraLogger.Log("GodotIKAvatar: Initialized with skeleton");
        else
            LumoraLogger.Warn("GodotIKAvatar: Failed to initialize - missing hips bone");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (!Enabled.Value) return;

        // Always push updates to the hook so fallback IK (direct Skeleton3D binding)
        // keeps receiving tracker targets even when no SkeletonBuilder exists.
        RunApplyChanges();

        if (!_initialized)
        {
            TryInitialize();
            return;
        }

        // Update body orientation and basic positioning
        UpdateBody();
    }

    private void UpdateBody()
    {
        var headSlot = HeadTarget.Target;
        if (headSlot == null || _hips == null) return;

        // Get head position and facing direction
        float3 headPos = headSlot.GlobalPosition;
        floatQ headRot = headSlot.GlobalRotation;

        // Body faces where head faces (horizontal only)
        float3 headForward = GetBodyForward();
        floatQ bodyRot = floatQ.LookRotation(headForward, float3.Up);

        // Hips position: below head, pushed backward behind head center
        // Godot: forward is -Z, so backward is +Z in local space
        float3 hipsPos = headPos
            + new float3(0f, -HipHeight.Value, 0f)
            + bodyRot * new float3(0f, 0f, HipsBackOffset.Value);
        _hips.GlobalPosition = hipsPos;

        // Hips rotation: body direction + forward spine tilt
        floatQ spineTiltRot = floatQ.AxisAngle(float3.Right, SpineTilt.Value * Deg2Rad);
        _hips.GlobalRotation = bodyRot * spineTiltRot;

        // Head bone follows tracker exactly
        if (_head != null)
            _head.GlobalRotation = headRot;
    }

    // ===== PUBLIC HELPER METHODS (used by ProceduralLegs and GodotIKAvatarHook) =====

    /// <summary>
    /// Returns the horizontal body forward direction derived from the head tracker.
    /// </summary>
    public float3 GetBodyForward()
    {
        var headSlot = HeadTarget.Target;
        if (headSlot == null) return float3.Backward;

        float3 forward = headSlot.GlobalRotation * float3.Backward; // Godot: -Z is forward
        forward.y = 0f;
        if (forward.LengthSquared < 0.001f)
            return float3.Backward;
        return forward.Normalized;
    }

    /// <summary>
    /// Ideal world position for the left foot zone, using configured offsets and detected ground Y.
    /// </summary>
    public float3 GetLeftFootIdealPosition()
    {
        return GetFootIdealPosition(isLeft: true);
    }

    /// <summary>
    /// Ideal world position for the right foot zone, using configured offsets and detected ground Y.
    /// </summary>
    public float3 GetRightFootIdealPosition()
    {
        return GetFootIdealPosition(isLeft: false);
    }

    private float3 GetFootIdealPosition(bool isLeft)
    {
        var headSlot = HeadTarget.Target;
        if (headSlot == null) return float3.Zero;

        float3 headPos = headSlot.GlobalPosition;
        floatQ bodyRot = floatQ.LookRotation(GetBodyForward(), float3.Up);
        float3 right = bodyRot * float3.Right;

        float3 offset = FootZoneOffset.Value;
        float sideOffset = FootSeparation.Value * 0.5f * (isLeft ? -1f : 1f);
        float groundY = isLeft ? LeftFootGroundY.Value : RightFootGroundY.Value;

        // Build position: head + Y drop + forward offset + side offset
        float3 pos = headPos
            + new float3(0f, offset.y, 0f)                            // vertical drop
            + (bodyRot * new float3(0f, 0f, offset.z))               // forward/back offset
            + (right * sideOffset);                                    // left/right spread

        // Snap to ground + hover
        pos.y = groundY + FootHoverHeight.Value;
        return pos;
    }

    /// <summary>Returns the current left foot target rotation for the IK hook.</summary>
    public floatQ GetLeftFootTargetRotation() => LeftFootTarget.Target?.GlobalRotation ?? floatQ.Identity;

    /// <summary>Returns the current right foot target rotation for the IK hook.</summary>
    public floatQ GetRightFootTargetRotation() => RightFootTarget.Target?.GlobalRotation ?? floatQ.Identity;

    /// <summary>
    /// Setup tracking targets from UserRoot.
    /// </summary>
    public void SetupTracking(UserRoot userRoot)
    {
        if (userRoot == null) return;

        UserRoot.Target = userRoot;
        HeadTarget.Target = userRoot.HeadSlot;
        LeftHandTarget.Target = userRoot.LeftHandSlot;
        RightHandTarget.Target = userRoot.RightHandSlot;
        LeftFootTarget.Target = userRoot.LeftFootSlot;
        RightFootTarget.Target = userRoot.RightFootSlot;

        LumoraLogger.Log("GodotIKAvatar: Tracking setup complete");
    }

    // ===== GETTERS FOR HOOK =====

    public float3 GetHeadTargetPosition() => HeadTarget.Target?.GlobalPosition ?? float3.Zero;
    public floatQ GetHeadTargetRotation() => HeadTarget.Target?.GlobalRotation ?? floatQ.Identity;

    public float3 GetLeftHandTargetPosition() => LeftHandTarget.Target?.GlobalPosition ?? float3.Zero;
    public floatQ GetLeftHandTargetRotation() => LeftHandTarget.Target?.GlobalRotation ?? floatQ.Identity;

    public float3 GetRightHandTargetPosition() => RightHandTarget.Target?.GlobalPosition ?? float3.Zero;
    public floatQ GetRightHandTargetRotation() => RightHandTarget.Target?.GlobalRotation ?? floatQ.Identity;

    public float3 GetLeftFootTargetPosition() => LeftFootTarget.Target?.GlobalPosition ?? float3.Zero;
    public float3 GetRightFootTargetPosition() => RightFootTarget.Target?.GlobalPosition ?? float3.Zero;

    public Slot GetHips() => _hips;
    public Slot GetLeftUpperArm() => _leftUpperArm;
    public Slot GetRightUpperArm() => _rightUpperArm;
    public Slot GetLeftUpperLeg() => _leftUpperLeg;
    public Slot GetRightUpperLeg() => _rightUpperLeg;
}
