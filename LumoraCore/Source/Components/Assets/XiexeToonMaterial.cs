// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
//
// The shading model this drives is ported from Xiexe's Unity Shaders (XSToon),
// MIT licensed, Copyright (c) 2019 Xiexe. See LumoraGodot/Shaders/Xiexe_Core.gdshaderinc.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// A denser toon model than the plain ToonMaterial: ramp shading plus a lit rim AND a shadow-side rim,
// subsurface bleed through thin geometry, a sharpenable specular lobe, matcap, occlusion tinting and an
// inverted-hull outline.
//
// Deliberately a SEPARATE component rather than more knobs on ToonMaterial. That one is our own simpler
// model and content already depends on how it looks; widening it to cover this would change existing
// worlds. Avatars authored against the original shader family land here instead and keep their
// intended look. -xlinka
[ComponentCategory("Assets/Materials")]
public class XiexeToonMaterial : MaterialProvider, ICommonMaterial
{
    // ALBEDO
    public readonly Sync<colorHDR> AlbedoColor;
    public readonly AssetRef<TextureAsset> AlbedoTexture;
    public readonly Sync<float2> MainTextureScale;
    public readonly Sync<float2> MainTextureOffset;
    public readonly Sync<bool> UseVertexColors;
    // Turns the albedo's hue round the colour wheel, in whole turns (0.5 is the opposite colour), before
    // saturation and lighting.
    public readonly Sync<float> HueShift;
    // Pulls the albedo toward or away from its own luminance before any lighting runs.
    public readonly Sync<float> Saturation;

    // NORMAL
    public readonly AssetRef<TextureAsset> NormalMap;
    public readonly Sync<float> NormalScale;

    // SHADING RAMP - the ramp IS the light response: U is the wrapped N.L, V picks a row.
    public readonly AssetRef<TextureAsset> ShadowRamp;
    public readonly Sync<float> ShadowRampRow;
    // Only used when no ramp is bound.
    public readonly Sync<float> ShadowSharpness;

    // RIM, lit side. Gated on N.L, so it appears only where light reaches - that is what separates it
    // from a plain fresnel.
    public readonly Sync<colorHDR> RimColor;
    public readonly Sync<float> RimIntensity;
    public readonly Sync<float> RimRange;
    public readonly Sync<float> RimThreshold;
    public readonly Sync<float> RimSharpness;
    public readonly Sync<float> RimAlbedoTint;
    public readonly Sync<float> RimAttenuationEffect;

    // RIM, shadow side. Tints the dark side rather than adding light, so it reads as bounce.
    public readonly Sync<colorHDR> ShadowRim;
    public readonly Sync<float> ShadowRimRange;
    public readonly Sync<float> ShadowRimThreshold;
    public readonly Sync<float> ShadowRimSharpness;
    public readonly Sync<float> ShadowRimAlbedoTint;

    // SPECULAR
    public readonly Sync<float> SpecularIntensity;
    // 0 is a hard toon dot, 1 lets the lobe spread into an ordinary highlight.
    public readonly Sync<float> SpecularArea;
    public readonly Sync<float> Metallic;
    public readonly Sync<float> Glossiness;
    public readonly Sync<float> Reflectivity;
    // Not glTF's packing: metallic in R, smoothness in A.
    public readonly AssetRef<TextureAsset> MetallicGlossMap;

    // MATCAP / EMISSION / OCCLUSION
    public readonly AssetRef<TextureAsset> Matcap;
    public readonly Sync<colorHDR> MatcapTint;
    // Laid over the lookup the view-space normal makes into the matcap, 0..1 on both axes.
    public readonly Sync<float2> MatcapScale;
    public readonly Sync<float2> MatcapOffset;
    public readonly AssetRef<TextureAsset> EmissionMap;
    public readonly Sync<colorHDR> EmissionColor;
    public readonly AssetRef<TextureAsset> OcclusionMap;
    public readonly Sync<colorHDR> OcclusionColor;

