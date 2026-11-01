// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids;

// Which FluidPerson a reader trusts: minted by its wearer, fixed lengths, oldest first. WearerRule only ever sees
// later writes. A record that creates an element lands unjudged, so an editor someone let in could still attach a
// second, forged FluidPerson to their root, and a wearer could create its own with arrays of any length. Every reader
// therefore asks here first, and copies that fail are ignored and logged once each. -xlinka
public static class FluidProvenance
{
    private static readonly HashSet<RefID> _logged = new();

    [ThreadStatic]
    private static List<FluidPerson>? _scratch;

    // The user's trusted record in this world, or null when there is none yet.
    public static FluidPerson? Of(User? user)
    {
        var root = user?.Root;
        if (root == null || root.IsDestroyed)
            return null;
        var list = _scratch ??= new List<FluidPerson>();
        list.Clear();
        root.GetRegisteredComponents(list);
        FluidPerson? best = null;
        foreach (var person in list)
        {
            if (!IsTrusted(person, user, out var why))
            {
                LogOnce(person, why);
                continue;
            }
            if (best == null || person.ReferenceID.GetPosition() < best.ReferenceID.GetPosition())
                best = person;
        }
        list.Clear();
        return best;
    }

    public static bool IsTrusted(FluidPerson? person, User? wearer = null) => IsTrusted(person, wearer, out _);

    public static bool IsTrusted(FluidPerson? person, User? wearer, out string why)
    {
        why = string.Empty;
        if (person == null || person.IsDestroyed)
        {
            why = "gone";
            return false;
        }
        var holder = person.Slot?.ActiveUser;
        wearer ??= holder;
        if (wearer == null || !ReferenceEquals(holder, wearer))
        {
            why = "not under its wearer's root";
            return false;
        }
        if (person.ReferenceID.GetUserByte() != wearer.AllocationID.Value)
        {
            why = "not minted by its wearer";
            return false;
        }
        if (!HasFixedLengths(person))
        {
            why = "arrays not at their fixed lengths";
            return false;
        }
        if (person.Stick.IsDriven || person.Gpu.IsDriven)
        {
            why = "a member is driven";
            return false;
        }
        return true;
    }

    public static bool HasFixedLengths(FluidPerson person)
        => person.Spots.Count == FluidPerson.SpotCount
            && person.SpotMeta.Count == FluidPerson.SpotCount
            && person.Held.Count == FluidPerson.HeldCount
            && person.Pour.Count == FluidPerson.PourCount
            && person.Limbs.Count == FluidPerson.LimbCount
            && person.Touched.Count == FluidPerson.TouchedCount
            && person.Releases.Count == FluidPerson.ReleaseCount
            && person.Requests.Count == FluidPerson.RequestCount
            && person.Report.Count == FluidPerson.ReportCount;

    private static void LogOnce(FluidPerson person, string why)
    {
        lock (_logged)
        {
            if (!_logged.Add(person.ReferenceID))
                return;
        }
        LumoraLogger.Warn($"Fluids: ignoring a FluidPerson {person.ReferenceID} on {person.Slot?.ActiveUser?.UserName?.Value ?? "nobody"}: {why}.");
    }
}
