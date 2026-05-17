// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

/// <summary>
/// Built-in water floor material backed by res://Shaders/Metaball.gdshader.
/// Renders a dark futuristic liquid surface with small rising droplet metaballs.
/// </summary>
[ComponentCategory("Assets/Materials")]
public class MetaballMaterial : MaterialProvider, ICommonMaterial
{
    public readonly Sync<colorHDR> TintA;
    public readonly Sync<colorHDR> TintB;
    public readonly Sync<colorHDR> WaterDeepColor;
    public readonly Sync<colorHDR> WaterSurfaceColor;
    public readonly Sync<colorHDR> RippleColor;

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
    public readonly Sync<float> WaveStrength;
    public readonly Sync<float> RippleStrength;
    public readonly Sync<float> RippleRadius;
    public readonly Sync<float> RippleWidth;
    public readonly Sync<float> LineStrength;

    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    public readonly Sync<int> RenderQueue;

    protected override MaterialType MaterialType => MaterialType.Metaball;

    public colorHDR Color
    {
        get => TintA.Value;
        set => TintA.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => null;
        set { }
    }

    public MetaballMaterial()
    {
        TintA = new Sync<colorHDR>(this, new colorHDR(0.72f, 1.00f, 0.92f, 1f));
        TintB = new Sync<colorHDR>(this, new colorHDR(1.00f, 0.56f, 0.88f, 1f));
        WaterDeepColor = new Sync<colorHDR>(this, new colorHDR(0.035f, 0.032f, 0.065f, 1f));
        WaterSurfaceColor = new Sync<colorHDR>(this, new colorHDR(0.155f, 0.140f, 0.190f, 1f));
        RippleColor = new Sync<colorHDR>(this, new colorHDR(0.70f, 1.00f, 0.92f, 1f));

        BlobRadius = new Sync<float>(this, 0.12f);
        BlobSmoothness = new Sync<float>(this, 0.075f);
        BlobCount = new Sync<int>(this, 72);
        RiseSpeed = new Sync<float>(this, 0.34f);

        VolumeExtents = new Sync<float2>(this, new float2(24.5f, 24.5f));
        VolumeHeight = new Sync<float>(this, 3.4f);
        VolumeOffset = new Sync<float3>(this, new float3(0f, 0.5f, 0f));

        RimStrength = new Sync<float>(this, 1.55f);
        RimFalloff = new Sync<float>(this, 2.8f);
        FresnelPower = new Sync<float>(this, 3.0f);
        AlphaScale = new Sync<float>(this, 1.0f);
        EmissionStrength = new Sync<float>(this, 0.82f);
        TimeScale = new Sync<float>(this, 1.0f);
        WaveStrength = new Sync<float>(this, 0.28f);
        RippleStrength = new Sync<float>(this, 0.62f);
        RippleRadius = new Sync<float>(this, 0.58f);
        RippleWidth = new Sync<float>(this, 0.032f);
        LineStrength = new Sync<float>(this, 0.34f);

        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);

        asset.SetColor("TintA", TintA.Value);
        asset.SetColor("TintB", TintB.Value);
        asset.SetColor("WaterDeepColor", WaterDeepColor.Value);
        asset.SetColor("WaterSurfaceColor", WaterSurfaceColor.Value);
        asset.SetColor("RippleColor", RippleColor.Value);

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
        asset.SetFloat("WaveStrength", WaveStrength.Value);
        asset.SetFloat("RippleStrength", RippleStrength.Value);
        asset.SetFloat("RippleRadius", RippleRadius.Value);
        asset.SetFloat("RippleWidth", RippleWidth.Value);
        asset.SetFloat("LineStrength", LineStrength.Value);

    }
}
