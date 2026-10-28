// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using DiscordRPC;

namespace Lumora.Source.Godot.Bootstrap;

// Closed: a Private world. Its host's door only takes accounts on the host's invite book, and a Discord ask or
// invite names a Discord user, never one of ours, so there is nothing to put on the book for it. The presence
// carries no join secret for one, so Discord offers neither.
public sealed record DiscordPresenceState(string SessionId, string Name, string Mode, int Users, int MaxUsers,
    bool Public, bool AllowJoin, bool Networked, IReadOnlyList<Uri> Addresses, bool Closed = false);

public static class DiscordPresence
{
    public static RichPresence Build(DiscordPresenceState? world, DateTime started, string version)
    {
        bool isPublic = world is { Public: true, Networked: true };
        int users = Math.Max(1, world?.Users ?? 1);
        int capacity = world?.Networked == true ? Math.Max(users, world.MaxUsers) : users;
        string identity = world?.SessionId ?? "userspace";
        if (string.IsNullOrWhiteSpace(identity)) identity = "local";
        if (Encoding.UTF8.GetByteCount(identity) > 128)
            identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var presence = new RichPresence
        {
            Details = isPublic ? Text(string.IsNullOrWhiteSpace(world!.Name) ? "Unnamed world" : world.Name) : null,
            State = world == null ? "In Userspace" : isPublic ? "Public world" : "In a private world",
            Timestamps = new Timestamps(started),
            Assets = new Assets
            {
                LargeImageKey = "lumoravr",
                LargeImageText = Text("LumoraVR " + version),
                SmallImageKey = isPublic ? "status_public" : "status_private",
                SmallImageText = world == null ? "Userspace" : Text((isPublic ? "Public" : "Private") + " world · " + world.Mode),
            },
            Party = new Party
            {
                ID = identity, Size = users, Max = capacity,
                Privacy = isPublic ? Party.PrivacySetting.Public : Party.PrivacySetting.Private,
            },
        };
        if (world is { Networked: true, AllowJoin: true, Closed: false } && users < capacity
            && DiscordJoinLink.BestAddress(world.Addresses) is { } address)
            presence.Secrets = new Secrets { JoinSecret = address.AbsoluteUri };
        return presence;
    }

    // Discord limits these fields in UTF-8 bytes, not UTF-16 characters.
    private static string Text(string value)
    {
        var result = new StringBuilder();
        int bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 128) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
