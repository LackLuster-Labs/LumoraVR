using System;
using Lumora.Core;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// VR Inverse Kinematics component for full-body avatar animation.
/// Drives avatar skeleton from head/hand/foot tracking data using Kinetix IK solver.
/// Standard VRIK avatar pattern.
/// </summary>
[ComponentCategory("Avatar")]
[DefaultUpdateOrder(-5000)] // Runs early - after tracking but before most other components
public class KinetixVRIK : ImplementableComponent
{
    // ===== REFERENCES =====

    /// <summary>
    /// Reference to the SkeletonBuilder that manages the avatar bones.
    /// </summary>
    public SyncRef<SkeletonBuilder> Skeleton { get; private set; }

    /// <summary>
    /// UserRoot that owns this avatar (for tracking references).
    /// </summary>
    public SyncRef<UserRoot> UserRoot { get; private set; }

    // ===== TRACKING TARGETS =====

    /// <summary>
    /// Head tracking target (from VR headset).
    /// </summary>
    public SyncRef<Slot> HeadTarget { get; private set; }

    /// <summary>
    /// Left hand tracking target (from VR controller).
    /// </summary>
    public SyncRef<Slot> LeftHandTarget { get; private set; }

    /// <summary>
    /// Right hand tracking target (from VR controller).
    /// </summary>
    public SyncRef<Slot> RightHandTarget { get; private set; }

    /// <summary>
    /// Left foot tracking target (optional, from tracker).
    /// </summary>
    public SyncRef<Slot> LeftFootTarget { get; private set; }

    /// <summary>
    /// Right foot tracking target (optional, from tracker).
    /// </summary>
    public SyncRef<Slot> RightFootTarget { get; private set; }

    // ===== IK SETTINGS =====

    /// <summary>
    /// Weight for head IK (0-1). 1 = fully driven by tracking.
    /// </summary>
    public Sync<float> HeadWeight { get; private set; }

    /// <summary>
    /// Weight for left hand IK (0-1).
    /// </summary>
    public Sync<float> LeftHandWeight { get; private set; }

    /// <summary>
    /// Weight for right hand IK (0-1).
    /// </summary>
    public Sync<float> RightHandWeight { get; private set; }

    /// <summary>
    /// Weight for left foot IK (0-1).
    /// </summary>
    public Sync<float> LeftFootWeight { get; private set; }

    /// <summary>
    /// Weight for right foot IK (0-1).
    /// </summary>
    public Sync<float> RightFootWeight { get; private set; }

    /// <summary>
    /// Whether to use procedural animation for feet when no tracking available.
    /// </summary>
    public Sync<bool> UseProceduralFeet { get; private set; }

    /// <summary>
    /// Whether the IK system is enabled.
    /// </summary>
    public Sync<bool> Enabled { get; private set; }

    // ===== INTERNAL STATE =====

    private bool _isInitialized = false;

    // ===== LIFECYCLE =====