    // SUBSURFACE - light bleeding through thin geometry: ears, fins, membranes. Costs nothing while
    // SubsurfaceColor is black, which the shader branches on.
    public readonly AssetRef<TextureAsset> ThicknessMap;
    public readonly Sync<colorHDR> SubsurfaceColor;
    public readonly Sync<float> SubsurfaceDistortion;
    public readonly Sync<float> SubsurfacePower;
    public readonly Sync<float> SubsurfaceScale;

    // OUTLINE - an inverted hull on the material's next pass.
    // Model-space metres. A toon outline on a character reads at about one to three millimetres; five
    // is already heavy. This is NOT the flat toon material's 0.1, which is a depth-scaled factor.
    private const float MaxOutlineMetres = 0.005f;
    private bool _warnedOutlineWidth;

    public readonly Sync<colorHDR> OutlineColor;
    public readonly Sync<float> OutlineWidth;
    public readonly AssetRef<TextureAsset> OutlineMask;

    // SURFACE
    public readonly Sync<AlphaMode> AlphaMode;
    public readonly Sync<float> AlphaClip;
    public readonly Sync<Culling> Culling;
    public readonly Sync<int> RenderQueue;

    // OUTLINE STYLE. None draws no hull whatever the width says. Emissive is the flat colour every outline drew
    // before the style existed, so it is the default.
    public readonly Sync<ToonOutlineStyle> Outline;
    // Multiplies the outline colour by the albedo: its colour, and its texture where the hull lies.
    public readonly Sync<bool> OutlineAlbedoTint;

    // MAP COORDINATES. On, every map tiles with the albedo, which is how every material made before per-map
    // transforms looks. Off, each map keeps its own scale and offset.
    public readonly Sync<bool> MapsFollowAlbedo;
    public readonly Sync<float2> NormalMapScale;
    public readonly Sync<float2> NormalMapOffset;
    public readonly Sync<float2> MetallicGlossMapScale;
    public readonly Sync<float2> MetallicGlossMapOffset;
    public readonly Sync<float2> EmissionMapScale;
    public readonly Sync<float2> EmissionMapOffset;
    public readonly Sync<float2> OcclusionMapScale;
    public readonly Sync<float2> OcclusionMapOffset;
    public readonly Sync<float2> ThicknessMapScale;
    public readonly Sync<float2> ThicknessMapOffset;
    // The mesh UV set each map reads.
    public readonly Sync<TextureUVChannel> AlbedoUV;
    public readonly Sync<TextureUVChannel> NormalUV;
    public readonly Sync<TextureUVChannel> MetallicUV;
    public readonly Sync<TextureUVChannel> OcclusionUV;
    public readonly Sync<TextureUVChannel> EmissionUV;
    public readonly Sync<TextureUVChannel> ThicknessUV;

    // Picks the shadow ramp's row per pixel from its red channel, 0 the bottom row and 1 the top, so one ramp
    // texture can shade skin, cloth and metal differently. Without it ShadowRampRow picks the one row.
    public readonly AssetRef<TextureAsset> ShadowRampMask;
    public readonly Sync<float2> ShadowRampMaskScale;
    public readonly Sync<float2> ShadowRampMaskOffset;

    // How vertex colours blend across a triangle: Linear as always, or sRGB for gradients that step evenly.
    public readonly Sync<ColorProfile> VertexColorInterpolationSpace;

    // Auto keeps the depth pre-pass the shipped variants use. A depth bias pushes the surface toward the
    // camera (negative) or away from it in depth only.
    public readonly Sync<ZWrite> ZWrite;
    public readonly Sync<float> OffsetFactor;
    public readonly Sync<float> OffsetUnits;

    // Whether the last update put generated source on in place of a shipped file, for the surface and for
    // the outline pass.
    private bool _generatedVariant;
    private bool _litOutline;

    protected override MaterialType MaterialType => MaterialType.XiexeToon;

    // ICommonMaterial: the shared handle anything generic (the colour picker, the eyedropper) reaches
    // for, mapped onto this material's own fields.
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

