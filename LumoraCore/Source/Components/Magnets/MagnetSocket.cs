// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Magnets;

// A receptacle a carried item drops into: on release, an item whose Magnet passes the whitelists,
// tag list, filters, distance and angle gates is reparented under this slot and settled onto the
// socket pose. Holds one item at a time.
//
// Occupancy is BOTH a stored ref and a structural fact, and the structural fact wins. A guest that
// snaps into a socket sitting in host-owned content can reparent the item (the gate treats a
// grabbable's parent/pose write as grab traffic) but cannot necessarily write this component's
// fields, so a purely stored occupancy would read "free" on the very socket that visibly holds
// something and the next drop would stack a second item into it. Resolving through the children
// when the ref is empty or stale costs a short scan and cannot disagree with what the world shows.
//
// Taken out and let go anywhere else, an item does not return under the socket: the socket refuses a
// release's return to its own slot (IReleaseReturnBlock), so the item goes to the world root unless some
// socket or surface takes it there. -xlinka
[ComponentCategory("Interaction/Magnets")]
public class MagnetSocket : Component, ICustomInspectorUI, IReparentBlock, IReleaseReturnBlock
{
    // How far an item's snap point may sit from this socket, in the socket's local scale.
    public readonly Sync<float> MaxDistance;

    // Largest angle in degrees between the item and this socket that still snaps. 180 accepts any.
    public readonly Sync<float> MaxAngle;

    // Seconds the item takes to glide onto the socket pose. 0 places it instantly.
    public readonly Sync<float> SettleTime;

    // Take an item the moment it comes into range while it is still being carried.
    public readonly Sync<bool> AutoAttach;

    // Only accept a magnet sitting on the released object itself, never one on a child of it.
    public readonly Sync<bool> DirectOnly;

    // When non-empty, only these magnets may attach.
    public readonly SyncRefList<Magnet> MagnetWhitelist;

    // When non-empty, an item needs at least one of these tags.
    public readonly SyncFieldList<string> TagWhitelist;

    // Every referenced filter has to accept the item.
    public readonly SyncRefList<IMagnetFilter> Filters;

    // Written by whoever snapped it; read through CurrentItem.
    public readonly SyncRef<Magnet> Attached;

    // Lift the item out of this socket's hierarchy when it is grabbed away, instead of letting a
    // release with no new target drop it back in here.
    public readonly Sync<bool> UnparentOnDetach;

    // Keep the item's own up axis pointing at world up and only take yaw from this socket.
    public readonly Sync<bool> KeepItemUpright;

    // The item settles at a local scale of one under this socket, whatever size it was carried at.
    public readonly Sync<bool> ResetItemScale;

    // Found only by magnets whose SocketWhitelist names it, never by the world scan.
    public readonly Sync<bool> ListedOnly;

    private MagnetRegistry? _registry;

    public MagnetSocket()
    {
        MaxDistance = new Sync<float>(this, 0.1f);
        MaxAngle = new Sync<float>(this, 180f);
        SettleTime = new Sync<float>(this, 0.15f);
        AutoAttach = new Sync<bool>(this, false);
        DirectOnly = new Sync<bool>(this, false);
        MagnetWhitelist = new SyncRefList<Magnet>(this);
        TagWhitelist = new SyncFieldList<string>(this);
        Filters = new SyncRefList<IMagnetFilter>(this);
        Attached = new SyncRef<Magnet>(this);
        UnparentOnDetach = new Sync<bool>(this, false);
        KeepItemUpright = new Sync<bool>(this, false);
        ResetItemScale = new Sync<bool>(this, false);
        ListedOnly = new Sync<bool>(this, false);
    }

    bool IReleaseReturnBlock.BlocksReturn => true;

    int IReleaseReturnBlock.ReturnBlockDepth => 0;

    // MaxDistance in world units.
    public float WorldMaxDistance => Slot != null ? Slot.LocalScaleToGlobal(System.Math.Max(0f, MaxDistance.Value)) : 0f;

