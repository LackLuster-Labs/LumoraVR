// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Persistence;

// Context threaded through a world save. Provides GUID-stable reference encoding (via
// ReferenceTranslator) and collects type versions so a load can migrate older data.
public sealed class SaveControl
{
    private readonly Dictionary<Type, int> _typeVersions = new();
    private Dictionary<string, int>? _savedTypeVersions;

    public ReferenceTranslator ReferenceTranslator { get; }

    public IWorldElement SaveRoot { get; }

    // false (default) saves only persistent content; graph saves set this true while writing
    // collected asset dependencies so they're emitted regardless of their slot's persistence
    public bool SaveNonPersistent { get; set; }

    // Optional filter applied to every saved reference; return RefID.Null to drop a reference. A
    // graph save uses this to null out references pointing outside the saved subtree (and its
    // collected dependencies), keeping the produced graph self-contained.
    public Func<RefID, RefID>? ReferenceFilter { get; set; }

    // Slots a save leaves out whole, and asset components it leaves out unless something it does write points at
    // them: what a world save may not carry, another account's protected objects and the files only they use.
    private HashSet<Slot>? _leftOutSlots;
    private HashSet<Component>? _leftOutAssets;

    // call before ReserveSubtreeIdentities
    public void LeaveOut(HashSet<Slot> slots, HashSet<Component> assets)
    {
        _leftOutSlots = slots;
        _leftOutAssets = assets;
    }

    public bool LeavesOut(Slot slot) => _leftOutSlots != null && _leftOutSlots.Contains(slot);

    public bool LeavesOut(Component component)
        => _leftOutAssets != null && _leftOutAssets.Contains(component)
            && !(component is IAssetProvider provider && _referencedAssets != null && _referencedAssets.Contains(provider));

    // The object a save was asked for is written whether or not it is kept with its world: a panel kept out of
    // world saves still goes into an inventory whole, its own components with it. Its own children, and
    // everything else, by their persistence as ever. -xlinka
    public bool Writes(Slot slot) => slot.IsPersistent || ReferenceEquals(slot, SaveRoot);

    public bool Writes(Component component)
        => component.IsPersistent || component.Persistent && component.Slot != null && ReferenceEquals(component.Slot, SaveRoot);

    public SaveControl(IWorldElement saveRoot, ReferenceTranslator referenceTranslator)
    {
        SaveRoot = saveRoot;
        ReferenceTranslator = referenceTranslator;
    }

    // a null reference encodes as a null value
    public DataTreeValue SaveReference(RefID reference)
    {
        if (ReferenceFilter != null)
            reference = ReferenceFilter(reference);
        if (reference == RefID.Null)
            return new DataTreeValue((string?)null);
        return DataTreeValue.RawString(ReferenceTranslator.Fetch(reference).ToString());
    }


    // REFERENCE IDENTITY RESERVATION
    // A sync member's saved value carries no identity of its own, so Worker.Save only writes the
    // "<name>-ID" beside it for members something is known to point at. "Known" comes from the
    // translator, which holds an entry only once a reference has ALREADY been written - so a plain ref
    // that happens to serialize AFTER its target leaves that target with no identity in the file and
    // loads back empty. Whether it does is an accident of where the two sit in the tree, which nobody
    // authoring content can see or control.
    //
    // Fix it up front: before a byte is written, walk the references under the save root and claim the
    // GUID for whatever each one points at. Every referenced member then reaches its own Save with an
    // identity already reserved and writes it, whichever side of the referrer it sits on. Only
    // reference TARGETS get claimed, never every member, so the extra keys scale with how many
    // references a world holds rather than with how big it is. -xlinka

    // call once, before saving
    public void ReserveSubtreeIdentities(Slot root)
    {
        if (root == null || !Writes(root) || LeavesOut(root))
            return;

        ReserveWorkerIdentities(root);

        foreach (var component in root.Components)
        {
            if (component != null && Writes(component))
                ReserveWorkerIdentities(component);
        }

        foreach (var child in root.Children)
            ReserveSubtreeIdentities(child);
    }

    // for workers written outside a slot walk, e.g. a graph save's collected asset dependencies
    public void ReserveWorkerIdentities(Worker worker)
    {
        if (worker == null)
            return;

        // The reserve walk is the one pass that sees every worker a save is about to write, so the
        // version stamps ride along with it rather than costing a second traversal. Types that declare
        // no version add no entry and no work beyond a memoised lookup. -xlinka
        RegisterTypeVersion(worker.WorkerType, TypeVersioning.GetDeclaredVersion(worker.WorkerType));

        // A preserved component is written back out under the type it came from, so the version stamp
        // that type had in the ORIGINAL file has to be written back out with it. Losing it would leave
        // the build that can read the type reading it as version 0 and migrating data that was already
        // current. -xlinka
        if (worker is UnresolvedComponent { HasPreservedData: true } preserved)
            RegisterSavedTypeVersion(preserved.MissingType.Value, preserved.MissingTypeVersion.Value);

        for (int i = 0; i < worker.SyncMemberCount; i++)
        {
            var member = worker.GetSyncMember(i);
            if (member != null)
                ReserveMemberIdentities(member);
        }
    }

    // for a save rooted at a single member rather than a worker, e.g. an undo record of one list element
    public void ReserveMemberIdentities(ISyncMember member)
    {
        // A member whose value never reaches the tree has nothing to resolve on load either.
        if (member is SyncElement { IsPersistent: false })
            return;

        switch (member)
        {
            case ISyncRef syncRef:
                // The raw RefID, not the resolved target: it is exactly what SyncRef.Save writes, and it
                // costs no registry lookup.
                Reserve(syncRef.Value);
                if (_referencedAssets != null && syncRef.Target is IAssetProvider asset)
                    _referencedAssets.Add(asset);
                break;
            default:
                // Lists, sync objects and composite entries: a texture on a material's property is a
                // reference inside a list element.
                foreach (var (_, child) in Worker.InnerMembers(member))
                    ReserveMemberIdentities(child);
                break;
        }
    }

