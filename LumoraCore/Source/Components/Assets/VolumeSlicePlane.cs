// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets;

// A plane that cuts a volume texture away on its negative side, in the cube's own space: a point is kept while
// dot(point, Normal) + Offset is not below zero. Its fields replicate and save one by one; a change to either
// tells the material, which has to push the plane again.
public sealed class VolumeSlicePlane : SyncElement, ISyncMemberCopy
{
    public override SyncMemberType MemberType => SyncMemberType.Object;

    public readonly Sync<float3> Normal = new();

    public readonly Sync<float> Offset = new();

    internal VolumeTextureMaterial? Owner;

    // (normal, offset), the shape the shader takes.
    public float4 Plane
    {
        get
        {
            var normal = Normal.Value;
            return new float4(normal.x, normal.y, normal.z, Offset.Value);
        }
    }

    public override void Initialize(World world, IWorldElement? parent)
    {
        base.Initialize(world, parent);
        SyncMemberDiscovery.DiscoverAndInitializeSyncMembers(this, world, this);
        Normal.Changed += OnFieldChanged;
        Offset.Changed += OnFieldChanged;
    }

    private void OnFieldChanged(IChangeable member) => Owner?.PlanesChanged();

    public void CopyFromSource(ISyncMember source, Action<ISyncMember, ISyncMember> copyChild)
    {
        if (source is not VolumeSlicePlane other || ReferenceEquals(other, this))
            return;
        copyChild(other.Normal, Normal);
        copyChild(other.Offset, Offset);
    }

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage) { }
    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage) { }
    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage) { }
    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage) { }
    protected override void InternalClearDirty() { }

    public override DataTreeNode Save(SaveControl control)
    {
        var dictionary = new DataTreeDictionary();
        dictionary.Add("Normal", Normal.Save(control));
        dictionary.Add("Offset", Offset.Save(control));
        return dictionary;
    }

    public override void Load(DataTreeNode node, LoadControl control)
    {
        if (node is not DataTreeDictionary dictionary)
            return;
        if (dictionary.TryGetNode("Normal") is { } normalNode)
            Normal.Load(normalNode, control);
        if (dictionary.TryGetNode("Offset") is { } offsetNode)
            Offset.Load(offsetNode, control);
    }

    public override object? GetValueAsObject() => Normal.Value;
}
