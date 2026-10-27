// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Variables;

// How a driver folds every zone it stands in into one value.
public enum ZoneValueMode
{
    // The zone with the highest priority wins; a deeper one breaks a tie.
    HighestPriority,
    // Each zone weighted by how deep inside it the point is, faded toward the default near the edges.
    WeightedAverage,
    // Lowest priority first, each one blended over the last by its weight.
    PrioritySortedBlend,
    // Every zone's value, scaled by its weight, added to the default.
    Additive,
}

// What a zone's blend distance is measured in.
public enum ZoneBlendMode
{
    // A fraction of the zone's own size.
    Ratio,
    // Metres in the zone's space.
    Absolute,
}

// What a gradient zone's start and end distances are measured in.
public enum ZoneDistanceMode
{
    // A fraction of the radius.
    Normalized,
    // Metres in the zone's space.
    Absolute,
}

public interface IValueZone
{
    string? ZoneName { get; }
    int Priority { get; }
    RefID ReferenceID { get; }
}

public interface IValueZone<T> : IValueZone
{
    // False when the point is outside, or the zone is switched off. Weight runs 0 at the edge to 1 once the
    // point is a blend distance inside.
    bool TrySample(in float3 globalPoint, out T value, out float weight);
}

// Named zones, per world. One list per name, kept and mutated in place, so a driver holds on to the list for
// its name and a zone coming or going costs nothing on the sampling side. -xlinka
public static class ValueZoneRegistry
{
    private sealed class Table
    {
        public readonly Dictionary<string, List<IValueZone>> ByName = new(System.StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<World, Table> Tables = new();

    public static List<IValueZone> ListFor(World world, string name)
    {
        var table = Tables.GetValue(world, static _ => new Table());
        if (!table.ByName.TryGetValue(name, out var list))
            table.ByName[name] = list = new List<IValueZone>();
        return list;
    }

    // The zones under a name, without keeping a list for a name nothing has registered.
    public static List<IValueZone>? Find(World world, string name)
        => Tables.TryGetValue(world, out var table) && table.ByName.TryGetValue(name, out var list) ? list : null;

    public static void Add(World world, string name, IValueZone zone)
    {
        var list = ListFor(world, name);
        if (!list.Contains(zone))
            list.Add(zone);
    }

    public static void Remove(World world, string name, IValueZone zone)
    {
        if (Tables.TryGetValue(world, out var table) && table.ByName.TryGetValue(name, out var list))
            list.Remove(zone);
    }

    // The name a zone or driver goes by, or null when it has none worth registering.
    public static string? Filter(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return name.Trim();
    }
}
