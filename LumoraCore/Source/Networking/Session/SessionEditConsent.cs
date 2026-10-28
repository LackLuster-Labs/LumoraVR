// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Lumora.Core.Networking.Session;

// Where an asker stands with a person, as any machine reads it off the two records.
public enum EditAskState
{
    // nothing asked, or the last answer has run out
    None,
    // asked, no answer yet
    Pending,
    // they said yes, for this session
    Granted,
    // they said no: no new ask until the wait is over
    Denied,
    // no answer, or a yes taken back: a short wait before asking again
    Waiting,
}

// Puts what someone asked in front of this machine's user, and tells them how their own asks went. The engine's is
// the dashboard one; with no dashboard (a headless run) asks wait in PendingAskers until answered there.
public interface IEditConsentPrompter
{
    void Show(World world, User asker);

    // That ask is over: answered, timed out, or the asker left.
    void Withdraw(World world, User asker);

    // How this machine's user stands with owner changed from was to state: a yes, a no, no answer, or a yes taken back.
    void Answered(World world, User owner, EditAskState was, EditAskState state);
}

// EDITING SOMEBODY ELSE, AS THE SESSION KNOWS IT
//
// What hangs under a user's root is theirs and the gate refuses everyone else (PermissionEngine's person boundary).
// The way past it is that person's own yes, and this is where the yes lives and how it is asked for.
//
// The asker's machine puts a numbered line on its own User.EditAsks naming whom it asks and when. The person's machine
// reads it, holds back anything that looks like spam (one open ask per asker, a wait after a no, a few askers at once,
// one ask per asker every few seconds) and asks its own user. The answer goes on the person's own User.EditGrants:
// "g" yes, "d" no until a time, "w" a short wait (no answer, held back, or a yes taken back), each carrying the number
// of the ask it answers. Only that machine writes it, the host passes it on like any member, and every gate reads it,
// the host's included, so what the asker claims counts for nothing. A yes names the asker's user record of this
// session, so it ends when either leaves: the person's record goes with them, and someone who comes back is a new
// record nobody said yes to. -xlinka
public static class SessionEditConsent
{
    public const int AskDepth = 4;
    public const int RecordDepth = 24;
    // how long a no holds, a wait holds, and an ask stays up on the person's screen
    public const int DeniedSeconds = 180;
    public const int WaitSeconds = 60;
    public const int OpenSeconds = 60;
    // distinct askers waiting on one person at once; past this the rest are held back
    public const int MaxOpenAsks = 3;
    private const int AskGapSeconds = 10;
    // an ask nobody answered stops reading as pending after this, whatever happened to it
    private const int PendingSeconds = OpenSeconds + 30;
    private const int MaxRecordChars = RecordDepth * 64;
    private const int MaxAsksChars = AskDepth * 48;

    private static IEditConsentPrompter? s_prompter;

    public static IEditConsentPrompter Prompter
    {
        get => s_prompter ??= new Components.UI.EditConsentDashboardPrompter();
        set => s_prompter = value;
    }

    // Raised on a world's own thread when either record changed: screens showing who may edit whom look again.
    public static event Action<World, User>? Changed;

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static ulong IdOf(User user) => user.ReferenceID.RawValue;

    // RECORDS

    private readonly record struct Reply(char Kind, ulong Id, long Until, int Number);

    private readonly record struct AskLine(int Number, ulong Id, long At);

