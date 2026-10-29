// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Components.Interaction;

namespace Lumora.Core.Components.Avatar;

// Avatar root tag + IAvatarEquippable implementation. Lives on the root slot of
// an avatar hierarchy. When the user equips this avatar through AvatarEquipManager,
// the root reparents under the user's AvatarSocket tagged with
// BodyNode.Root (always present on AvatarEquipManager). MaxValue priority means
// the root is processed last after all per-body-node IAvatarEquippables on the
// tree are already dispatched. - xlinka
[ComponentCategory("Users/Avatar")]
public class AvatarForm : Component, IAvatarEquippable
{
    public readonly SyncRef<UserRoot> Owner = new();
    public readonly Sync<bool> IsActive = new();

    public readonly Sync<float3> Scale = new();
    // One is a valid authored/calibrated scale, including on imported package avatars.
    // A new model still needs to keep its unit-conversion transform until IK fits it.
    public readonly Sync<bool> HasEquipScale = new();

    public BodyNode Node => BodyNode.Root;
    public int EquipPriority => int.MaxValue;
    public bool IsEquipped => CurrentSocket != null;

    // The user wearing this avatar: the one whose Root socket holds it and names it as equipped. Held in a
    // hand, standing in the world or nested inside somebody else's avatar is not worn. -xlinka
    public UserRoot? Wearer
    {
        get
        {
            var socket = CurrentSocket;
            return socket != null && ReferenceEquals(socket.Equipped.Target, this) ? socket.Slot?.ActiveUserRoot : null;
        }
    }

    public AvatarSocket CurrentSocket
    {
        get
        {
            // When equipped via the new dispatch, our slot is reparented under
            // the AvatarSocket.Slot. Walk parents looking for one.
            var current = Slot?.Parent;
            while (current != null)
            {
                var objSlot = current.GetComponent<AvatarSocket>();
                if (objSlot != null && objSlot.Node.Value == BodyNode.Root)
                    return objSlot;
                current = current.Parent;
            }
            return null!;
        }
    }

    public IEnumerable<BodyNode> ConflictingNodes
    {
        get { yield break; }
    }

    public User AllowedEquipUser { get; private set; } = null!;

    public override void OnAwake()
    {
        base.OnAwake();
        if (Slot != null)
            Slot.ActiveUserRootChanged += OnActiveUserRootChanged;
        Owner.OnTargetChange += OnOwnerChanged;
    }

    public override void OnInit()
    {
        base.OnInit();
        IsActive.Value = true;
        Scale.Value = float3.One;
    }

    // CLICK-TO-EQUIP
    //
    // This lives on the avatar root component rather than on whatever built the avatar, because it has to
    // hold for EVERY avatar: one made in the studio, one imported from a package, one loaded back out of
    // inventory. It used to be wired by the studio's creation flow alone, so an imported avatar had no
    // click behaviour at all and the only way to wear it was the radial menu on middle-click - which is
    // not where anyone looks for it.
    //
    // Re-wired in OnStart, not once at creation: RayTarget.Activated is a plain C# event, so nothing
    // survives a save and reload. The component that owns the behaviour has to re-establish it every time
    // the avatar comes back. -xlinka
    private RayTarget _equipTarget = null!;

    public override void OnStart()
    {
        base.OnStart();
        EnsureEquipTarget();
    }

    public void EnsureEquipTarget()
    {
        if (Slot == null || Slot.IsDestroyed || _equipTarget != null)
            return;

        // An avatar someone is already WEARING is not an offer. Without this, every remote player in the
        // room grows a half-metre laser target on their body that steals hover from whatever is behind
        // them and does nothing when clicked. The confirm refuses a worn avatar anyway; this stops the
        // target existing in the first place. -xlinka
        if (Slot.ActiveUserRoot != null)
            return;

        // No hover ball: the laser reaches this target through the avatar's own body colliders, walking up
        // from whatever it hit. A half-metre ball round the root stood at the feet, a metre across, and took
        // every grab and point aimed anywhere near the avatar instead of the body itself. -xlinka
        var target = Slot.GetComponent<RayTarget>() ?? Slot.AttachComponent<RayTarget>();
        target.HoverRadius.Value = 0f;
        // Beat the root Grabbable for the laser's hovered target so a LEFT-click (use/interact) lands on this
        // RayTarget. Grab is GRIP/right-click and resolves the Grabbable by walking parents, so it's unaffected.
        target.InteractionPriority.Value = 10;
        target.Activated += OnEquipRayActivated;
        _equipTarget = target;
    }

    private void OnEquipRayActivated(float3 _) => ConfirmEquip();

    // Taken off and left standing, it is an offer again. A machine that first saw it worn never hooked the
    // target; the target itself is already there, put up by whoever first saw it in the world. -xlinka
    private void OnActiveUserRootChanged(Slot _)
    {
        if (_equipTarget != null || Slot == null || Slot.IsDestroyed || Slot.ActiveUserRoot != null)
            return;
        if (Slot.GetComponent<RayTarget>() != null)
            EnsureEquipTarget();
    }

