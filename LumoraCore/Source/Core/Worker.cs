using System;
using System.Collections.Generic;
using System.Reflection;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

public abstract class Worker : IWorker
{
    protected readonly WorkerInitInfo InitInfo;

    public World World { get; private set; }
    public IWorldElement? Parent { get; private set; }

    public RefID ReferenceID { get; private set; } = RefID.Null;
    public bool IsLocalElement { get; private set; }
    public bool IsDestroyed { get; protected set; }
    public bool IsInitialized { get; private set; }

    public virtual bool IsPersistent => true;
    public virtual bool IsRemoved => IsDestroyed;
    public bool PreserveWithAssets => InitInfo.PreserveWithAssets;
    public bool DontDuplicate => InitInfo.DontDuplicate;
    public bool GloballyRegistered => InitInfo.RegisterGlobally;

    public Type WorkerType => GetType();
    public string WorkerTypeName => WorkerType.FullName;

    public int SyncMemberCount => InitInfo.SyncMemberFields.Length;

    protected Worker()
    {
        InitInfo = WorkerInitializer.GetInitInfo(GetType());
    }

    public IEnumerable<ISyncMember> SyncMembers
    {
        get
        {
            for (int i = 0; i < SyncMemberCount; i++)
            {
                yield return GetSyncMember(i);
            }
        }
    }

    public virtual ISyncMember GetSyncMember(int index)
    {
        return InitInfo.SyncMemberFields[index].GetValue(this) as ISyncMember;
    }

    public FieldInfo GetSyncMemberFieldInfo(int index)
    {
        return InitInfo.SyncMemberFields[index];
    }

    public string GetSyncMemberName(int index)
    {
        return InitInfo.SyncMemberNames[index];
    }

    public int IndexOfMember(ISyncMember member)
    {
        if (member == null)
            return -1;

        for (int i = 0; i < SyncMemberCount; i++)
        {
            if (GetSyncMember(i) == member)
            {
                return i;
            }
        }
        return -1;
    }

    public IField TryGetField(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        if (InitInfo.SyncMemberNameToIndex.TryGetValue(name, out var index))
        {
            return GetSyncMember(index) as IField;
        }

        return null;
    }

    public IField<T> TryGetField<T>(string name)
    {
        return TryGetField(name) as IField<T>;
    }

    public virtual IEnumerable<IWorldElement> GetReferencedObjects(bool assetRefOnly, bool persistentOnly = true)
    {
        yield break;
    }

    public virtual string ParentHierarchyToString()
    {
        return WorkerType.Name;
    }

    protected virtual void InitializeSyncMembers()
    {
        for (int i = 0; i < SyncMemberCount; i++)
        {
            var field = InitInfo.SyncMemberFields[i];
            var member = field.GetValue(this) as ISyncMember;
            if (member == null)
            {
                member = Activator.CreateInstance(field.FieldType) as ISyncMember;
                if (member == null)
                {
                    throw new InvalidOperationException($"Failed to create sync member for field {field.Name} on {GetType().FullName}");
                }
                field.SetValue(this, member);
            }

            if (member is SyncElement syncElement)
            {
                if (InitInfo.SyncMemberNonpersistent[i])
                {
                    syncElement.MarkNonPersistent();
                }
                if (InitInfo.SyncMemberNondrivable[i])
                {
                    syncElement.MarkNonDrivable();
                }
            }
        }
    }

    protected virtual void InitializeSyncMemberDefaults()
    {
        for (int i = 0; i < SyncMemberCount; i++)
        {
            if (InitInfo.DefaultValues[i] == null)
            {
                continue;
            }

            if (GetSyncMember(i) is IField field)
            {
                field.BoxedValue = InitInfo.DefaultValues[i];
            }
        }
    }

    protected void AssignSyncMemberMetadata()
    {
        for (int i = 0; i < SyncMemberCount; i++)
        {
            var member = GetSyncMember(i);
            if (member == null)
            {
                continue;
            }

            member.MemberIndex = i;
            member.Name = InitInfo.SyncMemberNames[i];
        }
    }

    protected void InitializeWorker(IWorldElement parent)
    {
        if (parent == null)
            throw new ArgumentNullException(nameof(parent));

        InitializeWorker(parent.World, parent);
    }

    protected void InitializeWorker(World world, IWorldElement? parent)
    {
        if (world == null)
            throw new ArgumentNullException(nameof(world));

        try
        {
            World = world;
            Parent = parent;

            InitializeSyncMembers();
            AssignSyncMemberMetadata();

            ReferenceID = World.ReferenceController.AllocateID();
            IsLocalElement = ReferenceID.IsLocalID;

            for (int i = 0; i < SyncMemberCount; i++)
            {
                var member = GetSyncMember(i);
                if (member != null && member.World == null)
                {
                    member.Initialize(World, this);
                }
            }

            InitializeSyncMemberDefaults();
            HookSyncMemberChanges();

            World.ReferenceController.RegisterObject(this);
            PostInitializeWorker();
            IsInitialized = true;
        }
        catch (Exception ex)
        {
            throw new Exception($"Exception during initializing Worker of type {GetType().FullName}", ex);
        }
    }

    protected virtual void PostInitializeWorker()
    {
    }

    protected virtual void SyncMemberChanged(IChangeable member)
    {
    }

    protected void EndInitializationStageForMembers()
    {
        for (int i = 0; i < SyncMemberCount; i++)
        {
            var member = GetSyncMember(i);
            if (member != null && member.IsInInitPhase)
            {
                member.EndInitPhase();
            }
        }
    }

    protected void PrepareMembersForDestroy()
    {
    }

    public virtual void Dispose()
    {
        if (World == null && Parent == null)
        {
            return;
        }

        for (int i = 0; i < SyncMemberCount; i++)
        {
            GetSyncMember(i)?.Dispose();
        }

        World?.ReferenceController?.UnregisterObject(this);
        World = null;
        Parent = null;
        IsDestroyed = true;
    }

    private void HookSyncMemberChanges()
    {
        for (int i = 0; i < SyncMemberCount; i++)
        {
            if (GetSyncMember(i) is IChangeable changeable)
            {
                changeable.Changed += SyncMemberChangedInternal;
            }
        }
    }

    private void SyncMemberChangedInternal(IChangeable member)
    {
        SyncMemberChanged(member);
    }
}
