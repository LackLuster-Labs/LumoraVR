// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.Utility;

// Drives a float3 from the channels of a float2: the size of a quad onto the size of the box around it. A
// two-component source has no Z or W, so picking either reads zero.
[ComponentCategory("Utility/Values")]
[DefaultUpdateOrder(-100)]
public class Float2ToFloat3Remap : Component
{
    public readonly SyncRef<IField<float2>> Source;

    public readonly Sync<RemapChannel> X;

    public readonly Sync<RemapChannel> Y;

    public readonly Sync<RemapChannel> Z;

    public readonly FieldDrive<float3> Target;

    public Float2ToFloat3Remap()
    {
        Source = new SyncRef<IField<float2>>(this);
        X = new Sync<RemapChannel>(this, RemapChannel.X);
        Y = new Sync<RemapChannel>(this, RemapChannel.Y);
        Z = new Sync<RemapChannel>(this, RemapChannel.Zero);
        Target = new FieldDrive<float3>(this) { LocalValueOnly = true };
    }

    public override void OnUpdate(float delta)
    {
        if (!Target.IsLinkValid)
            return;
        var source = Source.Target;
        var value = source != null ? source.Value : float2.Zero;
        Target.SetValue(new float3(
            RemapChannels.Pick(X.Value, value.x, value.y, 0f, 0f),
            RemapChannels.Pick(Y.Value, value.x, value.y, 0f, 0f),
            RemapChannels.Pick(Z.Value, value.x, value.y, 0f, 0f)));
    }
}