    public override void OnAwake()
    {
        base.OnAwake();

        // Initialize sync fields
        Skeleton = new SyncRef<SkeletonBuilder>(this, null);
        UserRoot = new SyncRef<UserRoot>(this, null);

        HeadTarget = new SyncRef<Slot>(this, null);
        LeftHandTarget = new SyncRef<Slot>(this, null);
        RightHandTarget = new SyncRef<Slot>(this, null);
        LeftFootTarget = new SyncRef<Slot>(this, null);
        RightFootTarget = new SyncRef<Slot>(this, null);

        HeadWeight = new Sync<float>(this, 1.0f);
        LeftHandWeight = new Sync<float>(this, 1.0f);
        RightHandWeight = new Sync<float>(this, 1.0f);
        LeftFootWeight = new Sync<float>(this, 1.0f);
        RightFootWeight = new Sync<float>(this, 1.0f);

        UseProceduralFeet = new Sync<bool>(this, true);
        Enabled = new Sync<bool>(this, true);

        AquaLogger.Log($"KinetixVRIK: OnAwake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnStart()
    {
        base.OnStart();

        // Try to auto-find skeleton if not set
        if (Skeleton.Target == null)
        {
            Skeleton.Target = Slot.GetComponent<SkeletonBuilder>();
        }

        // Try to auto-find UserRoot if not set
        if (UserRoot.Target == null)
        {
            UserRoot.Target = Slot.GetComponent<UserRoot>();
            if (UserRoot.Target == null)
            {
                // Try parent slot
                UserRoot.Target = Slot.Parent?.GetComponent<UserRoot>();
            }
        }

        _isInitialized = Skeleton.Target != null;

        if (_isInitialized)
        {
            AquaLogger.Log($"KinetixVRIK: Started on slot '{Slot.SlotName.Value}' with skeleton '{Skeleton.Target.Slot.SlotName.Value}'");
        }
        else
        {
            AquaLogger.Warn($"KinetixVRIK: No SkeletonBuilder found on slot '{Slot.SlotName.Value}'");
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Late-init if skeleton was assigned after OnStart
        if (!_isInitialized && Skeleton.Target != null)
        {
            _isInitialized = true;
            AquaLogger.Log($"KinetixVRIK: Late-start with skeleton '{Skeleton.Target.Slot.SlotName.Value}'");
        }

        // CRITICAL: Always register for hook update every frame
        // This ensures the hook's ApplyChanges runs to:
        // 1. Resolve the skeleton if not found yet
        // 2. Update IK targets from tracking data
        // Without this, the hook never gets a chance to run!
        RunApplyChanges();

        if (!Enabled.Value || !_isInitialized)
            return;

        // IK solving happens in the hook
        // This component just maintains the targets and weights
    }

    public override void OnDestroy()
    {
        _isInitialized = false;
        base.OnDestroy();
        AquaLogger.Log($"KinetixVRIK: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    // ===== PUBLIC API =====

    /// <summary>
    /// Setup IK with tracking references from UserRoot.
    /// </summary>
    public void SetupTracking(UserRoot userRoot)
    {
        if (userRoot == null)
        {
            AquaLogger.Warn("KinetixVRIK: Cannot setup tracking with null UserRoot");
            return;
        }

        UserRoot.Target = userRoot;

        // Head tracking = HeadSlot from UserRoot
        HeadTarget.Target = userRoot.HeadSlot;

        // Hand tracking = LeftHandSlot/RightHandSlot from UserRoot
        LeftHandTarget.Target = userRoot.LeftHandSlot;
        RightHandTarget.Target = userRoot.RightHandSlot;

        // Foot tracking = LeftFootSlot/RightFootSlot from UserRoot (optional)
        LeftFootTarget.Target = userRoot.LeftFootSlot;
        RightFootTarget.Target = userRoot.RightFootSlot;

        AquaLogger.Log($"KinetixVRIK: Setup tracking from UserRoot '{userRoot.Slot.SlotName.Value}'");
    }

    /// <summary>
    /// Get the head target position in world space.
    /// </summary>
    public float3 GetHeadTargetPosition()
    {
        return HeadTarget.Target?.GlobalPosition ?? float3.Zero;
    }

    /// <summary>
    /// Get the head target rotation in world space.
    /// </summary>
    public floatQ GetHeadTargetRotation()
    {
        return HeadTarget.Target?.GlobalRotation ?? floatQ.Identity;
    }

    /// <summary>
    /// Get the left hand target position in world space.
    /// </summary>
    public float3 GetLeftHandTargetPosition()
    {
        return LeftHandTarget.Target?.GlobalPosition ?? float3.Zero;
    }

    /// <summary>
    /// Get the left hand target rotation in world space.
    /// </summary>
    public floatQ GetLeftHandTargetRotation()
    {
        return LeftHandTarget.Target?.GlobalRotation ?? floatQ.Identity;
    }

    /// <summary>
    /// Get the right hand target position in world space.
    /// </summary>
    public float3 GetRightHandTargetPosition()
    {
        return RightHandTarget.Target?.GlobalPosition ?? float3.Zero;
    }

    /// <summary>
    /// Get the right hand target rotation in world space.
    /// </summary>
    public floatQ GetRightHandTargetRotation()
    {
        return RightHandTarget.Target?.GlobalRotation ?? floatQ.Identity;
    }

    /// <summary>
    /// Get the left foot target position in world space.
    /// </summary>
    public float3 GetLeftFootTargetPosition()
    {
        if (LeftFootTarget.Target != null)
            return LeftFootTarget.Target.GlobalPosition;

        // Fallback to procedural position if no tracker
        if (UseProceduralFeet.Value)
            return GetProceduralFootPosition(true);

        return float3.Zero;
    }

    /// <summary>
    /// Get the right foot target position in world space.
    /// </summary>
    public float3 GetRightFootTargetPosition()
    {
        if (RightFootTarget.Target != null)
            return RightFootTarget.Target.GlobalPosition;

        // Fallback to procedural position if no tracker
        if (UseProceduralFeet.Value)
            return GetProceduralFootPosition(false);

        return float3.Zero;
    }

    /// <summary>
    /// Get procedural foot position based on hips position.
    /// Simple fallback when no foot tracking is available.
    /// </summary>
    private float3 GetProceduralFootPosition(bool isLeft)
    {
        if (Skeleton.Target == null || !Skeleton.Target.IsBuilt.Value)
            return float3.Zero;

        // Find hips bone
        var hipsSlot = Skeleton.Target.GetBoneSlot("Hips");
        if (hipsSlot == null)
            return float3.Zero;

        // Get hips position
        float3 hipsPos = hipsSlot.GlobalPosition;

        // Offset foot position from hips
        float footOffset = 0.2f; // Approximate hip width / 2
        float3 footPos = hipsPos;
        footPos.x += isLeft ? -footOffset : footOffset;
        footPos.y -= 0.9f; // Approximate leg length

        return footPos;
    }

    /// <summary>
    /// Check if IK is properly initialized and ready to solve.
    /// </summary>
    public bool IsInitialized()
    {
        return _isInitialized && Skeleton.Target != null && Skeleton.Target.IsBuilt.Value;
    }
}
