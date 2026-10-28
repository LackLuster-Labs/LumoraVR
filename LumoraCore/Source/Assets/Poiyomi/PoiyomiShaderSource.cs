// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lumora.Core.Assets.Poiyomi;

// The shader text a Poiyomi material compiles. Poiyomi turns a feature's code on with a shader keyword set
// from the feature's switch property; here each switch becomes the port's define, each assigned texture a
// PROP_ define (the source samples only textures a material has), and the render state Godot render modes,
// all ahead of the shared include. Materials with the same switches, textures and render state produce the
// same text, so they share one compiled shader. -xlinka
public static class PoiyomiShaderSource
{
    public const string MainInclude = "res://Shaders/Poiyomi/PoiyomiToon.gdshaderinc";
    public const string OutlineInclude = "res://Shaders/Poiyomi/PoiyomiToonOutline.gdshaderinc";

    private static readonly (string Property, string Define)[] Toggles =
    {
        ("_MainColorAdjustToggle", "POI_COLOR_ADJUST"),
        ("_UseBump2ndMap", "POI_BUMP2NDMAP"),
        ("_EnableBentNormal", "POI_BENTNORMALMAP"),
        ("_DetailEnabled", "POI_DETAIL"),
        ("_PoiSkinEnabled", "POI_SKIN"),
        ("_BackFaceEnabled", "POI_BACKFACE"),
        ("_RGBMaskEnabled", "POI_RGBMASK"),
        ("_ShadingEnabled", "POI_SHADING"),
        ("_EnableAniso", "POI_ANISOTROPICS"),
        ("_MatcapEnable", "POI_MATCAP_0"),
        ("_Matcap0CustomNormal", "POI_MATCAP_0_CUSTOM_NORMAL"),
        ("_Matcap2Enable", "POI_MATCAP_1"),
        ("_Matcap1CustomNormal", "POI_MATCAP_1_CUSTOM_NORMAL"),
        ("_Matcap3Enable", "POI_MATCAP_2"),
        ("_Matcap2CustomNormal", "POI_MATCAP_2_CUSTOM_NORMAL"),
        ("_Matcap4Enable", "POI_MATCAP_3"),
        ("_Matcap3CustomNormal", "POI_MATCAP_3_CUSTOM_NORMAL"),
        ("_CubeMapEnabled", "POI_CUBEMAP"),
        ("_EnableRimLighting", "POI_RIM_0"),
        ("_EnableRim2Lighting", "POI_RIM_1"),
        ("_EnableDepthRimLighting", "POI_DEPTH_RIM"),
        ("_SubsurfaceScattering", "POI_SUBSURFACESCATTERING"),
        ("_MochieBRDF", "POI_MOCHIE_BRDF"),
        ("_GGXAnisotropics", "POI_MOCHIE_GGX_ANISO"),
        ("_ClearCoatBRDF", "POI_CLEARCOAT"),
        ("_EnableEnvironmentalRim", "POI_ENVIRORIM"),
        ("_StylizedSpecular", "POI_STYLIZED_SPECULAR"),
        ("_BacklightEnabled", "POI_BACKLIGHT"),
        ("_EnableUDIMDiscardOptions", "POI_UDIMDISCARD"),
        ("_EnableDepthBulge", "POI_DEPTHBULGE"),
        ("_EnableDissolve", "POI_DISSOLVE"),
        ("_GlitterEnable", "POI_GLITTER"),
        ("_EnablePathing", "POI_PATHING"),
        ("_EnableMirrorOptions", "POI_MIRROR"),
        ("_MirrorTextureEnabled", "POI_MIRROR_TEXTURE"),
        ("_EnableTouchGlow", "POI_DEPTH_FX"),
        ("_TextEnabled", "POI_TEXT"),
        ("_PoiInternalParallax", "POI_INTERNALPARALLAX"),
        ("_VideoEffectsEnable", "POI_VIDEO_EFFECTS"),
        ("_VoronoiEnabled", "POI_VORONOI"),
        ("_EnableTruchet", "POI_TRUCHET"),
        ("_VertexManipulationsEnabled", "POI_VERTEX_MANIPULATION"),
        ("_VertexLookAtEnabled", "POI_VERTEX_LOOKAT"),
        ("_VertexGlitchingEnabled", "POI_VERTEX_GLITCHING"),
        ("_BlackLightMaskingEnabled", "POI_BLACKLIGHTMASKING"),
        ("_GlobalMaskTexturesEnable", "POI_GLOBALMASK_TEXTURES"),
        ("_EnableDistortion", "POI_DISTORTION"),
        ("_PoiParallax", "POI_PARALLAX"),
        ("_PostProcess", "POI_POSTPROCESS"),
        ("_DecalEnabled", "POI_DECAL_0"),
        ("_DecalEnabled1", "POI_DECAL_1"),
        ("_DecalEnabled2", "POI_DECAL_2"),
        ("_DecalEnabled3", "POI_DECAL_3"),
        ("_EnableFlipbook", "POI_FLIPBOOK_0"),
        ("_EnableFlipbook1", "POI_FLIPBOOK_1"),
        ("_EnableEmission", "POI_EMISSION_0"),
        ("_EnableEmission1", "POI_EMISSION_1"),
        ("_EnableEmission2", "POI_EMISSION_2"),
        ("_EnableEmission3", "POI_EMISSION_3"),
    };

