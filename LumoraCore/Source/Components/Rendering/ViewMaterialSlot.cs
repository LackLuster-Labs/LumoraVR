// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.IO;
using Lumora.Core.Assets;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;

namespace Lumora.Core.Components;

// One material a view material override puts in place of the renderer's own: Index names the renderer's
// material slot, Material what draws there instead.
public sealed class ViewMaterialSlot : SyncElement
{
    public override SyncMemberType MemberType => SyncMemberType.Object;
    public readonly Sync<int> Index = new();
    public readonly AssetRef<MaterialAsset> Material = new();

    public override void Initialize(World world, IWorldElement? parent)
    {
        base.Initialize(world, parent);
        SyncMemberDiscovery.DiscoverAndInitializeSyncMembers(this, world, this);
    }

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage) { }
    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage) { }
    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage) { }
    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage) { }
    protected override void InternalClearDirty() { }

    public override DataTreeNode Save(SaveControl control)
    {
        var dictionary = new DataTreeDictionary();
        dictionary.Add("Index", Index.Save(control));
        dictionary.Add("Material", Material.Save(control));
        return dictionary;
    }

    public override void Load(DataTreeNode node, LoadControl control)
    {
        if (node is not DataTreeDictionary dictionary)
            return;
        if (dictionary.TryGetNode("Index") is { } indexNode)
            Index.Load(indexNode, control);
        if (dictionary.TryGetNode("Material") is { } materialNode)
            Material.Load(materialNode, control);
    }

    public override object? GetValueAsObject() => Index.Value;
}
