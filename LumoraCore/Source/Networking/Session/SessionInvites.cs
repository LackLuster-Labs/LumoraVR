// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Lumora.Core.Management;

namespace Lumora.Core.Networking.Session;

// How an invite to a world gets someone past its door.
//   Open    the door lets them in already (anyone with an account, or anyone on the LAN); nothing to arrange
//   Grant   the door would refuse them, so the host puts the account on its book first
//   Closed  this user may not invite here (Bar says why)
public enum InviteWay
{
    Open,
    Grant,
    Closed,
}

public enum InviteBar
{
    None,
    NoSession,
    Home,
    SignedOut,
    // Private and LAN worlds: only the host lets anyone in, and only the host can list the world for it
    HostOnly,
    // Friends and members-only worlds: the host and co-hosts
    HostTeam,
    // Friends+: the host, co-hosts and the host's friends, since the level is meant to take friends of friends
    HostsFriends,
}

public enum InviteAnswer
{
    Pending,
    Granted,
    Refused,
}

public readonly struct InviteRight
{
    public readonly InviteWay Way;
    public readonly InviteBar Bar;
    // the asker is the host itself, so a grant goes straight onto the book
    public readonly bool ByHost;
    // a world no directory lists: its host lists it, hidden, before the first invite can name it
    public readonly bool NeedsListing;

    public InviteRight(InviteWay way, InviteBar bar, bool byHost, bool needsListing)
    {
        Way = way;
        Bar = bar;
        ByHost = byHost;
        NeedsListing = needsListing;
    }

    public bool Allowed => Way != InviteWay.Closed;

    public static InviteRight No(InviteBar bar) => new(InviteWay.Closed, bar, false, false);
}

// INVITES, AS THE SESSION KNOWS THEM
//
// Who may invite is the host's rule, worked out the same way on every machine from the world's access level,
// the inviter's role and, for Friends+, the host's friends list. An inviter's own machine works it out to know
// what to offer; the host works it out again for every ask and acts only on its own answer.
//
// The way an ask reaches the host is the inviter's own User.InviteAsks record, which only that machine may
// write. The host answers on its own user's InviteBook: the account, until when (wall clock, unix seconds), who
// asked and which ask, granted or refused. The door (SessionConnectionManager) reads the book on the host and
// nothing else, so a joiner can never talk its way in by claiming it was invited, and an account on the book
// gets in only until its line runs out. -xlinka
public static class SessionInvites
{
    // matches the service's SocialLimits.InviteMinutes, so the card and the door run out together
    public const int GrantMinutes = 60;
    public const int AskDepth = 8;
    public const int BookDepth = 64;
    // grants one asker may hold at once; past this the host turns their asks down
    public const int AsksPerUser = 16;
    private const int RefusedSeconds = 120;
    private const int MaxIdLength = 64;
    private const int MaxRecordChars = BookDepth * (MaxIdLength * 2 + 40);
    // the role's identifier; screens call it Co-host
    public const string CoHostRole = "Admin";
    private const string NoOne = "-";

    private sealed class AskerState
    {
        public int LastSeen;
    }

    private static readonly ConditionalWeakTable<User, AskerState> Askers = new();

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // RULES

