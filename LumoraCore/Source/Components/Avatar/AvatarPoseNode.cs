using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

/// <summary>
/// A component that receives pose data from an AvatarObjectSlot and drives a slot's transform.
/// This is the key component that bridges tracking data to avatar bones.
/// </summary>
[ComponentCategory("Users/Common Avatar System")]
[DefaultUpdateOrder(-7500)]
public class AvatarPoseNode : Component, IAvatarObject, IInputUpdateReceiver
{
    /// <summary>
    /// The body node this pose node corresponds to.
    /// </summary>
    public Sync<BodyNode> Node { get; private set; }

    /// <summary>
    /// Priority for equipping order (higher = later).
    /// </summary>
    public Sync<int> EquipOrderPriority_ { get; private set; }

    /// <summary>
    /// Whether to run the update after input update instead of before.
    /// </summary>
    public Sync<bool> RunAfterInputUpdate { get; private set; }

    /// <summary>
    /// Body nodes that cannot be equipped simultaneously.
    /// </summary>
    public SyncList<BodyNode> MutuallyExclusiveNodes_ { get; private set; }

    /// <summary>
    /// Output: Whether tracking is currently active.
    /// </summary>
    public Sync<bool> IsTracking { get; private set; }

    /// <summary>
    /// Output: Whether the source slot is tracking.
    /// </summary>
    public Sync<bool> SourceIsTracking { get; private set; }

    /// <summary>
    /// Output: Whether the source slot is active.
    /// </summary>
    public Sync<bool> SourceIsActive { get; private set; }

    // Internal references
    protected SyncRef<AvatarObjectSlot> _objectSlot;
    protected SyncRef<Slot> _source;

    // Field drives for position/rotation
    protected FieldDrive<float3> _position;
    protected FieldDrive<floatQ> _rotation;
    protected FieldDrive<float3> _scale;
    protected FieldDrive<bool> _active;

    // Internal state
    private bool _isRegistered;

    // IAvatarObject implementation
    BodyNode IAvatarObject.Node => Node.Value;
    public bool IsEquipped => EquippingSlot != null;
    public int EquipOrderPriority => EquipOrderPriority_.Value;
    public AvatarObjectSlot EquippingSlot => _objectSlot?.Target;
    public IEnumerable<BodyNode> MutuallyExclusiveNodes => MutuallyExclusiveNodes_;
    public User ExplicitlyAllowedUser { get; private set; }

    /// <summary>
    /// Whether equipped and the source is active.
    /// </summary>
    public bool IsEquippedAndActive => IsEquipped && SourceIsActive.Value;

    /// <summary>
    /// Whether tracking and the source is active.
    /// </summary>
    public bool IsTrackingAndActive => IsTracking.Value && SourceIsActive.Value;

    /// <summary>
    /// Whether this node can be equipped (slot position/rotation not already driven).
    /// </summary>
    public bool CanEquip => !Slot.LocalPosition.IsDriven && !Slot.LocalRotation.IsDriven;

    public override void OnAwake()
    {
        base.OnAwake();

        Node = new Sync<BodyNode>(this, BodyNode.NONE);
        EquipOrderPriority_ = new Sync<int>(this, 0);
        RunAfterInputUpdate = new Sync<bool>(this, false);
        MutuallyExclusiveNodes_ = new SyncList<BodyNode>(this);
        IsTracking = new Sync<bool>(this, false);
        SourceIsTracking = new Sync<bool>(this, false);
        SourceIsActive = new Sync<bool>(this, false);

        _objectSlot = new SyncRef<AvatarObjectSlot>(this, null);
        _source = new SyncRef<Slot>(this, null);

        _position = new FieldDrive<float3>(World);
        _rotation = new FieldDrive<floatQ>(World);
        _scale = new FieldDrive<float3>(World);
        _active = new FieldDrive<bool>(World);
    }

    public override void OnStart()
    {
        base.OnStart();

        // Register for input updates
        var input = Engine.Current?.InputInterface;
        if (input != null)
        {
            input.RegisterInputEventReceiver(this);
            _isRegistered = true;
        }

        EnsureCorrectUpdateOrder();
    }

    public override void OnDestroy()
    {
        // Unregister from input updates
        if (_isRegistered)
        {
            var input = Engine.Current?.InputInterface;
            input?.UnregisterInputEventReceiver(this);
            _isRegistered = false;
        }

        ExplicitlyAllowedUser = null;
        base.OnDestroy();
    }

    /// <summary>
    /// Ensure update order is correct relative to parent AvatarPoseNodes.
    /// </summary>
    private void EnsureCorrectUpdateOrder(bool updateChildren = true)
    {
        // Find parent AvatarPoseNode
        var parent = Slot.Parent;
        while (parent != null)
        {
            var parentNode = parent.GetComponent<AvatarPoseNode>();
            if (parentNode != null && parentNode != this)
            {
                UpdateOrder = parentNode.UpdateOrder + 10;
                break;
            }
            parent = parent.Parent;
        }

        // Update children
        if (updateChildren)
        {
            foreach (var child in Slot.Children)
            {
                var childNode = child.GetComponent<AvatarPoseNode>();
                childNode?.EnsureCorrectUpdateOrder(updateChildren: false);
            }
        }
    }

