// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Picks the keeper on the host, at 1 Hz and on joins, leaves, focus and tier changes. The keeper is the one GPU
// machine whose simulation the ledger follows; everyone else reconciles toward the ledger.
//
// A candidate is present, has a trusted FluidPerson whose Gpu says a tier above None, is focused on this world, has a
// healthy budget and is drawing in real time, may keep under the world's KeeperAccess, and has not been benched by
// the report judge. The host comes first when it qualifies (no relay hop, nothing on the wire twice); then the
// current keeper while it still qualifies; then the best tier, the nearest to the fluid, the longest present.
//
// Losing health or the real-time bit costs the current keeper its role only after 5 s, so a budget that jitters does
// not make the role flap; losing focus, leaving or dropping to no GPU costs it at once. A host that only qualifies in
// the background keeps the role while nobody focused does. A held container's keeper is its holder when the holder
// qualifies. With no candidate the keeper is null and the cells stay as they are, which only happens when no
// machine in the world can simulate. -xlinka
public sealed class FluidKeeperElection
{
    public const double Interval = 1.0;
    public const double Hysteresis = 5.0;
    public const double FailWindow = 30.0;
    public const int FailLimit = 3;
    public const double Bench = 300.0;

    private readonly Dictionary<User, double> _firstSeen = new();
    private readonly Dictionary<User, double> _lapsedSince = new();
    private readonly Dictionary<User, List<double>> _failures = new();
    private readonly Dictionary<User, double> _benchedUntil = new();
    private readonly List<User> _users = new();
    private double _lastElection = double.NegativeInfinity;
    private long _signature;

    public FluidKeeperElection(World world)
    {
        World = world;
    }

    public World World { get; }

    public User? WorldKeeper { get; private set; }

    public int Elections { get; private set; }

    // The report judge's verdict: three failures in 30 s bench a user for 5 minutes and call an election.
    public void NoteFailure(User user, double now)
    {
        if (user == null)
            return;
        if (!_failures.TryGetValue(user, out var times))
            _failures[user] = times = new List<double>();
        times.Add(now);
        times.RemoveAll(t => now - t > FailWindow);
        if (times.Count >= FailLimit)
        {
            _benchedUntil[user] = now + Bench;
            times.Clear();
            _lastElection = double.NegativeInfinity;
        }
    }

    public bool IsBenched(User user, double now) => _benchedUntil.TryGetValue(user, out double until) && now < until;

    public static FluidGpuTier TierOf(int gpu) => (FluidGpuTier)Math.Clamp(gpu & FluidPerson.GpuTierMask, 0, (int)FluidGpuTier.Ultra);

    public static int PackGpu(FluidGpuTier tier, bool focused, bool healthy, bool realTime)
        => ((int)tier & FluidPerson.GpuTierMask) | (focused ? FluidPerson.GpuFocused : 0)
            | (healthy ? FluidPerson.GpuHealthy : 0) | (realTime ? FluidPerson.GpuRealTime : 0);

    // Host, every update. Elects when a second has passed or a candidate's standing changed.
    public bool Update(double now, IReadOnlyList<FluidSystem> systems, FluidWorldRules? rules)
    {
        if (!World.IsAuthority)
            return false;
        _users.Clear();
        World.GetAllUsers(_users);
        long signature = _users.Count;
        foreach (var user in _users)
        {
            if (!_firstSeen.ContainsKey(user))
                _firstSeen[user] = now;
            var person = FluidProvenance.Of(user);
            signature = signature * 31 + (person?.Gpu.Value ?? -1) + user.ReferenceID.GetUserByte() * 1009L;
        }
        // A container changing hands is an election too: its keeper follows the holder.
        foreach (var system in systems)
            if (system != null && !system.IsDestroyed)
                signature = signature * 31 + (long)(HolderOf(system)?.ReferenceID.RawValue ?? 0);
        foreach (var gone in new List<User>(_firstSeen.Keys))
        {
            if (!_users.Contains(gone))
            {
                _firstSeen.Remove(gone);
                _lapsedSince.Remove(gone);
            }
        }
        bool changed = signature != _signature;
        _signature = signature;
        if (!changed && now - _lastElection < Interval)
            return false;
        _lastElection = now;
        Elections++;
        Elect(now, systems, rules);
        return true;
    }

