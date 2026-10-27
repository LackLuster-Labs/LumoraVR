// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Components.UI;
using Lumora.Core.Components.Utility;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// Lists a world builds from a template, one copy per entry: the package's feed, its view and template mapper,
// and the interfaces on its templates become an entry source, a TemplateList and a TemplateListRow. -xlinka
public static partial class PackageTypeMap
{
    static partial void AliasesK7(Dictionary<string, string> aliases)
    {
        aliases["WorldUsersFeed"] = "SessionUserEntries";
        // The view and the mapper beside it are one list here, with one template: the first the mapper names
        // that came across (WorldK7.FinishTemplateLists).
        aliases["SingleFeedView"] = "TemplateList";
        Carried.Add("DataFeedItemMapper");
        aliases["FeedItemInterface"] = "TemplateListRow";
        aliases["FeedEntityInterface"] = "TemplateListRow";

        // A user reference kept equal to a plain reference to a user. Both are the one reference here, which
        // the row writes and the template's scripts read (WorldK7.LinkTemplateLists).
        aliases["UserRefToSyncRefAdapter"] = "ReferenceHolder<User>";
        // A reference read as a wider kind of element.
        ClosedAliases["ReferenceCast<User,IWorldElement>"] = typeof(CopyReference<IWorldElement>);

        // Opens a contact card for an account id from the package's own account service. There is no
        // contacts service here to open, and account ids from there match nobody here.
        Carried.Add("ContactLink");
    }
}

public static partial class PackageMemberMapper
{
    private const string HandledK7 = "\u0000k7";

    static partial void MemberAliasesK7(Dictionary<string, Dictionary<string, string>> table)
    {
        AliasK7(table, "TemplateList", ("Feed", "Source"), ("SearchPhrase", "Search"), ("UpdatingUser", "Builder"),
            ("ItemsManager", HandledK7));
        AliasK7(table, "TemplateListRow", ("ItemName", "Name"), ("ItemKey", "Key"), ("EnabledState", "Shown"), ("Reference", "Entity"));
        AliasK7(table, "ReferenceHolder`1", ("User", "Reference"), ("TargetReference", HandledK7));
        AliasK7(table, "ReferenceProxySource", ("Reference", "Target"));
    }

    private static void AliasK7(Dictionary<string, Dictionary<string, string>> table, string ours, params (string Stored, string Ours)[] pairs)
    {
        if (!table.TryGetValue(ours, out var names))
            table[ours] = names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (stored, mine) in pairs)
            names[stored] = mine;
    }

    static partial void ApplyK7(Component component, DataTreeDictionary data, Action<ISyncRef, string> queueReference,
        Action<object, string, IReadOnlyList<string>> queueReferenceList, Func<Uri, Uri?> remapUrl)
    {
        switch (component)
        {
            case ReferenceHolder<User> holder when data.TryGetNode("TargetReference") != null:
                FoldUserAdapter(holder, data, queueReference);
                break;
            // The package pulls a card from anywhere on the element it sits on; ours from the grab area only.
            case ReferenceProxySource pull when data.TryGetNode("Reference") != null && pull.GrabArea.Target == null:
                pull.GrabArea.Target = pull.Slot;
                break;
        }
    }

    // The package's adapter drives a second reference to the same user, usually the source of a cast. Here the
    // cast reads the holder's reference itself; anything else that took the user is driven from it.
    private static void FoldUserAdapter(ReferenceHolder<User> holder, DataTreeDictionary data, Action<ISyncRef, string> queueReference)
    {
        if ((data.TryGetNode("TargetReference") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeValue { Value: string id }
            || id.Length == 0 || IsNullId(id))
            return;
        queueReference(new OnResolvedK7(target =>
        {
            if (holder.IsDestroyed || target is not ISyncRef reference)
                return false;
            if (reference.TrySet(holder.Reference))
                return true;
            if (target is not SyncRef<User> plain || holder.Slot == null)
                return false;
            var copy = holder.Slot.AttachComponent<CopyReference<User>>();
            copy.Source.Target = holder.Reference;
            copy.Target.DriveTarget(plain);
            return true;
        }), id);
    }

    private sealed class OnResolvedK7 : ISyncRef
    {
        private readonly Func<IWorldElement, bool> _then;

        public OnResolvedK7(Func<IWorldElement, bool> then) => _then = then;

        public RefID Value { get => default; set { } }
        public IWorldElement Target { get => null!; set { } }
        public IWorldElement RawTarget => null!;
        public Type TargetType => typeof(IWorldElement);
        public ReferenceState State => default;
        public void Clear() { }
        public bool TrySet(IWorldElement target) => target != null && _then(target);
    }
}

// Joins what the package kept on separate components. Before references are wired, the reference inside a
// user reference goes onto the holder that stands for it, so the row and the template's scripts that name it
// land. Once the scripts are made, the view's container and the mapper's template go onto the list: a list
// given both copies its template at once, and a template copied before its scripts exist would be copied
// without them.
internal static class WorldK7
{
    public static bool Collects(string type) => type is "SingleFeedView" or "DataFeedItemMapper" or "UserRefToSyncRefAdapter" or "CloudUserInfo";

