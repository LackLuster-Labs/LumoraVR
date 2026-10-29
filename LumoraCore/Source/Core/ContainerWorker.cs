// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Networking.Sync;
using LumoraLogger = Lumora.Core.Logging.Logger;
// Aliased because this type has a Components PROPERTY, which shadows the namespace of the same name at
// every use site inside it. -xlinka
using IUndoBatch = Lumora.Core.Components.IUndoBatch;
using ComponentExistenceUndoBatch = Lumora.Core.Components.ComponentExistenceUndoBatch;
using FieldEditUndoBatch = Lumora.Core.Components.FieldEditUndoBatch;

namespace Lumora.Core;

public abstract class ContainerWorker<C> : Worker where C : ComponentBase<C>
{
    [NameOverride("Components")]
    [HideInInspector]
    protected readonly ReplicatedComponentCollection<C> componentCollection = new();

    protected readonly List<IInitializable> childInitializables = new();

    public IEnumerable<C> Components => componentCollection.Values;
    public int ComponentCount => componentCollection.Count;

    public bool IsInInitPhase { get; protected set; }

    // While a peer's removal of components here is being decoded: that it is, and whose it is.
    internal bool RemovingComponentsByDecode => componentCollection.IsLoading;
    internal User? ComponentRemovalSender => componentCollection.DecodingRemovalSender;

    public event Action<C>? ComponentAdded;
    public event Action<C>? ComponentRemoved;

    internal virtual void Initialize(IWorldElement parent)
    {
        if (parent == null)
            throw new ArgumentNullException(nameof(parent));

        InitializeWorker(parent);
        componentCollection.OnElementAdded += OnComponentAdded;
        componentCollection.OnElementRemoved += OnComponentRemoved;
    }

    internal virtual void Initialize(World world, IWorldElement? parent = null)
    {
        InitializeWorker(world, parent);
        componentCollection.OnElementAdded += OnComponentAdded;
        componentCollection.OnElementRemoved += OnComponentRemoved;
    }

    public T AttachComponent<T>(bool runOnAttachBehavior = true, Action<T>? beforeAttach = null) where T : C, new()
    {
        ComponentExecutionPolicy.Demand(typeof(T), World);
        CheckAttachComponent(typeof(T));
        var component = WorkerManager.Instantiate<T>();
        AttachComponentInternal(component, runOnAttachBehavior, c => beforeAttach?.Invoke((T)c));
        return component;
    }

    public C AttachComponent(Type type, bool runOnAttachBehavior = true, Action<C>? beforeAttach = null)
    {
        ComponentExecutionPolicy.Demand(type, World);
        CheckAttachComponent(type);
        var component = (C)WorkerManager.Instantiate(type);
        AttachComponentInternal(component, runOnAttachBehavior, beforeAttach);
        return component;
    }

    // COMPONENT OPS
    // Copy, move, reset and bulk-remove, all permission-checked up front so a refusal leaves the container
    // exactly as it was. Each takes an optional undo collector: hand it a list and it appends the reversal
    // steps for whatever it did, hand it nothing and it costs not one allocation more than the raw
    // operation. Composing and recording the collected steps is the caller's call. -xlinka

    public T CopyComponent<T>(T source, IList<IUndoBatch>? undo = null) where T : C, new()
        => (T)CopyComponent((C)(ComponentBase<C>)source, undo);

    public C CopyComponent(C source, IList<IUndoBatch>? undo = null)
    {
        if (source == null || source.IsDestroyed)
            return null!;

        AuthorizeStructuralChange(World, DataModelPermissionAction.Create,
            DataModelPermissionSurface.Component, this, Parent);

        // runOnAttachBehavior:false for the same reason the loader skips it - attach defaults would
        // overwrite the values we are about to copy in.
        var copy = AttachComponent(source.GetType(), runOnAttachBehavior: false);
        copy.CopyValues(source);

        if (undo != null && copy is Component undoable)
        {
            var batch = ComponentExistenceUndoBatch.CreateAttach(undoable);
            if (batch != null)
                undo.Add(batch);
        }

        return copy;
    }

    public T MoveComponent<T>(T original, IList<IUndoBatch>? undo = null) where T : C, new()
        => (T)MoveComponent((C)(ComponentBase<C>)original, undo);

