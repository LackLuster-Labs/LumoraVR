// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// What the importer keeps of a package for its node graphs: the node components it does not attach, where
// every slot sat in the package, and which of our members each of the package's member ids became.
internal sealed class PackageNodeInput
{
    public readonly List<PackageNodeRecord> Nodes = new();
    public readonly Dictionary<string, PackageSlotRecord> Slots = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, PackageMemberRecord> Members = new(StringComparer.OrdinalIgnoreCase);

    // Where each slot stood among its siblings in the package, whether it was on, and whether it was a node's
    // card, by the package's slot id.
    public readonly Dictionary<string, PackageSlotFacts> SlotFacts = new(StringComparer.OrdinalIgnoreCase);

    // The members of components that were not attached, by id, with their component's type: only to say what a
    // graph reached for that did not come across.
    public readonly Dictionary<string, string> Unattached = new(StringComparer.OrdinalIgnoreCase);

    // The same components by their own id, with what they stored, for a graph that reads one it can stand in for
    // itself: a websocket client the graph listens to becomes its own connection.
    public readonly Dictionary<string, (string Type, DataTreeDictionary Data)> UnattachedParts = new(StringComparer.OrdinalIgnoreCase);

    public void RecordUnattached(string type, DataTreeDictionary data)
    {
        if ((data.TryGetNode("ID") as DataTreeValue)?.Value is string own)
            UnattachedParts[own] = (type, data);
        foreach (var (name, value) in data.Children)
        {
            if (value is DataTreeDictionary member && (member.TryGetNode("ID") as DataTreeValue)?.Value is string id)
                Unattached[id] = $"{type}.{name}";
        }
    }

    // Recorded only for a package that has node graphs at all.
    public bool Wanted { get; set; }

    // The package's type table: a stored type is an index into it.
    public IReadOnlyList<string> TypeNames { get; set; } = System.Array.Empty<string>();

    public void RecordMembers(IWorldElement owner, DataTreeDictionary data)
    {
        if (owner is Slot && (data.TryGetNode("ID") as DataTreeValue)?.Value is string slotId)
        {
            long order = Stored(data, "OrderOffset") is IConvertible offset and not string and not bool
                ? Convert.ToInt64(offset, System.Globalization.CultureInfo.InvariantCulture) : 0;
            SlotFacts[slotId] = new PackageSlotFacts(order, SlotFacts.Count, Stored(data, "Active") is not false,
                Stored(data, "Name") as string == "<NODE_UI>");
        }
        foreach (var (name, value) in data.Children)
        {
            if (value is not DataTreeDictionary member || (member.TryGetNode("ID") as DataTreeValue)?.Value is not string id)
                continue;
            Members[id] = new PackageMemberRecord(owner, name, -1);
            if (member.TryGetNode("Data") == null && owner is Component flattened)
                RecordNested(flattened, name, member);
            if (member.TryGetNode("Data") is not DataTreeList list)
                continue;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is DataTreeDictionary item && (item.TryGetNode("ID") as DataTreeValue)?.Value is string itemId)
                {
                    Members[itemId] = new PackageMemberRecord(owner, name, i);
                    RecordWithin(owner, name, i, item, string.Empty);
                }
            }
        }
    }

    // A list entry that is a record keeps its members beside its id, and a record inside it keeps its own the same
    // way. Each is kept by its entry and its path in the entry, so a drive or a driver of one finds the member of
    // the entry it means.
    private void RecordWithin(IWorldElement owner, string list, int index, DataTreeDictionary record, string path)
    {
        foreach (var (name, value) in record.Children)
        {
            if (value is not DataTreeDictionary member || (member.TryGetNode("ID") as DataTreeValue)?.Value is not string id)
                continue;
            string within = path.Length == 0 ? name : path + "." + name;
            Members.TryAdd(id, new PackageMemberRecord(owner, list, index, within));
            if (member.TryGetNode("Data") == null && path.Length == 0)
                RecordWithin(owner, list, index, member, within);
        }
    }

    // A record held in place keeps its members beside its id. A component of ours that carries them as members of
    // its own names them "Record.Member" in its aliases, and only then are their ids kept, so a driver of one
    // finds the member it means.
    private void RecordNested(Component owner, string record, DataTreeDictionary data)
    {
        foreach (var (name, value) in data.Children)
        {
            if (value is not DataTreeDictionary member || (member.TryGetNode("ID") as DataTreeValue)?.Value is not string id)
                continue;
            string dotted = record + "." + name;
            if (PackageMemberMapper.ResolveMemberName(owner, dotted) != dotted)
                Members.TryAdd(id, new PackageMemberRecord(owner, dotted, -1));
        }
    }

    private static object? Stored(DataTreeDictionary data, string member)
        => ((data.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("Data") as DataTreeValue)?.Value;
}

// Sequence counts slots in the order they were read, so it keeps the package's order among siblings.
internal readonly record struct PackageSlotFacts(long OrderOffset, int Sequence, bool Active, bool NodeCard);

internal readonly record struct PackageNodeRecord(string SlotId, string Encoded, DataTreeDictionary Data);

internal sealed record PackageSlotRecord(string Id, string? ParentId, string Name, Slot Slot);

// Within is a member's path inside the list entry at Index, for one kept in an entry.
internal readonly record struct PackageMemberRecord(IWorldElement Owner, string Member, int Index, string? Within = null);
