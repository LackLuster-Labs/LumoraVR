using System;
using Lumora.Core;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Container component that marks a slot as belonging to a user.
/// Provides access to avatar body nodes and manages user transforms.
/// </summary>
[ComponentCategory("Users")]
public class UserRoot : Component
{
    /// <summary>
    /// User node types for positioning and targeting.
    /// </summary>
    public enum UserNode
    {
        None,
        Root,
        Head,
        Body,
        LeftHand,
        RightHand,
        LeftFoot,
        RightFoot
    }

    // ===== USER REFERENCE =====
    /// <summary>
    /// Synced reference to the user that owns this UserRoot.
    /// This syncs over the network so clients can identify their own UserRoot.
    /// </summary>
    public readonly SyncRef<User> TargetUser;

    private bool _isRegistered = false;

    /// <summary>
    /// The User that owns this UserRoot.
    /// </summary>
    public User ActiveUser => TargetUser?.Target;

    /// <summary>
    /// Check if this UserRoot belongs to the local user.
    /// Uses direct object reference comparison.
    /// </summary>
    public bool IsLocalUserRoot => TargetUser?.Target != null && TargetUser.Target == World?.LocalUser;

    // ===== CACHED BODY NODES =====
    private Slot _cachedHeadSlot;
    private Slot _cachedBodySlot;
    private Slot _cachedLeftHandSlot;
    private Slot _cachedRightHandSlot;
    private Slot _cachedLeftFootSlot;
    private Slot _cachedRightFootSlot;

    // ===== BODY NODE ACCESSORS =====

    /// <summary>
    /// Get the head slot. Cached for performance.
    /// </summary>
    public Slot HeadSlot
    {
        get
        {
            if ((_cachedHeadSlot == null || _cachedHeadSlot.IsDestroyed) && !IsDestroyed)
            {
                // Prefer Body Nodes tracking slots, then fall back to avatar bones
                _cachedHeadSlot = Slot.FindChild("Body Nodes", recursive: false)?.FindChild("Head", recursive: false) ??
                                  Slot.FindChild("Head", recursive: false) ??
                                  Slot.FindChild("Avatar", recursive: false)?.FindChild("Head", recursive: false);
            }
            return _cachedHeadSlot;
        }
    }

    /// <summary>
    /// Get the body slot.
    /// </summary>
    public Slot BodySlot
    {
        get
        {
            if ((_cachedBodySlot == null || _cachedBodySlot.IsDestroyed) && !IsDestroyed)
            {
                _cachedBodySlot = Slot.FindChild("Avatar", recursive: false)?.FindChild("Body", recursive: false);
            }
            return _cachedBodySlot;
        }
    }

    /// <summary>
    /// Get the left hand slot.
    /// </summary>
    public Slot LeftHandSlot
    {
        get
        {
            if ((_cachedLeftHandSlot == null || _cachedLeftHandSlot.IsDestroyed) && !IsDestroyed)
            {
                _cachedLeftHandSlot =
                    Slot.FindChild("Body Nodes", recursive: false)?.FindChild("LeftHand", recursive: false) ??
                    Slot.FindChild("LeftHand", recursive: false) ??
                    Slot.FindChild("Avatar", recursive: false)?.FindChild("LeftHand", recursive: false);
            }
            return _cachedLeftHandSlot;
        }
    }

    /// <summary>
    /// Get the right hand slot.
    /// </summary>
    public Slot RightHandSlot
    {
        get
        {
            if ((_cachedRightHandSlot == null || _cachedRightHandSlot.IsDestroyed) && !IsDestroyed)
            {
                _cachedRightHandSlot =
                    Slot.FindChild("Body Nodes", recursive: false)?.FindChild("RightHand", recursive: false) ??
                    Slot.FindChild("RightHand", recursive: false) ??
                    Slot.FindChild("Avatar", recursive: false)?.FindChild("RightHand", recursive: false);
            }
            return _cachedRightHandSlot;
        }
    }

    /// <summary>
    /// Get the left foot slot.
    /// </summary>
    public Slot LeftFootSlot
    {
        get
        {
            if ((_cachedLeftFootSlot == null || _cachedLeftFootSlot.IsDestroyed) && !IsDestroyed)
            {
                _cachedLeftFootSlot =
                    Slot.FindChild("Body Nodes", recursive: false)?.FindChild("LeftFoot", recursive: false) ??
                    Slot.FindChild("LeftFoot", recursive: false) ??
                    Slot.FindChild("Avatar", recursive: false)?.FindChild("LeftFoot", recursive: false);
            }
            return _cachedLeftFootSlot;
        }
    }

    /// <summary>
    /// Get the right foot slot.
    /// </summary>
    public Slot RightFootSlot
    {
        get
        {
            if ((_cachedRightFootSlot == null || _cachedRightFootSlot.IsDestroyed) && !IsDestroyed)
            {
                _cachedRightFootSlot =
                    Slot.FindChild("Body Nodes", recursive: false)?.FindChild("RightFoot", recursive: false) ??
                    Slot.FindChild("RightFoot", recursive: false) ??
                    Slot.FindChild("Avatar", recursive: false)?.FindChild("RightFoot", recursive: false);
            }
            return _cachedRightFootSlot;
        }
    }

