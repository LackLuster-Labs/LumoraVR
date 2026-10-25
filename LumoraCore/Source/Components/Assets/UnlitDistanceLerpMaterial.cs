// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Unlit, with two looks blended by distance to a Point: NearTexture times NearColor within Distance of it,
// FarTexture times FarColor beyond, over a crossover TransitionRange wide. The point is in world space, or in
// the mesh's own space with LocalSpace, so a surface can fade from one look to another as something comes near,
// or reveal a second picture round a spot. Unset textures read as white. -xlinka
[ComponentCategory("Assets/Materials")]
public class UnlitDistanceLerpMaterial : MaterialProvider
{
    [Group("Distance")]
    public readonly Sync<bool> LocalSpace;
    public readonly Sync<float3> Point;
    public readonly Sync<float> Distance;
    public readonly Sync<float> TransitionRange;

    [Group("Near")]
    public readonly AssetRef<TextureAsset> NearTexture;
    public readonly Sync<float2> NearTextureScale;
    public readonly Sync<float2> NearTextureOffset;
    public readonly Sync<colorHDR> NearColor;

    [Group("Far")]
    public readonly AssetRef<TextureAsset> FarTexture;
    public readonly Sync<float2> FarTextureScale;
    public readonly Sync<float2> FarTextureOffset;
    public readonly Sync<colorHDR> FarColor;

    [Group("Rendering")]
    public readonly Sync<BlendMode> BlendMode;
    // Used by the Cutout blend.
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> AlphaCutoff;
    public readonly Sync<Culling> Culling;
    public readonly Sync<ZWrite> ZWrite;
    // Depth bias: negative pulls the surface toward the camera in depth only.
    public readonly Sync<float> OffsetFactor;
    public readonly Sync<float> OffsetUnits;
    // -1 = default
    public readonly Sync<int> RenderQueue;

    private const string ShippedFile = "res://Shaders/Mat_UnlitDistanceLerp.gdshader";
    private (BlendMode, ZWrite, Culling, bool) _state;
    private bool _stateSet;
    private string? _source;

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public UnlitDistanceLerpMaterial()
    {
        LocalSpace = new Sync<bool>(this, false);
        Point = new Sync<float3>(this, float3.Zero);
        Distance = new Sync<float>(this, 2f);
        TransitionRange = new Sync<float>(this, 0.2f);

        NearTexture = new AssetRef<TextureAsset>(this);
        NearTextureScale = new Sync<float2>(this, float2.One);
        NearTextureOffset = new Sync<float2>(this, float2.Zero);
        NearColor = new Sync<colorHDR>(this, colorHDR.White);

        FarTexture = new AssetRef<TextureAsset>(this);
        FarTextureScale = new Sync<float2>(this, float2.One);
        FarTextureOffset = new Sync<float2>(this, float2.Zero);
        FarColor = new Sync<colorHDR>(this, colorHDR.Black);

        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
        RenderQueue = new Sync<int>(this, -1);
    }

    public override bool TryGetPrimaryColor(out colorHDR color)
    {
        color = NearColor.Value;
        return true;
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        bool depthOffset = OffsetFactor.Value != 0f || OffsetUnits.Value != 0f;
        var state = (BlendMode.Value, ZWrite.Value, Culling.Value, depthOffset);
        if (!_stateSet || state != _state)
        {
            _stateSet = true;
            _state = state;
            _source = state == (Assets.BlendMode.Opaque, Assets.ZWrite.Auto, Assets.Culling.Back, false)
                ? null
                : SpecialMaterialSource.Unlit("UnlitDistanceLerp_Core", BlendMode.Value, ZWrite.Value, ZTest.LessOrEqual,
                    Culling.Value, depthOffset);
        }
        SpecialMaterialSource.Wear(asset, _source, ShippedFile);
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);

        asset.SetBool("LocalSpace", LocalSpace.Value);
        asset.SetFloat3("LerpPoint", Point.Value);
        asset.SetFloat("LerpDistance", Distance.Value);
        asset.SetFloat("TransitionRange", TransitionRange.Value);

        asset.SetTexture("NearTexture", NearTexture.Asset);
        asset.SetFloat2("NearTextureScale", NearTextureScale.Value);
        asset.SetFloat2("NearTextureOffset", NearTextureOffset.Value);
        asset.SetColor("NearColor", NearColor.Value);

        asset.SetTexture("FarTexture", FarTexture.Asset);
        asset.SetFloat2("FarTextureScale", FarTextureScale.Value);
        asset.SetFloat2("FarTextureOffset", FarTextureOffset.Value);
        asset.SetColor("FarColor", FarColor.Value);
    }
}
