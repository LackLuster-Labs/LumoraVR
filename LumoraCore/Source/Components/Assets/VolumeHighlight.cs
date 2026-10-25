// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets;

// A slab of a volume texture tinted to pick it out: everything within Range of the plane (dot(point, Normal) +
// Offset, in the cube's own space) is multiplied by Color. Its fields replicate and save one by one; a change to
// any tells the material, which has to push the highlight again.
public sealed class VolumeHighlight : SyncElement, ISyncMemberCopy
{
    public override SyncMemberType MemberType => SyncMemberType.Object;

    public readonly Sync<float3> Normal = new();

    public readonly Sync<float> Offset = new();

    public readonly Sync<float> Range = new();

    public readonly Sync<colorHDR> Color = new(colorHDR.White);

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
        Range.Changed += OnFieldChanged;
        Color.Changed += OnFieldChanged;
    }

    private void OnFieldChanged(IChangeable member) => Owner?.PlanesChanged();

    public void CopyFromSource(ISyncMember source, Action<ISyncMember, ISyncMember> copyChild)
    {
        if (source is not VolumeHighlight other || ReferenceEquals(other, this))
            return;
        copyChild(other.Normal, Normal);
        copyChild(other.Offset, Offset);
        copyChild(other.Range, Range);
        copyChild(other.Color, Color);
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
        dictionary.Add("Range", Range.Save(control));
        dictionary.Add("Color", Color.Save(control));
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
        if (dictionary.TryGetNode("Range") is { } rangeNode)
            Range.Load(rangeNode, control);
        if (dictionary.TryGetNode("Color") is { } colorNode)
            Color.Load(colorNode, control);
    }

    public override object? GetValueAsObject() => Normal.Value;
}
