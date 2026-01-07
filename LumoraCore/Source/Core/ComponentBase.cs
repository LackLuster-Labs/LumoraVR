using System;
using System.Collections.Generic;

namespace Lumora.Core;

public abstract class ComponentBase<C> : Worker, IUpdatable, IChangeable, IInitializable, ILinkable, IWorldEventReceiver where C : ComponentBase<C>
{
    [NonPersistent]
    protected readonly Sync<bool> persistent = new();

    [NameOverride("UpdateOrder")]
    protected readonly Sync<int> updateOrder = new();

    [NameOverride("Enabled")]
    public readonly Sync<bool> Enabled = new();

    private bool _runningOnDestroying;
    private bool _isChangeDirty;
    private bool _wasEnabled;

    internal ContainerWorker<C> Container { get; private set; }

    public bool IsStarted { get; private set; }
    public bool IsValid { get; private set; }
    public bool IsChangeDirty => _isChangeDirty;
    public int LastChangeUpdateIndex { get; private set; }

    public virtual bool UserspaceOnly => false;

    protected virtual bool CanRunUpdates => true;

    public int UpdateOrder
    {
        get => updateOrder.Value;
        set => updateOrder.Value = value;
    }

    public bool Persistent
    {
        get => persistent.Value;
        set => persistent.Value = value;
    }

    public override bool IsPersistent => persistent.Value && (Parent?.IsPersistent ?? true);

    public bool IsInInitPhase { get; private set; }

    public bool IsLinked => ActiveLink != null;
    public bool IsDriven => ActiveLink?.IsDriving ?? false;
    public bool IsHooked => ActiveLink?.IsHooking ?? false;
    public ILinkRef? ActiveLink => DirectLink;
    public ILinkRef? DirectLink { get; private set; }
    public ILinkRef? InheritedLink => null;
    public IEnumerable<ILinkable>? LinkableChildren => GetLinkableChildren();

    public event Action<IChangeable>? Changed;

    internal virtual void Initialize(ContainerWorker<C> container, bool isNew)
    {
        if (container == null)
            throw new ArgumentNullException(nameof(container));

        IsInInitPhase = true;
        Container = container;
        InitializeWorker(container);

        if (World?.IsAuthority == true)
        {
            World.Workers.InformOfTypeUse(WorkerType);
        }

        persistent.Value = true;
        Enabled.Value = true;
        updateOrder.Value = InitInfo.DefaultUpdateOrder;

        Enabled.OnValueChange += EnabledField_OnValueChange;
        updateOrder.OnValueChange += UpdateOrder_OnValueChange;

        World?.ReferenceController?.BlockAllocationsStart();
        try
        {
            OnAwake();
        }
        finally
        {
            World?.ReferenceController?.BlockAllocationsEnd();
        }

        if (isNew)
        {
            OnInit();
        }

        World?.UpdateManager?.RegisterForStartup(this);

        IsValid = true;
        if (InitInfo.SingleInstancePerSlot && !IsSingleValidInstance())
        {
            IsValid = false;
            if (World?.IsAuthority == true)
            {
                World.RunSynchronously(() =>
                {
                    if (!IsSingleValidInstance())
                    {
                        Destroy(sendDestroyingEvent: false);
                    }
                });
            }
            else
            {
                Container.ComponentRemoved += ValidateSingleInstance;
            }
        }
    }

    private void EnabledField_OnValueChange(IField<bool> field)
    {
        World?.RunSynchronously(RunEnabledChanged);
    }

    private void UpdateOrder_OnValueChange(IField<int> field)
    {
        World?.UpdateManager?.UpdateBucketChanged(this);
    }

    private void RunEnabledChanged()
    {
        if (!IsStarted)
        {
            _wasEnabled = Enabled.Value;
            return;
        }

        if (Enabled.Value && !_wasEnabled)
        {
            _wasEnabled = true;
            OnEnabled();
        }
        else if (!Enabled.Value && _wasEnabled)
        {
            _wasEnabled = false;
            OnDisabled();
        }
    }

    protected bool IsSingleValidInstance()
    {
        return Container.GetComponent(c => c != this && c.GetType() == GetType() && c.IsValid) == null;
    }

    private void ValidateSingleInstance(C component)
    {
        if (ReferenceEquals(component, this))
        {
            Container.ComponentRemoved -= ValidateSingleInstance;
        }
        else if (IsSingleValidInstance())
        {
            IsValid = true;
            MarkChangeDirty();
            Container.ComponentRemoved -= ValidateSingleInstance;
        }
    }

    protected override void SyncMemberChanged(IChangeable member)
    {
        OnSyncMemberChanged(member);
    }

    protected virtual void OnSyncMemberChanged(IChangeable member)
    {
        MarkChangeDirty();
    }

    public void MarkChangeDirty()
    {
        if (World == null || _isChangeDirty || !IsStarted)
        {
            return;
        }

        _isChangeDirty = true;
        if (!IsDestroyed)
        {
            World.UpdateManager?.RegisterForChanges(this);
        }

        TriggerChangedEvent();
    }

    public void NotifyChanged()
    {
        if (IsDestroyed)
        {
            return;
        }

        MarkChangeDirty();
    }

    protected void TriggerChangedEvent()
    {
        OnImmediateChanged();
        Changed?.Invoke(this);
    }

    public void Destroy()
    {
        Destroy(sendDestroyingEvent: true);
    }

