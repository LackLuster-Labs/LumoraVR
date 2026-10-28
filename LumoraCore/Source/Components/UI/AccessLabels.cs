// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Localization;

namespace Lumora.Core.Components.UI;

// Who can join a world, in the words every screen uses for it. The enums keep their member names
// (Contacts, ContactsPlus): saves, the wire and the session directory all carry them.
//
// An ordinary world offers five levels and a world hosted for a group offers two, members only or anyone.
// Group+, Group Public on an ordinary world and Registered Users were levels nothing behind the door could
// tell apart from their neighbours, so no screen offers them; a world saved on one still reads correctly.
// -xlinka
public static class AccessLabels
{
    public static readonly LocaleText Private = "Access.Private".AsLocale("Private");
    public static readonly LocaleText Lan = "Access.LAN".AsLocale("LAN");
    public static readonly LocaleText Friends = "Access.Friends".AsLocale("Friends");
    public static readonly LocaleText FriendsPlus = "Access.FriendsPlus".AsLocale("Friends+");
    public static readonly LocaleText Anyone = "Access.Anyone".AsLocale("Anyone");
    public static readonly LocaleText MembersOnly = "Access.MembersOnly".AsLocale("Members only");
    public static readonly LocaleText GroupPlus = "Access.GroupPlus".AsLocale("Group+");
    public static readonly LocaleText SignedIn = "Access.SignedIn".AsLocale("Signed in");

    // What each level does today, said plainly. Friends lists do not exist yet, and the session directory
    // never lists a friends-only session to anybody, so the hint says that rather than promising a list.
    public static readonly LocaleText HintPrivate = "Access.Hint.Private".AsLocale("Not listed anywhere. Only you, your co-hosts and people you invite get in.");
    public static readonly LocaleText HintLan = "Access.Hint.LAN".AsLocale("Listed to people on your local network.");
    public static readonly LocaleText HintFriends =
        "Access.Hint.Friends".AsLocale("Unlisted until friends lists arrive.");
    public static readonly LocaleText HintAnyone = "Access.Hint.Anyone".AsLocale("Listed for everyone.");
    public static readonly LocaleText HintMembersOnly =
        "Access.Hint.MembersOnly".AsLocale("Listed for everyone. Only the group's members get in.");

    // The same sets the join level defaults choose from, so a default is always a level the screens offer.
    public static IReadOnlyList<World.WorldAccessLevel> Offered(bool hostedForGroup)
        => hostedForGroup ? SettingsCatalog.GroupJoinLevel.Options : SettingsCatalog.DefaultJoinLevel.Options;

    public static LocaleText Label(World.WorldAccessLevel level) => level switch
    {
        World.WorldAccessLevel.Private => Private,
        World.WorldAccessLevel.LAN => Lan,
        World.WorldAccessLevel.Contacts => Friends,
        World.WorldAccessLevel.ContactsPlus => FriendsPlus,
        World.WorldAccessLevel.GroupMembers => MembersOnly,
        World.WorldAccessLevel.GroupPlus => GroupPlus,
        World.WorldAccessLevel.RegisteredUsers => SignedIn,
        _ => Anyone,
    };

    public static LocaleText Hint(World.WorldAccessLevel level, bool hostedForGroup) => level switch
    {
        World.WorldAccessLevel.Private => HintPrivate,
        World.WorldAccessLevel.LAN => HintLan,
        World.WorldAccessLevel.Contacts or World.WorldAccessLevel.ContactsPlus => HintFriends,
        World.WorldAccessLevel.GroupMembers or World.WorldAccessLevel.GroupPlus
            => hostedForGroup ? HintMembersOnly : HintPrivate,
        _ => HintAnyone,
    };

    public static LocaleText GroupLabel(bool membersOnly) => membersOnly ? MembersOnly : Anyone;
}