    public XiexeToonMaterial()
    {
        AlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture = new AssetRef<TextureAsset>(this);
        MainTextureScale = new Sync<float2>(this, float2.One);
        MainTextureOffset = new Sync<float2>(this, float2.Zero);
        UseVertexColors = new Sync<bool>(this, false);
        HueShift = new Sync<float>(this, 0f);
        Saturation = new Sync<float>(this, 0f);

        NormalMap = new AssetRef<TextureAsset>(this);
        NormalScale = new Sync<float>(this, 1f);

        ShadowRamp = new AssetRef<TextureAsset>(this);
        ShadowRampRow = new Sync<float>(this, 0f);
        ShadowSharpness = new Sync<float>(this, 0.5f);

        RimColor = new Sync<colorHDR>(this, colorHDR.White);
        RimIntensity = new Sync<float>(this, 0f);
        RimRange = new Sync<float>(this, 0.7f);
        RimThreshold = new Sync<float>(this, 0.1f);
        RimSharpness = new Sync<float>(this, 0.1f);
        RimAlbedoTint = new Sync<float>(this, 0f);
        RimAttenuationEffect = new Sync<float>(this, 0f);

        ShadowRim = new Sync<colorHDR>(this, colorHDR.White);
        ShadowRimRange = new Sync<float>(this, 0f);
        ShadowRimThreshold = new Sync<float>(this, 0.1f);
        ShadowRimSharpness = new Sync<float>(this, 0.1f);
        ShadowRimAlbedoTint = new Sync<float>(this, 0f);

        SpecularIntensity = new Sync<float>(this, 0f);
        SpecularArea = new Sync<float>(this, 0.5f);
        Metallic = new Sync<float>(this, 0f);
        Glossiness = new Sync<float>(this, 0.5f);
        Reflectivity = new Sync<float>(this, 0f);
        MetallicGlossMap = new AssetRef<TextureAsset>(this);

        Matcap = new AssetRef<TextureAsset>(this);
        MatcapTint = new Sync<colorHDR>(this, colorHDR.White);
        MatcapScale = new Sync<float2>(this, float2.One);
        MatcapOffset = new Sync<float2>(this, float2.Zero);
        EmissionMap = new AssetRef<TextureAsset>(this);
        EmissionColor = new Sync<colorHDR>(this, colorHDR.Black);
        OcclusionMap = new AssetRef<TextureAsset>(this);
        OcclusionColor = new Sync<colorHDR>(this, colorHDR.White);

        ThicknessMap = new AssetRef<TextureAsset>(this);
        SubsurfaceColor = new Sync<colorHDR>(this, colorHDR.Black);
        SubsurfaceDistortion = new Sync<float>(this, 0.2f);
        SubsurfacePower = new Sync<float>(this, 1f);
        SubsurfaceScale = new Sync<float>(this, 1f);

        OutlineColor = new Sync<colorHDR>(this, colorHDR.Black);
        OutlineWidth = new Sync<float>(this, 0f);
        OutlineMask = new AssetRef<TextureAsset>(this);

        AlphaMode = new Sync<AlphaMode>(this, Assets.AlphaMode.Opaque);
        AlphaClip = new Sync<float>(this, 0.5f);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);

        // Every addition below starts where the material already was, so existing content looks the same.
        Outline = new Sync<ToonOutlineStyle>(this, ToonOutlineStyle.Emissive);
        OutlineAlbedoTint = new Sync<bool>(this, false);

