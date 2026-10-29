// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Interaction;

[ComponentCategory("Interaction/Tools")]
public abstract class ToolItem : Component
{
    private static readonly float[] EquipMenuColor = { 0.30f, 0.22f, 0.12f, 0.92f };
    public readonly SyncRef<Slot> TipReference = new();
    public readonly Sync<bool> BlockGripEquip = new();
    public readonly Sync<bool> BlockRemoteEquip = new();
    public readonly Sync<string> EquipName = new();
    public readonly SyncRef<Tool> OverrideActiveTool = new();

    // Equipped = the equip link is set (EquipToolItem wires it both ways). NOT a parent walk: a merely HELD
    // tool is also parented under the hand (grabber holder), so walking parents for a Tool would read a
    // grabbed-but-not-equipped item as equipped and lock it out of every equip route. - xlinka
    public Tool? ActiveTool => OverrideActiveTool.Target;

    public bool IsEquipped => ActiveTool != null;

    public float3 Tip => Slot.LocalPointToGlobal(LocalTip);

    // The slot carrying this tool's in-hand visual, when it has one. Read by the hand's TOOLHOLD trace
    // to say where the thing being held actually is in the world; null means "the tool's own slot".
    public virtual Slot? HeldVisual => null;

    // The direction the tip points, world space. A tool docked in the holder aims down its local -Z like
    // the beam and the holder frame do (DefaultLocalTip is Backward * 0.075 on every laser tool), so the
    // reference slot's Backward is its forward here, not its Forward. -xlinka
    public virtual float3 TipForward => TipReference.Target?.Backward ?? Slot.Backward;

    // Whether the hand should start its beam at this tool's Tip along TipForward rather than at its own
    // raw tip ahead of the holder. Laser tools carry one by default; a laser tool that wants the raw tip
    // overrides this to false.
    public virtual bool HasTipReference => UsesLaser;

    public virtual float3 LocalTip
    {
        get
        {
            if (TipReference.Target == null)
            {
                return DefaultLocalTip;
            }

            if (ReferenceEquals(TipReference.Target.Parent, Slot))
            {
                return TipReference.Target.LocalPosition.Value;
            }

            return Slot.GlobalPointToLocal(TipReference.Target.GlobalPosition);
        }
    }

    protected virtual float3 DefaultLocalTip => float3.Zero;

    public virtual float3 InteractionOrigin => ActiveTool?.Laser?.RayOrigin ?? Tip;

    public virtual float3 InteractionDirection => ActiveTool?.Laser?.RayDirection ?? TipForward;

    public virtual bool UsesLaser => false;

    public virtual bool UsesLaserGrip => false;

    public virtual bool BlocksPrimaryWhenTouching => true;

    public virtual bool UsesSecondary => true;

    public virtual bool CanUseWhenHolding => false;

    public virtual bool IsInUse => ActiveTool?.PrimaryHeld == true;

    // Whether this user may take the item into a hand at all. Base items are ordinary props and say
    // yes; the build tools answer the permission gate. Asked by the hand before it equips, so a
    // refusal is a tool that will not be picked up rather than one that throws on first press.
    public virtual bool AllowsEquip(User? user) => true;

    // The item's side of an equip, asked by the host of the sender for the same reason as the hand's.
    public override void OnAwake()
    {
        base.OnAwake();
        OverrideActiveTool.CheckRemoteSets((user, _, _) => AllowsEquip(user));
    }

    public override void OnAttach()
    {
        base.OnAttach();
        // A tool in the world is an ordinary grabbable prop. BlockWhenWorn doubles as the "can't grab while
        // equipped" guard: an equipped tool sits under the user root, same as a worn avatar. - xlinka
        var grabbable = Slot.GetComponent<Grabbable>() ?? Slot.AttachComponent<Grabbable>();
        grabbable.BlockWhenWorn.Value = true;
    }

    public override void OnStart()
    {
        base.OnStart();
        // Touch-to-equip: primary-click on the tool pops an "Equip <name>" confirm. The RayTarget persists but
        // its Activated event doesn't, so re-hook every session (OnStart, not OnAttach). Priority beats the
        // sibling Grabbable for the hovered target; grab resolves the Grabbable by parent-walk regardless. -xlinka
        //
        // Only the machine that made the tool gives it the target. Every other peer starts it the moment it
        // arrives, often before the target has, and while somebody holds it: creating one there was refused,
        // the start threw and retried, and a start that ran out of retries left the tool dead on that machine
        // for good, so it would not work for whoever it was handed to next. The others hook the one that is
        // there, or the one that turns up. -xlinka
        if (!HookRayTarget())
            Slot.OnComponentAdded += OnPartArrived;
    }

    public override void OnDestroy()
    {
        if (Slot != null)
            Slot.OnComponentAdded -= OnPartArrived;
        base.OnDestroy();
    }

