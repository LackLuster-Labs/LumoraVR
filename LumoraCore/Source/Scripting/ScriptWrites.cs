// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Audio;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The one way a node writes a field. The gate is asked first, so a refusal is counted instead of
// vanishing into the setter's silent no-op, and four things are refused before the gate is asked:
//   host-only and config-locked members: the gate enforces those only on the wire, and a script runs on
//   the host, where its author's own user record would otherwise read as theirs to edit;
//   anything mid-load or mid-init: the gate waves those through as engine plumbing;
//   a driven member: the setter would drop the write and say nothing;
//   a field in another world: a handle is only ever meant for the world the script runs in.
//
// Structure comes through here as well. Destroy and Set Parent are judged by the gate as the author,
// like the same edit by hand, after what every script is refused: the world root, a protected slot,
// anything in someone's body or hands. The slot the script runs on, or anything above it, only Destroy
// takes, and only once the call that asked has returned (ScriptContext.RunOwnDestroy).
//
// A world script makes world content, under world content. A guest's script makes the guest's own
// things: the host mints them in the guest's byte, in the range it keeps for that guest's scripts, so
// they are the guest's by ownership and never an id the guest's own machine mints. Only where the
// world's mode lets people bring their own things in and the guest's role holds Spawn, under the world
// root or the guest's own things, never saved, and gone when the guest leaves. What a script copies
// holds no script, or each copy would make the next, and is the world's own all the way down, or the
// world's and its author's for a guest's copy.
//
// A Local script runs on every machine as its author. A person's changes only what it made on its own
// machine: local slots under its own object, what is on them, and local copies. Nothing it touches leaves
// the machine, and the gate has nothing to judge; otherwise a guest's script would change things from the
// host's machine as the host.
//
// A world's Local script changes the world as well, as the user at the machine running it: it writes,
// destroys and moves what is already there, the world's own things and that user's own, never anyone
// else's. The gate judges each change as that user's own edit, under their role, the world's mode and its
// config lock, and the host judges it again as theirs when it arrives. What it makes still stays on its
// own machine. Every machine runs the same flow, so a write that would change nothing is not made. -xlinka
internal static class ScriptWrites
{
    private const string LocalOnly = "a Local script changes only what it made on this machine";
    private const string SomeoneElses = "a Local script changes the world's own things and its viewer's, never anyone else's";

    public static void Write<T>(ScriptContext ctx, IField<T>? field, T value)
    {
        if (field == null || field.IsDestroyed)
        {
            ctx.Fail(field == null ? "no field" : "the field is gone");
            return;
        }
        if (Member(field) is { } element)
        {
            if (ctx.WritesAsMachineUser && !element.IsLocalElement && Readable(ctx, field)
                && EqualityComparer<T>.Default.Equals(field.Value, value))
                return;
            Check(ctx, element);
        }
        else if (ctx.IsLocal)
            throw new UnauthorizedAccessException(LocalOnly);
        else if (!ReferenceEquals(field.World, ctx.World))
            throw new UnauthorizedAccessException("scripts write only their own world");
        field.Value = value;
    }

    // Every refusal Write makes, without the write: for a node about to change something through a
    // method rather than a field.
    public static void Check(ScriptContext ctx, SyncElement element)
    {
        if (!ReferenceEquals(element.World, ctx.World))
            throw new UnauthorizedAccessException("scripts write only their own world");
        if (ctx.IsLocal)
        {
            if (element.IsLocalElement)
            {
                if (element.IsBlockedByDrive)
                    throw new UnauthorizedAccessException("the member is driven by something else");
                return;
            }
            MachineUserReach(ctx, element);
        }
        if (Refusal(element) is { } why)
            throw new UnauthorizedAccessException(why);
    }