        MapsFollowAlbedo = new Sync<bool>(this, true);
        NormalMapScale = new Sync<float2>(this, float2.One);
        NormalMapOffset = new Sync<float2>(this, float2.Zero);
        MetallicGlossMapScale = new Sync<float2>(this, float2.One);
        MetallicGlossMapOffset = new Sync<float2>(this, float2.Zero);
        EmissionMapScale = new Sync<float2>(this, float2.One);
        EmissionMapOffset = new Sync<float2>(this, float2.Zero);
        OcclusionMapScale = new Sync<float2>(this, float2.One);
        OcclusionMapOffset = new Sync<float2>(this, float2.Zero);
        ThicknessMapScale = new Sync<float2>(this, float2.One);
        ThicknessMapOffset = new Sync<float2>(this, float2.Zero);
        AlbedoUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);
        NormalUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);
        MetallicUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);
        OcclusionUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);
        EmissionUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);
        ThicknessUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);

        ShadowRampMask = new AssetRef<TextureAsset>(this);
        ShadowRampMaskScale = new Sync<float2>(this, float2.One);
        ShadowRampMaskOffset = new Sync<float2>(this, float2.Zero);

        VertexColorInterpolationSpace = new Sync<ColorProfile>(this, ColorProfile.Linear);

        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        // Culling is a compile-time render_mode, so this swaps the shader variant rather than setting a
        // uniform. It runs FIRST: the swap keeps every uniform on the material, but the property pushes
        // below have to land on whatever shader ends up bound.
        asset.SetCulling(Culling.Value);
        // Depth write and bias are compile-time as well. Past what the three files cover the material names
        // its own source over the same body, after the culling swap so the swap cannot undo it.
        bool depthOffset = OffsetFactor.Value != 0f || OffsetUnits.Value != 0f;
        _generatedVariant = MaterialVariantSource.Apply(asset, SurfaceSource(Culling.Value, ZWrite.Value, depthOffset),
            SurfaceFile(Culling.Value), _generatedVariant);

        // Names are PascalCase here and the hook snake-cases them onto the shader uniform.
        asset.SetColor("BaseColor", AlbedoColor.Value);
        asset.SetTexture("MainTexture", AlbedoTexture.Asset);
        asset.SetBool("UseMainTexture", AlbedoTexture.Asset != null);
        asset.SetFloat2("MainTextureScale", MainTextureScale.Value);
        asset.SetFloat2("MainTextureOffset", MainTextureOffset.Value);
        asset.SetBool("UseVertexColors", UseVertexColors.Value);
        asset.SetInt("VertexColorInterpolationSpace", (int)VertexColorInterpolationSpace.Value);
        asset.SetFloat("HueShift", HueShift.Value);
        asset.SetFloat("Saturation", Saturation.Value);

        asset.SetBool("MapsFollowAlbedo", MapsFollowAlbedo.Value);
        asset.SetInt("AlbedoUvSet", (int)AlbedoUV.Value);
        asset.SetInt("NormalUvSet", (int)NormalUV.Value);
        asset.SetInt("MetallicUvSet", (int)MetallicUV.Value);
        asset.SetInt("OcclusionUvSet", (int)OcclusionUV.Value);
        asset.SetInt("EmissionUvSet", (int)EmissionUV.Value);
        asset.SetInt("ThicknessUvSet", (int)ThicknessUV.Value);
        asset.SetFloat2("NormalMapScale", NormalMapScale.Value);
        asset.SetFloat2("NormalMapOffset", NormalMapOffset.Value);
        asset.SetFloat2("MetallicGlossMapScale", MetallicGlossMapScale.Value);
        asset.SetFloat2("MetallicGlossMapOffset", MetallicGlossMapOffset.Value);
        asset.SetFloat2("EmissionMapScale", EmissionMapScale.Value);
        asset.SetFloat2("EmissionMapOffset", EmissionMapOffset.Value);
        asset.SetFloat2("OcclusionMapScale", OcclusionMapScale.Value);
        asset.SetFloat2("OcclusionMapOffset", OcclusionMapOffset.Value);
        asset.SetFloat2("ThicknessMapScale", ThicknessMapScale.Value);
        asset.SetFloat2("ThicknessMapOffset", ThicknessMapOffset.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);

        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetBool("UseNormalMap", NormalMap.Asset != null);
        asset.SetFloat("NormalScale", NormalScale.Value);

        asset.SetTexture("ShadowRamp", ShadowRamp.Asset);
        asset.SetBool("UseShadowRamp", ShadowRamp.Asset != null);
        asset.SetFloat("RampRow", ShadowRampRow.Value);
        asset.SetTexture("ShadowRampMask", ShadowRampMask.Asset);
        asset.SetBool("UseShadowRampMask", ShadowRampMask.Asset != null);
        asset.SetFloat2("ShadowRampMaskScale", ShadowRampMaskScale.Value);
        asset.SetFloat2("ShadowRampMaskOffset", ShadowRampMaskOffset.Value);
        asset.SetFloat("ShadowSharpness", ShadowSharpness.Value);

        asset.SetColor("RimColor", RimColor.Value);
        asset.SetFloat("RimIntensity", RimIntensity.Value);
        asset.SetFloat("RimRange", RimRange.Value);
        asset.SetFloat("RimThreshold", RimThreshold.Value);
        asset.SetFloat("RimSharpness", RimSharpness.Value);
        asset.SetFloat("RimAlbedoTint", RimAlbedoTint.Value);
        asset.SetFloat("RimAttenuationEffect", RimAttenuationEffect.Value);

        asset.SetColor("ShadowRimColor", ShadowRim.Value);
        asset.SetFloat("ShadowRimRange", ShadowRimRange.Value);
        asset.SetFloat("ShadowRimThreshold", ShadowRimThreshold.Value);
        asset.SetFloat("ShadowRimSharpness", ShadowRimSharpness.Value);
        asset.SetFloat("ShadowRimAlbedoTint", ShadowRimAlbedoTint.Value);

        asset.SetFloat("SpecularIntensity", SpecularIntensity.Value);
        asset.SetFloat("SpecularArea", SpecularArea.Value);
        asset.SetFloat("Metallic", Metallic.Value);
        asset.SetFloat("Glossiness", Glossiness.Value);
        asset.SetFloat("Reflectivity", Reflectivity.Value);
        asset.SetTexture("MetallicGlossMap", MetallicGlossMap.Asset);
        asset.SetBool("UseMetallicGlossMap", MetallicGlossMap.Asset != null);

        asset.SetTexture("Matcap", Matcap.Asset);
        asset.SetBool("UseMatcap", Matcap.Asset != null);
        asset.SetColor("MatcapTint", MatcapTint.Value);
        asset.SetFloat2("MatcapScale", MatcapScale.Value);
        asset.SetFloat2("MatcapOffset", MatcapOffset.Value);

        asset.SetTexture("EmissionMap", EmissionMap.Asset);
        asset.SetBool("UseEmissionMap", EmissionMap.Asset != null);
        asset.SetColor("EmissionColor", EmissionColor.Value);

        asset.SetTexture("OcclusionMap", OcclusionMap.Asset);
        asset.SetBool("UseOcclusionMap", OcclusionMap.Asset != null);
        asset.SetColor("OcclusionColor", OcclusionColor.Value);

        asset.SetTexture("ThicknessMap", ThicknessMap.Asset);
        asset.SetBool("UseThicknessMap", ThicknessMap.Asset != null);
        asset.SetColor("SubsurfaceColor", SubsurfaceColor.Value);
        asset.SetFloat("SubsurfaceDistortion", SubsurfaceDistortion.Value);
        asset.SetFloat("SubsurfacePower", SubsurfacePower.Value);
        asset.SetFloat("SubsurfaceScale", SubsurfaceScale.Value);

        // The hook routes the outline's colour, width, mask and texture to the chained hull material instead of
        // the lit surface.
        var outlineColor = OutlineColor.Value;
        var outlineTexture = OutlineAlbedoTint.Value ? AlbedoTexture.Asset : null;
        if (OutlineAlbedoTint.Value)
        {
            var albedo = AlbedoColor.Value;
            outlineColor = new colorHDR(outlineColor.r * albedo.r, outlineColor.g * albedo.g, outlineColor.b * albedo.b, outlineColor.a);
        }
        asset.SetColor("OutlineColor", outlineColor);
        asset.SetTexture("OutlineTexture", outlineTexture!);
        asset.SetBool("UseOutlineTexture", outlineTexture != null);
        asset.SetFloat2("OutlineTextureScale", MainTextureScale.Value);
        asset.SetFloat2("OutlineTextureOffset", MainTextureOffset.Value);
        // Lit swaps the hull to a shaded source over the same body; coming back to Emissive names the unshaded
        // one again. None keeps whichever it has and draws no hull at all.
        if (Outline.Value == ToonOutlineStyle.Lit)
        {
            asset.SetNextPassShaderSource(LitOutlineSource);
            _litOutline = true;
        }
        else if (_litOutline && Outline.Value == ToonOutlineStyle.Emissive)
        {
            asset.SetNextPassShaderSource(EmissiveOutlineSource);
            _litOutline = false;
        }
        // 0.1 HERE IS NOT 0.1 THERE. The flat toon outline multiplies its width by view depth, so its
        // number is a screen-space factor and 0.1 is a reasonable ceiling. This one extrudes the hull in
        // MODEL SPACE METRES (XiexeOutline_Core: VERTEX += normal * outline_width), so the same 0.1 is a
        // TEN CENTIMETRE shell. Copied from the other material, it wrapped an imported fox in a black
        // block that reads as a fat outline rather than as a line.
        //
        // A drawn character outline is a millimetre or two, so that is the ceiling. Anything over it is
        // reported once with the authored number, because a value this far out means a different unit
        // scale and the log is what tells us by how much. -xlinka
        float authoredOutline = Outline.Value == ToonOutlineStyle.None ? 0f : OutlineWidth.Value;
        float outline = System.Math.Clamp(authoredOutline, 0f, MaxOutlineMetres);
        if (authoredOutline > MaxOutlineMetres && !_warnedOutlineWidth)
        {
            _warnedOutlineWidth = true;
            Logging.Logger.Warn(
                $"XiexeToonMaterial on '{Slot?.SlotName.Value}': outline width {authoredOutline} is model-space "
                + $"metres here, clamped to {MaxOutlineMetres}. An authored value this large usually means the "
                + "source measured it in different units.");
        }
        asset.SetFloat("OutlineWidth", outline);
        asset.SetTexture("OutlineMask", OutlineMask.Asset);
        asset.SetBool("UseOutlineMask", OutlineMask.Asset != null);

        asset.SetInt("AlphaMode", (int)AlphaMode.Value);
        asset.SetFloat("AlphaClip", AlphaClip.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);
    }

    private const string OutlineCore = "#include \"res://Shaders/XiexeOutline_Core.gdshaderinc\"\n";
    private const string LitOutlineSource = "shader_type spatial;\nrender_mode cull_front;\n#define OUTLINE_LIT\n" + OutlineCore;
    // The same state Mat_XiexeOutline ships in.
    private const string EmissiveOutlineSource = "shader_type spatial;\nrender_mode unshaded, cull_front;\n" + OutlineCore;

    // The material hook loads these three by culling; nothing else about the surface needs a file of its own.
    private static string SurfaceFile(Culling culling) => culling switch
    {
        Assets.Culling.Front => "res://Shaders/Mat_XiexeFrontCull.gdshader",
        Assets.Culling.None => "res://Shaders/Mat_XiexeDoubleSided.gdshader",
        _ => "res://Shaders/Mat_Xiexe.gdshader"
    };

    // Null while a shipped file covers the state: the depth pre-pass and no bias.
    private static string? SurfaceSource(Culling culling, ZWrite zWrite, bool depthOffset)
    {
        if (zWrite == Assets.ZWrite.Auto && !depthOffset)
            return null;
        string depth = zWrite switch
        {
            Assets.ZWrite.On => "depth_draw_always",
            Assets.ZWrite.Off => "depth_draw_never",
            _ => "depth_prepass_alpha",
        };
        string cull = culling switch
        {
            Assets.Culling.Front => "cull_front",
            Assets.Culling.None => "cull_disabled",
            _ => "cull_back",
        };
        return "shader_type spatial;\nrender_mode blend_mix, " + depth + ", " + cull + ";\n"
            + (depthOffset ? "#define DEPTH_OFFSET\n" : string.Empty)
            + "#include \"res://Shaders/Xiexe_Core.gdshaderinc\"\n";
    }
}