    // ===== POSITION ACCESSORS =====

    /// <summary>
    /// Head position in world space.
    /// </summary>
    public float3 HeadPosition
    {
        get => HeadSlot?.GlobalPosition ?? Slot.GlobalPosition;
        set
        {
            if (HeadSlot != null)
            {
                var offset = value - HeadPosition;
                Slot.GlobalPosition += offset;
            }
        }
    }

    /// <summary>
    /// Head rotation in world space.
    /// </summary>
    public floatQ HeadRotation
    {
        get => HeadSlot?.GlobalRotation ?? Slot.GlobalRotation;
        set
        {
            if (HeadSlot != null)
            {
                // TODO: Platform driver - Rotation delta calculation
                // var currentPos = HeadPosition;
                // var rotationDelta = value * HeadRotation.Inverse();
                // Slot.GlobalTransform = new Transform3D(
                // 	Slot.GlobalTransform.Basis * new Basis(rotationDelta),
                // 	Slot.GlobalPosition
                // );
                // HeadPosition = currentPos; // Restore head position
                HeadSlot.GlobalRotation = value;
            }
        }
    }

    /// <summary>
    /// Feet position in world space (center between feet).
    /// </summary>
    public float3 FeetPosition
    {
        get
        {
            if (LeftFootSlot != null && RightFootSlot != null)
            {
                return (LeftFootSlot.GlobalPosition + RightFootSlot.GlobalPosition) / 2f;
            }
            // Fallback: project head position to ground
            var headPos = HeadPosition;
            return new float3(headPos.x, Slot.GlobalPosition.y, headPos.z);
        }
        set
        {
            var offset = value - FeetPosition;
            Slot.GlobalPosition += offset;
        }
    }

    /// <summary>
    /// Global scale of this UserRoot.
    /// </summary>
    public float GlobalScale
    {
        get => Slot.Scale.x;
        set
        {
            Slot.Scale = float3.One * value;
        }
    }

    /// <summary>
    /// Check if we've received first positional tracking data.
    /// Used to know when VR tracking has started.
    /// </summary>
    public bool ReceivedFirstPositionalData
    {
        get
        {
            if (HeadSlot != null)
            {
                var headPos = HeadSlot.LocalPosition.Value;
                var headRot = HeadSlot.LocalRotation.Value;
                // If head has moved from default position, we have tracking
                if (headPos != float3.Zero || headRot != floatQ.Identity)
                    return true;
            }

            // Check if we have a TrackedDevicePositioner that is tracking
            var headPositioner = HeadSlot?.GetComponent<TrackedDevicePositioner>();
            if (headPositioner != null)
                return headPositioner.IsTracking.Value;

            // For desktop mode, we consider it always tracked
            if (ActiveUser != null && Engine.Current?.InputInterface != null)
            {
                var headDevice = Engine.Current.InputInterface.HeadDevice;
                return headDevice == null || !headDevice.IsTracked; // Desktop mode
            }

            return false;
        }
    }

    // ===== INITIALIZATION =====

    /// <summary>
    /// Initialize this UserRoot with a User.
    /// Called by SimpleUserSpawn after attaching the component.
    /// Sets TargetUser which syncs to clients.
    /// </summary>
    public void Initialize(User user)
    {
        if (user == null)
        {
            AquaLogger.Error("UserRoot: Cannot initialize with null user");
            return;
        }

        // Set the synced reference - this will sync to clients
        TargetUser.Target = user;

        // Register with user on authority
        if (World?.IsAuthority == true)
        {
            user.Root = this;
            AquaLogger.Log($"User: Registered UserRoot for authority user '{user.UserName.Value}'");
        }

        AquaLogger.Log($"UserRoot: Initialized for user '{user.UserName.Value}' (RefID: {user.ReferenceID})");
    }

    /// <summary>
    /// Called when synced fields change. Handles client-side local user detection.
    /// Simple direct object reference comparison.
    /// </summary>
    public override void OnChanges()
    {
        base.OnChanges();

        // Simple direct reference comparison
        if (TargetUser.Target == World?.LocalUser && !_isRegistered)
        {
            World.LocalUser.Root = this;
            _isRegistered = true;
            AquaLogger.Log($"UserRoot: Registered as Root for local user '{TargetUser.Target?.UserName?.Value}'");
        }

        if (TargetUser.Target != World?.LocalUser && _isRegistered)
        {
            if (World?.LocalUser?.Root == this)
            {
                World.LocalUser.Root = null;
            }
            _isRegistered = false;
        }
    }