    /// <summary>
    /// Dequip this pose node from its current slot.
    /// </summary>
    public void Dequip()
    {
        _objectSlot.Target = null;
        _source.Target = null;
        _position.ReleaseLink();
        _rotation.ReleaseLink();
        _scale.ReleaseLink();
        _active.ReleaseLink();

        AquaLogger.Log($"AvatarPoseNode: Dequipped {Node.Value} from '{Slot.SlotName.Value}'");
    }

    /// <summary>
    /// Equip this pose node to an AvatarObjectSlot.
    /// </summary>
    public void Equip(AvatarObjectSlot slot)
    {
        _objectSlot.Target = slot;
        _source.Target = slot.Slot;

        // Drive slot position and rotation
        _position.DriveTarget(Slot.LocalPosition);
        _rotation.DriveTarget(Slot.LocalRotation);

        // Optionally drive scale
        if (slot.DriveScale.Value)
        {
            _scale.DriveTarget(Slot.LocalScale);
        }

        // Optionally drive active state
        if (slot.DriveActive.Value)
        {
            _active.DriveTarget(Slot.ActiveSelf);
        }

        AquaLogger.Log($"AvatarPoseNode: Equipped {Node.Value} to '{Slot.SlotName.Value}'");
    }

    /// <summary>
    /// Explicitly allow a user to equip this node.
    /// </summary>
    public void ExplicitlyAllowEquip(User user)
    {
        if (ExplicitlyAllowedUser != null && user != ExplicitlyAllowedUser)
        {
            throw new InvalidOperationException("Another user has already been assigned!");
        }
        ExplicitlyAllowedUser = user;
    }

    /// <summary>
    /// Run the pose update - called by BeforeInputUpdate or AfterInputUpdate.
    /// </summary>
    private void RunUpdate()
    {
        if (_source?.Target != null && EquippingSlot != null)
        {
            // Get filtered pose data from the slot
            float3 position;
            floatQ rotation;
            bool isTracking;
            var space = EquippingSlot.GetFilteredPoseData(out position, out rotation, out isTracking);

            // Update outputs
            IsTracking.Value = isTracking;
            SourceIsTracking.Value = EquippingSlot.IsTracking.Value;
            SourceIsActive.Value = EquippingSlot.IsActive.Value;

            // Transform position from user space to local space
            if (Slot.Parent != null)
            {
                // Convert from user root space to slot's parent space
                // First to global, then to local
                var globalPos = space.GlobalPosition + (space.GlobalRotation * position);
                var globalRot = space.GlobalRotation * rotation;

                // Then to local space
                Slot.LocalPosition.Value = Slot.Parent.GlobalPointToLocal(globalPos);
                Slot.LocalRotation.Value = Slot.Parent.GlobalRotation.Inverse * globalRot;
            }
            else
            {
                // No parent, use directly
                Slot.LocalPosition.Value = position;
                Slot.LocalRotation.Value = rotation;
            }

            // Update scale if driven
            if (_scale.IsActive)
            {
                Slot.LocalScale.Value = EquippingSlot.Slot.LocalScale.Value;
            }

            // Update active if driven
            if (_active.IsActive)
            {
                Slot.ActiveSelf.Value = EquippingSlot.Slot.ActiveSelf.Value;
            }
        }
        else
        {
            // Not equipped
            IsTracking.Value = false;
            SourceIsTracking.Value = false;

            // Release drives if under local user
            if (IsUnderLocalUser)
            {
                _position.ReleaseLink();
                _rotation.ReleaseLink();
                _scale.ReleaseLink();
                _active.ReleaseLink();
            }
        }
    }

    /// <summary>
    /// Check if this component is under the local user.
    /// </summary>
    private bool IsUnderLocalUser
    {
        get
        {
            var userRoot = Slot.GetComponent<UserRoot>();
            var current = Slot.Parent;
            while (userRoot == null && current != null)
            {
                userRoot = current.GetComponent<UserRoot>();
                current = current.Parent;
            }
            return userRoot?.ActiveUser == World?.LocalUser;
        }
    }

    /// <summary>
    /// Called before input update.
    /// </summary>
    public void BeforeInputUpdate()
    {
        if (!RunAfterInputUpdate.Value)
        {
            RunUpdate();
        }
    }

    /// <summary>
    /// Called after input update.
    /// </summary>
    public void AfterInputUpdate()
    {
        if (RunAfterInputUpdate.Value)
        {
            RunUpdate();
        }
    }
}