    // KeywordEnum properties: the value picks the define by index.
    private static readonly (string Property, string Requires, string[] Defines)[] Enums =
    {
        ("_LightingMode", "_ShadingEnabled", new[] { "POI_LIGHTING_TEXTURERAMP", "POI_LIGHTING_MULTILAYER_MATH", "POI_LIGHTING_WRAPPED", "POI_LIGHTING_SKIN", "POI_LIGHTING_SHADEMAP", "POI_LIGHTING_FLAT", "POI_LIGHTING_REALISTIC", "POI_LIGHTING_CLOTH", "POI_LIGHTING_SDF" }),
        ("_RimStyle", "_EnableRimLighting", new[] { "POI_RIM_0_POIYOMI", "POI_RIM_0_UTS2", "POI_RIM_0_LILTOON" }),
        ("_Rim2Style", "_EnableRim2Lighting", new[] { "POI_RIM_1_POIYOMI", "POI_RIM_1_UTS2", "POI_RIM_1_LILTOON" }),
        ("_StylizedReflectionMode", "_StylizedSpecular", new[] { "POI_STYLIZED_SPECULAR_UTS", "POI_STYLIZED_SPECULAR_LILTOON" }),
    };

    private static readonly string[] StochasticModes = { "POI_STOCHASTIC_DELIOT_HEITZ", "POI_STOCHASTIC_HEXTILE", "POI_STOCHASTIC_NONE" };

    // The mode compiles in only when a texture samples stochastically. With a mode in, every fetch that can be
    // stochastic carries the three-fetch path behind a uniform switch, all of them off or not. -xlinka
    private static readonly string[] StochasticToggles =
    {
        "_MainTexStochastic", "_BumpMapStochastic", "_Bump2ndMapStochastic", "_BentNormalMapStochastic",
        "_DetailMaskStochastic", "_DetailTexStochastic", "_DetailNormalMapStochastic",
        "_RGBAMetallicMapsStochastic", "_RGBASmoothnessMapsStochastic",
        "_RedTextureStochastic", "_GreenTextureStochastic", "_BlueTextureStochastic", "_AlphaTextureStochastic",
        "_RgbNormalRStochastic", "_RgbNormalGStochastic", "_RgbNormalBStochastic", "_RgbNormalAStochastic",
        "_RGBARedPBRSplitMaskStochastic", "_RGBAGreenPBRSplitMaskStochastic", "_RGBABluePBRSplitMaskStochastic", "_RGBAAlphaPBRSplitMaskStochastic",
        "_MochieMetallicMapsStochastic", "_PBRSplitMaskStochastic", "_ClearCoatMapsStochastic", "_PBRMapsStochastic",
    };

    // value: a property's value (its x component), or Poiyomi's default when the material leaves it alone.
    // textures: the texture properties the material assigns. Returns the main pass and, when outlines are on,
    // the outline pass (null otherwise).
    public static (string Main, string? Outline) Build(Func<string, float> value, IEnumerable<string> textures)
    {
        var defines = new StringBuilder();
        foreach (var (property, define) in Toggles)
            if (value(property) != 0f)
                Define(defines, define);
        foreach (var (property, requires, names) in Enums)
        {
            if (value(requires) == 0f)
                continue;
            int index = (int)MathF.Round(value(property));
            if (index >= 0 && index < names.Length)
                Define(defines, names[index]);
        }
        if (Array.Exists(StochasticToggles, toggle => value(toggle) != 0f))
        {
            int mode = (int)MathF.Round(value("_StochasticMode"));
            if (mode >= 0 && mode < StochasticModes.Length)
                Define(defines, StochasticModes[mode]);
        }
        if (PicksDerivedUv(value))
            Define(defines, "POI_NEEDS_DERIVED_UVS");
        DefineLightHooks(defines, value);
        var props = new List<string>();
        foreach (var texture in textures)
            props.Add("PROP_" + texture.TrimStart('_').ToUpperInvariant());
        props.Sort(StringComparer.Ordinal);
        foreach (var prop in props)
            Define(defines, prop);

        string main = Pass(defines.ToString(), MainInclude, value,
            "_Cull", "_ZWrite", "_ZTest", "_SrcBlend", "_DstBlend",
            "_StencilRef", "_StencilPassOp", "_StencilZFailOp", "_StencilCompareFunction", cutout: true);
        string? outline = value("_EnableOutlines") != 0f
            ? Pass(defines.ToString(), OutlineInclude, value,
                "_OutlineCull", "_OutlineZWrite", "_OutlineZTest", "_OutlineSrcBlend", "_OutlineDstBlend",
                "_OutlineStencilRef", "_OutlineStencilPassOp", "_OutlineStencilZFailOp", "_OutlineStencilCompareFunction", cutout: false)
            : null;
        return (main, outline);
    }

