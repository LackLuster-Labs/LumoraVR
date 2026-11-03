// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Components.Touch;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// What a script running on the host does to someone at another machine. Their body, locomotion and hands are
// driven by their own machine, so a write from the host is overwritten or refused; the host asks that machine
// instead, and it does it to its own person as their own movement would.
//
// Each machine mints its person's relay in its own byte under their user root while the world has scripts, as
// the undo relay is. The host writes numbered asks into it, host-only, once the scripts have run for the frame;
// the owner's machine does each new one and writes back the newest it took, which clears the ones before it
// here. What moves a person in one frame goes as one ask per kind; anything else past MaxAsksPerSecond for one
// person is dropped and the script warned. -xlinka
public sealed class ScriptUserAsks
{
    // As many as a touch relay is taken at, for the same reason: one person's machine at the other end.
    public const int MaxAsksPerSecond = TouchRelay.MaxActivationsPerSecond;

    // How long an ask waits in a relay for its person's machine to take it. One that gets there later than this
    // is too late to be right, and goes untaken.
    public const double KeepSeconds = 2.0;

    internal struct Request
    {
        public ScriptUserAskKind Kind;
        public float3 Vector;
        public floatQ Rotation;
        public float Amount;
        public float Seconds;
        public string? Text;
        public Slot? Target;
        public bool Flag;
        public Chirality Side;

        public Request(ScriptUserAskKind kind)
        {
            this = default;
            Kind = kind;
            Rotation = floatQ.Identity;
        }
    }

    private sealed class Person
    {
        public readonly List<Request> Waiting = new();
        public double WindowStart = double.NegativeInfinity;
        public int WindowCount;
    }

    private readonly World _world;
    private readonly List<ScriptUserRelay> _relays = new();
    private readonly Dictionary<User, Person> _people = new();
    private readonly Dictionary<int, double> _sentAt = new();
    private readonly List<User> _gone = new();
    private readonly List<int> _stale = new();
    private int _nextId;
    private bool _waiting;
    private double _nextSweep;
    private ScriptUserRelay? _mine;

    // Asks written, folded into one already waiting, and dropped past the rate.
    public int Sent { get; private set; }
    public int Folded { get; private set; }
    public int Dropped { get; private set; }

    internal ScriptUserAsks(World world)
    {
        _world = world;
    }

    internal void Register(ScriptUserRelay relay)
    {
        if (!_relays.Contains(relay))
            _relays.Add(relay);
    }

    internal void Unregister(ScriptUserRelay relay) => _relays.Remove(relay);

    // A script that runs on the host for everyone: a Shared one, or an Author one there.
    internal static bool OnHost(ScriptContext ctx) => !ctx.IsLocal && ctx.World.IsAuthority;

    // Whether what this script does to user goes to their own machine: it runs on the host, they are at another
    // machine, and the script may act on them at all, the world's on anyone and a person's on its author alone.
    internal static bool Away(ScriptContext ctx, [NotNullWhen(true)] User? user)
        => OnHost(ctx) && ScriptReads.Visible(ctx, user) && !ReferenceEquals(user, ctx.World.LocalUser) && ctx.MayActOn(user);

    // The node's half: the ask waits for the end of the script phase. False, and the call failed or warned, when
    // it cannot go.
    internal static bool Send(ScriptContext ctx, User user, Request request) => ctx.World.Scripts.UserAsks.Queue(ctx, user, request);

    private bool Queue(ScriptContext ctx, User user, Request request)
    {
        if (ScriptReads.RootOf(user) == null)
        {
            ctx.Fail("the user has no body here");
            return false;
        }
        if (RelayOf(user) == null)
        {
            ctx.Fail($"{ScriptReads.Named(user)}'s machine has not taken on this world's scripts yet");
            return false;
        }
        if (!_people.TryGetValue(user, out var person))
        {
            person = new Person();
            _people[user] = person;
        }
        if (Group(request.Kind) == 0)
            request.Vector = ScriptUserActions.Bounded(request.Vector);
        if (Fold(person.Waiting, request))
        {
            Folded++;
            return true;
        }
        if (Group(request.Kind) < 0)
        {
            double now = _world.Time.TotalTime;
            if (now - person.WindowStart >= 1.0)
            {
                person.WindowStart = now;
                person.WindowCount = 0;
            }
            if (++person.WindowCount > MaxAsksPerSecond)
            {
                Dropped++;
                ctx.Fail($"too many asks of {ScriptReads.Named(user)} in a second");
                ctx.Warn(ScriptWarning.People, $"more than {MaxAsksPerSecond} asks of {ScriptReads.Named(user)} in a second; the rest were dropped");
                return false;
            }
        }
        person.Waiting.Add(request);
        _waiting = true;
        return true;
    }

    // Movement kinds that fold into one ask a frame: a velocity, a scale, a place and a turn.
    private static int Group(ScriptUserAskKind kind) => kind switch
    {
        ScriptUserAskKind.Push or ScriptUserAskKind.SetVelocity => 0,
        ScriptUserAskKind.Scale => 1,
        ScriptUserAskKind.PlaceHips => 2,
        ScriptUserAskKind.TurnHips => 3,
        _ => -1,
    };

