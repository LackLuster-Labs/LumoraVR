// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.IO;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;
using Lumora.Core.Scripting;

namespace Lumora.Core.Components.Scripting;

// One thing a script on the host asks a person's machine to do to them: its number, its kind and what it
// carries. A velocity or a place rides in Vector, a turn in Rotation, a scale and its seconds in Amount and
// Seconds, a locomotion's name in Text, a seat or a source slot in Target, exact or clear in Flag, a hand in
// Side. Only the host writes any of it. -xlinka
public sealed class ScriptUserAsk : SyncElement
{
    public override SyncMemberType MemberType => SyncMemberType.Object;

    public readonly Sync<int> Id = new();
    public readonly Sync<ScriptUserAskKind> Kind = new();
    public readonly Sync<float3> Vector = new();
    public readonly Sync<floatQ> Rotation = new();
    public readonly Sync<float> Amount = new();
    public readonly Sync<float> Seconds = new();
    public readonly Sync<string> Text = new();
    public readonly SyncRef<Slot> Target = new();
    public readonly Sync<bool> Flag = new();
    public readonly Sync<Chirality> Side = new();

    public override void Initialize(World world, IWorldElement? parent)
    {
        base.Initialize(world, parent);
        SyncMemberDiscovery.DiscoverAndInitializeSyncMembers(this, world, this);
        Id.MarkHostOnly();
        Kind.MarkHostOnly();
        Vector.MarkHostOnly();
        Rotation.MarkHostOnly();
        Amount.MarkHostOnly();
        Seconds.MarkHostOnly();
        Text.MarkHostOnly();
        Target.MarkHostOnly();
        Flag.MarkHostOnly();
        Side.MarkHostOnly();
    }

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage) { }
    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage) { }
    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage) { }
    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage) { }
    protected override void InternalClearDirty() { }

    public override DataTreeNode Save(SaveControl control)
    {
        var dictionary = new DataTreeDictionary();
        dictionary.Add("Id", Id.Save(control));
        dictionary.Add("Kind", Kind.Save(control));
        dictionary.Add("Vector", Vector.Save(control));
        dictionary.Add("Rotation", Rotation.Save(control));
        dictionary.Add("Amount", Amount.Save(control));
        dictionary.Add("Seconds", Seconds.Save(control));
        dictionary.Add("Text", Text.Save(control));
        dictionary.Add("Target", Target.Save(control));
        dictionary.Add("Flag", Flag.Save(control));
        dictionary.Add("Side", Side.Save(control));
        return dictionary;
    }

    public override void Load(DataTreeNode node, LoadControl control)
    {
        if (node is not DataTreeDictionary dictionary)
            return;
        if (dictionary.TryGetNode("Id") is { } idNode)
            Id.Load(idNode, control);
        if (dictionary.TryGetNode("Kind") is { } kindNode)
            Kind.Load(kindNode, control);
        if (dictionary.TryGetNode("Vector") is { } vectorNode)
            Vector.Load(vectorNode, control);
        if (dictionary.TryGetNode("Rotation") is { } rotationNode)
            Rotation.Load(rotationNode, control);
        if (dictionary.TryGetNode("Amount") is { } amountNode)
            Amount.Load(amountNode, control);
        if (dictionary.TryGetNode("Seconds") is { } secondsNode)
            Seconds.Load(secondsNode, control);
        if (dictionary.TryGetNode("Text") is { } textNode)
            Text.Load(textNode, control);
        if (dictionary.TryGetNode("Target") is { } targetNode)
            Target.Load(targetNode, control);
        if (dictionary.TryGetNode("Flag") is { } flagNode)
            Flag.Load(flagNode, control);
        if (dictionary.TryGetNode("Side") is { } sideNode)
            Side.Load(sideNode, control);
    }

    public override object? GetValueAsObject() => Id.Value;
}
