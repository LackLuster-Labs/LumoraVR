// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Hyper-specific to the LocalHome world. If another world wants rising orbs,
// fork this rather than dragging LocalHome's tuning along for the ride. - xlinka
[ComponentCategory("Assets/Materials")]
public sealed class LocalHomeRisingMaterial : MaterialProvider
{
    public readonly Sync<colorHDR> TintA;
    public readonly Sync<colorHDR> TintB;
    public readonly Sync<float> BlobRadius;
    public readonly Sync<float> BlobSmoothness;
    public readonly Sync<int> BlobCount;
    public readonly Sync<float> RiseSpeed;
    public readonly Sync<float2> VolumeExtents;
    public readonly Sync<float> VolumeHeight;
    public readonly Sync<float3> VolumeOffset;
    public readonly Sync<float> RimStrength;
    public readonly Sync<float> RimFalloff;
    public readonly Sync<float> FresnelPower;
    public readonly Sync<float> AlphaScale;
    public readonly Sync<float> EmissionStrength;
    public readonly Sync<float> TimeScale;
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    public readonly Sync<int> RenderQueue;

    protected override MaterialType MaterialType => MaterialType.LocalHomeRising;

    public LocalHomeRisingMaterial()
    {
        TintA = new Sync<colorHDR>(this, new colorHDR(0.72f, 1.00f, 0.92f, 1f));
        TintB = new Sync<colorHDR>(this, new colorHDR(1.00f, 0.56f, 0.88f, 1f));
        BlobRadius = new Sync<float>(this, 0.12f);
        BlobSmoothness = new Sync<float>(this, 0.075f);
        BlobCount = new Sync<int>(this, 72);
        RiseSpeed = new Sync<float>(this, 0.34f);
        VolumeExtents = new Sync<float2>(this, new float2(24.0f, 24.0f));
        VolumeHeight = new Sync<float>(this, 7.0f);
        VolumeOffset = new Sync<float3>(this, new float3(0f, -3.5f, 0f));
        RimStrength = new Sync<float>(this, 1.55f);
        RimFalloff = new Sync<float>(this, 2.8f);
        FresnelPower = new Sync<float>(this, 3.0f);
        AlphaScale = new Sync<float>(this, 0.92f);
        EmissionStrength = new Sync<float>(this, 1.05f);
        TimeScale = new Sync<float>(this, 1.0f);
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Transparent);
        Culling = new Sync<Culling>(this, Assets.Culling.Front);
        RenderQueue = new Sync<int>(this, 35);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);

        asset.SetColor("TintA", TintA.Value);
        asset.SetColor("TintB", TintB.Value);
        asset.SetFloat("BlobRadius", BlobRadius.Value);
        asset.SetFloat("BlobSmoothness", BlobSmoothness.Value);
        asset.SetInt("BlobCount", BlobCount.Value);
        asset.SetFloat("RiseSpeed", RiseSpeed.Value);
        asset.SetFloat2("VolumeExtents", VolumeExtents.Value);
        asset.SetFloat("VolumeHeight", VolumeHeight.Value);
        asset.SetFloat3("VolumeOffset", VolumeOffset.Value);
        asset.SetFloat("RimStrength", RimStrength.Value);
        asset.SetFloat("RimFalloff", RimFalloff.Value);
        asset.SetFloat("FresnelPower", FresnelPower.Value);
        asset.SetFloat("AlphaScale", AlphaScale.Value);
        asset.SetFloat("EmissionStrength", EmissionStrength.Value);
        asset.SetFloat("TimeScale", TimeScale.Value);
    }
}