    public static void LinkTemplateLists(List<(Slot Slot, string Type, DataTreeDictionary Data)> parts, Dictionary<string, IWorldElement> byId,
        PackageNodeInput graphs)
    {
        if (parts.Count == 0)
            return;
        var profile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, type, data) in parts)
        {
            if (type == "UserRefToSyncRefAdapter" && Id(data) is { } id && byId.TryGetValue(id, out var element) && element is ReferenceHolder<User> holder
                && (data.TryGetNode("User") as DataTreeDictionary)?.TryGetNode("User") is DataTreeDictionary inner && Id(inner) is { } innerId)
                byId[innerId] = holder.Reference;
            else if (type == "CloudUserInfo")
            {
                // Its members, kept and unkept alike: the unkept ones are stored as a bare id beside the name.
                foreach (var (name, member) in data.Children)
                {
                    if (member is DataTreeDictionary kept && Id(kept) is { } keptId)
                        profile.Add(keptId);
                    else if (name.EndsWith("-ID", StringComparison.Ordinal) && member is DataTreeValue { Value: string { Length: > 0 } bareId })
                        profile.Add(bareId);
                }
            }
        }
        if (profile.Count > 0)
            DropAccountDrives(graphs, profile);
    }

    public static void FinishTemplateLists(List<(Slot Slot, string Type, DataTreeDictionary Data)> parts, Dictionary<string, IWorldElement> byId)
    {
        if (parts.Count == 0)
            return;
        var mappers = new Dictionary<string, DataTreeDictionary>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, type, data) in parts)
        {
            if (type == "DataFeedItemMapper" && Id(data) is { } id)
                mappers[id] = data;
        }
        foreach (var (_, type, data) in parts)
        {
            if (type != "SingleFeedView" || Id(data) is not { } id || !byId.TryGetValue(id, out var element) || element is not TemplateList list
                || list.IsDestroyed)
                continue;
            var manager = data.TryGetNode("ItemsManager") as DataTreeDictionary;
            if (Stored(manager, "ContainerRoot") is { } containerId && byId.TryGetValue(containerId, out var container) && container is Slot { IsDestroyed: false } box)
                list.Container.Target = box;
            if (Stored(manager, "TemplateMapper") is { } mapperId && mappers.TryGetValue(mapperId, out var mapper) && FirstTemplate(mapper, byId) is { } template)
                list.Template.Target = template;
        }
    }

    private static Slot? FirstTemplate(DataTreeDictionary mapper, Dictionary<string, IWorldElement> byId)
    {
        if ((mapper.TryGetNode("Mappings") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeList mappings)
            return null;
        foreach (var node in mappings)
        {
            if (node is DataTreeDictionary mapping && Stored(mapping, "Template") is { } templateId
                && byId.TryGetValue(templateId, out var element) && element is Component { IsDestroyed: false, Slot: { IsDestroyed: false } slot })
                return slot;
        }
        return null;
    }

    // A drive fed from a user's account profile (CloudUserInfo, left out on purpose: PackageWorldK2) has nothing
    // true to show: with no profile behind it an account age reads as the days since year one. The drive is
    // left out and a text it filled is emptied, so a row shows nothing there rather than a wrong number.
    private static void DropAccountDrives(PackageNodeInput graphs, HashSet<string> profile)
    {
        if (graphs.Nodes.Count == 0)
            return;

        var nodes = new Dictionary<string, PackageNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in graphs.Nodes)
        {
            if (Id(record.Data) is { } id)
                nodes[id] = new PackageNode(id, record.Encoded, record.Data, record.SlotId);
        }
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proxy in nodes.Values)
        {
            if (proxy.Class != "FieldDriveBase+Proxy" || proxy.Ref("Node") is not { } driveId || !nodes.TryGetValue(driveId, out var drive))
                continue;
            visited.Clear();
            if (!ReadsProfile(drive.Data, nodes, profile, visited))
                continue;
            dropped.Add(proxy.Id);
            dropped.Add(drive.Id);
            if (proxy.Ref("Drive") is { } target && graphs.Members.TryGetValue(target, out var member) && !member.Owner.IsDestroyed
                && PackageNodeIndex.MemberOf(member.Owner, member.Member, member.Index, member.Within) is IField<string> text)
                text.Value = string.Empty;
        }
        if (dropped.Count > 0)
            graphs.Nodes.RemoveAll(record => Id(record.Data) is { } id && dropped.Contains(id));
    }

    private static bool ReadsProfile(DataTreeNode node, Dictionary<string, PackageNode> nodes, HashSet<string> profile, HashSet<string> visited)
    {
        switch (node)
        {
            case DataTreeValue { Value: string id } when id.Length > 0:
                if (profile.Contains(id))
                    return true;
                return nodes.TryGetValue(id, out var next) && visited.Add(id) && ReadsProfile(next.Data, nodes, profile, visited);
            case DataTreeDictionary dictionary:
                foreach (var (key, child) in dictionary.Children)
                {
                    if (key != "ID" && ReadsProfile(child, nodes, profile, visited))
                        return true;
                }
                return false;
            case DataTreeList list:
                foreach (var child in list)
                {
                    if (ReadsProfile(child, nodes, profile, visited))
                        return true;
                }
                return false;
        }
        return false;
    }

    private static string? Id(DataTreeDictionary node)
        => (node.TryGetNode("ID") as DataTreeValue)?.Value is string { Length: > 0 } id && !PackageMemberMapper.IsNullId(id) ? id : null;

    private static string? Stored(DataTreeDictionary? owner, string member)
        => (owner?.TryGetNode(member) as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string { Length: > 0 } id }
            && !PackageMemberMapper.IsNullId(id) ? id : null;
}