    // Into the one of its kind already waiting this frame, in that one's place: a push adds to the velocity
    // there, set or pushed, and anything else takes it over.
    private static bool Fold(List<Request> waiting, in Request request)
    {
        int group = Group(request.Kind);
        if (group < 0)
            return false;
        for (int i = 0; i < waiting.Count; i++)
        {
            var held = waiting[i];
            if (Group(held.Kind) != group)
                continue;
            if (request.Kind == ScriptUserAskKind.Push)
                held.Vector += request.Vector;
            else
                held = request;
            waiting[i] = held;
            return true;
        }
        return false;
    }

    // Once the scripts have run: what each person was asked this frame, into their relay.
    internal void Flush()
    {
        if (!_waiting)
            return;
        _waiting = false;
        double now = _world.Time.TotalTime;
        foreach (var (user, person) in _people)
        {
            if (person.Waiting.Count == 0)
                continue;
            if (!user.IsDestroyed && RelayOf(user) is { } relay)
            {
                // Host bookkeeping on the owner's own component, so it takes the system scope.
                using (_world.DataModelPermissions?.EnterSystemBypass())
                {
                    foreach (var request in person.Waiting)
                        Write(relay, request, now);
                }
            }
            person.Waiting.Clear();
        }
    }

    private void Write(ScriptUserRelay relay, in Request request, double now)
    {
        var ask = relay.Asks.Add();
        int id = ++_nextId;
        ask.Id.Value = id;
        ask.Kind.Value = request.Kind;
        ask.Vector.Value = request.Vector;
        ask.Rotation.Value = request.Rotation;
        ask.Amount.Value = request.Amount;
        ask.Seconds.Value = request.Seconds;
        ask.Text.Value = request.Text ?? string.Empty;
        ask.Target.Target = request.Target!;
        ask.Flag.Value = request.Flag;
        ask.Side.Value = request.Side;
        _sentAt[id] = now;
        Sent++;
    }

    internal void Update(double now)
    {
        if (!_world.IsAuthority)
        {
            // Made only in a world that has scripts, so joining a world without any makes nothing.
            if (_mine is not { IsDestroyed: false } && _world.ScriptsIfAny?.HostCount > 0)
                _mine = ScriptUserRelay.For(_world);
            return;
        }
        Flush();
        if (_people.Count > 0)
        {
            _gone.Clear();
            foreach (var (user, person) in _people)
            {
                if (user.IsDestroyed || (person.Waiting.Count == 0 && now - person.WindowStart > 1.0))
                    _gone.Add(user);
            }
            foreach (var user in _gone)
                _people.Remove(user);
        }
        for (int i = _relays.Count - 1; i >= 0; i--)
        {
            var relay = _relays[i];
            if (relay.IsDestroyed)
                _relays.RemoveAt(i);
            else if (relay.Asks.Count > 0)
                Clear(relay, now);
        }
        // What a relay that went with its person still had out.
        if (_sentAt.Count > 0 && now >= _nextSweep)
        {
            _nextSweep = now + KeepSeconds;
            _stale.Clear();
            foreach (var (id, at) in _sentAt)
            {
                if (now - at > 2 * KeepSeconds)
                    _stale.Add(id);
            }
            foreach (int id in _stale)
                _sentAt.Remove(id);
        }
    }

    // Asks the owner's machine took, and ones that waited past KeepSeconds, out of their relay.
    private void Clear(ScriptUserRelay relay, double now)
    {
        int taken = TakenBy(relay);
        var asks = relay.Asks;
        bool any = false;
        for (int i = 0; i < asks.Count && !any; i++)
        {
            int id = asks[i].Id.Value;
            any = id <= taken || !_sentAt.TryGetValue(id, out var at) || now - at > KeepSeconds;
        }
        if (!any)
            return;
        using (_world.DataModelPermissions?.EnterSystemBypass())
        {
            for (int i = asks.Count - 1; i >= 0; i--)
            {
                int id = asks[i].Id.Value;
                if (id > taken && _sentAt.TryGetValue(id, out var at) && now - at <= KeepSeconds)
                    continue;
                _sentAt.Remove(id);
                asks.RemoveAt(i);
            }
        }
    }

    // The newest ask the relay's owner says it took. Only their own write counts.
    private static int TakenBy(ScriptUserRelay relay)
        => relay.Taken.LastModifyingUser is { } writer && writer.AllocationID.Value == relay.ReferenceID.GetUserByte() ? relay.Taken.Value : 0;

    // The person's relay: in their byte, minted by their own machine rather than by their scripts.
    private ScriptUserRelay? RelayOf(User user)
    {
        byte owned = user.AllocationID.Value;
        var controller = _world.ReferenceController;
        for (int i = _relays.Count - 1; i >= 0; i--)
        {
            var relay = _relays[i];
            if (!relay.IsDestroyed && relay.ReferenceID.GetUserByte() == owned && controller?.BelowScriptFence(relay.ReferenceID) != false)
                return relay;
        }
        return null;
    }
}
