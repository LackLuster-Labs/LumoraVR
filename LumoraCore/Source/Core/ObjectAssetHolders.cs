// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

// What a saved object carries beside its tree: the assets it wears without holding them, and the slots those assets
// are made of. A load hangs them from the object itself, so they go wherever it goes and are destroyed with it, by
// whoever may destroy it. They used to go to the world root as "<name> - Assets" and "<name> - Dependencies", a pair
// for every spawn, and nothing took them away again: a room after an evening of avatars was full of them. -xlinka
public static class ObjectAssetHolders
{
    public const string AssetsName = "Assets";
    public const string DependenciesName = "Dependencies";

    private const string RootAssetsSuffix = " - Assets";
    private const string RootDependenciesSuffix = " - Dependencies";

    // The object's own holder when it brought one, else a new one. A holder saved before comes back without the
    // components that do not save, which travel beside the tree instead; they go back into it, not into a second. -xlinka
    internal static Slot For(Slot loaded, string name)
        => loaded.FindChild(name) is { IsDestroyed: false, IsLocalElement: false } own ? own : loaded.AddSlot(name);

    // An avatar put on out of the wrapper it came in takes along what the wrapper held for it: the holders whose
    // assets it wears. True when that leaves the wrapper nothing but leftovers, so it may go. A wrapper holding
    // anything else keeps it and stays standing, and the avatar wears from there as it did. -xlinka
    internal static bool TakeAlong(Slot wrapper, Slot avatar, Predicate<Slot> leftover)
    {
        var holders = new List<Slot>();
        foreach (var child in wrapper.Children)
        {
            if (child.IsDestroyed || leftover(child))
                continue;
            if (!IsHolder(child))
                return false;
            holders.Add(child);
        }
        if (holders.Count == 0)
            return true;
        var reached = ReachedFrom(Subtree(avatar), holders);
        bool emptied = true;
        for (int i = 0; i < holders.Count; i++)
        {
            if (reached[i] && holders[i].ParentSlotRef.IsWriteAllowedQuietly())
                holders[i].SetParent(avatar, preserveGlobalTransform: false);
            else
                emptied = false;
        }
        return emptied;
    }

    // Worlds saved before still carry the old ones at their root. On load, the ones in exactly the shape a load made
    // go when nothing in the world points into them any more: an assets holder with nothing but assets on it and no
    // children, a dependencies holder with nothing on it. Anything else of that name is the world's own. -xlinka
    internal static int SweepRoot(World world)
    {
        // Housekeeping on the way in: whatever goes wrong here leaves the world as it loaded, never fails the load. -xlinka
        try
        {
            return Sweep(world);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"World '{world?.Name}': the leftover asset holders at the root were left as they are: {ex.Message}");
            return 0;
        }
    }

    private static int Sweep(World world)
    {
        var root = world?.RootSlot;
        if (world == null || root == null || root.IsDestroyed)
            return 0;
        var leftovers = new List<Slot>();
        foreach (var child in root.Children)
        {
            if (IsRootLeftover(child))
                leftovers.Add(child);
        }
        if (leftovers.Count == 0)
            return 0;

        var inside = new HashSet<Slot>();
        foreach (var leftover in leftovers)
            inside.UnionWith(Subtree(leftover));
        var sources = new List<Slot>();
        foreach (var slot in Subtree(root, local: true))
        {
            if (!inside.Contains(slot))
                sources.Add(slot);
        }

        var reached = ReachedFrom(sources, leftovers);
        int removed = 0;
        for (int i = 0; i < leftovers.Count; i++)
        {
            if (reached[i] || !leftovers[i].MayDestroy())
                continue;
            try
            {
                leftovers[i].Destroy();
                removed++;
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        Logging.Logger.Log($"World '{world.Name}': removed {removed} of {leftovers.Count} asset holder(s) object loads left at the root, the rest still in use");
        return removed;
    }

    private static bool IsRootLeftover(Slot slot)
    {
        if (slot.IsDestroyed || slot.IsLocalElement || slot.IsProtected || slot.LocalChildCount > 0)
            return false;
        var name = slot.Name.Value ?? string.Empty;
        if (name.Length > RootAssetsSuffix.Length && name.EndsWith(RootAssetsSuffix, StringComparison.Ordinal))
            return slot.ChildCount == 0 && AssetsOnly(slot);
        if (name.Length > RootDependenciesSuffix.Length && name.EndsWith(RootDependenciesSuffix, StringComparison.Ordinal))
            return slot.ComponentCount == 0;
        return false;
    }

    private static bool IsHolder(Slot slot)
    {
        if (slot.IsLocalElement)
            return false;
        if (slot.Name.Value == DependenciesName && slot.ComponentCount == 0)
            return true;
        bool any = false;
        foreach (var held in Subtree(slot))
        {
            if (!AssetsOnly(held))
                return false;
            any |= held.ComponentCount > 0;
        }
        return any;
    }

    private static bool AssetsOnly(Slot slot)
    {
        foreach (var component in slot.Components)
        {
            if (component is not IAssetProvider && !component.PreserveWithAssets)
                return false;
        }
        return true;
    }

    // Which holders the sources point into, directly or through a holder they reach.
    private static bool[] ReachedFrom(IEnumerable<Slot> sources, List<Slot> holders)
    {
        var holderOf = new Dictionary<Slot, int>();
        for (int i = 0; i < holders.Count; i++)
        {
            foreach (var slot in Subtree(holders[i]))
                holderOf[slot] = i;
        }
        var reached = new bool[holders.Count];
        var pending = new Queue<int>();

        // Through its members only: where a slot hangs, and what hangs from it, is not something it uses. -xlinka
        void Follow(Worker worker, int from)
        {
            var parentRef = (worker as Slot)?.ParentSlotRef;
            for (int i = 0; i < worker.SyncMemberCount; i++)
            {
                var member = worker.GetSyncMember(i);
                if (member == null || ReferenceEquals(member, parentRef))
                    continue;
                foreach (var target in Worker.CollectMemberReferences(member, assetRefOnly: false, persistentOnly: false))
                {
                    if (SlotOf(target) is { } slot && holderOf.TryGetValue(slot, out int hit) && hit != from && !reached[hit])
                    {
                        reached[hit] = true;
                        pending.Enqueue(hit);
                    }
                }
            }
        }
        void FollowSlot(Slot slot, int from)
        {
            Follow(slot, from);
            foreach (var component in slot.Components)
                Follow(component, from);
        }

        foreach (var slot in sources)
            FollowSlot(slot, -1);
        while (pending.Count > 0)
        {
            int next = pending.Dequeue();
            foreach (var slot in Subtree(holders[next]))
                FollowSlot(slot, next);
        }
        return reached;
    }

    private static Slot? SlotOf(IWorldElement? element)
    {
        for (int depth = 0; element != null && depth < 64; depth++)
        {
            if (element is Slot slot)
                return slot;
            if (element is Component component)
                return component.Slot;
            element = (element as SyncElement)?.Parent;
        }
        return null;
    }

    private static List<Slot> Subtree(Slot top, bool local = false)
    {
        var slots = new List<Slot>();
        var pending = new Stack<Slot>();
        pending.Push(top);
        while (pending.Count > 0)
        {
            var slot = pending.Pop();
            if (slot.IsDestroyed)
                continue;
            slots.Add(slot);
            foreach (var child in slot.Children)
                pending.Push(child);
            if (local)
            {
                foreach (var child in slot.LocalChildren)
                    pending.Push(child);
            }
        }
        return slots;
    }
}