    // Put on, the avatar becomes part of somebody's body, and a gizmo still up on it from when it stood in the
    // world went with it: it followed the wearer about and its box grew and shrank with the walking body. Every
    // machine drops its own rigs on the avatar as it goes on, wherever the equip came from. One put up on the worn
    // avatar afterwards is somebody editing it on purpose, and stays. -xlinka
    private void OnOwnerChanged(SyncRef<UserRoot> owner)
    {
        if (owner.Target == null)
            return;
        World?.RunSynchronously(() =>
        {
            if (!IsDestroyed && Owner.Target != null && Slot is { IsDestroyed: false } avatar)
                Gizmos.GizmoHelper.DismissWithin(avatar);
        });
    }

    // Left-click (use) on the avatar pops a small "Equip Avatar / Cancel" confirm, then equips.
    // Falls back to a direct equip if no context menu is available.
    private void ConfirmEquip()
    {
        var avatar = Slot;
        if (avatar == null || avatar.IsDestroyed)
            return;

        // Already worn: nothing to offer.
        if (IsEquipped || avatar.ActiveUserRoot != null)
            return;

        var userRootSlot = World?.LocalUser?.Root?.Slot;
        var menu = userRootSlot?.GetComponentInChildren<UI.ContextMenuSystem>();
        if (userRootSlot == null || menu == null)
        {
            TryEquip();
            return;
        }

        // Idempotent: the activation can fire repeatedly while the laser sits on the avatar, and
        // re-opening rebuilds the whole menu visual every frame.
        if (menu.IsShown)
            return;

        // Anchor the confirm to the hand that OWNS the menu, so the camera-freeze / mouse-aim AND the opening-press
        // guard engage (both key off context.Side, and the desktop aim only runs for the owner hand). On DESKTOP the
        // menu is right-hand-owned; in VR it's the hand whose laser is on the avatar. Matching the wrong hand made
        // the camera not freeze, so moving the mouse turned the view and the menu edge-closed.
        bool vr = Engine.Current?.InputInterface?.IsVRActive == true;
        var ctx = new UI.ContextMenuContext { Target = avatar };
        foreach (var hand in userRootSlot.GetComponentsInChildren<HandTool>())
        {
            bool isOwner = vr
                ? (hand.Laser != null && ReferenceEquals(hand.Laser.CurrentRayTarget, _equipTarget))
                : hand.Side.Value == Chirality.Right;
            if (!isOwner)
                continue;
            ctx.Pointer = hand.Laser?.Slot;
            ctx.Side = hand.Side.Value;
            break;
        }

        menu.OpenConfirm("Equip Avatar?", "Equip Avatar", new[] { 0.14f, 0.30f, 0.18f, 0.92f }, TryEquip, ctx);
    }

    private void TryEquip()
    {
        var avatar = Slot;
        if (avatar == null || avatar.IsDestroyed)
            return;

        var userRoot = World?.LocalUser?.Root;
        if (userRoot == null)
        {
            Logging.Logger.Warn("AvatarForm: no local user root to equip onto");
            return;
        }
        var manager = userRoot.GetRegisteredComponent<AvatarEquipManager>()
                      ?? userRoot.Slot.GetComponentInChildren<AvatarEquipManager>()
                      ?? userRoot.Slot.AttachComponent<AvatarEquipManager>();
        if (manager.UserRoot.Target == null)
            manager.UserRoot.Target = userRoot;
        manager.EquipAvatar(avatar);
    }

    public override void OnDestroy()
    {
        if (Slot != null)
            Slot.ActiveUserRootChanged -= OnActiveUserRootChanged;
        if (_equipTarget != null && !_equipTarget.IsDestroyed)
            _equipTarget.Activated -= OnEquipRayActivated;
        _equipTarget = null!;
        base.OnDestroy();
    }

    public void Equip(AvatarSocket slot)
    {
        if (slot == null || slot.Slot == null) return;

        Slot.SetParent(slot.Slot);
        Slot.LocalPosition.Value = float3.Zero;
        Slot.LocalRotation.Value = floatQ.Identity;
        // Restore authored package scales as well as IK calibration, even when they are One.
        // Older saved avatars lack the flag; keep recognizing their non-unit calibration.
        var sc = Scale.Value;
        bool calibrated = HasEquipScale.Value || System.MathF.Abs(sc.x - 1f) > 1e-5f
                       || System.MathF.Abs(sc.y - 1f) > 1e-5f
                       || System.MathF.Abs(sc.z - 1f) > 1e-5f;
        if (calibrated)
            Slot.LocalScale.Value = sc;

        Owner.Target = slot.Slot.ActiveUserRoot;
        Slot.ActiveSelf.Value = true;
        IsActive.Value = true;
    }

    public void SetEquipScale(float3 scale)
    {
        Scale.Value = scale;
        HasEquipScale.Value = true;
    }

    public void Dequip()
    {
        Owner.Target = null!;
        IsActive.Value = false;
    }

    public void AllowEquip(User user)
    {
        if (AllowedEquipUser != null && user != AllowedEquipUser)
            throw new System.InvalidOperationException("Another user has already been assigned!");
        AllowedEquipUser = user;
    }
}
