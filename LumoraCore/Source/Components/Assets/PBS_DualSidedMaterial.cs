// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// PBR with an independent albedo, normal map and emission per face side. For single-sheet geometry where
// the back is meant to read as something else: card faces, flags, book pages, the inside of a shell.
//
// The metallic and specular members differ only in how the surface reflects; see PBS_MetallicDualSided and
// PBS_SpecularDualSided. Saves from before the split name the old single type, which was the metallic one, and
// load as PBS_MetallicDualSided (WorkerManager's rename table).
//
// Culling is off for the life of this material and is not exposed. Face culling is a compile-time
// render mode in Godot shaders, and a one-sided variant of a dual-sided material is just the plain
// PBS material. -xlinka
public abstract class PBS_DualSidedMaterial : MaterialProvider, ICommonMaterial
{
    [Group("Front")]
    public readonly Sync<colorHDR> FrontAlbedoColor;

    [Group("Front")]
    public readonly AssetRef<TextureAsset> FrontAlbedoTexture;

    [Group("Front")]
    public readonly AssetRef<TextureAsset> FrontNormalMap;

    [Group("Front")]
    public readonly Sync<colorHDR> FrontEmissiveColor;

    [Group("Front")]
    public readonly AssetRef<TextureAsset> FrontEmissiveMap;

    [Group("Back")]
    public readonly Sync<colorHDR> BackAlbedoColor;

    [Group("Back")]
    public readonly AssetRef<TextureAsset> BackAlbedoTexture;

    [Group("Back")]
    public readonly AssetRef<TextureAsset> BackNormalMap;

    [Group("Back")]
    public readonly Sync<colorHDR> BackEmissiveColor;

    [Group("Back")]
    public readonly AssetRef<TextureAsset> BackEmissiveMap;

    [Group("Surface")]
    public readonly Sync<float2> TextureScale;

    [Group("Surface")]
    public readonly Sync<float2> TextureOffset;

    [Group("Surface")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale;

    [Group("Rendering")]
    public readonly Sync<bool> AlphaClip;

    [Group("Rendering")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> AlphaCutoff;

    [Group("Rendering")]
    public readonly Sync<int> RenderQueue;

    public colorHDR Color
    {
        get => FrontAlbedoColor.Value;
        set => FrontAlbedoColor.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => FrontAlbedoTexture.Target;
        set => FrontAlbedoTexture.Target = value;
    }

    protected PBS_DualSidedMaterial()
    {
        FrontAlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        FrontAlbedoTexture = new AssetRef<TextureAsset>(this);
        FrontNormalMap = new AssetRef<TextureAsset>(this);
        FrontEmissiveColor = new Sync<colorHDR>(this, colorHDR.Black);
        FrontEmissiveMap = new AssetRef<TextureAsset>(this);
        BackAlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        BackAlbedoTexture = new AssetRef<TextureAsset>(this);
        BackNormalMap = new AssetRef<TextureAsset>(this);
        BackEmissiveColor = new Sync<colorHDR>(this, colorHDR.Black);
        BackEmissiveMap = new AssetRef<TextureAsset>(this);
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);
        NormalScale = new Sync<float>(this, 1f);
        AlphaClip = new Sync<bool>(this, false);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetColor("FrontAlbedoColor", FrontAlbedoColor.Value);
        asset.SetTexture("FrontAlbedoTexture", FrontAlbedoTexture.Asset);
        asset.SetBool("UseFrontAlbedoTexture", FrontAlbedoTexture.Asset != null);
        asset.SetTexture("FrontNormalMap", FrontNormalMap.Asset);
        asset.SetBool("UseFrontNormalMap", FrontNormalMap.Asset != null);
        asset.SetColor("FrontEmissiveColor", FrontEmissiveColor.Value);
        asset.SetTexture("FrontEmissiveMap", FrontEmissiveMap.Asset);
        asset.SetBool("UseFrontEmissiveMap", FrontEmissiveMap.Asset != null);

        asset.SetColor("BackAlbedoColor", BackAlbedoColor.Value);
        asset.SetTexture("BackAlbedoTexture", BackAlbedoTexture.Asset);
        asset.SetBool("UseBackAlbedoTexture", BackAlbedoTexture.Asset != null);
        asset.SetTexture("BackNormalMap", BackNormalMap.Asset);
        asset.SetBool("UseBackNormalMap", BackNormalMap.Asset != null);
        asset.SetColor("BackEmissiveColor", BackEmissiveColor.Value);
        asset.SetTexture("BackEmissiveMap", BackEmissiveMap.Asset);
        asset.SetBool("UseBackEmissiveMap", BackEmissiveMap.Asset != null);

        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);
        asset.SetFloat("NormalScale", NormalScale.Value);

        asset.SetBool("AlphaClip", AlphaClip.Value);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetInt("RenderQueue", RenderQueue.Value);

        UpdateWorkflow(asset);
    }

    protected abstract void UpdateWorkflow(MaterialAsset asset);
}
