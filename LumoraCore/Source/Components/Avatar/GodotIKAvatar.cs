using System;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

/// <summary>
/// VR IK component that uses GodotIK (libik) for inverse kinematics.
/// Creates and manages GodotIK nodes on the Godot side via hooks.
/// </summary>
[ComponentCategory("Avatar")]
public class GodotIKAvatar : ImplementableComponent
{
    // ===== TRACKING TARGETS =====

    public SyncRef<Slot> HeadTarget { get; private set; }
    public SyncRef<Slot> LeftHandTarget { get; private set; }
    public SyncRef<Slot> RightHandTarget { get; private set; }
    public SyncRef<Slot> LeftFootTarget { get; private set; }
    public SyncRef<Slot> RightFootTarget { get; private set; }

    // ===== SKELETON =====

    public SyncRef<SkeletonBuilder> Skeleton { get; private set; }
    public SyncRef<UserRoot> UserRoot { get; private set; }

    // ===== SETTINGS =====

    public Sync<float> HipHeight { get; private set; }
    public Sync<bool> Enabled { get; private set; }

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

    public override void OnAwake()
    {
        base.OnAwake();

        HeadTarget = new SyncRef<Slot>(this, null);
        LeftHandTarget = new SyncRef<Slot>(this, null);
        RightHandTarget = new SyncRef<Slot>(this, null);
        LeftFootTarget = new SyncRef<Slot>(this, null);
        RightFootTarget = new SyncRef<Slot>(this, null);

        Skeleton = new SyncRef<SkeletonBuilder>(this, null);
        UserRoot = new SyncRef<UserRoot>(this, null);

        HipHeight = new Sync<float>(this, 0.95f);
        Enabled = new Sync<bool>(this, true);
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
            AquaLogger.Log("GodotIKAvatar: Initialized with skeleton");
        else
            AquaLogger.Warn("GodotIKAvatar: Failed to initialize - missing hips bone");
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (!_initialized)
        {
            TryInitialize();
            return;
        }

        if (!Enabled.Value) return;

        // Run hook to sync with Godot IK
        RunApplyChanges();

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
        float3 headForward = headRot * float3.Backward; // Godot: -Z forward
        headForward.y = 0;
        if (headForward.LengthSquared < 0.001f)
            headForward = float3.Backward;
        headForward = headForward.Normalized;

        // Hips position: below head
        float3 hipsPos = headPos - new float3(0, HipHeight.Value, 0);
        _hips.GlobalPosition = hipsPos;

        // Hips rotation: face same direction as head (horizontal)
        floatQ bodyRot = floatQ.LookRotation(headForward, float3.Up);
        _hips.GlobalRotation = bodyRot;

        // Head bone follows target exactly
        if (_head != null)
        {
            _head.GlobalRotation = headRot;
        }
    }

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

        AquaLogger.Log("GodotIKAvatar: Tracking setup complete");
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
