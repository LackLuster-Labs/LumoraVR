// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Core.Scripting;

// The slot writes of the hierarchy nodes beyond what ScriptWrites makes: a slot's tag, saved flag and order
// offset, its place among its siblings, clearing what is under it, and destroying while keeping the assets
// something else still uses. Each is judged as the author doing it by hand, after what every script is
// refused: the world root, a protected slot, and anything in someone's body or hands. A Local script
// changes what it made on this machine, and a world's Local script the world as the user at the machine,
// as ScriptWrites has it, that user's own body and hands included. -xlinka
internal static class ScriptSlotWrites
{
    private const string LocalOnly = "a Local script changes only what it made on this machine";

    public static void Editable(ScriptContext ctx, Slot slot, string what)
    {
        if (ctx.IsLocal && (slot.IsLocalElement || !ctx.WritesAsMachineUser))
            return;
        if (slot.IsRootSlot || slot.IsProtected)
            throw new UnauthorizedAccessException($"scripts cannot {what} the world root or a protected slot");
        if (slot.ActiveUserRoot is { } root && !(ctx.IsLocal && ReferenceEquals(root, ctx.MachineUser?.Root)))
            throw new UnauthorizedAccessException($"scripts cannot {what} anything in someone's body or hands");
    }

    public static void Write<T>(ScriptContext ctx, Slot slot, IField<T> field, T value)
    {
        Editable(ctx, slot, "change");
        ScriptWrites.Write(ctx, field, value);
    }

    // What a guest's script makes is never saved, so it may not mark its author's own things saved either.
    public static void SetPersistent(ScriptContext ctx, Slot slot, bool persistent)
    {
        Editable(ctx, slot, "change");
        if (persistent && !ctx.IsLocal && !ctx.Principal.IsWorldContent && !slot.IsLocalElement
            && slot.ReferenceID.GetUserByte() == ctx.Principal.AllocationByte)
            throw new UnauthorizedAccessException("a guest's script does not mark its author's things saved with the world");
        ScriptWrites.Write(ctx, slot.Persistent, persistent);
    }

    // Moves the slot to index among its siblings, clamped to the first or last place. The move is written as
    // order offsets, which every machine sorts children by, so it shows everywhere. Usually only the slot's
    // own offset changes; when its new neighbours leave no room, siblings out to one end are renumbered as
    // well, and each of those is judged like the slot. A move that would renumber one the script may not
    // change is refused whole.
    public static void SetChildIndex(ScriptContext ctx, Slot slot, int index)
    {
        var parent = slot.Parent;
        if (parent == null)
        {
            ctx.Fail($"{ScriptReads.Named(slot)} has no parent to be ordered under");
            return;
        }
        Editable(ctx, slot, "move");
        if (ctx.IsLocal && !slot.IsLocalElement && !ctx.WritesAsMachineUser)
            throw new UnauthorizedAccessException(LocalOnly);
        ScriptWrites.Check(ctx, slot.OrderOffset);
        var writes = new List<(Slot Slot, long Offset)>();
        if (!parent.PlanChildMove(slot, index, writes, sibling => Renumberable(ctx, sibling)))
        {
            if (ChildIndexOf(parent, slot) >= 0)
                throw new UnauthorizedAccessException("making room there would renumber a sibling the script may not change");
            return;
        }
        foreach (var (moved, offset) in writes)
            ScriptWrites.Write(ctx, moved.OrderOffset, offset);
    }

