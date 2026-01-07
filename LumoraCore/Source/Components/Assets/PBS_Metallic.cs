using Lumora.Core.Math;

namespace Lumora.Core.Assets;

/// <summary>
/// Physically-Based Rendering material using the metallic workflow.
/// Maps to StandardMaterial3D in Godot.
/// </summary>
[ComponentCategory("Assets/Materials")]
public class PBS_Metallic : MaterialProvider, ICommonMaterial
{
    // ===== TEXTURE TRANSFORM =====

    /// <summary>
    /// UV texture scale.
    /// </summary>
    public readonly Sync<float2> TextureScale;

    /// <summary>
    /// UV texture offset.
    /// </summary>
    public readonly Sync<float2> TextureOffset;

    // ===== ALBEDO =====

    /// <summary>
    /// Base color (albedo) of the material.
    /// </summary>
    public readonly Sync<colorHDR> AlbedoColor;

    /// <summary>
    /// Base color texture (albedo map).
    /// </summary>
    public readonly AssetRef<TextureAsset> AlbedoTexture;

    // ===== METALLIC/ROUGHNESS =====

    /// <summary>
    /// Metallic value (0 = dielectric, 1 = metal).
    /// </summary>
    public readonly Sync<float> Metallic;

    /// <summary>
    /// Smoothness value (0 = rough, 1 = smooth). Converted to roughness for Godot.
    /// </summary>
    public readonly Sync<float> Smoothness;

    /// <summary>
    /// Metallic/roughness map. R channel = metallic, A channel = smoothness.
    /// </summary>
    public readonly AssetRef<TextureAsset> MetallicMap;

    // ===== NORMAL MAP =====

    /// <summary>
    /// Normal map texture for surface detail.
    /// </summary>
    public readonly AssetRef<TextureAsset> NormalMap;

    /// <summary>
    /// Normal map strength/scale.
    /// </summary>
    public readonly Sync<float> NormalScale;

    // ===== EMISSION =====

    /// <summary>
    /// Emissive color (self-illumination).
    /// </summary>
    public readonly Sync<colorHDR> EmissiveColor;

    /// <summary>
    /// Emissive map texture.
    /// </summary>
    public readonly AssetRef<TextureAsset> EmissiveMap;

    // ===== OCCLUSION =====

    /// <summary>
    /// Ambient occlusion map.
    /// </summary>
    public readonly AssetRef<TextureAsset> OcclusionMap;

    // ===== BLEND SETTINGS =====

    /// <summary>
    /// Blend mode (Opaque, Cutout, Transparent, Additive).
    /// </summary>
    public readonly Sync<BlendMode> BlendMode;

    /// <summary>
    /// Alpha cutoff threshold (for Cutout blend mode).
    /// </summary>
    public readonly Sync<float> AlphaCutoff;

    /// <summary>
    /// Face culling mode.
    /// </summary>
    public readonly Sync<Culling> Culling;

    /// <summary>
    /// Render queue priority (-1 = default).
    /// </summary>
    public readonly Sync<int> RenderQueue;

    protected override MaterialType MaterialType => MaterialType.PBS_Metallic;

    // ===== ICommonMaterial IMPLEMENTATION =====

    public colorHDR Color
    {
        get => AlbedoColor.Value;
        set => AlbedoColor.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => AlbedoTexture.Target;
        set => AlbedoTexture.Target = value;
    }

    public PBS_Metallic()
    {
        // Texture transform
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);

        // Albedo
        AlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture = new AssetRef<TextureAsset>(this);

        // Metallic/Roughness
        Metallic = new Sync<float>(this, 0.0f);
        Smoothness = new Sync<float>(this, 0.5f);
        MetallicMap = new AssetRef<TextureAsset>(this);

        // Normal
        NormalMap = new AssetRef<TextureAsset>(this);
        NormalScale = new Sync<float>(this, 1.0f);

        // Emission
        EmissiveColor = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap = new AssetRef<TextureAsset>(this);

        // Occlusion
        OcclusionMap = new AssetRef<TextureAsset>(this);

        // Blend settings
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        // Blend settings
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);

        // Texture transform
        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);

        // Albedo
        asset.SetColor("AlbedoColor", AlbedoColor.Value);
        asset.SetTexture("AlbedoTexture", AlbedoTexture.Asset);

        // Metallic/Roughness
        asset.SetFloat("Metallic", Metallic.Value);
        asset.SetFloat("Smoothness", Smoothness.Value);
        asset.SetTexture("MetallicMap", MetallicMap.Asset);

        // Normal
        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetFloat("NormalScale", NormalScale.Value);

        // Emission
        asset.SetColor("EmissiveColor", EmissiveColor.Value);
        asset.SetTexture("EmissiveMap", EmissiveMap.Asset);

        // Occlusion
        asset.SetTexture("OcclusionMap", OcclusionMap.Asset);
    }
}