    /// <summary>
    /// Get the global position of a user node.
    /// </summary>
    public float3 GetGlobalPosition(UserNode node)
    {
        return node switch
        {
            UserNode.None => float3.Zero,
            UserNode.Root => Slot.GlobalPosition,
            UserNode.Head => HeadSlot?.GlobalPosition ?? Slot.GlobalPosition,
            UserNode.Body => BodySlot?.GlobalPosition ?? Slot.GlobalPosition,
            UserNode.LeftHand => LeftHandSlot?.GlobalPosition ?? Slot.GlobalPosition,
            UserNode.RightHand => RightHandSlot?.GlobalPosition ?? Slot.GlobalPosition,
            UserNode.LeftFoot => LeftFootSlot?.GlobalPosition ?? Slot.GlobalPosition,
            UserNode.RightFoot => RightFootSlot?.GlobalPosition ?? Slot.GlobalPosition,
            _ => throw new ArgumentException($"Invalid UserNode: {node}")
        };
    }

    /// <summary>
    /// Get the global rotation of a user node.
    /// </summary>
    public floatQ GetGlobalRotation(UserNode node)
    {
        return node switch
        {
            UserNode.None => floatQ.Identity,
            UserNode.Root => Slot.GlobalRotation,
            UserNode.Head => HeadSlot?.GlobalRotation ?? floatQ.Identity,
            UserNode.Body => BodySlot?.GlobalRotation ?? floatQ.Identity,
            UserNode.LeftHand => LeftHandSlot?.GlobalRotation ?? floatQ.Identity,
            UserNode.RightHand => RightHandSlot?.GlobalRotation ?? floatQ.Identity,
            UserNode.LeftFoot => LeftFootSlot?.GlobalRotation ?? floatQ.Identity,
            UserNode.RightFoot => RightFootSlot?.GlobalRotation ?? floatQ.Identity,
            _ => throw new ArgumentException($"Invalid UserNode: {node}")
        };
    }

    /// <summary>
    /// Forward direction the user is facing (from head), flattened for locomotion.
    /// </summary>
    public float3 HeadFacingDirection
    {
        get
        {
            floatQ headRot = HeadSlot?.GlobalRotation ?? Slot.GlobalRotation;
            // Godot uses -Z as forward; align locomotion basis accordingly
            float3 forward = headRot * float3.Backward;
            forward.y = 0;
            return forward.LengthSquared < 1e-6f ? float3.Backward : forward.Normalized;
        }
    }

    /// <summary>
    /// Full head-facing rotation (uses head slot if available).
    /// </summary>
    public floatQ HeadFacingRotation
    {
        get
        {
            if (HeadSlot != null)
                return HeadSlot.GlobalRotation;
            return Slot.GlobalRotation;
        }
    }

    /// <summary>
    /// Set the global position of a user node.
    /// </summary>
    public void SetGlobalPosition(UserNode node, float3 position)
    {
        switch (node)
        {
            case UserNode.Root:
                Slot.GlobalPosition = position;
                break;
            case UserNode.Head:
                HeadPosition = position;
                break;
            case UserNode.Body:
                if (BodySlot != null)
                    BodySlot.GlobalPosition = position;
                break;
            case UserNode.LeftHand:
                if (LeftHandSlot != null)
                    LeftHandSlot.GlobalPosition = position;
                break;
            case UserNode.RightHand:
                if (RightHandSlot != null)
                    RightHandSlot.GlobalPosition = position;
                break;
            case UserNode.LeftFoot:
                if (LeftFootSlot != null)
                    LeftFootSlot.GlobalPosition = position;
                break;
            case UserNode.RightFoot:
                if (RightFootSlot != null)
                    RightFootSlot.GlobalPosition = position;
                break;
        }
    }

    /// <summary>
    /// Called every frame to update the UserRoot.
    /// </summary>
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Validate scale to prevent invalid transforms
        var scale = Slot.Scale;
        if (scale.x <= 0 || scale.y <= 0 || scale.z <= 0 ||
            float.IsNaN(scale.x) || float.IsNaN(scale.y) || float.IsNaN(scale.z) ||
            float.IsInfinity(scale.x) || float.IsInfinity(scale.y) || float.IsInfinity(scale.z))
        {
            AquaLogger.Warn($"UserRoot: Invalid scale detected ({scale}), resetting to (1,1,1)");
            Slot.LocalScale.Value = float3.One;
        }

        // Ensure uniform scale (all axes same)
        if (System.Math.Abs(scale.x - scale.y) > 0.0001f || System.Math.Abs(scale.y - scale.z) > 0.0001f)
        {
            var avgScale = (scale.x + scale.y + scale.z) / 3f;
            Slot.Scale = float3.One * avgScale;
        }
    }

    /// <summary>
    /// Called when the component is destroyed.
    /// </summary>
    public override void OnDestroy()
    {
        AquaLogger.Log($"UserRoot: Destroying UserRoot for user '{ActiveUser?.UserName.Value ?? "Unknown"}'");

        // Unregister from user
        if (_isRegistered && World?.LocalUser?.Root == this)
        {
            World.LocalUser.Root = null;
        }
        _isRegistered = false;

        // Clear cached references
        _cachedHeadSlot = null;
        _cachedBodySlot = null;
        _cachedLeftHandSlot = null;
        _cachedRightHandSlot = null;
        _cachedLeftFootSlot = null;
        _cachedRightFootSlot = null;

        base.OnDestroy();
    }
}
