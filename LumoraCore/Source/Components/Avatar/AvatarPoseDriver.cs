// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Avatar;

// Equip only assigns the synced refs (_objectSlot/_source). Drive links are
// derived from those refs in RefreshDriveLinks on EVERY peer, and the drives
// write local-only values: each peer computes bone poses itself from the
// body-node transforms (which already replicate via tracking streams), so the
// results never generate sync traffic. Writing the fields directly here would
// double-send every bone every frame and fight the remote peer's own
// computation. - xlinka
[ComponentCategory("Users/Avatar")]
[DefaultUpdateOrder(500)]
public class AvatarPoseDriver : UserRootComponent, IAvatarEquippable
{
    public readonly Sync<BodyNode> Node = new();

    // higher = later
    [OldName("EquipOrderPriority_")]
    public readonly Sync<int> EquipPriority_ = new();

    // Retained for saved avatars; poses now refresh after movement in the component update.
    public readonly Sync<bool> RunAfterInputUpdate = new();

    public readonly SyncFieldList<BodyNode> ConflictingNodes_ = new();

    public readonly Sync<bool> IsTracking = new();

    public readonly Sync<bool> SourceIsTracking = new();

    public readonly Sync<bool> SourceIsActive = new();

    // Replicated equip state. Drive links below are derived from these. A copy starts unworn, never on
    // the wearer of the one it was copied from.
    [DontCopy]
    protected readonly SyncRef<AvatarSocket> _objectSlot = null!;
    [DontCopy]
    protected readonly SyncRef<Slot> _source = null!;

    // Drive links onto this slot's own transform. Declared members, so the link targets replicate and
    // persist with the component; the VALUES they push stay local-only (see LocalValueOnly).
    protected readonly FieldDrive<float3> _position = new() { LocalValueOnly = true };
    protected readonly FieldDrive<floatQ> _rotation = new() { LocalValueOnly = true };
    protected readonly FieldDrive<float3> _scale = new() { LocalValueOnly = true };
    protected readonly FieldDrive<bool> _active = new() { LocalValueOnly = true };


    // IAvatarEquippable implementation
    BodyNode IAvatarEquippable.Node => Node.Value;
    public bool IsEquipped => CurrentSocket != null;
    public int EquipPriority => EquipPriority_.Value;
    public AvatarSocket CurrentSocket => (_objectSlot?.Target) ?? null!;
    public IEnumerable<BodyNode> ConflictingNodes => ConflictingNodes_;
    public User AllowedEquipUser { get; private set; } = null!;

    public bool IsEquippedAndActive => IsEquipped && SourceIsActive.Value;

    public bool IsTrackingAndActive => IsTracking.Value && SourceIsActive.Value;

    public bool CanEquip => !Slot.LocalPosition.IsDriven && !Slot.LocalRotation.IsDriven;

    // Something other than this driver holds the slot's position or rotation, so a socket would move nothing.
    internal bool HeldElsewhere
        => Slot != null && (HeldByOther(Slot.LocalPosition.ActiveLink, _position) || HeldByOther(Slot.LocalRotation.ActiveLink, _rotation));

    private static bool HeldByOther(ILinkRef? link, ILinkRef own)
        => link != null && link.IsDriving && !ReferenceEquals(link, own);

    public override void OnInit()
    {
        base.OnInit();
        // BodyNode.NONE may not be enum value 0 - set explicitly
        Node.Value = BodyNode.NONE;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (UpdateOrder >= -7500 && UpdateOrder < -5000)
            UpdateOrder += 8000;

        EnsureCorrectUpdateOrder();
        RefreshDriveLinks();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        // _source replicates, and the machine that owns this driver aims the drive links from it; the links
        // are replicated members themselves and reach every other peer the same way, so remote bones are
        // driven (and excluded from inbound field writes) exactly like local ones. Every peer used to aim them
        // the moment the equip state arrived, which on another user's avatar is a write that is not its to
        // make. -xlinka
        RefreshDriveLinks();
    }

    // Letting go of its own drives as it goes is part of being destroyed, which was already allowed; asked
    // again of the gate, the host taking a departed guest's body down in a Social or Event world was refused.
    public override void OnDestroy()
    {
        using (World?.DataModelPermissions?.EnterSystemBypass())
            ReleaseDriveLinks();
        AllowedEquipUser = null!;
        base.OnDestroy();
    }

    private void EnsureCorrectUpdateOrder(bool updateChildren = true)
    {
        var parent = Slot.Parent;
        while (parent != null)
        {
            var parentNode = parent.GetComponent<AvatarPoseDriver>();
            if (parentNode != null && parentNode != this)
            {
                UpdateOrder = parentNode.UpdateOrder + 10;
                break;
            }
            parent = parent.Parent;
        }

        if (updateChildren)
        {
            foreach (var child in Slot.Children)
            {
                var childNode = child.GetComponent<AvatarPoseDriver>();
                childNode?.EnsureCorrectUpdateOrder(updateChildren: false);
            }
        }
    }

