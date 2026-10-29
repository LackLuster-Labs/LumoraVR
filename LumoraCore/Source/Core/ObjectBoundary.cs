// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Reflection;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

// Where one object ends, for the permission gate: the slot standing for a whole object (an avatar root, an
// Object Root or a grabbable), and which of what sits inside a worn avatar is the wearer's body machinery
// rather than the avatar's own content. Facts only; the gate decides what they allow. -xlinka
internal static class ObjectBoundary
{
    // What the IK solvers call the hitbox slots they fit to the bones.
    private const string BodyColliderSlot = "BodyCollider";

    private static readonly ConcurrentDictionary<Type, bool> UsersFamily = new();

    public static Slot? SlotOf(IWorldElement? element)
    {
        IWorldElement? node = element;
        for (int depth = 0; node != null && node is not Slot && depth < 8; depth++)
            node = (node as Component)?.Slot ?? (node as SyncElement)?.Parent ?? (node as Worker)?.Parent;
        return node as Slot;
    }

    public static bool IsObjectRoot(Slot slot)
        => slot.GetComponent<AvatarForm>() != null || slot.GetComponent<ObjectRoot>() != null || slot.GetComponent<Grabbable>() != null;

    // A hitbox an IK solver fitted to a bone: it stands for the bone it hangs off.
    public static bool IsBodyHitbox(Slot slot) => slot.SlotName.Value == BodyColliderSlot;

    // Up from a's slot to the first object root that holds b as well. A user root or the world root ends the
    // walk: nothing at or above either is part of anyone's object.
    public static Slot? SharedRoot(IWorldElement a, IWorldElement? b)
    {
        var from = SlotOf(a);
        var other = SlotOf(b);
        if (from == null || other == null)
            return null;
        for (var at = from; at != null && !at.IsRootSlot; at = at.Parent)
        {
            if (at.GetComponent<UserRoot>() != null)
                return null;
            if (IsObjectRoot(at) && (ReferenceEquals(other, at) || other.IsDescendantOf(at)))
                return at;
        }
        return null;
    }

    // A component of the user and avatar families, anything else on a slot that carries one (bar an object
    // root, whose own content sits beside its avatar form), and the IK hitboxes.
    public static bool IsWearerMachinery(IWorldElement element)
    {
        var component = OwningComponent(element);
        if (component != null && InUsersFamily(component.GetType()))
            return true;
        var slot = component?.Slot ?? SlotOf(element);
        if (slot == null)
            return false;
        if (slot.SlotName.Value == BodyColliderSlot)
            return true;
        if (IsObjectRoot(slot))
            return false;
        foreach (var other in slot.Components)
        {
            if (InUsersFamily(other.GetType()))
                return true;
        }
        return false;
    }

    private static Component? OwningComponent(IWorldElement element)
    {
        IWorldElement? node = element;
        for (int depth = 0; node != null && depth < 8; depth++)
        {
            switch (node)
            {
                case Component component:
                    return component;
                case Slot:
                    return null;
                case SyncElement member:
                    node = member.Parent;
                    break;
                case Worker worker:
                    node = worker.Parent;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    private static bool InUsersFamily(Type type) => UsersFamily.GetOrAdd(type, static t =>
        t.GetCustomAttribute<ComponentCategoryAttribute>()?.Category is { } category
        && (category == "Users" || category.StartsWith("Users/", StringComparison.Ordinal)));
}