    // The copy is a NEW element with a NEW RefID - a component's identity comes from its type at creation
    // and there is no way to carry an id onto a second instance without lying to the reference registry
    // and to every peer that already resolved the first. What DOES survive is everything that pointed at
    // the original: its members are paired with the copy's structurally, then every reference in the world
    // aimed at the original OR at one of its members is re-aimed at the matching element on the copy.
    // References the copy's type will not accept are cleared, which is the honest outcome - the alternative
    // is leaving them on an element that is about to be destroyed. A reference held by something that has
    // not resolved yet (a peer mid-join, an unloaded record) is not in the registry and cannot be re-aimed;
    // it comes back pointing at a dead id. -xlinka
    public C MoveComponent(C original, IList<IUndoBatch>? undo = null)
    {
        if (original == null || original.IsDestroyed)
            return null!;

        var world = World;
        AuthorizeStructuralChange(world, DataModelPermissionAction.Create,
            DataModelPermissionSurface.Component, this, Parent);
        AuthorizeStructuralChange(world, DataModelPermissionAction.Destroy,
            DataModelPermissionSurface.Component, original, original.Parent);

        var copy = CopyComponent(original, undo);
        if (copy == null)
            return null!;

        var map = new Dictionary<RefID, IWorldElement>();
        Worker.MapMembersOnto(original, copy, map);
        world?.ReplaceReferenceTargets(map, clearIfIncompatible: true);

        if (undo != null && original is Component undoableOriginal)
        {
            var batch = ComponentExistenceUndoBatch.CreateDestroy(undoableOriginal);
            if (batch != null)
            {
                undo.Add(batch);
                return copy; // CreateDestroy already destroyed it
            }
        }

        original.Destroy();
        return copy;
    }

    // Members back to type defaults. Returns false only when the reset was refused or there was nothing
    // to reset.
    public bool ResetComponent(C component, IList<IUndoBatch>? undo = null)
    {
        if (component == null || component.IsDestroyed)
            return false;

        AuthorizeStructuralChange(World, DataModelPermissionAction.Write,
            DataModelPermissionSurface.Component, component, component.Parent);

        if (undo == null)
            return component.ResetMembers() > 0;

        var fields = new List<(IField Field, object? Before, string Name)>();
        for (int i = 0; i < component.SyncMemberCount; i++)
        {
            if (component.GetSyncMember(i) is IField { CanWrite: true } field)
                fields.Add((field, SafeRead(field), component.GetSyncMemberName(i)));
        }

        int reset = component.ResetMembers();

        foreach (var (field, before, name) in fields)
        {
            var after = SafeRead(field);
            if (Equals(before, after))
                continue;
            undo.Add(new FieldEditUndoBatch(field, before, after, $"Reset {name}"));
        }

        return reset > 0;
    }

    private static object? SafeRead(IField field)
    {
        try { return field.BoxedValue; }
        catch (Exception) { return null; }
    }

    // Returns how many were removed. The predicate runs against a snapshot, so a match is free to look at
    // the rest of the container without the enumeration shifting under it.
    public int RemoveAllComponents(Predicate<C> match, IList<IUndoBatch>? undo = null)
    {
        if (match == null)
            return 0;

        var doomed = new List<C>();
        foreach (var kvp in componentCollection)
        {
            var component = kvp.Value;
            if (component != null && !component.IsDestroyed && match(component))
                doomed.Add(component);
        }

        if (doomed.Count == 0)
            return 0;

        // Ask about every one of them BEFORE destroying any: a bulk remove that gets halfway and then hits
        // a component the actor may not touch would leave the container in a state no one asked for.
        foreach (var component in doomed)
        {
            AuthorizeStructuralChange(World, DataModelPermissionAction.Destroy,
                DataModelPermissionSurface.Component, component, component.Parent);
        }

        int removed = 0;
        foreach (var component in doomed)
        {
            if (component.IsDestroyed)
                continue;

            if (undo != null && component is Component undoable)
            {
                var batch = ComponentExistenceUndoBatch.CreateDestroy(undoable);
                if (batch != null)
                {
                    undo.Add(batch);
                    removed++;
                    continue; // CreateDestroy already destroyed it
                }
            }

            component.Destroy();
            removed++;
        }

        return removed;
    }

    public int RemoveAllComponents<T>(Predicate<T>? match = null, IList<IUndoBatch>? undo = null) where T : class
        => RemoveAllComponents(c => c is T typed && (match == null || match(typed)), undo);

    public bool RemoveComponent(C component)
    {
        if (component == null)
            return false;

        if (RefuseConfigRemoval(component) || RefusedInsideReplicatedDestroy(component.ReferenceID))
            return false;

        return componentCollection.Remove(component.ReferenceID);
    }

    // Takes a component off here only, without sending the removal: the effect of a destroy that every
    // peer applies from the same records, or a component this peer may not remove while tearing down its
    // own slot, which the authority's copy of the destroy takes away everywhere else. -xlinka
    internal void RemoveComponentWithoutSync(C component)
    {
        if (component != null)
            componentCollection.RemoveWithoutSync(component.ReferenceID);
    }

    internal bool MayRemoveComponent(C component)
        => component != null && componentCollection.MayRemove(component.ReferenceID);

    internal bool MayRemoveComponentBy(C component, User actor)
        => component != null && componentCollection.MayRemoveQuietly(component.ReferenceID, actor, unscoped: true);

    internal bool MayRemoveComponentQuietly(C component)
        => component != null && Slot.MayTakeOut(componentCollection, component.ReferenceID);