    private RayTarget? _hookedTarget;

    private bool HookRayTarget()
    {
        var target = Slot.GetComponent<RayTarget>();
        if (target == null && IsMintedHere)
        {
            target = Slot.AttachComponent<RayTarget>();
            target.HoverRadius.Value = 0.025f;
        }
        if (target == null)
            return false;
        if (ReferenceEquals(target, _hookedTarget))
            return true;
        if (IsMintedHere)
        {
            // Repair the exact radius the old tool setup forced onto saved tools. Other authored
            // radii, including zero for collider-only targeting, stay as chosen. -xlinka
            if (target.HoverRadius.Value == 0.15f && target.HoverRadius.IsWriteAllowedQuietly())
                target.HoverRadius.Value = 0.025f;
            if (target.InteractionPriority.Value < 10)
                target.InteractionPriority.Value = 10;
        }
        _hookedTarget = target;
        target.Activated += _ => ConfirmEquip();
        return true;
    }

    private void OnPartArrived(Slot slot, Component component)
    {
        if (component is RayTarget && !IsDestroyed && HookRayTarget())
            Slot.OnComponentAdded -= OnPartArrived;
    }

    private void ConfirmEquip()
    {
        if (IsEquipped || BlockRemoteEquip.Value || IsDestroyed)
            return;
        // Held items equip through the held-object context menu, not touch (the grab state has to be released
        // by the same code that equips, or the grabber is left pointing at a docked slot). - xlinka
        if (Slot.GetComponent<Grabbable>()?.IsGrabbed == true)
            return;

        var userRootSlot = World?.LocalUser?.Root?.Slot;
        if (userRootSlot == null)
            return;

        // No confirm for a tool this user may not take: the answer would only be a second refusal after it.
        if (!AllowsEquip(World?.LocalUser))
        {
            ToolNotice.Refused(World, EquipName.Value);
            return;
        }

        // Owner hand = right on desktop (the menu-owning hand), the pointing hand in VR. Same owner resolution
        // as the avatar equip confirm: the wrong hand means no camera freeze and the menu edge-closes. - xlinka
        bool vr = Engine.Current?.InputInterface?.IsVRActive == true;
        var rayTarget = Slot.GetComponent<RayTarget>();
        HandTool? owner = null;
        foreach (var hand in userRootSlot.GetComponentsInChildren<HandTool>())
        {
            bool isOwner = vr
                ? (hand.Laser != null && ReferenceEquals(hand.Laser.CurrentRayTarget, rayTarget))
                : hand.Side.Value == Input.Chirality.Right;
            if (!isOwner)
                continue;
            owner = hand;
            break;
        }
        if (owner == null)
            return;

        var menu = userRootSlot.GetComponentInChildren<UI.ContextMenuSystem>();
        if (menu == null)
        {
            owner.EquipToolItem(this);
            return;
        }
        if (menu.IsShown)
            return;

        string name = string.IsNullOrWhiteSpace(EquipName.Value) ? Slot.SlotName.Value ?? "Tool" : EquipName.Value;
        var ctx = new UI.ContextMenuContext { Target = Slot, Pointer = owner.Laser?.Slot, Side = owner.Side.Value };
        var hand2 = owner;
        menu.OpenConfirm($"Equip {name}?", "Equip Tool", EquipMenuColor, () => hand2.EquipToolItem(this), ctx);
    }

    internal void SetActiveTool(Tool? tool)
    {
        OverrideActiveTool.Target = tool!;
    }

    public virtual bool UpdateTool(float primaryStrength, float2 secondaryAxis)
    {
        return false;
    }

    public virtual bool IsInteractionTarget(Slot target)
    {
        return true;
    }

    public virtual bool IsMovingTarget(Slot target)
    {
        return false;
    }

    public virtual float3? OverrideTargetPoint(float3 origin, float3 direction)
    {
        return null;
    }

    public virtual bool OnPrimaryPress()
    {
        return false;
    }

    public virtual bool OnPrimaryHold()
    {
        return false;
    }

    public virtual bool OnPrimaryRelease()
    {
        return false;
    }

    public virtual bool OnSecondaryPress()
    {
        return false;
    }

    public virtual bool OnSecondaryHold()
    {
        return false;
    }

    public virtual bool OnSecondaryRelease()
    {
        return false;
    }

    public virtual bool OnGrabbing(bool isLaserGrab)
    {
        return false;
    }

    // What a grab in this tool's hand takes along: asked once the hand has hold of something, and each one
    // given back is taken by the same hand where it is, so the lot moves together. -xlinka
    public virtual IEnumerable<IGrabbable>? GrabCompanions(Grabber grabber, IGrabbable grabbed)
    {
        return null;
    }

    public virtual void OnEquipped()
    {
    }

    public virtual void OnDequipped()
    {
    }
}