    private static void Define(StringBuilder sb, string name) => sb.Append("#define ").Append(name).Append('\n');

    // UV selectors pick 0-3 for the mesh UVs and 4 and up for the sets the shader works out per pixel
    // (panosphere, world, polar, distorted, local, view normal, screen), which compile in only when a selector
    // picks one. Every float or int property with "uv" in its name is checked: a superset of the selectors.
    // -xlinka
    private static string[]? _uvSelectors;

    private static bool PicksDerivedUv(Func<string, float> value)
    {
        var selectors = _uvSelectors;
        if (selectors == null)
        {
            var names = new List<string>();
            foreach (var property in PoiyomiProperties.All)
                if ((property.Type == ShaderUniformType.Float || property.Type == ShaderUniformType.Int)
                    && property.Name.Contains("uv", StringComparison.OrdinalIgnoreCase))
                    names.Add(property.Name);
            _uvSelectors = selectors = names.ToArray();
        }
        foreach (var name in selectors)
            if (value(name) >= 4f)
                return true;
        return false;
    }

    // Emission, rim, matcap, cubemap and glitter run again per light in light(), each returning at once unless
    // the material's values make it read the light. The hook compiles in only when they do: one compiled in and
    // skipped still costs every pixel, since the GPU sizes the shader for its heaviest path. The conditions are
    // the hooks' own early-outs. -xlinka
    private static readonly string[] EmissionSlots = { "", "1", "2", "3" };
    private static readonly string[] MatcapSlots = { "", "2", "3", "4" };

    private static void DefineLightHooks(StringBuilder defines, Func<string, float> value)
    {
        string text = defines.ToString();
        bool Has(string define) => text.Contains("#define " + define + "\n", StringComparison.Ordinal);
        bool On(string property) => value(property) != 0f;

        bool emission = false, rim = false, matcap = false;
        for (int i = 0; i < 4; i++)
        {
            string e = EmissionSlots[i];
            emission |= Has("POI_EMISSION_" + i) && (On("_EnableGITDEmission" + e) || On("_EmissionFluorescence" + e));
            string m = "_Matcap" + MatcapSlots[i];
            matcap |= Has("POI_MATCAP_" + i) && (On(m + "LightColorMix") || On(m + "LightMask") || value(m + "UVMode") == 4f || On(m + "AddToLight"));
        }
        for (int i = 0; i < 2; i++)
        {
            string r = i == 0 ? "" : "2";
            rim |= Has($"POI_RIM_{i}_POIYOMI") && On($"_Rim{r}ShadowToggle");
            rim |= Has($"POI_RIM_{i}_UTS2") && (On($"_Is_LightColor_Rim{r}Light") || On($"_LightDirection_MaskOn{r}") || On($"_Add_Antipodean_Rim{r}Light"));
            rim |= Has($"POI_RIM_{i}_LILTOON") && (On($"_Rim{r}DirStrength") || On($"_Rim{r}ShadowMask"));
        }
        float glitterMode = value("_GlitterMode");
        bool glitter = Has("POI_GLITTER") && (glitterMode == 2f || On("_GlitterHideInShadow") || On("_GlitterScaleWithLighting")
            || (glitterMode == 1f && On("_GlitterLinearLightDirection")));
        bool cubemap = Has("POI_CUBEMAP") && On("_CubeMapLightMask");

        if (emission) Define(defines, "POI_EMISSION_READS_LIGHT");
        if (rim) Define(defines, "POI_RIM_READS_LIGHT");
        if (matcap) Define(defines, "POI_MATCAP_READS_LIGHT");
        if (cubemap) Define(defines, "POI_CUBEMAP_READS_LIGHT");
        if (glitter) Define(defines, "POI_GLITTER_READS_LIGHT");
    }