    // A removal some other peer's destroy set off on this machine, which the peer it speaks for may not make:
    // left alone, like a slot in the same spot (see Slot.Destroy).
    private bool RefusedInsideReplicatedDestroy(in RefID id)
        => Slot.InsideReplicatedDestroy && !IsLocalElement && componentCollection.ContainsKey(id)
            && !Slot.MayTakeOut(componentCollection, id);

    public bool RemoveComponent(RefID id)
    {
        if (componentCollection.TryGetValue(id, out var existing) && RefuseConfigRemoval(existing))
            return false;
        if (RefusedInsideReplicatedDestroy(id))
            return false;

        return componentCollection.Remove(id);
    }

    // The world's two config components are not detachable from inside the world. Both public removal
    // entries funnel here, and ComponentBase.Destroy funnels into the second one, so the X in an
    // inspector, a script calling Destroy and a tool stripping a slot all hit the same refusal. -xlinka
    private static bool RefuseConfigRemoval(object? component)
    {
        if (!WorldConfigLock.RefusesRemoval(component))
            return false;

        LumoraLogger.Warn(
            $"Config lock: refused removal of {component!.GetType().Name} - the world's configuration "
            + "components live on the world root for the session.");
        return true;
    }

    public C GetComponent(Predicate<C> predicate)
    {
        foreach (var kvp in componentCollection)
        {
            if (predicate(kvp.Value))
            {
                return kvp.Value;
            }
        }

        return null!;
    }

    protected virtual void CheckAttachComponent(Type componentType)
    {
    }

    protected virtual void RunComponentAdded(C component)
    {
        ComponentAdded?.Invoke(component);
    }

    protected virtual void RunComponentRemoved(C component)
    {
        ComponentRemoved?.Invoke(component);
    }

    private void AttachComponentInternal(C component, bool runOnAttachBehavior, Action<C>? beforeAttach)
    {
        if (component == null)
            return;

        // The add runs the component's whole Initialize, OnAwake and OnInit inside this scope, and any of
        // those can throw. Without the scope that throw skipped the end of the local block and left the
        // controller minting LOCAL ids for the rest of the session. -xlinka
        using (World.ReferenceController.ElementAllocationScope(RefID.Null, IsLocalElement))
        using (IsLocalElement ? default : World.ReferenceController.InheritedAllocationScope(ReferenceID))
        {
            var key = World.ReferenceController.PeekID();
            componentCollection.Add(key, component, isNewlyCreated: true);
        }

        beforeAttach?.Invoke(component);
        if (runOnAttachBehavior)
        {
            component.RunOnAttach();
        }
    }

    private void OnComponentAdded(ReplicatedDictionary<RefID, C> collection, RefID idStart, C component, bool isNew)
    {
        bool wasInInitPhase = IsInInitPhase;
        try
        {
            using (World.ReferenceController.AllocationScopeAt(idStart))
            {
                IsInInitPhase = true;
                component.Initialize(this, isNew);
                childInitializables.Add(component);
                if (!wasInInitPhase)
                {
                    EndInitPhase();
                }
            }
        }
        catch
        {
            // A component that threw out of Initialize must not leave its container stuck in the init
            // phase: every sync member on it reads IsInInitPhase to decide whether a write is authored
            // or replayed, so a stuck container silently stops replicating. The half-built component is
            // dropped from the init list for the same reason. -xlinka
            IsInInitPhase = wasInInitPhase;
            childInitializables.Remove(component);
            throw;
        }

        // Keep sync members in loading state until their values
        // are decoded from network. This prevents them from being marked dirty
        // if the world transitions to Running before all values are decoded.
        // For clients, ALL network-created elements need this (regardless of isNew flag,
        // which indicates whether the HOST created it newly). Only network-created ones: the collection
        // is loading exactly while it decodes. A component the client attached itself used to land here
        // too, and since only a decode clears the flag, none of its members ever sent a change. -xlinka
        if (!World.IsAuthority && collection.IsLoading)
        {
            foreach (var member in component.SyncMembers)
            {
                if (member is Networking.Sync.SyncElement syncElement)
                {
                    World.SyncController.HoldLoading(syncElement);
                }
            }
        }

        RunComponentAdded(component);
        if (IsDestroyed)
        {
            component.PrepareDestruction();
        }
    }

    private void OnComponentRemoved(ReplicatedDictionary<RefID, C> collection, RefID key, C component)
    {
        component?.RunOnDetach();
        component?.PrepareDestruction();
        RunComponentRemoved(component!);
    }

    public virtual void EndInitPhase()
    {
        foreach (var child in childInitializables)
        {
            child.EndInitPhase();
        }
        childInitializables.Clear();
        IsInInitPhase = false;
    }

    internal virtual void PrepareDestruction()
    {
        if (IsDestroyed)
        {
            return;
        }

        IsDestroyed = true;
        foreach (var kvp in componentCollection)
        {
            kvp.Value.PrepareDestruction();
        }

        PrepareMembersForDestroy();
    }
}