    public static InviteRight RightOf(World? world, User? user)
    {
        if (world == null || world.IsDestroyed || world.IsDisposed || world.IsUserspace || world.Session == null)
            return InviteRight.No(InviteBar.NoSession);
        if (WorldManager.IsLocalHome(world))
            return InviteRight.No(InviteBar.Home);
        if (user == null || user.IsDestroyed || string.IsNullOrEmpty(user.AccountId?.Value))
            return InviteRight.No(InviteBar.SignedOut);

        var config = world.Configuration;
        var level = config?.AccessLevel.Value ?? World.WorldAccessLevel.Private;
        bool host = user.IsHost;
        bool coHost = !host && string.Equals(world.DataModelPermissions?.GetRole(user)?.Name, CoHostRole, StringComparison.Ordinal);
        switch (level)
        {
            case World.WorldAccessLevel.Anyone:
            case World.WorldAccessLevel.GroupPublic:
            case World.WorldAccessLevel.RegisteredUsers:
                return new InviteRight(InviteWay.Open, InviteBar.None, host, false);
            case World.WorldAccessLevel.Contacts:
            case World.WorldAccessLevel.GroupMembers:
            case World.WorldAccessLevel.GroupPlus:
                return host || coHost ? new InviteRight(InviteWay.Grant, InviteBar.None, host, false) : InviteRight.No(InviteBar.HostTeam);
            case World.WorldAccessLevel.ContactsPlus:
                return host || coHost || IsHostsFriend(world, user)
                    ? new InviteRight(InviteWay.Grant, InviteBar.None, host, false)
                    : InviteRight.No(InviteBar.HostsFriends);
            case World.WorldAccessLevel.LAN:
                return host ? new InviteRight(InviteWay.Open, InviteBar.None, true, true) : InviteRight.No(InviteBar.HostOnly);
            default:
                // Private has a door: the host's book is the way past it
                return host ? new InviteRight(InviteWay.Grant, InviteBar.None, true, true) : InviteRight.No(InviteBar.HostOnly);
        }
    }

    // On the host, from its own friends list; on the user's own machine, from theirs (a friendship is both ways).
    private static bool IsHostsFriend(World world, User user)
    {
        var social = Engine.Current?.Social;
        if (social == null || !social.IsReady)
            return false;
        if (world.IsAuthority)
            return social.IsFriend(user.AccountId.Value);
        if (!ReferenceEquals(user, world.LocalUser))
            return false;
        var host = HostOf(world);
        return host != null && !string.IsNullOrEmpty(host.AccountId.Value) && social.IsFriend(host.AccountId.Value);
    }

    public static User? HostOf(World? world)
    {
        if (world == null || world.IsDestroyed)
            return null;
        if (world.IsAuthority)
            return world.LocalUser;
        foreach (var user in world.GetAllUsers())
        {
            if (user != null && !user.IsDestroyed && user.IsHost)
                return user;
        }
        return null;
    }

    // THE INVITER'S SIDE

    // Puts accountId on this machine's ask record and answers its number, 0 when it could not. Call on the
    // world's own update. On the host it goes straight onto the book and answers as granted at once.
    public static int Ask(World world, string accountId)
    {
        var user = world?.LocalUser;
        if (world == null || user == null || user.IsDestroyed || !IsId(accountId))
            return 0;
        if (world.IsAuthority)
        {
            Grant(world, accountId, user.AccountId.Value, 0);
            return -1;
        }
        var asks = Parse(user.InviteAsks.Value, out int highest);
        int number = highest + 1;
        asks.Add((number, accountId));
        while (asks.Count > AskDepth)
            asks.RemoveAt(0);
        var text = new StringBuilder();
        foreach (var (n, id) in asks)
            text.Append(n).Append(' ').Append(id).Append('\n');
        user.InviteAsks.Value = text.ToString();
        return number;
    }

    // How the host answered ask `number` of this machine's user; -1 is the host's own and always granted.
    public static InviteAnswer AnswerFor(World world, int number)
    {
        if (number == -1)
            return InviteAnswer.Granted;
        var me = world?.LocalUser;
        var host = HostOf(world);
        if (me == null || host == null || number <= 0)
            return InviteAnswer.Pending;
        string mine = me.AccountId.Value ?? string.Empty;
        foreach (var line in ReadBook(host.InviteBook.Value))
        {
            if (line.Number == number && string.Equals(line.By, mine, StringComparison.Ordinal))
                return line.Granted ? InviteAnswer.Granted : InviteAnswer.Refused;
        }
        return InviteAnswer.Pending;
    }

    // THE HOST'S SIDE

    // Every ask on asker's record it has not answered yet, answered by the host's own rule for that user now.
    public static void TakeAsks(World world, User asker)
    {
        if (world == null || !world.IsAuthority || asker == null || asker.IsDestroyed || ReferenceEquals(asker, world.LocalUser))
            return;
        var state = Askers.GetOrCreateValue(asker);
        var asks = Parse(asker.InviteAsks.Value, out _);
        string by = asker.AccountId.Value ?? string.Empty;
        foreach (var (number, accountId) in asks)
        {
            if (number <= state.LastSeen)
                continue;
            state.LastSeen = number;
            var right = RightOf(world, asker);
            bool ok = right.Way == InviteWay.Grant && by.Length > 0 && LiveGrantsBy(world, by) < AsksPerUser;
            if (ok)
                Grant(world, accountId, by, number);
            else
                Refuse(world, accountId, by, number);
        }
    }