    private static string Pass(string defines, string include, Func<string, float> value,
        string cullProp, string zWriteProp, string zTestProp, string srcProp, string dstProp,
        string stencilRefProp, string stencilPassProp, string stencilZFailProp, string stencilCompareProp, bool cutout)
    {
        var modes = new List<string>();
        var extra = new StringBuilder();

        // Unity BlendMode: 0 Zero, 1 One, 2 DstColor, 3 SrcColor, 4 OneMinusDstColor, 5 SrcAlpha,
        // 6 OneMinusSrcColor, 7 DstAlpha, 8 OneMinusDstAlpha, 9 SrcAlphaSaturate, 10 OneMinusSrcAlpha.
        int src = (int)value(srcProp), dst = (int)value(dstProp);
        bool blended = true;
        switch (src, dst)
        {
            case (1, 0):
                modes.Add("blend_mix");
                blended = false;
                break;
            case (1, 10):
                modes.Add("blend_premul_alpha");
                break;
            case (5, 1):
                modes.Add("blend_add");
                break;
            case (1, 1):
            case (4, 1):
            case (6, 1):
                // Color is added as is; Godot's add weighs it by the output alpha, so that alpha is 1.
                modes.Add("blend_add");
                extra.Append("#define POI_ALPHA_OVERRIDE 1.0\n");
                break;
            case (2, 0):
            case (0, 3):
                modes.Add("blend_mul");
                break;
            case (2, 3):
            case (3, 2):
                // 2x Multiplicative: the shader doubles its color under a plain multiply.
                modes.Add("blend_mul");
                extra.Append("#define POI_BLEND_2X\n");
                break;
            default:
                modes.Add("blend_mix");
                break;
        }
        if (blended)
            extra.Append("#define POI_ALPHA_BLENDED\n");
        float mode = value("_Mode");
        bool clip = cutout && (mode == 1f || mode == 9f);
        if (clip)
            extra.Append("#define POI_ALPHA_CUTOUT\n");
        bool coverage = clip && value("_AlphaToCoverage") != 0f;
        if (coverage)
            extra.Append("#define POI_ALPHA_TO_COVERAGE\n");

        // Godot copies the depth buffer for the alpha pass only, so a surface that reads it (depth rim, depth
        // FX, depth bulge) moves there, with a depth prepass so the copy holds the surface itself, as the
        // source's camera depth texture holds opaque objects.
        bool readsDepth = !blended && (value("_EnableDepthRimLighting") != 0f || value("_EnableTouchGlow") != 0f
            || value("_EnableDepthBulge") != 0f);
        if (readsDepth)
            extra.Append("#define POI_FORCE_ALPHA_PASS\n");

        // Stencil (Unity CompareFunction: 0 Disabled, 1 Never, 2 Less, 3 Equal, 4 LessEqual, 5 Greater,
        // 6 NotEqual, 7 GreaterEqual, 8 Always; StencilOp 2 Replace). Godot writes only by replacing.
        int compare = (int)value(stencilCompareProp);
        bool read = compare is >= 1 and <= 7;
        bool write = (int)value(stencilPassProp) == 2;
        bool writeDepthFail = (int)value(stencilZFailProp) == 2;
        string stencil = string.Empty;
        if (read || write || writeDepthFail)
        {
            var flags = new List<string>();
            if (read) flags.Add("read");
            if (write) flags.Add("write");
            if (writeDepthFail) flags.Add("write_depth_fail");
            string cmp = compare switch
            {
                1 => "compare_never",
                2 => "compare_less",
                3 => "compare_equal",
                4 => "compare_less_or_equal",
                5 => "compare_greater",
                6 => "compare_not_equal",
                7 => "compare_greater_or_equal",
                _ => "compare_always",
            };
            int reference = System.Math.Clamp((int)value(stencilRefProp), 0, 255);
            stencil = $"stencil_mode {string.Join(", ", flags)}, {cmp}, {reference.ToString(CultureInfo.InvariantCulture)};\n";
        }

        // Godot only lets a pass in the alpha queue read the stencil; an opaque one gives up its depth write.
        bool zWrite = value(zWriteProp) != 0f && !(read && !blended);
        if (readsDepth && zWrite)
            modes.Add("depth_prepass_alpha");
        modes.Add(!zWrite ? "depth_draw_never" : blended || readsDepth ? "depth_draw_always" : "depth_draw_opaque");

        modes.Add((int)value(cullProp) switch { 0 => "cull_disabled", 1 => "cull_front", _ => "cull_back" });

        int zTest = (int)value(zTestProp);
        if (zTest is 0 or 8)
            modes.Add("depth_test_disabled");
        else if (zTest is 5 or 7)
            modes.Add("depth_test_inverted");

        if (coverage)
            modes.Add("alpha_to_coverage");

        if (value("_IgnoreFog") != 0f)
            modes.Add("fog_disabled");

        var sb = new StringBuilder();
        sb.Append("shader_type spatial;\n");
        sb.Append("render_mode ").Append(string.Join(", ", modes)).Append(";\n");
        sb.Append(stencil);
        sb.Append(defines);
        sb.Append(extra);
        sb.Append("#include \"").Append(include).Append("\"\n");
        return sb.ToString();
    }
}