    private void Elect(double now, IReadOnlyList<FluidSystem> systems, FluidWorldRules? rules)
    {
        var host = World.LocalUser;
        var current = WorldKeeper;
        if (current != null && !_users.Contains(current))
            current = null;

        User? pick = null;
        if (host != null && Qualifies(host, now, rules, systems, requireFocus: true, keeper: ReferenceEquals(host, current)))
            pick = host;
        else if (current != null && Qualifies(current, now, rules, systems, requireFocus: true, keeper: true))
            pick = current;
        else
        {
            var centre = FluidCentre(systems);
            double bestScore = double.NegativeInfinity;
            foreach (var user in _users)
            {
                if (!Qualifies(user, now, rules, systems, requireFocus: true, keeper: false))
                    continue;
                var tier = TierOf(FluidProvenance.Of(user)!.Gpu.Value);
                float distance = centre is { } c && user.Root?.Slot is { } slot ? float3.Distance(slot.GlobalPosition, c) : 0f;
                double score = (int)tier * 1e6 - Math.Min(distance, 9999f) * 10.0 + (now - _firstSeen.GetValueOrDefault(user, now)) * 1e-3;
                if (score > bestScore)
                {
                    bestScore = score;
                    pick = user;
                }
            }
            if (pick == null && host != null && Qualifies(host, now, rules, systems, requireFocus: false, keeper: ReferenceEquals(host, current)))
                pick = host;
        }

        WorldKeeper = pick;
        using (World.DataModelPermissions?.EnterSystemBypass())
        {
            foreach (var system in systems)
            {
                if (system == null || system.IsDestroyed)
                    continue;
                var keeper = pick;
                var holder = HolderOf(system);
                if (holder != null && Qualifies(holder, now, rules, systems, requireFocus: true, keeper: ReferenceEquals(holder, system.Keeper.Target)))
                    keeper = holder;
                if (!ReferenceEquals(system.Keeper.Target, keeper))
                {
                    system.Keeper.Target = keeper!;
                    system.KeeperSince.Value = now;
                }
            }
        }
    }

    // Who is carrying a system around: a held container sits under its holder's root.
    public static User? HolderOf(FluidSystem system) => system.Slot?.ActiveUser;

    public bool Qualifies(User user, double now, FluidWorldRules? rules, IReadOnlyList<FluidSystem> systems, bool requireFocus, bool keeper)
    {
        var person = FluidProvenance.Of(user);
        if (person == null || IsBenched(user, now))
        {
            _lapsedSince.Remove(user);
            return false;
        }
        int gpu = person.Gpu.Value;
        if (TierOf(gpu) == FluidGpuTier.None)
            return false;
        if (requireFocus && (gpu & FluidPerson.GpuFocused) == 0)
            return false;
        if (rules != null && rules.KeeperAccess.Value == FluidKeeperAccess.Builders)
        {
            foreach (var system in systems)
                if (system != null && !system.IsDestroyed && !FluidRequestJudge.MayEditSystem(World, user, system))
                    return false;
        }
        bool fit = (gpu & FluidPerson.GpuHealthy) != 0 && (gpu & FluidPerson.GpuRealTime) != 0;
        if (fit)
        {
            _lapsedSince.Remove(user);
            return true;
        }
        if (!keeper)
            return false;
        if (!_lapsedSince.TryGetValue(user, out double since))
            _lapsedSince[user] = since = now;
        return now - since < Hysteresis;
    }

    private static float3? FluidCentre(IReadOnlyList<FluidSystem> systems)
    {
        float3 sum = default;
        int n = 0;
        foreach (var system in systems)
        {
            if (system?.Slot == null || system.IsDestroyed)
                continue;
            sum += system.Slot.LocalPointToGlobal(system.BoundsCenter.Value);
            n++;
        }
        return n == 0 ? null : sum / n;
    }
}