    // What Check refuses a write in this world for, past the Local rules, or null when it lets it through.
    private static string? Refusal(SyncElement element)
    {
        if (element is ConflictingSyncElement { IsHostOnly: true } || element.IsConfigLocked)
            return "scripts cannot write host-only or config-locked members";
        if (element.Parent is Lumora.Core.Components.AvatarProtection)
            return "scripts cannot write Avatar Protection";
        if (element.IsLoading || element.IsInInitPhase)
            return "scripts cannot write a member that is still loading";
        if (element.IsBlockedByDrive)
            return "the member is driven by something else";
        if (!element.IsWriteAllowed())
            return $"the permission gate refused the write to {ScriptReads.Named(element)}";
        return null;
    }

    // Write for a node whose write is the last thing it does. A refusal turns the call down instead of
    // throwing, which a script refused every frame paid for every frame; the invoker answers it the same way,
    // outputs at their defaults and the failed exit taken. A Local script keeps Write's own rules. -xlinka
    public static void Set<T>(ScriptContext ctx, IField<T>? field, T value)
    {
        if (ctx.IsLocal || field == null || Member(field) is not { } element || field.IsDestroyed)
        {
            Write(ctx, field, value);
            return;
        }
        if (!ReferenceEquals(element.World, ctx.World))
        {
            ctx.TurnDown("scripts write only their own world");
            return;
        }
        if (Refusal(element) is { } why)
        {
            ctx.TurnDown(why);
            return;
        }
        field.Value = value;
    }

    // What a write to a field is judged on: the field, or the member a field view is part of.
    public static SyncElement? Member(IField? field) => field as SyncElement ?? (field as IFieldView)?.Member;

    public static bool Readable<T>(ScriptContext ctx, IField<T>? field)
        => field != null && !field.IsDestroyed && ReferenceEquals(field.World, ctx.World)
            && field is not ConflictingSyncElement { IsHostOnly: true };

    // The one way a node points a reference field at something, or at nothing: asked like Write. A
    // reference with rules of its own for who may set it (a drive or hook, an equip slot, a slot's parent)
    // is refused, since those rules judge a set that arrives from another machine and a script's set is
    // made on the host, where they are never asked. An action binding is refused too: moving one changes
    // what runs when someone else presses. -xlinka
    public static void WriteReference(ScriptContext ctx, ISyncRef? reference, IWorldElement? target)
    {
        if (reference is not SyncElement element || element.IsDestroyed)
        {
            ctx.Fail(reference == null ? "no reference" : reference is SyncElement ? "the reference is gone" : "scripts cannot write that reference");
            return;
        }
        if (ctx.WritesAsMachineUser && !element.IsLocalElement && ReferenceEquals(element.World, ctx.World)
            && element is not ConflictingSyncElement { IsHostOnly: true } && ReferenceEquals(reference.Target, target))
            return;
        Check(ctx, element);
        if (element.HasInboundRules || reference is ISyncDelegate)
            throw new UnauthorizedAccessException("scripts do not move a drive, a hook, an action or a reference with rules of its own");
        if (target == null)
        {
            reference.Target = null!;
            if (reference.Target != null)
                throw new UnauthorizedAccessException("the reference refused the change");
            return;
        }
        if (!ReferenceEquals(target.World, ctx.World))
            throw new UnauthorizedAccessException("scripts write only their own world");
        if (target.IsLocalElement && !element.IsLocalElement)
            throw new UnauthorizedAccessException("a reference in the world cannot point at something on one machine");
        if (!reference.TargetType.IsInstanceOfType(target))
            throw new InvalidOperationException($"the reference holds a {Named(reference.TargetType)}, not a {Named(target.GetType())}");
        if (!reference.TrySet(target) || !ReferenceEquals(reference.Target, target))
            throw new UnauthorizedAccessException("the reference refused the target");

        static string Named(Type type) => ScriptTypes.TryGet(type, out var named) ? named.Name : type.Name;
    }