    // Host only. Puts accountId on the book until GrantMinutes from now, over any line it already had.
    public static void Grant(World world, string accountId, string? by, int number)
        => Write(world, accountId, by, number, Now() + GrantMinutes * 60L, true);

    private static void Refuse(World world, string accountId, string? by, int number)
        => Write(world, accountId, by, number, Now() + RefusedSeconds, false);

    private static void Write(World world, string accountId, string? by, int number, long until, bool granted)
    {
        var host = world?.LocalUser;
        if (world == null || !world.IsAuthority || host == null || host.IsDestroyed || !IsId(accountId))
            return;
        string asker = IsId(by) ? by! : NoOne;
        long now = Now();
        var lines = new List<BookLine>();
        foreach (var line in ReadBook(host.InviteBook.Value))
        {
            if (line.Until <= now)
                continue;
            // a fresh grant replaces the account's old one; an answer to an ask replaces nothing else
            if (granted && line.Granted && string.Equals(line.AccountId, accountId, StringComparison.Ordinal))
                continue;
            lines.Add(line);
        }
        lines.Add(new BookLine(accountId, until, asker, number, granted));
        while (lines.Count > BookDepth)
        {
            int drop = lines.FindIndex(l => !l.Granted);
            lines.RemoveAt(drop >= 0 ? drop : 0);
        }
        var text = new StringBuilder();
        foreach (var line in lines)
            text.Append(line.AccountId).Append(' ').Append(line.Until).Append(' ').Append(line.By).Append(' ')
                .Append(line.Number).Append(' ').Append(line.Granted ? 'g' : 'r').Append('\n');
        host.InviteBook.Value = text.ToString();
    }

    // The door: whether the host's book lets accountId in right now. Host only.
    public static bool Admits(World? world, string? accountId)
    {
        var host = world?.LocalUser;
        if (world == null || !world.IsAuthority || host == null || !IsId(accountId))
            return false;
        long now = Now();
        foreach (var line in ReadBook(host.InviteBook.Value))
        {
            if (line.Granted && line.Until > now && string.Equals(line.AccountId, accountId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static int LiveGrantsBy(World world, string by)
    {
        long now = Now();
        int count = 0;
        foreach (var line in ReadBook(world.LocalUser?.InviteBook.Value))
        {
            if (line.Granted && line.Until > now && string.Equals(line.By, by, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    // RECORDS

    private readonly record struct BookLine(string AccountId, long Until, string By, int Number, bool Granted);

    private static List<BookLine> ReadBook(string? text)
    {
        var lines = new List<BookLine>();
        if (string.IsNullOrEmpty(text) || text.Length > MaxRecordChars)
            return lines;
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(' ');
            if (parts.Length != 5 || !IsId(parts[0]) || !long.TryParse(parts[1], out long until)
                || !(parts[2] == NoOne || IsId(parts[2])) || !int.TryParse(parts[3], out int number)
                || parts[4] is not ("g" or "r"))
                continue;
            lines.Add(new BookLine(parts[0], until, parts[2], number, parts[4] == "g"));
        }
        return lines;
    }

    private static List<(int Number, string AccountId)> Parse(string? text, out int highest)
    {
        highest = 0;
        var asks = new List<(int, string)>();
        if (string.IsNullOrEmpty(text) || text.Length > AskDepth * (MaxIdLength + 16))
            return asks;
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int space = raw.IndexOf(' ');
            if (space <= 0 || !int.TryParse(raw.AsSpan(0, space), out int number) || number <= 0)
                continue;
            string id = raw.Substring(space + 1);
            if (!IsId(id))
                continue;
            asks.Add((number, id));
            if (number > highest)
                highest = number;
        }
        return asks;
    }

    private static bool IsId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength || id == NoOne)
            return false;
        foreach (char c in id)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
                return false;
        }
        return true;
    }
}