    // only assigns the synced refs; the drive links follow from them on the machine that owns this
    public void Equip(AvatarSocket slot)
    {
        _objectSlot.Target = slot;
        _source.Target = slot.Slot;
        RefreshDriveLinks();
    }

    // The outputs are settled here, while the wearer still owns this. Left to the update, every machine would
    // write them a frame later, after the avatar has already left its wearer. -xlinka
    public void Dequip()
    {
        _objectSlot.Target = null!;
        _source.Target = null!;
        RefreshDriveLinks();
        SetOutput(IsTracking, false);
        SetOutput(SourceIsTracking, false);
    }

    private void RefreshDriveLinks()
    {
        if (Slot == null || IsDestroyed || !MayAimLinks())
            return;

        // Assign only where nothing named a target yet - a link restored from a save or received from a
        // peer already points at this slot's field and must not be re-written every frame.
        if (_source?.Target != null && _objectSlot?.Target != null)
        {
            if (!_position.HasTarget)
                _position.DriveTarget(Slot.LocalPosition);
            if (!_rotation.HasTarget)
                _rotation.DriveTarget(Slot.LocalRotation);

            var objSlot = _objectSlot.Target;
            if (objSlot.DriveScale.Value)
            {
                if (!_scale.HasTarget)
                    _scale.DriveTarget(Slot.LocalScale);
            }
            else if (_scale.HasTarget)
            {
                _scale.ReleaseLink();
            }

            if (objSlot.DriveActive.Value)
            {
                if (!_active.HasTarget)
                    _active.DriveTarget(Slot.ActiveSelf);
            }
            else if (_active.HasTarget)
            {
                _active.ReleaseLink();
            }
        }
        else
        {
            ReleaseDriveLinks();
        }
    }

    // The wearer, or whoever made this driver: the machines whose writes to it are theirs to make.
    private bool MayAimLinks() => Slot.IsUnderLocalUser || IsMintedHere;

    private void ReleaseDriveLinks()
    {
        if (!MayAimLinks())
            return;
        if (_position.HasTarget)
            _position.ReleaseLink();
        if (_rotation.HasTarget)
            _rotation.ReleaseLink();
        if (_scale.HasTarget)
            _scale.ReleaseLink();
        if (_active.HasTarget)
            _active.ReleaseLink();
    }

    public void AllowEquip(User user)
    {
        if (AllowedEquipUser != null && user != AllowedEquipUser)
        {
            throw new InvalidOperationException("Another user has already been assigned!");
        }
        AllowedEquipUser = user;
    }

    // Runs on every peer; the pose is written through local-only drives. The tracking outputs and the links
    // are synced members, written by the owner alone and received by everyone else: every peer used to write
    // them, and a peer whose copy of the equip state had not resolved yet released the owner's links. -xlinka
    private void RunUpdate()
    {
        var equippingSlot = CurrentSocket;
        bool mine = MayAimLinks();
        if (_source?.Target != null && equippingSlot != null && !equippingSlot.IsDestroyed)
        {
            if (mine && (!_position.HasTarget || !_rotation.HasTarget))
                RefreshDriveLinks();

            var space = equippingSlot.GetFilteredPose(out var position, out var rotation, out var isTracking);

            if (mine)
            {
                SetOutput(IsTracking, isTracking);
                SetOutput(SourceIsTracking, equippingSlot.IsTracking.Value);
                SetOutput(SourceIsActive, equippingSlot.IsActive.Value);
            }

            // Pose is in user-root-local space. Full space conversion (not
            // position + rotation composition) so user scale is respected.
            var parent = Slot.Parent;
            if (parent != null)
            {
                _position.SetValue(parent.GlobalPointToLocal(space.LocalPointToGlobal(position)));
                _rotation.SetValue(parent.GlobalRotationToLocal(space.LocalRotationToGlobal(rotation)));
            }
            else
            {
                _position.SetValue(position);
                _rotation.SetValue(rotation);
            }

            if (_scale.IsLinkValid)
            {
                _scale.SetValue(equippingSlot.Slot.LocalScale.Value);
            }

            if (_active.IsLinkValid)
            {
                _active.SetValue(equippingSlot.Slot.ActiveSelf.Value);
            }
        }
        else if (mine)
        {
            SetOutput(IsTracking, false);
            SetOutput(SourceIsTracking, false);
            ReleaseDriveLinks();
        }
    }

    // Sync<bool> writes invalidate sync data unconditionally; these outputs
    // flip rarely, so only write on actual change.
    private static void SetOutput(Sync<bool> field, bool value)
    {
        if (field.Value != value)
            field.Value = value;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        using var bypass = World?.DataModelPermissions?.EnterSystemBypass();
        RunUpdate();
    }

}