    // The stored ref when it still points at a live child, otherwise whichever child magnet is
    // actually sitting here.
    public Magnet? CurrentItem
    {
        get
        {
            var stored = Attached.Target;
            if (IsHeldHere(stored))
                return stored;
            return FindChildItem();
        }
    }

    public bool Occupied => CurrentItem != null;

    // An occupied socket owns its child's parentage; other systems must leave it alone.
    public bool BlocksReparent => Occupied;

    // Refuses to have its own item moved elsewhere. Only a grab takes it out.
    //
    // Scoped to the item itself rather than everything underneath the socket: moving a part out of
    // a socketed assembly is somebody rearranging the assembly, and none of the socket's business.
    public bool AllowsReparent(Slot target, Slot newParent)
    {
        var item = CurrentItem;
        return item == null || !ReferenceEquals(item.Slot, target);
    }

    public override void OnStart()
    {
        base.OnStart();
        _registry = MagnetRegistry.For(World);
        _registry?.Register(this);
    }

    public override void OnDestroy()
    {
        _registry?.Unregister(this);
        _registry = null;
        base.OnDestroy();
    }

    // Ignoring where it currently is. Position and angle are checked separately by CanAttach.
    public bool Accepts(Magnet? magnet)
    {
        if (magnet == null || magnet.IsDestroyed || !magnet.Enabled.Value)
            return false;
        if (IsDestroyed || !Enabled.Value || Slot == null || Slot.IsDestroyed || !Slot.IsActive)
            return false;

        var itemSlot = magnet.Slot;
        if (itemSlot == null || itemSlot.IsDestroyed)
            return false;

        // A socket underneath the item it would swallow is a cycle waiting to happen, and an item
        // cannot hold itself.
        if (ReferenceEquals(itemSlot, Slot) || Slot.IsDescendantOf(itemSlot))
            return false;

        // Already holding something else.
        var current = CurrentItem;
        if (current != null && !ReferenceEquals(current, magnet))
            return false;

        // DirectOnly means the magnet has to be on the object the hand actually let go of, so a
        // pouch full of magnetised parts cannot dump one of its children into this socket.
        if (DirectOnly.Value && !magnet.IsOnCarriedRoot)
            return false;

        if (MagnetWhitelist.Count > 0 && !MagnetWhitelist.Contains(magnet))
            return false;

        if (TagWhitelist.Count > 0 && !HasAnyTag(magnet))
            return false;

        foreach (var filter in Filters)
        {
            if (filter == null)
                continue;
            if (filter is Component component && (component.IsDestroyed || !component.Enabled.Value))
                continue;
            if (!filter.Accept(magnet))
                return false;
        }
        return true;
    }

    // Full gate: acceptance plus the pose test against the item's snap point and orientation.
    public bool CanAttach(Magnet? magnet, in float3 worldPoint, in floatQ worldRotation, bool ignorePose = false)
    {
        if (!Accepts(magnet))
            return false;
        if (ignorePose)
            return true;

        float reach = WorldMaxDistance + (magnet?.WorldRadius ?? 0f);
        if (float3.DistanceSquared(worldPoint, Slot!.GlobalPosition) > reach * reach)
            return false;

        float limit = MaxAngle.Value;
        if (limit >= 180f)
            return true;
        return MagnetHelper.AngleDegrees(in worldRotation, Slot.GlobalRotation) <= limit;
    }

    // For ranking candidates.
    public float DistanceTo(in float3 worldPoint)
        => Slot == null ? float.MaxValue : (worldPoint - Slot.GlobalPosition).Length;

    // Expressed in this socket's local space. The item's landing point lands exactly on the socket
    // origin.
    public void ComputeItemPose(Magnet magnet, in float3 itemLocalScale, out float3 localPosition, out floatQ localRotation)
    {
        localRotation = ComputeItemRotation(magnet);
        var offset = magnet.LocalLandingPoint * itemLocalScale;
        localPosition = -(localRotation * offset);
    }

