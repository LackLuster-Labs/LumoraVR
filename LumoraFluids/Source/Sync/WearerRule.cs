// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Networking.Sync;
using Lumora.Warden;

namespace Lumora.Fluids;

// The wearer check WearerSync and WearerArray share. A person's fluid record says what happened to their own body,
// so only their own machine may write it from the wire: the user whose root holds the member, read off the slot at
// the moment the write is judged. Roles, ownership and consent never come into it, because the engine's permission
// gate is never asked: a moderator, the host's own guest copy or someone the wearer let edit their avatar are all
// refused alike. Writes made on the host itself never reach a member's inbound rule; those are held by the engine's
// consent gate for another user's root and by no fluid code ever making one. -xlinka
public static class WearerRule
{
    // The person a member belongs to, or null when it is not under anyone's root (nobody may write it then).
    public static User? WearerOf(SyncElement member) => (member.Parent as Component)?.Slot?.ActiveUser;

    public static MessageValidity CheckSender(SyncElement member, User? sender)
    {
        var wearer = WearerOf(member);
        if (sender != null && wearer != null && ReferenceEquals(sender, wearer))
            return MessageValidity.Valid;
        member.World?.DataModelPermissions?.ReportDenial(sender, PermissionDenialKind.ForeignWrite, "fluid state of another user");
        return MessageValidity.Conflict;
    }

    // A wearer's record that tried to reshape its own array: refused, and worth reporting, since the honest client
    // never sends one.
    public static MessageValidity Reshaped(SyncElement member, User? sender, string what)
    {
        member.World?.DataModelPermissions?.ReportDenial(sender, PermissionDenialKind.ForeignWrite, "fluid array " + what);
        return MessageValidity.Conflict;
    }
}