    private static List<Reply> ReadAnswers(string? text)
    {
        var lines = new List<Reply>();
        if (string.IsNullOrEmpty(text) || text.Length > MaxRecordChars)
            return lines;
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(' ');
            if (parts.Length != 4 || parts[0].Length != 1 || parts[0][0] is not ('g' or 'd' or 'w')
                || !ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id) || id == 0
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long until)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                continue;
            lines.Add(new Reply(parts[0][0], id, until, number));
        }
        return lines;
    }

    private static string WriteAnswers(List<Reply> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
            text.Append(line.Kind).Append(' ').Append(line.Id.ToString("X", CultureInfo.InvariantCulture)).Append(' ')
                .Append(line.Until.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(line.Number.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return text.ToString();
    }

    private static List<AskLine> ReadAsks(string? text, out int highest)
    {
        highest = 0;
        var asks = new List<AskLine>();
        if (string.IsNullOrEmpty(text) || text.Length > MaxAsksChars)
            return asks;
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(' ');
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number <= 0
                || !ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id) || id == 0
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at))
                continue;
            asks.Add(new AskLine(number, id, at));
            if (number > highest)
                highest = number;
        }
        return asks;
    }

    // THE GATE'S QUESTION
    //
    // Asked on every refused-looking write into someone else, on whatever thread validates it, so the parsed record
    // is kept per person and swapped whole when the text changes.
    private sealed class Parsed
    {
        public readonly string Text;
        public readonly ulong[] Granted;

        public Parsed(string text, ulong[] granted)
        {
            Text = text;
            Granted = granted;
        }
    }

    private sealed class GrantCache
    {
        public volatile Parsed? Last;
    }

    private static readonly ConditionalWeakTable<User, GrantCache> Grants = new();

    public static bool Allows(User? owner, ulong actorId)
    {
        if (owner == null || owner.IsDestroyed || actorId == 0)
            return false;
        var text = owner.EditGrants.Value;
        if (string.IsNullOrEmpty(text))
            return false;
        var cache = Grants.GetOrCreateValue(owner);
        var parsed = cache.Last;
        if (parsed == null || !ReferenceEquals(parsed.Text, text))
        {
            var granted = new List<ulong>();
            foreach (var line in ReadAnswers(text))
            {
                if (line.Kind == 'g')
                    granted.Add(line.Id);
            }
            parsed = new Parsed(text, granted.ToArray());
            cache.Last = parsed;
        }
        foreach (var id in parsed.Granted)
        {
            if (id == actorId)
                return true;
        }
        return false;
    }

    public static bool Allows(User? owner, User? actor) => actor != null && Allows(owner, IdOf(actor));

    // The person whose body a slot is part of, when that is not viewer: their body, their worn avatar, what they hold.
    public static User? PersonOf(Slot? slot, User? viewer)
    {
        var person = slot?.ActiveUserRoot?.ActiveUser;
        return person == null || person.IsDestroyed || ReferenceEquals(person, viewer) ? null : person;
    }

    // Whether viewer may not change slot because it is part of somebody who has not said yes to them. A person's
    // root slot itself stays theirs even with a yes.
    public static bool Refuses(Slot? slot, User? viewer)
    {
        var person = PersonOf(slot, viewer);
        if (person == null)
            return false;
        return ReferenceEquals(slot, person.Root?.Slot) || viewer == null || !Allows(person, viewer);
    }

    // THE ASKER'S SIDE

    // Where asker stands with owner, read off both records the same way on every machine.
    public static EditAskState StateOf(User? asker, User? owner)
    {
        if (asker == null || owner == null || asker.IsDestroyed || owner.IsDestroyed || ReferenceEquals(asker, owner))
            return EditAskState.None;
        ulong me = IdOf(asker);
        long now = Now();
        Reply? answer = null;
        foreach (var line in ReadAnswers(owner.EditGrants.Value))
        {
            if (line.Id == me)
                answer = line;
        }
        if (answer is { Kind: 'g' })
            return EditAskState.Granted;
        if (answer is { } held && held.Until > now)
            return held.Kind == 'd' ? EditAskState.Denied : EditAskState.Waiting;

        AskLine? latest = null;
        ulong them = IdOf(owner);
        foreach (var ask in ReadAsks(asker.EditAsks.Value, out _))
        {
            if (ask.Id == them && (latest == null || ask.Number > latest.Value.Number))
                latest = ask;
        }
        if (latest is not { } open || open.Number <= (answer?.Number ?? 0) || now - open.At > PendingSeconds)
            return EditAskState.None;
        // asked while a no or a wait still stood: that ask was never put to them
        if (answer is { } old && open.At < old.Until)
            return EditAskState.None;
        return EditAskState.Pending;
    }

    public static EditAskState StateOf(World? world, User? owner) => StateOf(world?.LocalUser, owner);

    // Seconds until a no or a wait runs out, 0 when none stands.
    public static long SecondsLeft(User? asker, User? owner)
    {
        if (asker == null || owner == null)
            return 0;
        ulong me = IdOf(asker);
        long now = Now();
        foreach (var line in ReadAnswers(owner.EditGrants.Value))
        {
            if (line.Id == me && line.Kind != 'g' && line.Until > now)
                return line.Until - now;
        }
        return 0;
    }

    // Puts an ask for owner on this machine's own record. Call on the world's own update. False when this user may not
    // ask now: it is their own body, the answer is already yes, an ask is open, or a no or a wait still stands.
    public static bool Ask(World? world, User? owner)
    {
        var me = world?.LocalUser;
        if (world == null || me == null || owner == null || me.IsDestroyed || owner.IsDestroyed || ReferenceEquals(me, owner)
            || owner.World != world || world.IsUserspace)
            return false;
        if (StateOf(me, owner) != EditAskState.None)
            return false;
        var asks = ReadAsks(me.EditAsks.Value, out int highest);
        asks.Add(new AskLine(highest + 1, IdOf(owner), Now()));
        while (asks.Count > AskDepth)
            asks.RemoveAt(0);
        var text = new StringBuilder();
        foreach (var ask in asks)
            text.Append(ask.Number.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(ask.Id.ToString("X", CultureInfo.InvariantCulture)).Append(' ')
                .Append(ask.At.ToString(CultureInfo.InvariantCulture)).Append('\n');
        me.EditAsks.Value = text.ToString();
        SideOf(world).Known[IdOf(owner)] = EditAskState.Pending;
        return true;
    }

    // THE PERSON'S SIDE

    // What this machine keeps per session world: as the person asked, who has an ask up and what was last seen of each
    // asker's record; as the asker, how each person stood last time, so an answer is told once.
    private sealed class Side
    {
        public readonly Dictionary<ulong, int> LastSeen = new();
        public readonly Dictionary<ulong, long> LastPutAt = new();
        public readonly List<(User Asker, int Number, long Opened)> Open = new();
        public readonly Dictionary<ulong, EditAskState> Known = new();
    }

    private static readonly ConditionalWeakTable<World, Side> Sides = new();

    private static Side SideOf(World world) => Sides.GetValue(world, static _ => new Side());

    // Who is waiting on this machine's user for an answer, oldest first.
    public static IReadOnlyList<User> PendingAskers(World? world)
    {
        var list = new List<User>();
        if (world == null || !Sides.TryGetValue(world, out var side))
            return list;
        foreach (var open in side.Open)
            list.Add(open.Asker);
        return list;
    }

    // The people this person said yes to who are still here.
    public static List<User> GrantedBy(User? owner)
    {
        var list = new List<User>();
        var world = owner?.World;
        if (owner == null || world == null || owner.IsDestroyed)
            return list;
        foreach (var user in world.GetAllUsers())
        {
            if (user != null && !user.IsDestroyed && !ReferenceEquals(user, owner) && Allows(owner, IdOf(user)))
                list.Add(user);
        }
        return list;
    }

    internal static void AsksChanged(User asker)
    {
        var world = asker?.World;
        if (world == null || world.IsDestroyed || world.IsDisposed || world.IsUserspace)
            return;
        world.RunSynchronously(() =>
        {
            if (asker!.IsDestroyed || world.IsDestroyed)
                return;
            TakeAsks(world, asker);
            Changed?.Invoke(world, asker);
        });
    }

    internal static void GrantsChanged(User owner)
    {
        var world = owner?.World;
        if (world == null || world.IsDestroyed || world.IsDisposed || world.IsUserspace)
            return;
        // A yes taken back takes with it the drives and scripts that leaned on it.
        world.DataModelPermissions?.RightsChanged();
        world.RunSynchronously(() =>
        {
            if (owner!.IsDestroyed || world.IsDestroyed)
                return;
            TellAsker(world, owner);
            Changed?.Invoke(world, owner);
        });
    }

    // Every new ask on asker's record that names this machine's user, held to the spam rules and put to them.
    private static void TakeAsks(World world, User asker)
    {
        var me = world.LocalUser;
        if (me == null || me.IsDestroyed || ReferenceEquals(asker, me))
            return;
        var side = SideOf(world);
        ulong them = IdOf(asker);
        side.LastSeen.TryGetValue(them, out int seen);
        int newest = 0;
        ulong mine = IdOf(me);
        foreach (var ask in ReadAsks(asker.EditAsks.Value, out _))
        {
            if (ask.Number <= seen)
                continue;
            seen = System.Math.Max(seen, ask.Number);
            if (ask.Id == mine)
                newest = System.Math.Max(newest, ask.Number);
        }
        side.LastSeen[them] = seen;
        if (newest > 0)
            Consider(world, me, asker, newest, side);
    }

    private static void Consider(World world, User me, User asker, int number, Side side)
    {
        ulong them = IdOf(asker);
        long now = Now();
        if (Allows(me, them))
            return;
        foreach (var line in ReadAnswers(me.EditGrants.Value))
        {
            // a no or a wait still stands: the ask is not put to them, and the asker already reads why
            if (line.Id == them && line.Kind != 'g' && line.Until > now)
                return;
        }
        foreach (var open in side.Open)
        {
            if (ReferenceEquals(open.Asker, asker))
                return;
        }
        bool tooSoon = side.LastPutAt.TryGetValue(them, out long last) && now - last < AskGapSeconds;
        if (tooSoon || side.Open.Count >= MaxOpenAsks)
        {
            Put(world, me, them, 'w', now + WaitSeconds, number);
            return;
        }
        side.LastPutAt[them] = now;
        side.Open.Add((asker, number, now));
        Prompter.Show(world, asker);
    }

    // This machine's user answers asker. Call on the world's own update.
    public static bool Answer(World? world, User? asker, bool allow, bool remember = false)
    {
        var me = world?.LocalUser;
        if (world == null || me == null || asker == null || asker.IsDestroyed || ReferenceEquals(asker, me) || asker.World != world)
            return false;
        if (remember && (!allow || !RememberedEditConsent.Remember(world, asker)))
            return false;
        if (!allow)
            RememberedEditConsent.Forget(me, asker.AccountId.Value);
        int number = TakeOpen(world, asker);
        if (number == 0)
            number = LatestAskFrom(asker, me);
        Put(world, me, IdOf(asker), allow ? 'g' : 'd', allow ? 0 : Now() + DeniedSeconds, number);
        return true;
    }

    // The ask went unanswered: closed without a choice, or up too long.
    public static void Unanswered(World? world, User? asker)
    {
        var me = world?.LocalUser;
        if (world == null || me == null || asker == null || ReferenceEquals(asker, me))
            return;
        int number = TakeOpen(world, asker);
        if (number == 0 || asker.IsDestroyed)
            return;
        Put(world, me, IdOf(asker), 'w', Now() + WaitSeconds, number);
    }

    // This machine's user takes their yes to grantee back.
    public static bool Revoke(World? world, User? grantee)
    {
        var me = world?.LocalUser;
        if (world == null || me == null || grantee == null || ReferenceEquals(grantee, me) || !Allows(me, IdOf(grantee)))
            return false;
        RememberedEditConsent.Forget(me, grantee.AccountId.Value);
        Put(world, me, IdOf(grantee), 'w', Now() + WaitSeconds, LatestAskFrom(grantee, me));
        return true;
    }

    // Signing out or switching accounts stops restored grants without deleting the former account's
    // remembered choices. The next owner of this device session starts with their own choices. -xlinka
    internal static void RevokeRememberedSession(World world, User grantee)
    {
        var me = world.LocalUser;
        if (me != null && !grantee.IsDestroyed && grantee.World == world && Allows(me, IdOf(grantee)))
            Put(world, me, IdOf(grantee), 'w', Now() + WaitSeconds, LatestAskFrom(grantee, me));
    }

    private static int TakeOpen(World world, User asker)
    {
        if (!Sides.TryGetValue(world, out var side))
            return 0;
        int number = 0;
        for (int i = side.Open.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(side.Open[i].Asker, asker))
                continue;
            number = System.Math.Max(number, side.Open[i].Number);
            side.Open.RemoveAt(i);
        }
        if (number > 0)
            Prompter.Withdraw(world, asker);
        return number;
    }

    private static int LatestAskFrom(User asker, User owner)
    {
        int number = 0;
        ulong them = IdOf(owner);
        foreach (var ask in ReadAsks(asker.EditAsks.Value, out _))
        {
            if (ask.Id == them)
                number = System.Math.Max(number, ask.Number);
        }
        return number;
    }

    // Rewrites this machine's own record with one answer for id in place of any it had, dropping waits that ran out
    // and yeses to people who are gone.
    private static void Put(World world, User me, ulong id, char kind, long until, int number)
    {
        if (!ReferenceEquals(me, world.LocalUser))
            return;
        long now = Now();
        var present = PresentIds(world);
        var lines = new List<Reply>();
        foreach (var line in ReadAnswers(me.EditGrants.Value))
        {
            if (line.Id == id || (line.Kind != 'g' && line.Until <= now) || !present.Contains(line.Id))
                continue;
            lines.Add(line);
        }
        lines.Add(new Reply(kind, id, until, number));
        while (lines.Count > RecordDepth)
        {
            int drop = lines.FindIndex(l => l.Kind != 'g');
            lines.RemoveAt(drop >= 0 ? drop : 0);
        }
        var text = WriteAnswers(lines);
        if (me.EditGrants.Value != text)
            me.EditGrants.Value = text;
    }

    private static HashSet<ulong> PresentIds(World world)
    {
        var ids = new HashSet<ulong>();
        foreach (var user in world.GetAllUsers())
        {
            if (user != null && !user.IsDestroyed)
                ids.Add(IdOf(user));
        }
        return ids;
    }

    // Twice a second on every machine in a session: asks up too long go unanswered, asks from people who left are
    // taken down, and this user's yeses to people who left come off the record.
    internal static void Upkeep(World world)
    {
        var me = world.LocalUser;
        if (me == null || me.IsDestroyed || world.IsUserspace)
            return;
        long now = Now();
        var present = PresentIds(world);
        if (Sides.TryGetValue(world, out var side))
        {
            for (int i = side.Open.Count - 1; i >= 0; i--)
            {
                var (asker, _, opened) = side.Open[i];
                if (asker.IsDestroyed || asker.World != world || !present.Contains(IdOf(asker)))
                {
                    side.Open.RemoveAt(i);
                    Prompter.Withdraw(world, asker);
                }
                else if (now - opened > OpenSeconds)
                {
                    Unanswered(world, asker);
                }
            }
        }

        RememberedEditConsent.Apply(world);
        var text = me.EditGrants.Value;
        if (string.IsNullOrEmpty(text))
            return;
        foreach (var line in ReadAnswers(text))
        {
            if (!present.Contains(line.Id) || (line.Kind != 'g' && line.Until <= now))
            {
                var kept = new List<Reply>();
                foreach (var keep in ReadAnswers(text))
                {
                    if (present.Contains(keep.Id) && (keep.Kind == 'g' || keep.Until > now))
                        kept.Add(keep);
                }
                me.EditGrants.Value = WriteAnswers(kept);
                return;
            }
        }
    }

    // On the asker's machine: an answer from owner came in, or a yes went away.
    private static void TellAsker(World world, User owner)
    {
        var me = world.LocalUser;
        if (me == null || me.IsDestroyed || ReferenceEquals(me, owner))
            return;
        var side = SideOf(world);
        var state = StateOf(me, owner);
        ulong them = IdOf(owner);
        side.Known.TryGetValue(them, out var was);
        side.Known[them] = state;
        if (state == was)
            return;
        bool answered = was == EditAskState.Pending && state is EditAskState.Granted or EditAskState.Denied or EditAskState.Waiting;
        bool lost = was == EditAskState.Granted;
        if (answered || lost || state == EditAskState.Granted)
            Prompter.Answered(world, owner, was, state);
    }
}