    // ASSETS THE TREE DOES NOT HOLD
    // A world save writes the persistent tree and nothing else, but a persistent surface can wear a material
    // that lives where the save never goes: an orb seated in a tool someone is holding (users are not saved),
    // an orb parked by an undoable delete, anything on a non-persistent slot. Every one of those references
    // loaded back empty. The reserve walk already visits every reference the save writes, so it notes the
    // providers they point at, and the save writes the ones its tree does not hold beside it. -xlinka
    private HashSet<IAssetProvider>? _referencedAssets;

    // call before ReserveSubtreeIdentities
    public void TrackReferencedAssets() => _referencedAssets ??= new HashSet<IAssetProvider>();

    // What a save of root has to write beside its tree: the tracked providers it does not write, the unwritten
    // providers those point at in turn (a kept material needs its texture too), and whole the slots a kept asset
    // is nothing without (a shader graph's nodes). A kept asset living in a carried slot goes with that slot.
    // Both lists come out ordered by RefID so an unchanged world hashes the same.
    public void CollectUnsavedAssets(Slot root, List<Component> kept, List<Slot> carried)
    {
        if (_referencedAssets == null || root == null)
            return;

        var carriedSaved = new HashSet<Slot>();
        var seen = new HashSet<Component>();
        var queue = new Queue<Component>();

        bool Written(Component component)
            => component.IsDestroyed || component.IsLocalElement
            || (component.IsPersistent && (carriedSaved.Contains(component.Slot) || SlotWrittenUnder(component.Slot, root)));

        void Consider(IWorldElement? element)
        {
            if (element is IAssetProvider and Component component && !Written(component) && seen.Add(component))
                queue.Enqueue(component);
        }

        void ConsiderReferencesOf(Worker worker)
        {
            for (int i = 0; i < worker.SyncMemberCount; i++)
            {
                if (worker.GetSyncMember(i) is not { } member)
                    continue;
                foreach (var target in Worker.CollectMemberReferences(member, assetRefOnly: true, persistentOnly: false))
                    Consider(target);
            }
        }

        foreach (var asset in _referencedAssets)
            Consider(asset);

        while (queue.Count > 0)
        {
            var component = queue.Dequeue();
            kept.Add(component);
            ConsiderReferencesOf(component);
            if (component is not IAssetSlotDependencies carrier)
                continue;
            foreach (var slot in carrier.SlotDependencies)
            {
                if (slot == null || slot.IsDestroyed || slot.IsLocalElement || !slot.IsPersistent
                    || carriedSaved.Contains(slot) || SlotWrittenUnder(slot, root))
                    continue;
                var subtree = new HashSet<Slot>();
                slot.GenerateSavedHierarchy(subtree);
                carried.RemoveAll(subtree.Contains);
                carried.Add(slot);
                carriedSaved.UnionWith(subtree);
                foreach (var held in subtree)
                {
                    ConsiderReferencesOf(held);
                    foreach (var inner in held.Components)
                    {
                        if (inner.IsPersistent)
                            ConsiderReferencesOf(inner);
                    }
                }
            }
        }

        kept.RemoveAll(component => component.IsPersistent && carriedSaved.Contains(component.Slot));
        kept.Sort(ByRefID);
        carried.Sort(ByRefID);
    }

    private static int ByRefID(IWorldElement a, IWorldElement b) => a.ReferenceID.RawValue.CompareTo(b.ReferenceID.RawValue);

    // Whether a save of root writes this slot: it hangs from root down a line of persistent, shared slots.
    private static bool SlotWrittenUnder(Slot? slot, Slot root)
    {
        while (slot != null && !ReferenceEquals(slot, root))
        {
            if (slot.IsLocalElement || !slot.IsPersistent)
                return false;
            slot = slot.Parent;
        }
        return slot != null;
    }

    private void Reserve(RefID target)
    {
        if (target == RefID.Null)
            return;

        // A graph save nulls references that leave the subtree, and nothing they point at gets written,
        // so reserving for them would burn GUIDs on elements the file never mentions.
        if (ReferenceFilter != null && ReferenceFilter(target) == RefID.Null)
            return;

        ReferenceTranslator.Fetch(target);
    }

    // lets the loader detect and migrate older serialized data
    public void RegisterTypeVersion(Type type, int version)
    {
        if (version > 0)
            _typeVersions.TryAdd(type, version);
    }

    // For a stamp that has no local Type behind it - a preserved component carrying forward the version
    // its own file recorded.
    public void RegisterSavedTypeVersion(string typeName, int version)
    {
        if (version <= 0 || string.IsNullOrEmpty(typeName))
            return;
        _savedTypeVersions ??= new Dictionary<string, int>(StringComparer.Ordinal);
        _savedTypeVersions.TryAdd(typeName, version);
    }

    public void StoreTypeVersions(DataTreeDictionary dictionary)
    {
        foreach (var (type, version) in _typeVersions)
        {
            if (type.FullName != null)
                dictionary.AddOrUpdate(type.FullName, version);
        }

        if (_savedTypeVersions == null)
            return;

        foreach (var (typeName, version) in _savedTypeVersions)
        {
            // A live type that resolves under the same name has already spoken for it; the carried
            // stamp only fills names this build has nothing for.
            if (!dictionary.ContainsKey(typeName))
                dictionary.Add(typeName, version);
        }
    }
}
