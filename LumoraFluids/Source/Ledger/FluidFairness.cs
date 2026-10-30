// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;

namespace Lumora.Fluids;

// Per-person shares and the world limit, applied as commands, from the per-origin totals in the cells, so a headless
// host enforces them with no GPU. Past PersonShare a person's oldest loose poured fluid evaporates first; script emits
// do not count unless the world sets ShareCountsScripts. WorldLitres does the same for the world's total, only when the
// world sets it. Neither ever refuses anything, a Loom call least of all: they take the oldest back out. A leaver's
// pours fade over 5 s when the world keeps RemoveOnLeave on; the world's own fluid stays. An origin already asked to
// evaporate is left alone for 3 s while the keeper's next report shows it. -xlinka
public sealed class FluidFairness
{
    public const double Interval = 1.0;
    public const double InFlight = 3.0;
    public const float FadeSeconds = 5f;

    private readonly Dictionary<(FluidSystem, int), double> _asked = new();
    private readonly Dictionary<int, double> _scratch = new();
    private double _last = double.NegativeInfinity;

    public FluidFairness(World world)
    {
        World = world;
    }

    public World World { get; }

    public int Evaporations { get; private set; }

    public int Fades { get; private set; }

    public void Update(double now, FluidWorldRules? rules, IReadOnlyList<FluidLedger> systems, Action<FluidSystem, FluidCommand> issue)
    {
        if (!World.IsAuthority || now - _last < Interval)
            return;
        _last = now;
        float share = rules?.PersonShare.Value ?? 20f;
        bool scripts = rules?.ShareCountsScripts.Value ?? false;
        float worldLimit = rules?.WorldLitres.Value ?? 0f;

        // Every poured origin with its litres, oldest first per person.
        var pours = new List<(ulong User, int Number, FluidLedger Ledger, int Origin, double Litres)>();
        double worldTotal = 0;
        foreach (var ledger in systems)
        {
            ledger.OriginLitres(_scratch);
            foreach (var (origin, litres) in _scratch)
            {
                worldTotal += litres;
                if (origin == 0)
                    continue;
                var entry = ledger.OriginEntry(origin);
                ulong user = FluidLedger.OriginUser(entry);
                if (user == 0 || (FluidLedger.OriginIsScript(entry) && !scripts))
                    continue;
                if (_asked.TryGetValue((ledger.System, origin), out double at) && now - at < InFlight)
                    continue;
                pours.Add((user, FluidLedger.OriginNumber(entry), ledger, origin, litres));
            }
        }
        pours.Sort((a, b) => a.User != b.User ? a.User.CompareTo(b.User) : a.Number.CompareTo(b.Number));

        if (share > 0f)
        {
            var byUser = new Dictionary<ulong, double>();
            foreach (var p in pours)
                byUser[p.User] = byUser.GetValueOrDefault(p.User) + p.Litres;
            foreach (var (user, total) in byUser)
            {
                double over = total - share;
                foreach (var p in pours)
                {
                    if (over <= 0.001)
                        break;
                    if (p.User != user)
                        continue;
                    over -= Evaporate(p.Ledger, issue, p.Origin, Math.Min(over, p.Litres), now);
                }
            }
        }

        if (worldLimit > 0f && worldTotal > worldLimit)
        {
            double over = worldTotal - worldLimit;
            pours.Sort((a, b) => a.Number.CompareTo(b.Number));
            foreach (var p in pours)
            {
                if (over <= 0.001)
                    break;
                if (_asked.TryGetValue((p.Ledger.System, p.Origin), out double at) && now - at < InFlight)
                    continue;
                over -= Evaporate(p.Ledger, issue, p.Origin, Math.Min(over, p.Litres), now);
            }
        }
    }

    private double Evaporate(FluidLedger ledger, Action<FluidSystem, FluidCommand> issue, int origin, double litres, double now)
    {
        issue(ledger.System, new FluidCommand(FluidCommandKind.Evaporate, origin, default, 0f, (float)litres, 0, 0));
        _asked[(ledger.System, origin)] = now;
        Evaporations++;
        return litres;
    }

    // A leaver's poured origins fade, in every system. The world's own and authored fluid stays.
    public void UserLeft(ulong user, FluidWorldRules? rules, IReadOnlyList<FluidLedger> systems, Action<FluidSystem, FluidCommand> issue)
    {
        if (!World.IsAuthority || !(rules?.RemoveOnLeave.Value ?? true))
            return;
        foreach (var ledger in systems)
        {
            foreach (int origin in FluidRequestJudge.OriginsOf(ledger, user))
            {
                issue(ledger.System, new FluidCommand(FluidCommandKind.FadeOrigin, origin, default, FadeSeconds, 0f, 0, 0));
                ledger.MarkOriginFading(origin);
                Fades++;
            }
        }
    }
}
