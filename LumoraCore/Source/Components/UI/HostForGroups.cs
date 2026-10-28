// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Localization;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// One group as a create form offers it: the id the world is stamped with, the short tag that rides the
// session and the card, the name the pill shows, and the tier the group wants its worlds to start on.
internal readonly struct HostGroupOption
{
    public readonly string Id;
    public readonly string Tag;
    public readonly string Name;
    public readonly bool MembersOnly;

    public HostGroupOption(string id, string tag, string name, bool membersOnly = true)
    {
        Id = id ?? string.Empty;
        Tag = tag ?? string.Empty;
        Name = name ?? string.Empty;
        MembersOnly = membersOnly;
    }

    public bool IsEmpty => Id.Length == 0;

    // The tag when there is one, else the name. A group with no tag still has to be pickable.
    public string Label => Tag.Length > 0 ? Tag : Name;
}

// The "Host for" row is the same row on the Home dialog and on the Worlds create page, so the list behind
// it and the words on it live once. The fetch is the caller's own /groups/mine read, filtered to the
// groups they may actually speak for; nothing here decides anything the service would not, and nothing
// here is what lets a world in - the world's door reads the HOST's roster, not this. -xlinka
internal static class HostForGroups
{
    public static readonly LocaleText Label = "Worlds.HostFor".AsLocale("Host for");
    public static readonly LocaleText Nobody = "Worlds.HostFor.Nobody".AsLocale("Nobody");

    public static LocaleText DefaultWorldName(string groupName)
        => "Worlds.HostFor.DefaultName".AsLocale("{0} world", groupName);

    // Builder and above. That is the rung the service already requires to announce an event for a group,
    // and hosting a world in the group's name is the louder version of the same statement.
    public static bool CanHost(string? myRole)
        => string.Equals(myRole, "Owner", StringComparison.OrdinalIgnoreCase)
            || string.Equals(myRole, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(myRole, "Moderator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(myRole, "Builder", StringComparison.OrdinalIgnoreCase);

    // The tier a group keeps for the worlds hosted for it. Blank or unknown, from a service too old to keep
    // one, reads as members only, the tier the forms always started on.
    public static World.WorldAccessLevel GroupDefault(string? level)
        => Enum.TryParse<World.WorldAccessLevel>(level, ignoreCase: true, out var parsed)
            ? SettingsCatalog.GroupJoinLevel.Clamp(parsed)
            : SettingsCatalog.GroupJoinLevel.Default;

    public static bool MembersOnlyByDefault(string? level)
        => GroupDefault(level) != World.WorldAccessLevel.GroupPublic;

    public static bool SignedIn
    {
        get
        {
            var client = Engine.Current?.CDNClient;
            return client != null && client.IsAuthenticated;
        }
    }

    // An empty list is the honest answer to signed out, to a failed read, and to "you are in no group you
    // could host for". The row is simply not shown for all three; a form that offered a group and then
    // refused the host would be worse than one that never offered it.
    public static async Task<List<HostGroupOption>> FetchAsync()
    {
        var options = new List<HostGroupOption>();
        var client = Engine.Current?.CDNClient;
        if (client == null || !client.IsAuthenticated)
            return options;

        ApiResponse<List<GroupInfo>>? mine = null;
        try
        {
            mine = await client.GetMyGroups();
        }
        catch (Exception)
        {
            return options;
        }

        if (mine is not { Success: true, Data: not null })
            return options;

        for (int i = 0; i < mine.Data.Count; i++)
        {
            var group = mine.Data[i];
            if (group == null || string.IsNullOrEmpty(group.Id) || !CanHost(group.MyRole))
                continue;
            options.Add(new HostGroupOption(group.Id, group.Tag, group.Name, MembersOnlyByDefault(group.DefaultJoinLevel)));
        }
        return options;
    }

    // Every group the caller belongs to, by id, for the Worlds tab's Groups filter. Membership at any
    // rung counts here: this is "worlds my groups are hosting", not "worlds I could host".
    public static async Task<HashSet<string>> FetchMyGroupIdsAsync()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var client = Engine.Current?.CDNClient;
        if (client == null || !client.IsAuthenticated)
            return ids;

        ApiResponse<List<GroupInfo>>? mine = null;
        try
        {
            mine = await client.GetMyGroups();
        }
        catch (Exception)
        {
            return ids;
        }

        if (mine is not { Success: true, Data: not null })
            return ids;

        for (int i = 0; i < mine.Data.Count; i++)
        {
            var group = mine.Data[i];
            if (group != null && !string.IsNullOrEmpty(group.Id))
                ids.Add(group.Id);
        }
        return ids;
    }
}