    // Silently does nothing when the gate refuses the write.
    public void Claim(Magnet magnet)
    {
        if (magnet == null || magnet.IsDestroyed || IsDestroyed)
            return;
        if (!ReferenceEquals(Attached.Target, magnet) && Attached.IsWriteAllowedQuietly())
            Attached.Target = magnet;
    }

    // Only if it is the one we think we are holding.
    public void ReleaseItem(Magnet magnet)
    {
        if (IsDestroyed)
            return;
        if (Attached.Target == null || !ReferenceEquals(Attached.Target, magnet))
            return;
        // Occupancy follows the children even when the grabbing user cannot update this cache. -xlinka
        if (!Attached.IsWriteAllowedQuietly())
            return;
        // The setter reads null as "clear the ref"; null! only quiets the annotation.
        Attached.Target = null!;
    }

    private floatQ ComputeItemRotation(Magnet magnet)
    {
        bool upright = KeepItemUpright.Value || magnet.KeepUpright.Value;
        if (!upright || Slot == null)
            return floatQ.Identity;

        // Level the socket's own orientation about world up, then bring it back into socket space.
        // A shelf that is bolted on at an angle still parks its bottles standing straight. -xlinka
        var socketRotation = Slot.GlobalRotation;
        var forward = Flatten(socketRotation * float3.Forward);
        if (forward.LengthSquared < 1e-8f)
            forward = Flatten(socketRotation * float3.Up);
        if (forward.LengthSquared < 1e-8f)
            forward = float3.Forward;

        var world = MagnetHelper.Facing(forward.Normalized, float3.Up);
        return socketRotation.Inverse * world;
    }

    private static float3 Flatten(in float3 v) => new float3(v.x, 0f, v.z);

    private bool HasAnyTag(Magnet magnet)
    {
        foreach (var wanted in TagWhitelist)
        {
            if (string.IsNullOrEmpty(wanted))
                continue;
            foreach (var tag in magnet.Tags)
            {
                if (string.Equals(tag, wanted, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private bool IsHeldHere(Magnet? magnet)
    {
        if (magnet == null || magnet.IsDestroyed)
            return false;
        var itemSlot = magnet.Slot;
        return itemSlot != null && !itemSlot.IsDestroyed && ReferenceEquals(itemSlot.Parent, Slot);
    }

    private Magnet? FindChildItem()
    {
        var slot = Slot;
        if (slot == null || slot.IsDestroyed)
            return null;
        var children = slot.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var magnet = children[i].GetComponent<Magnet>();
            if (magnet != null && !magnet.IsDestroyed)
                return magnet;
        }
        return null;
    }

    public void BuildInspectorBody(UIBuilder ui)
    {
        var item = CurrentItem;
        InspectorStats.AddRow(ui, "Holding", item?.Slot?.SlotName.Value ?? "empty");
        InspectorStats.AddRow(ui, "Reach", $"{WorldMaxDistance:0.###} m world");
        InspectorStats.AddRow(ui, "Angle limit", MaxAngle.Value >= 180f ? "any" : $"{MaxAngle.Value:0.#} deg");
        InspectorStats.AddRow(ui, "Mode", AutoAttach.Value ? "auto-attach while carried" : "on release");

        // Counted by walking the world's magnets rather than the socket registry, which only knows
        // about sockets. An inspector body is built once when the section opens, so the sweep is
        // paid on a click and never per frame.
        int candidates = 0;
        if (Slot != null && !Slot.IsDestroyed)
        {
            var magnets = new List<Magnet>();
            World?.RootSlot?.GetComponentsInChildren(magnets);
            for (int i = 0; i < magnets.Count; i++)
            {
                var magnet = magnets[i];
                if (magnet.Slot == null || ReferenceEquals(magnet, item))
                    continue;
                if (CanAttach(magnet, magnet.WorldSnapPoint, magnet.Slot.GlobalRotation))
                    candidates++;
            }
        }
        InspectorStats.AddRow(ui, "In range", candidates.ToString());
    }
}