    public void Destroy(bool sendDestroyingEvent)
    {
        if (IsDestroyed)
        {
            return;
        }

        if (sendDestroyingEvent)
        {
            RunOnDestroying();
        }

        Container.RemoveComponent(ReferenceID);
    }

    internal void PrepareDestruction()
    {
        if (IsDestroyed)
        {
            return;
        }

        MarkChangeDirty();
        PrepareMembersForDestroy();
        IsDestroyed = true;
        World?.UpdateManager?.RegisterForDestruction(this);
        OnPrepareDestroy();
    }

    internal void RunOnAttach()
    {
        OnAttach();
    }

    internal void RunOnDetach()
    {
        OnDetach();
    }

    internal void RunOnPaste()
    {
        OnPaste();
    }

    public void EndInitPhase()
    {
        EndInitializationStageForMembers();
        IsInInitPhase = false;
    }

    public void Link(ILinkRef link)
    {
        DirectLink = link;
        RunLinked();
    }

    public void ReleaseLink(ILinkRef link)
    {
        if (link != DirectLink)
        {
            return;
        }

        DirectLink = null;
        RunUnlinked();
    }

    public void InheritLink(ILinkRef link)
    {
        throw new InvalidOperationException("Components cannot inherit links.");
    }

    public void ReleaseInheritedLink(ILinkRef link)
    {
        throw new InvalidOperationException("Components cannot inherit links.");
    }

    public virtual void InternalRunStartup()
    {
        if (IsInInitPhase)
        {
            EndInitPhase();
        }

        OnStart();
        IsStarted = true;

        if (InitInfo.HasUpdateMethods)
        {
            World?.UpdateManager?.RegisterForUpdates(this);
        }

        if (InitInfo.ReceivesAnyWorldEvent)
        {
            World?.RegisterEventReceiver(this);
        }

        _wasEnabled = Enabled.Value;
        if (_wasEnabled)
        {
            OnEnabled();
        }

        MarkChangeDirty();
    }

    public virtual void InternalRunUpdate()
    {
        if (IsDestroyed || !Enabled.Value || !CanRunUpdates)
        {
            return;
        }

        OnBehaviorUpdate();
        OnCommonUpdate();
    }

    public virtual void InternalRunApplyChanges(int changeUpdateIndex)
    {
        _isChangeDirty = false;
        LastChangeUpdateIndex = changeUpdateIndex;
        OnChanges();
    }

    public virtual void InternalRunDestruction()
    {
        OnDestroy();
        if (InitInfo.HasUpdateMethods)
        {
            World?.UpdateManager?.UnregisterFromUpdates(this);
        }

        if (InitInfo.ReceivesAnyWorldEvent)
        {
            World?.UnregisterEventReceiver(this);
        }

        Dispose();
    }

    public virtual void RunOnDestroying()
    {
        if (_runningOnDestroying)
        {
            return;
        }

        try
        {
            _runningOnDestroying = true;
            OnDestroying();
        }
        finally
        {
            _runningOnDestroying = false;
        }
    }

    public virtual void OnInit() { }
    public virtual void OnAwake() { }
    public virtual void OnStart() { }
    public virtual void OnAttach() { }
    public virtual void OnDetach() { }
    public virtual void OnDuplicate() { }
    public virtual void OnPaste() { }
    public virtual void OnCommonUpdate() { }
    public virtual void OnBehaviorUpdate() { }
    public virtual void OnImmediateChanged() { }
    public virtual void OnChanges() { }
    public virtual void OnLinked() { }
    public virtual void OnUnlinked() { }
    public virtual void OnEnabled() { }
    public virtual void OnDisabled() { }
    public virtual void OnPrepareDestroy() { }
    public virtual void OnDestroy() { }
    public virtual void OnDestroying() { }

    protected void InitializeNewSyncMembers()
    {
        if (World == null)
        {
            return;
        }

        AssignSyncMemberMetadata();
        for (int i = 0; i < SyncMemberCount; i++)
        {
            var member = GetSyncMember(i);
            if (member != null && member.World == null)
            {
                member.Initialize(World, this);
                if (member.IsInInitPhase)
                {
                    member.EndInitPhase();
                }

                if (member is IChangeable changeable)
                {
                    changeable.Changed += OnSyncMemberChangedInternal;
                }
            }
        }
    }

    private void OnSyncMemberChangedInternal(IChangeable member)
    {
        SyncMemberChanged(member);
    }

    private void RunLinked()
    {
        if (!InitInfo.HasLinkedMethod)
        {
            return;
        }

        OnLinked();
    }

    private void RunUnlinked()
    {
        if (!InitInfo.HasUnlinkedMethod)
        {
            return;
        }

        OnUnlinked();
    }

    private IEnumerable<ILinkable> GetLinkableChildren()
    {
        foreach (var member in SyncMembers)
        {
            if (member is ILinkable linkable)
            {
                yield return linkable;
            }
        }
    }

    public bool HasEventHandler(World.WorldEvent eventType)
    {
        return InitInfo.ReceivesWorldEvent[(int)eventType];
    }

    public virtual void OnFocusChanged(World.WorldFocus focus)
    {
    }

    public virtual void OnWorldDestroy()
    {
    }

    public virtual void OnUserJoined(User user)
    {
    }

    public virtual void OnUserLeft(User user)
    {
    }
}
