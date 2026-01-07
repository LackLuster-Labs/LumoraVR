using System;
using System.Collections.Generic;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

public abstract class ContainerWorker<C> : Worker where C : ComponentBase<C>
{
    [NameOverride("Components")]
    [HideInInspector]
    protected readonly WorkerBag<C> componentBag = new();

    protected readonly List<IInitializable> childInitializables = new();

    public IEnumerable<C> Components => componentBag.Values;
    public int ComponentCount => componentBag.Count;

    public bool IsInInitPhase { get; protected set; }

    public event Action<C>? ComponentAdded;
    public event Action<C>? ComponentRemoved;

    internal virtual void Initialize(IWorldElement parent)
    {
        if (parent == null)
            throw new ArgumentNullException(nameof(parent));

        InitializeWorker(parent);
        componentBag.OnElementAdded += OnComponentAdded;
        componentBag.OnElementRemoved += OnComponentRemoved;
    }

    internal virtual void Initialize(World world, IWorldElement? parent = null)
    {
        InitializeWorker(world, parent);
        componentBag.OnElementAdded += OnComponentAdded;
        componentBag.OnElementRemoved += OnComponentRemoved;
    }

    public T AttachComponent<T>(bool runOnAttachBehavior = true, Action<T>? beforeAttach = null) where T : C, new()
    {
        CheckAttachComponent(typeof(T));
        var component = WorkerManager.Instantiate<T>();
        AttachComponentInternal(component, runOnAttachBehavior, c => beforeAttach?.Invoke((T)c));
        return component;
    }

    public C AttachComponent(Type type, bool runOnAttachBehavior = true, Action<C>? beforeAttach = null)
    {
        CheckAttachComponent(type);
        var component = (C)WorkerManager.Instantiate(type);
        AttachComponentInternal(component, runOnAttachBehavior, beforeAttach);
        return component;
    }

    public bool RemoveComponent(C component)
    {
        if (component == null)
            return false;

        return componentBag.Remove(component.ReferenceID);
    }

    public bool RemoveComponent(RefID id)
    {
        return componentBag.Remove(id);
    }

    public C GetComponent(Predicate<C> predicate)
    {
        foreach (var kvp in componentBag)
        {
            if (predicate(kvp.Value))
            {
                return kvp.Value;
            }
        }

        return null;
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

        if (IsLocalElement)
        {
            World.ReferenceController.LocalAllocationBlockBegin();
        }

        var key = World.ReferenceController.PeekID();
        componentBag.Add(key, component, isNewlyCreated: true);

        if (IsLocalElement)
        {
            World.ReferenceController.LocalAllocationBlockEnd();
        }

        beforeAttach?.Invoke(component);
        if (runOnAttachBehavior)
        {
            component.RunOnAttach();
        }
    }

    private void OnComponentAdded(ReplicatedDictionary<RefID, C> bag, RefID idStart, C component, bool isNew)
    {
        World.ReferenceController.AllocationBlockBegin(idStart);
        bool wasInInitPhase = IsInInitPhase;
        try
        {
            IsInInitPhase = true;
            component.Initialize(this, isNew);
            childInitializables.Add(component);
            if (!wasInInitPhase)
            {
                EndInitPhase();
            }
        }
        finally
        {
            World.ReferenceController.AllocationBlockEnd();
        }

        // Keep sync members in loading state until their values
        // are decoded from network. This prevents them from being marked dirty
        // if the world transitions to Running before all values are decoded.
        // For clients, ALL network-created elements need this (regardless of isNew flag,
        // which indicates whether the HOST created it newly).
        if (!World.IsAuthority)
        {
            foreach (var member in component.SyncMembers)
            {
                if (member is Networking.Sync.SyncElement syncElement)
                {
                    syncElement.IsLoading = true;
                }
            }
        }

        RunComponentAdded(component);
        if (IsDestroyed)
        {
            component.PrepareDestruction();
        }
    }

    private void OnComponentRemoved(ReplicatedDictionary<RefID, C> bag, RefID key, C component)
    {
        component?.RunOnDetach();
        component?.PrepareDestruction();
        RunComponentRemoved(component);
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
        foreach (var kvp in componentBag)
        {
            kvp.Value.PrepareDestruction();
        }

        PrepareMembersForDestroy();
    }
}
