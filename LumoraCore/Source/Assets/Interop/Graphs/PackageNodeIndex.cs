// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// Every node of a package by id, every member of a node by id, the components that serve a node from beside
// it (a proxy holding its drive target, a store holding its value), what any id points at, and the graphs:
// nodes joined by any reference between them, a node to what serves it, and a sent impulse to every receiver
// it reaches (PackageNodeIndex.Impulses.cs). -xlinka
internal sealed partial class PackageNodeIndex
{
    public readonly Dictionary<string, PackageNode> Nodes = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, (PackageNode Node, string Member)> MemberOwners = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<PackageNode, List<PackageNode>> Helpers = new();
    public readonly List<List<PackageNode>> Groups = new();

    // Every id some node's member holds: what is read, called or written somewhere.
    public readonly HashSet<string> Read = new(StringComparer.OrdinalIgnoreCase);

    private readonly PackageNodeInput _input;
    private readonly IReadOnlyDictionary<string, IWorldElement> _byId;

    public PackageNodeIndex(PackageNodeInput input, IReadOnlyDictionary<string, IWorldElement> byId)
    {
        _input = input;
        _byId = byId;
        foreach (var record in input.Nodes)
        {
            if ((record.Data.TryGetNode("ID") as DataTreeValue)?.Value is not string id)
                continue;
            var node = new PackageNode(id, record.Encoded, record.Data, record.SlotId);
            Nodes[id] = node;
            _stored[node] = _stored.Count;
            foreach (var (name, value) in record.Data.Children)
            {
                if (value is not DataTreeDictionary member || (member.TryGetNode("ID") as DataTreeValue)?.Value is not string memberId)
                    continue;
                MemberOwners[memberId] = (node, name);
                if (member.TryGetNode("Data") is not DataTreeList list)
                    continue;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is DataTreeDictionary item && (item.TryGetNode("ID") as DataTreeValue)?.Value is string itemId)
                        MemberOwners[itemId] = (node, $"{name}[{i}]");
                }
            }
        }

        foreach (var node in Nodes.Values)
        {
            foreach (var id in References(node))
                Read.Add(id);
        }

        foreach (var node in Nodes.Values)
        {
            if (!node.Class.Contains('+') || node.Ref("Node") is not { } servedId || !Nodes.TryGetValue(servedId, out var served))
                continue;
            if (!Helpers.TryGetValue(served, out var list))
                Helpers[served] = list = new List<PackageNode>();
            list.Add(node);
        }
        Group();
    }

    // The helper whose Drive points at this id: a drive's proxy aimed at a member inside the package's graph.
    public PackageNode? DriverOf(string id)
    {
        foreach (var list in Helpers.Values)
        {
            foreach (var helper in list)
            {
                if (string.Equals(helper.Ref("Drive"), id, StringComparison.OrdinalIgnoreCase))
                    return helper;
            }
        }
        return null;
    }

    // The component beside a node that serves it, by class ("FieldDriveBase+Proxy"), or the first one when
    // no class is given.
    public PackageNode? Helper(PackageNode node, string? cls = null)
    {
        if (!Helpers.TryGetValue(node, out var list))
            return null;
        foreach (var helper in list)
        {
            if (cls == null || helper.Class == cls || helper.Class.EndsWith(cls, StringComparison.Ordinal))
                return helper;
        }
        return null;
    }

    // A node, or a node and one of its members, for an id that names one.
    public bool TryNode(string? id, out PackageNode node, out string member)
    {
        member = string.Empty;
        node = null!;
        if (id == null)
            return false;
        if (Nodes.TryGetValue(id, out node!))
            return true;
        if (MemberOwners.TryGetValue(id, out var owned))
        {
            (node, member) = owned;
            return true;
        }
        return false;
    }

    // Whether the package holds anything under an id: a node, something built, a member, a slot, or a part that
    // did not come across. An id it holds nothing under points at what the package itself had already lost.
    public bool Holds(string id)
        => Nodes.ContainsKey(id) || MemberOwners.ContainsKey(id) || _byId.ContainsKey(id) || _input.Members.ContainsKey(id)
            || _input.Unattached.ContainsKey(id) || _input.Slots.ContainsKey(id);

    // The short name of a type the package stored as an index into its type table.
    public string? TypeName(object? stored)
    {
        if (stored == null)
            return null;
        int index;
        try { index = System.Convert.ToInt32(stored, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
        return index >= 0 && index < _input.TypeNames.Count ? PackageNode.ShortType(_input.TypeNames[index]) : null;
    }

    // The world object an id names, built by the importer: a slot, a component, or one of their members.
    public IWorldElement? World(string? id)
    {
        if (id == null)
            return null;
        if (_byId.TryGetValue(id, out var element))
        {
            // The package's clock is a value in itself, read straight off the component; ours keeps it in a field.
            if (element is Components.Utility.TimeCounter clock)
                element = clock.Value;
            return element is { IsDestroyed: false } ? element : null;
        }
        if (!_input.Members.TryGetValue(id, out var record) || record.Owner.IsDestroyed)
            return null;
        var found = MemberOf(record.Owner, record.Member, record.Index, record.Within);
        if (found != null)
            _fieldNames[found] = record.Member;
        return found;
    }

    // The package's name for a member it resolved, which says what a vector in it is.
    public string? FieldName(IWorldElement element) => _fieldNames.TryGetValue(element, out var name) ? name : null;

    private readonly Dictionary<IWorldElement, string> _fieldNames = new(ReferenceEqualityComparer.Instance);

    // The slot or component a member id belongs to, and the package's name for the member, even where ours has
    // no member of that name.
    public IWorldElement? OwnerOf(string? id, out string member)
    {
        member = string.Empty;
        if (id == null || !_input.Members.TryGetValue(id, out var record) || record.Owner.IsDestroyed)
            return null;
        member = record.Member;
        return record.Owner;
    }

    // What an id that did not resolve was, for saying why: a member of a component that did not come across, a
    // member we have under no such name, or nothing the package kept.
    public string Describe(string? id)
    {
        if (id == null)
            return "nothing";
        if (_input.Members.TryGetValue(id, out var record))
            return record.Owner.IsDestroyed ? $"{record.Member} on a pruned {record.Owner.GetType().Name}"
                : $"{record.Owner.GetType().Name}.{record.Member}{(record.Index >= 0 ? $"[{record.Index}]" : "")}{(record.Within != null ? "." + record.Within : "")}, which ours has under no such name";
        if (_byId.TryGetValue(id, out var element))
            return element.GetType().Name + (element.IsDestroyed ? " (pruned)" : "");
        if (_input.Unattached.TryGetValue(id, out var unattached))
            return unattached + ", whose component did not come across";
        return "something that was not imported";
    }

    public PackageSlotRecord? SlotRecord(string? id) => id != null && _input.Slots.TryGetValue(id, out var record) ? record : null;

    // A component that did not come across, with what it stored, by its id.
    public (string Type, DataTreeDictionary Data)? UnattachedPart(string? id)
        => id != null && _input.UnattachedParts.TryGetValue(id, out var part) ? part : null;

    // Every one of them, for a graph that names one by what it stored rather than by id.
    public IEnumerable<KeyValuePair<string, (string Type, DataTreeDictionary Data)>> UnattachedParts => _input.UnattachedParts;

    // Everything the importer built, for the same.
    public IEnumerable<IWorldElement> Built => _byId.Values;

    // A member id that names an entry of the package's interface palette: its colour as a hex code, and whether
    // the entry is that code as text.
    public bool PaletteEntry(string? id, out string code, out bool hex) => WorldUi.TryPaletteEntry(_input, id, out code, out hex);

    // Our member under the package's member name. A slot names its members differently.
    public static IWorldElement? MemberOf(IWorldElement owner, string name, int index, string? within = null)
    {
        ISyncMember? member = null;
        if (owner is Slot slot)
        {
            member = name switch
            {
                "Name" => slot.Name,
                "Active" => slot.ActiveSelf,
                "Position" => slot.LocalPosition,
                "Rotation" => slot.LocalRotation,
                "Scale" => slot.LocalScale,
                "Tag" => slot.Tag,
                "OrderOffset" => slot.OrderOffset,
                "Persistent" => slot.Persistent,
                _ => null,
            };
        }
        else if (owner is Worker worker)
        {
            if (worker is Component component)
            {
                // A member the importer fills in by hand is kept from the plain copy under a mark, and to a graph
                // it is still the member of that name.
                string resolved = PackageMemberMapper.ResolveMemberName(component, name);
                if (resolved.Length == 0 || resolved[0] != '\0')
                    name = resolved;
            }
            for (int i = 0; i < worker.SyncMemberCount; i++)
            {
                if (worker.GetSyncMemberName(i) == name)
                {
                    member = worker.GetSyncMember(i);
                    break;
                }
            }
        }
        if (member == null || index < 0)
            return member as IWorldElement;
        var entry = member is ISyncList list && index < list.Count ? list.GetElement(index) as IWorldElement : null;
        return within == null ? entry : Within(entry, within);
    }

    // A member of a list entry by the package's path inside it. A user the package keeps as a record (the user, and
    // the ids it finds them again by) is one reference here, so a step naming the reference it stands on stays.
    private static IWorldElement? Within(IWorldElement? entry, string path)
    {
        var at = entry;
        string? last = null;
        foreach (var step in path.Split('.'))
        {
            if (at == null)
                return null;
            var next = NamedMember(at, step);
            if (next == null && step == last && at is ISyncRef)
                continue;
            at = next;
            last = step;
        }
        return at;
    }

    private static IWorldElement? NamedMember(IWorldElement owner, string name)
    {
        if (owner is Worker worker)
        {
            for (int i = 0; i < worker.SyncMemberCount; i++)
            {
                if (worker.GetSyncMemberName(i) == name)
                    return worker.GetSyncMember(i) as IWorldElement;
            }
            return null;
        }
        var field = owner.GetType().GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return field != null && typeof(ISyncMember).IsAssignableFrom(field.FieldType) ? field.GetValue(owner) as IWorldElement : null;
    }

    private void Group()
    {
        var parent = new Dictionary<PackageNode, PackageNode>();
        foreach (var node in Nodes.Values)
            parent[node] = node;
        PackageNode Find(PackageNode x)
        {
            while (!ReferenceEquals(parent[x], x))
                x = parent[x] = parent[parent[x]];
            return x;
        }
        void Union(PackageNode a, PackageNode b)
        {
            a = Find(a);
            b = Find(b);
            if (!ReferenceEquals(a, b))
                parent[a] = b;
        }

        foreach (var node in Nodes.Values)
        {
            foreach (var id in References(node))
            {
                if (TryNode(id, out var other, out _))
                    Union(node, other);
            }
        }

        // A sender and every receiver it reaches are one graph: the send becomes flow wires. A button joins as
        // the press that starts them.
        foreach (var node in Nodes.Values)
        {
            foreach (var receiver in Reached(node))
                Union(node, receiver);
        }

        foreach (var group in Nodes.Values.GroupBy(Find))
            Groups.Add(group.OrderBy(n => n.Id, StringComparer.Ordinal).ToList());
    }

    // Every id a node's members hold.
    public static IEnumerable<string> References(PackageNode node)
    {
        foreach (var (name, value) in node.Data.Children)
        {
            if (value is not DataTreeDictionary member)
                continue;
            switch (member.TryGetNode("Data"))
            {
                case DataTreeValue { Value: string id } when id.Length == 36:
                    yield return id;
                    break;
                case DataTreeList list:
                    foreach (var item in list)
                    {
                        if (((item as DataTreeDictionary)?.TryGetNode("Data") as DataTreeValue)?.Value is string itemId && itemId.Length == 36)
                            yield return itemId;
                    }
                    break;
            }
        }
    }

    // The text a constant node or a global value holds, for a tag that has to be known before converting.
    public string? ConstantText(string? id)
    {
        if (!TryNode(id, out var node, out _))
            return null;
        if (node.Class.StartsWith("ValueObjectInput", StringComparison.Ordinal) || node.Class.StartsWith("GlobalValue", StringComparison.Ordinal))
            return node.Value("Value") as string;
        return null;
    }
}
