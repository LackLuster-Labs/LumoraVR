using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

public abstract class Component : ComponentBase<Component>
{
    public Slot Slot { get; private set; }

    public bool IsUnderLocalUser => Slot?.IsUnderLocalUser ?? false;

    protected override bool CanRunUpdates => Slot?.IsActive ?? false;

    internal override void Initialize(ContainerWorker<Component> container, bool isNew)
    {
        Slot = container as Slot;
        base.Initialize(container, isNew);
    }

    public override IEnumerable<IWorldElement> GetReferencedObjects(bool assetRefOnly, bool persistentOnly = true)
    {
        if (Slot != null && (!persistentOnly || Slot.IsPersistent))
        {
            yield return Slot;
        }
    }

    public override string ParentHierarchyToString()
    {
        return Slot?.ParentHierarchyToString() ?? $"{GetType().Name}(Unattached)";
    }

    public override void OnCommonUpdate()
    {
        var delta = World?.UpdateManager?.DeltaTime ?? 0f;
        OnUpdate(delta);
    }

    public virtual void OnUpdate(float delta) { }

    public virtual void OnFixedUpdate(float fixedDelta) { }

    public virtual void OnLateUpdate(float delta) { }

    public virtual void Encode(BinaryWriter writer)
    {
        if (writer == null)
            throw new ArgumentNullException(nameof(writer));

        writer.Write(SyncMemberCount);
        for (int i = 0; i < SyncMemberCount; i++)
        {
            writer.Write(GetSyncMemberName(i));
            GetSyncMember(i)?.Encode(writer);
        }
    }

    public virtual void Decode(BinaryReader reader)
    {
        if (reader == null)
            throw new ArgumentNullException(nameof(reader));

        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            var field = TryGetField(name) as ISyncMember;
            field?.Decode(reader);
        }
    }
}
