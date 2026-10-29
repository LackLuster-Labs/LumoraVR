// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Lumora.Core.Components;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Scripting;

// What a script may look at. A slot or user from another world, one already gone, or a slot local to
// the machine running the script reads as nothing: a script on the host is not the host. -xlinka
internal static class ScriptReads
{
    // A Local script also sees the local slots under its own object: the ones it made on this machine.
    public static bool Visible(ScriptContext ctx, Slot? slot)
        => slot != null && !slot.IsDestroyed && ReferenceEquals(slot.World, ctx.World)
            && (!slot.IsLocalElement || (ctx.IsLocal && ctx.Self != null && slot.IsDescendantOf(ctx.Self)));

    public static bool Visible(ScriptContext ctx, User? user)
        => user != null && !user.IsDestroyed && ReferenceEquals(user.World, ctx.World);

    public static bool Visible(ScriptContext ctx, Component? component)
        => component != null && !component.IsDestroyed && Visible(ctx, component.Slot);

    // Any world object a script is handed, by what holds it: a slot or user as themselves, a component by
    // its slot, a member by the object it belongs to.
    public static bool VisibleElement(ScriptContext ctx, IWorldElement? element)
    {
        for (int depth = 0; element != null && depth < 16; depth++)
        {
            switch (element)
            {
                case Slot slot:
                    return Visible(ctx, slot);
                case User user:
                    return Visible(ctx, user);
                case Component component:
                    return Visible(ctx, component);
                case SyncElement member:
                    if (member.IsDestroyed || !ReferenceEquals(member.World, ctx.World))
                        return false;
                    element = member.Parent;
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    // Visible, and when not, the failure of the action asking: which thing was missing, gone or out of reach.
    public static bool Reached(ScriptContext ctx, [NotNullWhen(true)] Slot? slot, string what = "slot")
    {
        if (Visible(ctx, slot))
            return true;
        ctx.Fail(slot == null ? $"no {what}"
            : slot.IsDestroyed ? $"the {what} is gone"
            : !ReferenceEquals(slot.World, ctx.World) ? $"the {what} is in another world"
            : ctx.IsLocal ? $"{Named(slot)} is not under this script's object"
            : $"{Named(slot)} is only on one machine");
        return false;
    }

    public static bool Reached(ScriptContext ctx, [NotNullWhen(true)] User? user, string what = "user")
    {
        if (Visible(ctx, user))
            return true;
        ctx.Fail(user == null ? $"no {what}" : user.IsDestroyed ? $"the {what} has left" : $"the {what} is in another world");
        return false;
    }

    public static bool Reached(ScriptContext ctx, [NotNullWhen(true)] Component? component, string what = "component")
    {
        if (component != null && !component.IsDestroyed)
            return Reached(ctx, component.Slot, what + "'s slot");
        ctx.Fail(component == null ? $"no {what}" : $"the {what} is gone");
        return false;
    }

    public static bool ReachedElement(ScriptContext ctx, [NotNullWhen(true)] IWorldElement? element, string what)
    {
        if (VisibleElement(ctx, element))
            return true;
        ctx.Fail(element == null ? $"no {what}"
            : element.IsDestroyed ? $"the {what} is gone"
            : !ReferenceEquals(element.World, ctx.World) ? $"the {what} is in another world"
            : $"the {what} is out of this script's reach");
        return false;
    }

    // Whether user is the person at this machine, for a node that only ever moves its own person. Anyone else
    // fails only where no other machine runs the flow: a Local script runs on every machine, each for its own.
    public static bool HereFor(ScriptContext ctx, [NotNullWhen(true)] User? user)
    {
        if (ctx.IsMachineUser(user))
            return true;
        if (Reached(ctx, user) && !ctx.IsLocal)
            ctx.Fail(ctx.MayActOn(user) ? $"{Named(user)} is not the user at this machine" : $"a person's script acts only on its author, not {Named(user)}");
        return false;
    }

    // How a failure names what an element belongs to: its slot's name, or a user's.
    public static string Named(IWorldElement? element)
    {
        for (int depth = 0; element != null && depth < 16; depth++)
        {
            switch (element)
            {
                case Slot slot:
                    return string.IsNullOrEmpty(slot.Name.Value) ? "a slot with no name" : $"'{slot.Name.Value}'";
                case User user:
                    return string.IsNullOrEmpty(user.UserName.Value) ? "a user with no name" : $"'{user.UserName.Value}'";
                case Component component:
                    element = component.Slot;
                    break;
                case SyncElement member:
                    element = member.Parent;
                    break;
                default:
                    return "it";
            }
        }
        return "it";
    }

    // A reference field whose target a script may read: one it can see, and not host-only.
    public static bool Readable(ScriptContext ctx, ISyncRef? reference)
        => reference is SyncElement member && VisibleElement(ctx, member)
            && member is not ConflictingSyncElement { IsHostOnly: true };

    // Part of someone's body: under a user root, and not something they are holding. A person is found
    // once, by their root, instead of once per limb.
    public static bool IsWornPart(Slot slot)
    {
        var root = slot.ActiveUserRoot?.Slot;
        if (root == null)
            return false;
        for (var current = slot; current != null && !ReferenceEquals(current, root); current = current.Parent)
        {
            if (current.GetComponent<Grabbable>() is { IsGrabbed: true })
                return false;
        }
        return true;
    }

    // A user's root as any machine can find it. User.Root is filled in on the host and for the local user
    // only; the replicated reference is there everywhere.
    public static UserRoot? RootOf(User? user)
    {
        if (user == null || user.IsDestroyed)
            return null;
        var root = user.Root ?? user.UserRootRef.Target;
        return root is { IsDestroyed: false } ? root : null;
    }

    // The user whose user root this slot is.
    public static User? BodyOf(Slot? slot)
        => slot?.ActiveUserRoot is { } root && ReferenceEquals(root.Slot, slot) && root.ActiveUser is { IsDestroyed: false } user ? user : null;

    // The users in the session, the host included.
    public static List<User> Users(ScriptContext ctx)
    {
        var users = ctx.UserScratch;
        ctx.World.GetAllUsers(users);
        users.RemoveAll(user => user.IsDestroyed);
        return users;
    }
}