    public static void Destroy(ScriptContext ctx, Slot? slot)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return;
        if (HoldsSelf(ctx, slot!))
        {
            CheckDestroy(ctx, slot!, own: true);
            ctx.DestroyOwn(slot!, keepAssets: false);
            return;
        }
        CheckDestroy(ctx, slot!);
        slot!.Destroy();
    }

    // Every refusal Destroy makes, without the destroy. Own lets through the slot the script runs on and what
    // is above it, for a destroy that waits until the call has returned.
    public static void CheckDestroy(ScriptContext ctx, Slot slot, bool own = false)
    {
        if (ctx.IsLocal)
        {
            if (slot.IsLocalElement)
                return;
            MachineUserReach(ctx, slot);
        }
        Restructurable(ctx, slot, "destroy");
        if (!own && HoldsSelf(ctx, slot))
            throw new UnauthorizedAccessException("a script cannot destroy the slot it runs on");

        var world = ctx.World;
        var request = new DataModelPermissionRequest(world, null, slot, null, slot,
            DataModelPermissionSurface.Slot, DataModelPermissionAction.Destroy, isNetwork: false);
        if (world.DataModelPermissions is { } permissions && !permissions.Authorize(request, out var reason))
            throw new UnauthorizedAccessException(reason ?? "the permission gate refused the destroy");
        if (world.SlotRegistry is { } registry && !registry.MayRemove(slot.ReferenceID))
            throw new UnauthorizedAccessException("the permission gate refused the destroy");
    }

    // The slot the script runs on, or one above it.
    internal static bool HoldsSelf(ScriptContext ctx, Slot slot)
        => ctx.Self is { } self && (ReferenceEquals(self, slot) || self.IsDescendantOf(slot));

    // What a world's Local script may change in the world as the user at this machine: the world's own things
    // and that user's own. Anything of anyone else's is refused, their body and what they hold included.
    internal static void MachineUserReach(ScriptContext ctx, IWorldElement element)
    {
        if (!ctx.WritesAsMachineUser)
            throw new UnauthorizedAccessException(LocalOnly);
        var user = ctx.MachineUser ?? throw new UnauthorizedAccessException("nobody is at this machine for a Local script to write as");
        byte owner = element.ReferenceID.GetUserByte();
        if (owner != RefIDConstants.AUTHORITY_BYTE && !IsUsersByte(user, owner))
            throw new UnauthorizedAccessException(SomeoneElses);
        IWorldElement? node = element;
        for (int depth = 0; node != null && depth < 16; depth++)
        {
            switch (node)
            {
                case Slot slot:
                    if (slot.ActiveUserRoot is { } root && !ReferenceEquals(root, user.Root))
                        throw new UnauthorizedAccessException(SomeoneElses);
                    return;
                case User someone:
                    if (!ReferenceEquals(someone, user))
                        throw new UnauthorizedAccessException(SomeoneElses);
                    return;
                case Component component:
                    node = component.Slot;
                    break;
                case SyncElement member:
                    node = member.Parent;
                    break;
                case Worker worker:
                    node = worker.Parent;
                    break;
                default:
                    return;
            }
        }
    }

    private static bool IsUsersByte(User user, byte owner)
        => RefIDConstants.IsValidUserByte(owner) && (user.HoldsByte(owner) || owner == user.ReferenceID.GetUserByte());

    // Under parent, or under the world root with no parent. Keeping the world transform writes the
    // local one as well, so those are checked before anything moves.
    public static void Reparent(ScriptContext ctx, Slot? slot, Slot? parent, bool keepWorldTransform)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return;
        if (ctx.IsLocal)
        {
            if (slot!.IsLocalElement)
            {
                ReparentLocal(ctx, slot, parent ?? ctx.Self, keepWorldTransform);
                return;
            }
            MachineUserReach(ctx, slot);
            if (parent is { IsLocalElement: true })
                throw new UnauthorizedAccessException("a slot in the world cannot go under something on one machine");
        }
        parent ??= ctx.World.RootSlot;
        if (!ScriptReads.Reached(ctx, parent, "parent"))
            return;
        if (ReferenceEquals(slot!.Parent, parent))
            return;
        if (ReferenceEquals(parent, slot) || parent!.IsDescendantOf(slot))
            throw new UnauthorizedAccessException("a slot cannot go under itself or its own child");
        // A script's own object may go onto and come off the body of the user at this machine, when the script is
        // theirs, and it moves as their own hand would move it: a band puts itself on the wrist it was let go
        // beside, and off again. Nothing else in a body is in a script's reach, and nothing goes into anybody else's.
        User? wearer = null;
        if (slot.ActiveUserRoot is { } wornOn && !slot.IsRootSlot && !slot.IsProtected && Carries(ctx, slot))
        {
            wearer = wornOn.ActiveUser;
            if (!ctx.IsMachineUser(wearer))
                throw new UnauthorizedAccessException("scripts cannot move anything in someone else's body or hands");
        }
        else
        {
            Restructurable(ctx, slot, "move");
        }
        if (parent!.ActiveUserRoot is { } body)
        {
            if (!Carries(ctx, slot) || !ctx.IsMachineUser(body.ActiveUser))
                throw new UnauthorizedAccessException("scripts do not put things in someone else's body or hands");
            wearer = body.ActiveUser;
        }
        if (ctx.Principal.IsWorldContent && !IsWorldSlot(parent))
            throw new UnauthorizedAccessException("a world script keeps world content under world content");
        if (!ReparentGuard.CanReparent(slot, parent))
            throw new UnauthorizedAccessException("the slot or its new parent refuses the move");

        using (wearer != null ? ctx.AsSelf(wearer) : null)
        {
            Check(ctx, slot.ParentSlotRef);
            if (keepWorldTransform)
            {
                Check(ctx, slot.LocalPosition);
                Check(ctx, slot.LocalRotation);
                Check(ctx, slot.LocalScale);
            }
            if (!slot.TrySetParent(parent, keepWorldTransform) || !ReferenceEquals(slot.Parent, parent))
                throw new UnauthorizedAccessException("the move was refused");
        }
    }

    public static Slot? CreateChild(ScriptContext ctx, Slot? parent, string? name)
    {
        if (!ScriptReads.Reached(ctx, parent, "parent"))
            return null;
        if (ctx.IsLocal)
        {
            if (!parent!.IsLocalElement && !ReferenceEquals(parent, ctx.Self))
                throw new UnauthorizedAccessException("a Local script makes slots only under its own object");
            ctx.ReserveMade(1);
            var local = parent.AddLocalSlot(string.IsNullOrEmpty(name) ? "Slot" : name);
            ctx.Made(local, 1);
            return local;
        }
        if (parent!.ActiveUserRoot != null)
            throw new UnauthorizedAccessException("scripts do not put things in someone's body or hands");
        string slotName = string.IsNullOrEmpty(name) ? "Slot" : name;
        if (ctx.Principal.IsWorldContent)
        {
            if (!IsWorldSlot(parent))
                throw new UnauthorizedAccessException("a world script makes slots under world content only");
            ctx.ReserveMade(1);
            var child = parent.AddSlot(slotName);
            ctx.Made(child, 1);
            return child;
        }

        MayBringIn(ctx, "make slots");
        if (!IsAuthorsPlace(ctx, parent))
            throw new UnauthorizedAccessException("a guest's script makes slots under the world root or its author's own things");
        ctx.ReserveMade(1);
        Slot made;
        using (AuthorsRange(ctx))
        {
            made = parent.AddSlot(slotName);
            made.Persistent.Value = false;
        }
        ctx.Made(made, 1);
        return made;
    }

    // Beside the original, under the same parent. A Local script's copy is a local one under its own
    // object, where the original stands in the world. A guest's copy stands beside the original where
    // that is the guest's to put things, else in the same place under the world root.
    public static Slot? Duplicate(ScriptContext ctx, Slot? slot)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return null;
        if (ctx.IsLocal)
            return DuplicateLocal(ctx, slot!);
        Restructurable(ctx, slot!, "copy");
        var parent = slot!.Parent;
        int count = 0;
        Slot? copy;
        if (ctx.Principal.IsWorldContent)
        {
            if (parent == null || !IsWorldSlot(parent))
                throw new UnauthorizedAccessException("a world script copies world content only");
            CopyableSubtree(slot, ref count);
            ctx.ReserveMade(count);
            copy = slot.Duplicate(parent);
        }
        else
        {
            MayBringIn(ctx, "copy slots");
            CopyableByGuest(ctx, slot, ref count);
            ctx.ReserveMade(count);
            bool beside = parent != null && IsAuthorsPlace(ctx, parent);
            using (AuthorsRange(ctx))
            {
                copy = slot.Duplicate(beside ? parent! : ctx.World.RootSlot, preserveGlobalTransform: !beside);
                if (copy != null)
                    copy.Persistent.Value = false;
            }
        }
        if (copy == null)
            throw new InvalidOperationException("the copy was not made");
        ctx.Made(copy, count);
        return copy;
    }

    private static void ReparentLocal(ScriptContext ctx, Slot slot, Slot? parent, bool keepWorldTransform)
    {
        if (!slot.IsLocalElement || parent == null || !ScriptReads.Visible(ctx, parent)
            || (!parent.IsLocalElement && !ReferenceEquals(parent, ctx.Self)))
            throw new UnauthorizedAccessException("a Local script keeps what it made under its own object");
        if (ReferenceEquals(slot.Parent, parent))
            return;
        if (ReferenceEquals(parent, slot) || parent.IsDescendantOf(slot))
            throw new UnauthorizedAccessException("a slot cannot go under itself or its own child");
        slot.SetParent(parent, keepWorldTransform);
    }

    private static Slot DuplicateLocal(ScriptContext ctx, Slot slot)
    {
        var home = ctx.Self ?? throw new InvalidOperationException("a Local script copies under its own object");
        if (slot.IsRootSlot || slot.ActiveUserRoot != null)
            throw new UnauthorizedAccessException("scripts cannot copy the world root or anything in someone's body or hands");
        int count = 0;
        CopyableLocally(ctx, slot, slot, ref count);
        ctx.ReserveMade(count);
        var holder = home.AddLocalSlot("Copying");
        var copy = slot.Duplicate(holder, preserveGlobalTransform: true);
        if (copy == null)
        {
            holder.Destroy();
            throw new InvalidOperationException("the copy was not made");
        }
        copy.SetParent(home, preserveGlobalTransform: true);
        holder.Destroy();
        ctx.Made(copy, count);
        return copy;
    }

    // What a Local copy may hold: things that draw, sound, collide or lay out a panel, from the world's
    // own content or its author's. Anything that acts could act on the world from every machine at once,
    // as whoever is at each, and a drive reaching outside the copy would hold something real on this
    // machine.
    private static void CopyableLocally(ScriptContext ctx, Slot root, Slot slot, ref int count)
    {
        count++;
        if (slot.ActiveUserRoot != null)
            throw new UnauthorizedAccessException("scripts cannot copy anything in someone's body or hands");
        if (!slot.IsLocalElement && !IsAuthors(ctx, slot))
            throw new UnauthorizedAccessException("a Local script copies the world's own things or its author's");
        foreach (var component in slot.Components)
        {
            if (component is ScriptHost)
                throw new UnauthorizedAccessException("scripts do not copy scripts");
            if (!IsPassive(component))
                throw new UnauthorizedAccessException($"a Local copy holds only what draws, sounds, collides or lays out a panel, and {component.GetType().Name} does more");
            if (!component.IsLocalElement && !IsAuthors(ctx, component))
                throw new UnauthorizedAccessException("a Local script copies the world's own things or its author's");
            if (DrivesOutside(component, root))
                throw new UnauthorizedAccessException("a Local copy may not drive anything outside itself");
        }
        foreach (var child in slot.Children)
            CopyableLocally(ctx, root, child, ref count);
    }

    private static bool IsAuthors(ScriptContext ctx, IWorldElement element)
    {
        byte owner = element.ReferenceID.GetUserByte();
        return owner == RefIDConstants.AUTHORITY_BYTE || (!ctx.Principal.IsWorldContent && owner == ctx.Principal.AllocationByte);
    }

    private static bool IsPassive(Component component) => component switch
    {
        MeshRenderer or SkinnedMeshRenderer or ProceduralMesh or MaterialProvider or Light or Collider or RigidBody or RespawnData
            or AudioClipPlayer or AudioOutput or OneShotSound or IUrlAssetProvider => true,
        _ => component.GetType().Namespace == "Helio.UI",
    };

    private static bool DrivesOutside(Worker worker, Slot root)
    {
        for (int i = 0; i < worker.SyncMemberCount; i++)
        {
            if (worker.GetSyncMember(i) is ILinkRef { Target: { } target } && !Inside(target, root))
                return true;
        }
        return false;
    }

    private static bool Inside(IWorldElement element, Slot root)
    {
        IWorldElement? current = element;
        for (int depth = 0; current != null && depth < 16; depth++)
        {
            switch (current)
            {
                case Slot slot:
                    return ReferenceEquals(slot, root) || slot.IsDescendantOf(root);
                case Component component:
                    current = component.Slot;
                    break;
                case SyncElement member:
                    current = member.Parent;
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    // The slot the script sits in, or one holding it: the object the script belongs to.
    private static bool Carries(ScriptContext ctx, Slot slot)
        => ctx.Self is { } self && (ReferenceEquals(self, slot) || self.IsDescendantOf(slot));

    private static void Restructurable(ScriptContext ctx, Slot slot, string what)
    {
        if (slot.IsRootSlot || slot.IsProtected)
            throw new UnauthorizedAccessException($"scripts cannot {what} the world root or a protected slot");
        if (slot.ActiveUserRoot != null)
            throw new UnauthorizedAccessException($"scripts cannot {what} anything in someone's body or hands");
    }

    // A guest's script brings things into the world as its author would by hand: the world's mode has to
    // let people bring their own things in, and the author's role has to hold Spawn.
    private static void MayBringIn(ScriptContext ctx, string what)
    {
        var world = ctx.World;
        if (!world.AllowsItemSpawning)
            throw new UnauthorizedAccessException($"this world does not let people bring their own things in, so a guest's script may not {what}");
        if (world.DataModelPermissions?.AllowsDomain(ctx.Principal.Author, DataModelPermissionDomain.Spawn) != true)
            throw new UnauthorizedAccessException($"its author's role may not bring things in, so their scripts may not {what}");
    }

    // Where a guest's script may put what it makes: the world root, or anything of its author's own.
    private static bool IsAuthorsPlace(ScriptContext ctx, Slot parent)
        => parent.IsRootSlot || (!parent.IsLocalElement && parent.ReferenceID.GetUserByte() == ctx.Principal.AllocationByte);

    // The host's range for the author's scripts, in the author's own byte.
    private static ReferenceController.AllocationScope AuthorsRange(ScriptContext ctx)
    {
        var controller = ctx.World.ReferenceController ?? throw new InvalidOperationException("the world has no id allocator");
        if (controller.GetScriptFence(ctx.Principal.AllocationByte) == 0)
            throw new UnauthorizedAccessException("this session keeps no room for its author's scripts to make things");
        return controller.ScriptAllocationScope(ctx.Principal.AllocationByte);
    }

    // What a guest's script may copy: the world's own things or its author's, holding no script and
    // nothing in anyone's body or hands.
    private static void CopyableByGuest(ScriptContext ctx, Slot slot, ref int count)
    {
        count++;
        if (slot.ActiveUserRoot != null)
            throw new UnauthorizedAccessException("scripts cannot copy anything in someone's body or hands");
        if (!IsAuthors(ctx, slot))
            throw new UnauthorizedAccessException("a guest's script copies the world's own things or its author's");
        foreach (var component in slot.Components)
        {
            if (component is ScriptHost)
                throw new UnauthorizedAccessException("scripts do not copy scripts");
            if (!IsAuthors(ctx, component))
                throw new UnauthorizedAccessException("a guest's script copies the world's own things or its author's");
        }
        foreach (var child in slot.Children)
        {
            if (!child.IsLocalElement)
                CopyableByGuest(ctx, child, ref count);
        }
    }

    private static bool IsWorldSlot(Slot slot)
        => slot.IsRootSlot || slot.ReferenceID.GetUserByte() == RefIDConstants.AUTHORITY_BYTE;

    private static void CopyableSubtree(Slot slot, ref int count)
    {
        count++;
        if (!IsWorldSlot(slot) || slot.ActiveUserRoot != null)
            throw new UnauthorizedAccessException("a world script copies world content only");
        foreach (var component in slot.Components)
        {
            if (component is ScriptHost)
                throw new UnauthorizedAccessException("scripts do not copy scripts");
            if (component.ReferenceID.GetUserByte() != RefIDConstants.AUTHORITY_BYTE)
                throw new UnauthorizedAccessException("a world script copies world content only");
        }
        foreach (var child in slot.Children)
        {
            if (!child.IsLocalElement)
                CopyableSubtree(child, ref count);
        }
    }

    // A value a node was given to write that no field should hold.
    public static float3 Finite(float3 value, string name)
    {
        if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z))
            throw new ArgumentException($"{name} is not a finite number");
        return value;
    }

    public static floatQ Unit(floatQ rotation)
    {
        float lengthSquared = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-12f)
            throw new ArgumentException("rotation is not a usable quaternion");
        float scale = 1f / MathF.Sqrt(lengthSquared);
        return new floatQ(rotation.x * scale, rotation.y * scale, rotation.z * scale, rotation.w * scale);
    }

    public static float3 Scale(float3 scale)
    {
        Finite(scale, "scale");
        if (scale.x == 0f || scale.y == 0f || scale.z == 0f)
            throw new ArgumentException("scale has a zero axis");
        return scale;
    }

    // What Destroy refuses, for a slot a script would put out of the world where an undo can bring it back:
    // the world root, a protected slot, anything in a body or hands, the slot the script runs on and what is
    // above it, and whatever the gate would not let its author destroy by hand. A Local script parks only
    // what it made on this machine.
    public static void CheckPark(ScriptContext ctx, Slot slot, bool own = false)
    {
        if (ctx.IsLocal && !slot.IsLocalElement)
            throw new UnauthorizedAccessException("a Local script puts away only what it made on this machine");
        CheckDestroy(ctx, slot, own);
    }

    // Makes something of count slots under parent, the way Create Child makes one: under world content for
    // a world script; for a guest's, where the world lets people bring things in and their role holds Spawn,
    // under the world root or the author's own things, in the host's range for the author's scripts, never
    // saved. Counted against the script's share and its author's.
    public static Slot MakeUnder(ScriptContext ctx, Slot parent, int count, Func<Slot> make)
    {
        if (ctx.IsLocal)
            throw new UnauthorizedAccessException(LocalOnly);
        if (parent.ActiveUserRoot != null)
            throw new UnauthorizedAccessException("scripts do not put things in someone's body or hands");
        if (ctx.Principal.IsWorldContent)
        {
            if (!IsWorldSlot(parent))
                throw new UnauthorizedAccessException("a world script makes slots under world content only");
            ctx.ReserveMade(count);
            var made = make();
            ctx.Made(made, count);
            return made;
        }

        MayBringIn(ctx, "make slots");
        if (!IsAuthorsPlace(ctx, parent))
            throw new UnauthorizedAccessException("a guest's script makes slots under the world root or its author's own things");
        ctx.ReserveMade(count);
        Slot slot;
        using (AuthorsRange(ctx))
        {
            slot = make();
            slot.Persistent.Value = false;
        }
        ctx.Made(slot, count);
        return slot;
    }

    // What the host mints for a principal outside a script's call: in the author's script range for a
    // guest's, in the world's own for the world's.
    public static IDisposable? RangeOf(World world, ScriptPrincipal principal)
    {
        // On the author's own machine what it makes is theirs by the machine's own range.
        if (principal.IsWorldContent || !world.IsAuthority)
            return null;
        var controller = world.ReferenceController ?? throw new InvalidOperationException("the world has no id allocator");
        if (controller.GetScriptFence(principal.AllocationByte) == 0)
            throw new UnauthorizedAccessException("this session keeps no room for its author's scripts to make things");
        return controller.ScriptAllocationScope(principal.AllocationByte);
    }
}