    private static bool Renumberable(ScriptContext ctx, Slot slot)
    {
        try
        {
            Editable(ctx, slot, "move");
            ScriptWrites.Check(ctx, slot.OrderOffset);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int ChildIndexOf(Slot parent, Slot slot)
    {
        var children = parent.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (ReferenceEquals(children[i], slot))
                return i;
        }
        return -1;
    }

    // Every child the script can see, each judged like Destroy, all of them checked before any goes, so a
    // refusal changes nothing. With keepAssets, as DestroyKeepingAssets does for one.
    public static void DestroyChildren(ScriptContext ctx, Slot parent, bool keepAssets)
    {
        Editable(ctx, parent, "clear");
        var children = new List<Slot>();
        foreach (var child in parent.Children)
        {
            if (ScriptReads.Visible(ctx, child))
                children.Add(child);
        }
        if (children.Count == 0)
            return;
        foreach (var child in children)
        {
            if (keepAssets)
                CheckTakeApart(ctx, child);
            else
                ScriptWrites.CheckDestroy(ctx, child);
        }
        if (!keepAssets)
        {
            foreach (var child in children)
            {
                if (!child.IsDestroyed)
                    child.Destroy();
            }
            return;
        }
        var holder = AssetHolder(ctx, parent.Name.Value);
        foreach (var child in children)
        {
            if (!child.IsDestroyed)
                child.DestroyPreservingAssets(holder);
        }
        DropIfEmpty(holder);
    }

    // Destroys the slot and what is under it except the asset providers something outside it still uses,
    // which move, each on its own slot, under a holder named after it: for a world script one it makes under
    // the world root, for a Local script a local one under its own object. A guest's script, which makes
    // nothing that outlasts its author, leaves them under the world root instead. Everything under the slot
    // has to be its author's to destroy, or nothing changes. The script's own slot, or one above it, goes once
    // the call has returned, as with Destroy.
    public static void DestroyKeepingAssets(ScriptContext ctx, Slot slot)
    {
        if (ScriptWrites.HoldsSelf(ctx, slot))
        {
            if (ctx.IsLocal)
                throw new UnauthorizedAccessException("a Local script keeps assets under its own object, which this destroys");
            CheckTakeApart(ctx, slot, own: true);
            ctx.DestroyOwn(slot, keepAssets: true);
            return;
        }
        CheckTakeApart(ctx, slot);
        TakeApart(ctx, slot);
    }

    // What DestroyKeepingAssets asked for the script's own slot, as its call returns.
    internal static void DestroyOwnKeepingAssets(ScriptContext ctx, Slot slot)
    {
        CheckTakeApart(ctx, slot, own: true);
        TakeApart(ctx, slot);
    }

    private static void TakeApart(ScriptContext ctx, Slot slot)
    {
        var holder = AssetHolder(ctx, slot.Name.Value);
        slot.DestroyPreservingAssets(holder);
        DropIfEmpty(holder);
    }

    private static void CheckTakeApart(ScriptContext ctx, Slot slot, bool own = false)
    {
        ScriptWrites.CheckPark(ctx, slot, own);
        if (ctx.IsLocal)
            return;
        var registry = ctx.World.SlotRegistry;
        var pending = new Stack<Slot>();
        pending.Push(slot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.IsProtected || current.ActiveUserRoot != null)
                throw new UnauthorizedAccessException("scripts cannot destroy a protected slot or anything in someone's body or hands");
            if (registry != null && !registry.MayRemove(current.ReferenceID))
                throw new UnauthorizedAccessException("something under the slot is not its author's to destroy");
            ScriptWrites.Check(ctx, current.ParentSlotRef);
            foreach (var component in current.Components)
            {
                if (!component.IsDestroyed && !current.MayRemoveComponent(component))
                    throw new UnauthorizedAccessException("something under the slot is not its author's to destroy");
            }
            foreach (var child in current.Children)
                pending.Push(child);
        }
    }

    private static Slot AssetHolder(ScriptContext ctx, string? name)
    {
        string holderName = (string.IsNullOrEmpty(name) ? "Slot" : name) + " - Assets";
        if (ctx.IsLocal)
        {
            var home = ctx.Self ?? throw new InvalidOperationException("a Local script keeps assets under its own object");
            ctx.ReserveMade(1);
            var local = home.AddLocalSlot(holderName);
            ctx.Made(local, 1);
            return local;
        }
        var root = ctx.World.RootSlot;
        if (!ctx.Principal.IsWorldContent)
            return root;
        return ScriptWrites.MakeUnder(ctx, root, 1, () => root.AddSlot(holderName));
    }

    private static void DropIfEmpty(Slot holder)
    {
        if (!holder.IsRootSlot && !holder.IsDestroyed && holder.ChildCount == 0 && holder.LocalChildCount == 0
            && holder.ComponentCount == 0)
            holder.Destroy();
    }
}
