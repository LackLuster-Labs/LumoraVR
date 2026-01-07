using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

/// <summary>
/// Simple unlit material - no lighting calculations.
/// Useful for UI, particles, and special effects.
/// Uses ShaderMaterial with custom shader in Godot.
/// </summary>
[ComponentCategory("Assets/Materials")]
public class UnlitMaterial : MaterialProvider, ICommonMaterial
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

    // ===== COLOR AND TEXTURE =====

    /// <summary>
    /// Base color/tint.
    /// </summary>
    public readonly Sync<colorHDR> TintColor;

    /// <summary>
    /// Base texture.
    /// </summary>
    public readonly AssetRef<TextureAsset> Texture;

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

    protected override MaterialType MaterialType => MaterialType.Unlit;

    // ===== ICommonMaterial IMPLEMENTATION =====

    public colorHDR Color
    {
        get => TintColor.Value;
        set => TintColor.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => Texture.Target;
        set => Texture.Target = value;
    }

    public UnlitMaterial()
    {
        // Texture transform
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);

        // Color and texture
        TintColor = new Sync<colorHDR>(this, colorHDR.White);
        Texture = new AssetRef<TextureAsset>(this);

        // Blend settings
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        var textureAsset = Texture.Asset;
        AquaLogger.Debug($"UnlitMaterial.UpdateMaterial: Texture.Target={Texture.Target?.GetType().Name}, Texture.Asset={textureAsset?.GetType().Name}, HasHook={textureAsset?.Hook != null}");

        // Blend settings
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);

        // Texture transform
        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);

        // Color and texture
        asset.SetColor("TintColor", TintColor.Value);
        asset.SetTexture("Texture", textureAsset);
    }
}
