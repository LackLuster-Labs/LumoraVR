// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Lumora.Core.Components;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Assets.Interop.Unity;

public enum UnityShaderFamily
{
    Unknown,
    Standard,
    StandardSpecular,
    Unlit,
    Poiyomi,
    LilToon,
    UnityChanToon,
    MToon,
    Xiexe,
    Toon,
    ToonStandard,
    ToonLit,
}

// A texture as its import settings left it, ready to attach.
public sealed record UnityTextureRef(Uri Uri, bool NormalMap, bool Linear, TextureWrapMode WrapU, TextureWrapMode WrapV, bool Mipmaps);

// Materials are converted, never their shaders. The shader decides which of our materials the look goes
// into (a toon family becomes the toon material, Standard becomes PBS, Unlit stays unlit), and the saved
// property values fill it: the main texture and colour, normal map, emission, matcap, rim, outline,
// alpha and culling, each read under the names the families actually use. A shader nobody recognises is
// placed by its properties. -xlinka
public static class UnityMaterialBuilder
{
    private const string BuiltinExtra = "0000000000000000f000000000000000";
    private const string BuiltinDefault = "0000000000000000e000000000000000";

    private static readonly Regex ShaderDeclaration = new(@"^\s*Shader\s+""([^""]+)""", RegexOptions.Multiline);

    // The avatar SDK's own shaders. Packages never carry them (the SDK does), so their materials would
    // otherwise be read from leftovers, and a material keeps every property of every shader it ever had.
    private static readonly Dictionary<string, string> SdkShaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0b7113dea2069fc4e8943843eff19f70"] = "SDK/Mobile/Standard Lite",
        ["affc81f3d164d734d8f13053effb1c5c"] = "SDK/Mobile/Toon Lit",
        ["e765db0afa7ecfc44ade2e4e2491f65a"] = "SDK/Mobile/Toon Standard",
    };

    // The name a material's shader goes by: a built-in by its fixed id, the declaration at the top of a
    // shader file carried in the package, or the SDK's for one of its own. Poiyomi's locked copies also
    // leave the original name in a tag.
    public static string? ShaderName(UnityMaterialData mat, UnityPackageArchive archive, Dictionary<string, string?> cache)
    {
        var guid = mat.Shader.Guid;
        if (guid == BuiltinExtra || guid == BuiltinDefault)
        {
            return mat.Shader.FileId switch
            {
                46 => "Standard",
                45 => "Standard (Specular setup)",
                10750 => "Unlit/Transparent",
                10751 => "Unlit/Transparent Cutout",
                10752 => "Unlit/Texture",
                10755 => "Unlit/Color",
                _ => null,
            };
        }
        string? name = null;
        if (!string.IsNullOrEmpty(guid) && !cache.TryGetValue(guid, out name))
        {
            var asset = archive.Find(guid);
            if (asset?.File != null && asset.Size < 8 * 1024 * 1024)
            {
                var text = archive.ReadText(asset);
                var match = text != null ? ShaderDeclaration.Match(text) : null;
                name = match is { Success: true } ? match.Groups[1].Value : null;
            }
            if (name == null && asset?.File == null && SdkShaders.TryGetValue(guid, out var sdk))
                name = sdk;
            cache[guid] = name;
        }
        if (mat.Tags.TryGetValue("OriginalShader", out var original) && !string.IsNullOrEmpty(original))
            return name == null || name.StartsWith("Hidden/Locked/", StringComparison.Ordinal) ? original : name;
        return name;
    }

    public static UnityShaderFamily Identify(UnityMaterialData mat, string? shaderName)
    {
        var name = (shaderName ?? "").ToLowerInvariant();
        if (name.Contains("poiyomi")) return UnityShaderFamily.Poiyomi;
        if (name.Contains("liltoon") || name.StartsWith("lil/") || name.StartsWith("_lil/")) return UnityShaderFamily.LilToon;
        if (name.Contains("unitychantoon") || name.Contains("toon (uts")) return UnityShaderFamily.UnityChanToon;
        if (name.Contains("mtoon")) return UnityShaderFamily.MToon;
        if (name.Contains("xiexe") || name.Contains("xstoon")) return UnityShaderFamily.Xiexe;
        if (name.EndsWith("/toon standard") || name.EndsWith("/toon standard (outline)")) return UnityShaderFamily.ToonStandard;
        if (name.EndsWith("/toon lit")) return UnityShaderFamily.ToonLit;
        if (name.Contains("toon")) return UnityShaderFamily.Toon;
        if (name.StartsWith("standard (specular")) return UnityShaderFamily.StandardSpecular;
        if (name == "standard" || name.StartsWith("autodesk interactive") || name.EndsWith("/standard lite")) return UnityShaderFamily.Standard;
        if (name.StartsWith("unlit/") || name.Contains("/unlit") || name.StartsWith("particles/") || name.Contains("/particles/")) return UnityShaderFamily.Unlit;

        // No name, or one we do not know: the properties give the family away.
        if (mat.HasProperty("shader_master_label") || mat.HasProperty("_ShadingEnabled") || mat.HasProperty("_LightingMode")) return UnityShaderFamily.Poiyomi;
        if (mat.HasProperty("_lilToonVersion")) return UnityShaderFamily.LilToon;
        if (mat.HasProperty("_ShadowBoost") && mat.HasProperty("_MinBrightness") && mat.HasProperty("_Ramp")) return UnityShaderFamily.ToonStandard;
        if (mat.HasProperty("_utsVersion") || mat.HasProperty("_1st_ShadeColor") && mat.HasProperty("_Outline_Width")) return UnityShaderFamily.UnityChanToon;
        if (mat.HasProperty("_ShadeToony") || mat.HasProperty("_ShadeColor") && mat.HasProperty("_OutlineWidthMode")) return UnityShaderFamily.MToon;
        if (mat.HasProperty("_SpecGlossMap") || mat.HasProperty("_SpecColor") && mat.HasProperty("_Glossiness")) return UnityShaderFamily.StandardSpecular;
        if (mat.HasProperty("_Glossiness") || mat.HasProperty("_Metallic") || mat.HasProperty("_MetallicGlossMap")) return UnityShaderFamily.Standard;
        if (mat.HasProperty("_ShadowStrength") || mat.HasProperty("_Ramp") || mat.HasProperty("_ToonRamp")) return UnityShaderFamily.Toon;
        return UnityShaderFamily.Unknown;
    }

    // The family of a material whose shader the package leaves out, read off what every material on that
    // shader saves rather than off this one alone: a material keeps the properties of every shader it was
    // ever given, so one alone can pass for whatever it used to be. Null when nothing more is known than
    // the material itself says.
    public static UnityShaderFamily? IdentifyMissing(UnityMaterialData mat, UnityPackageArchive archive)
    {
        var guid = mat.Shader.Guid;
        if (string.IsNullOrEmpty(guid) || mat.Tags.ContainsKey("OriginalShader"))
            return null;
        if (UnityShaderFingerprint.Common(archive, guid) is not { } common)
            return null;
        var family = Identify(common, null);
        return family == UnityShaderFamily.Unknown ? null : family;
    }

    // A shader the package leaves out: what we make of the material is read from leftover values only.
    public static bool ShaderMissing(UnityMaterialData mat, UnityPackageArchive archive)
    {
        var guid = mat.Shader.Guid;
        return !string.IsNullOrEmpty(guid) && guid != BuiltinExtra && guid != BuiltinDefault && archive.Find(guid)?.File == null
            && !mat.Tags.ContainsKey("OriginalShader") && !SdkShaders.ContainsKey(guid);
    }

    public static bool IsToon(UnityShaderFamily family) => family is UnityShaderFamily.Poiyomi or UnityShaderFamily.LilToon
        or UnityShaderFamily.UnityChanToon or UnityShaderFamily.MToon or UnityShaderFamily.Xiexe or UnityShaderFamily.Toon
        or UnityShaderFamily.ToonStandard or UnityShaderFamily.ToonLit;

    // Every texture the converted material will bind, with what kind of texture it is.
    public static IEnumerable<(string Guid, bool Normal)> TexturesUsed(UnityMaterialData mat, UnityShaderFamily family)
    {
        if (family == UnityShaderFamily.Poiyomi)
            return PoiyomiMaterialBuilder.TexturesUsed(mat);

        var used = new List<(string, bool)>();
        void Add(UnityTextureSlot? slot, bool normal = false)
        {
            if (slot?.Guid != null)
                used.Add((slot.Value.Guid, normal));
        }
        Add(Albedo(mat, family));
        Add(NormalMap(mat, family), normal: true);
        if (EmissionOn(mat, family))
            Add(mat.Texture("_EmissionMap", "_EmissiveMap", "_Emission"));
        if (IsToon(family))
        {
            Add(Matcap(mat, family));
            Add(Ramp(mat, family));
            if (family == UnityShaderFamily.ToonStandard && Ramp(mat, family)?.Guid == null)
                used.Add((UnityToonRamps.Flat, false));
            if (family is not (UnityShaderFamily.ToonStandard or UnityShaderFamily.ToonLit))
                Add(OutlineMask(mat));
            if (family == UnityShaderFamily.ToonStandard && mat.ValidKeywords.Contains("USE_OCCLUSION_MAP"))
                Add(mat.Texture("_OcclusionMap"));
        }
        else if (family != UnityShaderFamily.Unlit)
        {
            Add(mat.Texture("_MetallicGlossMap"));
            Add(mat.Texture("_SpecGlossMap"));
            Add(mat.Texture("_OcclusionMap"));
        }
        return used;
    }

    // Every texture the converted material binds as a cubemap.
    public static IEnumerable<string> CubemapsUsed(UnityMaterialData mat, UnityShaderFamily family)
        => family == UnityShaderFamily.Poiyomi ? PoiyomiMaterialBuilder.CubemapsUsed(mat) : Array.Empty<string>();

    // The SDK's toon lit shader reads its main texture and nothing else, not even a colour.
    private static UnityTextureSlot? Albedo(UnityMaterialData mat, UnityShaderFamily family) => family == UnityShaderFamily.ToonLit
        ? mat.Texture("_MainTex")
        : mat.Texture("_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_MainTexture");

    private static color MainColor(UnityMaterialData mat, UnityShaderFamily family) => family == UnityShaderFamily.ToonLit
        ? new color(1f, 1f, 1f, 1f)
        : mat.FirstColor("_Color", "_BaseColor", "_MainColor", "_TintColor") ?? new color(1f, 1f, 1f, 1f);

    // The SDK toon material samples its normal map only with the feature switched on.
    private static UnityTextureSlot? NormalMap(UnityMaterialData mat, UnityShaderFamily family) => family switch
    {
        UnityShaderFamily.ToonLit => null,
        UnityShaderFamily.ToonStandard when !mat.ValidKeywords.Contains("USE_NORMAL_MAPS") => null,
        _ => mat.Texture("_BumpMap", "_NormalMap", "_BumpMapTex"),
    };

    private static UnityTextureSlot? OutlineMask(UnityMaterialData mat) => mat.Texture("_OutlineMask", "_OutlineWidthTexture", "_OutlineWidthMask", "_Outline_Sampler");

    private static bool EmissionOn(UnityMaterialData mat, UnityShaderFamily family)
    {
        if (family == UnityShaderFamily.ToonLit)
            return false;
        if (family == UnityShaderFamily.Poiyomi)
            return mat.Toggle("_EnableEmission") || mat.Keywords.Contains("_EMISSION");
        if (family == UnityShaderFamily.LilToon)
            return mat.Toggle("_UseEmission");
        if (mat.Keywords.Contains("_EMISSION"))
            return true;
        var c = mat.FirstColor("_EmissionColor", "_EmissiveColor", "_Emissive_Color") ?? default;
        bool lit = c.r + c.g + c.b > 0.001f;
        if (family == UnityShaderFamily.ToonStandard)
            return lit && mat.Float("_EmissionStrength", 1f) > 0f;
        return lit && mat.Texture("_EmissionMap", "_EmissiveMap", "_Emissive_Tex") != null;
    }

    private static colorHDR EmissionColor(UnityMaterialData mat, UnityShaderFamily family)
    {
        var c = mat.FirstColor("_EmissionColor", "_EmissiveColor", "_Emissive_Color", "_Emission") ?? new color(0f, 0f, 0f, 1f);
        float strength = family is UnityShaderFamily.Poiyomi or UnityShaderFamily.ToonStandard ? mat.Float("_EmissionStrength", 1f) : 1f;
        if (family == UnityShaderFamily.Poiyomi && strength <= 0f && mat.Toggle("_EnableEmission"))
            strength = 1f;
        return new colorHDR(c.r * strength, c.g * strength, c.b * strength, 1f);
    }

    // Poiyomi's colour adjust (a switch over hue, in turns, and saturation around its middle) and lilToon's
    // HSVG vector (hue offset in turns, saturation as a multiplier).
    public static (float Hue, float Saturation) ColorAdjust(UnityMaterialData mat, UnityShaderFamily family)
    {
        switch (family)
        {
            case UnityShaderFamily.Poiyomi:
            {
                bool adjust = mat.Floats.TryGetValue("_MainColorAdjustToggle", out var on) ? on > 0.5f : mat.Floats.ContainsKey("_MainHueShift");
                if (!adjust)
                    return (0f, 0f);
                bool hueOn = !mat.Floats.TryGetValue("_MainHueShiftToggle", out var hueToggle) || hueToggle > 0.5f;
                float hue = hueOn ? mat.Float("_MainHueShift", 0f) : 0f;
                return (hue - MathF.Floor(hue), System.Math.Clamp(mat.Float("_Saturation", 0f), -1f, 1f));
            }
            case UnityShaderFamily.LilToon when mat.FirstColor("_MainTexHSVG") is { } hsvg:
            {
                float hue = hsvg.r;
                return (hue - MathF.Floor(hue), System.Math.Clamp(hsvg.g - 1f, -1f, 1f));
            }
            case UnityShaderFamily.ToonStandard when mat.ValidKeywords.Contains("USE_HUE_SHIFT"):
            {
                // Radians there, whole turns here.
                float hue = mat.Float("_HueShift", 0f) / (2f * MathF.PI);
                return (hue - MathF.Floor(hue), 0f);
            }
            default:
                return (0f, 0f);
        }
    }

    private static UnityTextureSlot? Matcap(UnityMaterialData mat, UnityShaderFamily family) => family switch
    {
        UnityShaderFamily.Poiyomi => mat.Toggle("_MatcapEnable") ? mat.Texture("_Matcap") : null,
        UnityShaderFamily.LilToon => mat.Toggle("_UseMatCap") ? mat.Texture("_MatCapTex") : null,
        UnityShaderFamily.MToon => mat.Texture("_SphereAdd"),
        // Its multiplicative matcap has no counterpart here; the additive one rides on emission as ours does.
        UnityShaderFamily.ToonStandard => mat.ValidKeywords.Contains("USE_MATCAP") && mat.Float("_MatcapType") < 0.5f ? mat.Texture("_Matcap") : null,
        UnityShaderFamily.ToonLit => null,
        UnityShaderFamily.UnityChanToon => mat.Toggle("_MatCap") ? mat.Texture("_MatCap_Sampler") : null,
        _ => mat.Texture("_Matcap", "_MatCap"),
    };

    private static UnityTextureSlot? Ramp(UnityMaterialData mat, UnityShaderFamily family) => family switch
    {
        UnityShaderFamily.Poiyomi => mat.Keywords.Contains("_LIGHTINGMODE_TEXTURERAMP") || mat.Float("_LightingMode", -1f) == 0f ? mat.Texture("_ToonRamp") : null,
        UnityShaderFamily.Xiexe or UnityShaderFamily.ToonStandard => mat.Texture("_Ramp"),
        UnityShaderFamily.ToonLit => null,
        _ => mat.Texture("_Ramp", "_ToonRamp", "_ShadowRamp"),
    };

    // How the surface treats alpha. The blend factors are the most direct answer when the shader saved
    // them; then the alpha-test keywords and the render type tag; then the render queue; Standard's own
    // mode is the last word only for Standard.
    private enum Alpha { Opaque, Cutout, Blend, Premultiplied, Additive, Multiply }

    // Unity's blend factors: Zero 0, One 1, DstColor 2, SrcColor 3, SrcAlpha 5, OneMinusSrcAlpha 10.
    private static Alpha? FromFactors(float src, float dst)
    {
        if (dst == 1f && (src == 1f || src == 5f))
            return Alpha.Additive;
        if ((src == 2f && dst == 0f) || (src == 0f && dst == 3f))
            return Alpha.Multiply;
        if (dst == 10f)
            return src == 1f ? Alpha.Premultiplied : Alpha.Blend;
        return null;
    }

    private static Alpha AlphaOf(UnityMaterialData mat, UnityShaderFamily family, string? shaderName)
    {
        var name = (shaderName ?? "").ToLowerInvariant();
        // The SDK's mobile shaders other than its particles draw opaque whatever a leftover value says.
        if (family is UnityShaderFamily.ToonStandard or UnityShaderFamily.ToonLit || StandardLite(shaderName))
            return Alpha.Opaque;
        if (name.Contains("additive"))
            return Alpha.Additive;
        if (name.Contains("multiply"))
            return Alpha.Multiply;
        if (mat.Floats.TryGetValue("_DstBlend", out float dst) && FromFactors(mat.Float("_SrcBlend", 1f), dst) is { } blended)
            return blended;
        if (mat.Keywords.Contains("_ALPHATEST_ON") || mat.Toggle("_AlphaTest") || mat.Tags.GetValueOrDefault("RenderType") == "TransparentCutout")
            return Alpha.Cutout;
        if (mat.Keywords.Contains("_ALPHAPREMULTIPLY_ON"))
            return Alpha.Premultiplied;
        if (mat.Keywords.Contains("_ALPHAMODULATE_ON"))
            return Alpha.Multiply;
        if (mat.Keywords.Contains("_ALPHABLEND_ON") || mat.Tags.GetValueOrDefault("RenderType") == "Transparent")
            return Alpha.Blend;
        if (family == UnityShaderFamily.LilToon && mat.Floats.TryGetValue("_TransparentMode", out float lil))
            return lil == 1f ? Alpha.Cutout : lil >= 2f ? Alpha.Blend : Alpha.Opaque;
        if (mat.RenderQueue >= 3000)
            return Alpha.Blend;
        if (mat.RenderQueue >= 2450)
            return Alpha.Cutout;
        if (family is UnityShaderFamily.Standard or UnityShaderFamily.StandardSpecular or UnityShaderFamily.ToonStandard)
            return mat.Float("_Mode") switch { 1f => Alpha.Cutout, 2f => Alpha.Blend, 3f => Alpha.Premultiplied, _ => Alpha.Opaque };
        return Alpha.Opaque;
    }

    private static Culling CullOf(UnityMaterialData mat, UnityShaderFamily family, string? shaderName)
    {
        float cull = family switch
        {
            UnityShaderFamily.ToonStandard => mat.Float("_Culling", 2f),
            UnityShaderFamily.ToonLit => 2f,
            _ when StandardLite(shaderName) => 2f,
            _ => mat.FirstFloat("_Cull", "_CullMode", "_CullingMode", "_Culling") ?? 2f,
        };
        return cull switch { 0f => Culling.None, 1f => Culling.Front, _ => Culling.Back };
    }

    private static bool StandardLite(string? shaderName) => shaderName?.EndsWith("/standard lite", StringComparison.OrdinalIgnoreCase) == true;

    // Standard's metallic slider is tagged gamma, so a linear project reads it decoded.
    private static float GammaDecoded(float value)
    {
        value = System.Math.Clamp(value, 0f, 1f);
        return value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    public static MaterialProvider Build(Slot holder, UnityMaterialData mat, UnityShaderFamily family,
        IReadOnlyDictionary<string, UnityTextureRef> textures, IReadOnlyDictionary<string, Uri>? cubemaps = null, string? shaderName = null)
    {
        var slot = holder.AddSlot(string.IsNullOrEmpty(mat.Name) ? "Material" : mat.Name);
        var textureProviders = new Dictionary<string, ImageProvider>(StringComparer.OrdinalIgnoreCase);
        ImageProvider? Texture(UnityTextureSlot? source, bool clamp = false)
        {
            if (source?.Guid is not { } guid || !textures.TryGetValue(guid, out var tex))
                return null;
            string key = guid + (clamp ? "/clamp" : "");
            if (textureProviders.TryGetValue(key, out var existing))
                return existing;
            var provider = slot.AttachComponent<ImageProvider>();
            provider.URL.Value = tex.Uri;
            if (tex.NormalMap) provider.IsNormalMap.Value = true;
            provider.WrapModeU.Value = clamp ? TextureWrapMode.Clamp : tex.WrapU;
            provider.WrapModeV.Value = clamp ? TextureWrapMode.Clamp : tex.WrapV;
            if (!tex.Mipmaps) provider.GenerateMipmaps.Value = false;
            textureProviders[key] = provider;
            return provider;
        }
        var cubemapProviders = new Dictionary<string, StaticCubemap>(StringComparer.OrdinalIgnoreCase);
        StaticCubemap? Cubemap(string guid)
        {
            if (cubemaps == null || !cubemaps.TryGetValue(guid, out var uri))
                return null;
            if (cubemapProviders.TryGetValue(guid, out var existing))
                return existing;
            var provider = slot.AttachComponent<StaticCubemap>();
            provider.URL.Value = uri;
            cubemapProviders[guid] = provider;
            return provider;
        }

        if (family == UnityShaderFamily.Poiyomi)
            return PoiyomiMaterialBuilder.Build(slot, mat, Texture, Cubemap);

        var albedo = Albedo(mat, family);
        var mainColor = MainColor(mat, family);
        var alpha = AlphaOf(mat, family, shaderName);
        float cutoff = mat.Float("_Cutoff", 0.5f);
        var cull = CullOf(mat, family, shaderName);
        var normal = NormalMap(mat, family);
        float normalScale = mat.FirstFloat("_BumpScale", "_NormalScale") ?? 1f;
        bool emission = EmissionOn(mat, family);
        var emissionMap = emission ? mat.Texture("_EmissionMap", "_EmissiveMap", "_Emission", "_Emissive_Tex") : null;
        var emissionColor = emission ? EmissionColor(mat, family) : new colorHDR(0f, 0f, 0f, 1f);
        if (emission && emissionMap != null && emissionColor.r + emissionColor.g + emissionColor.b < 0.001f)
            emissionColor = new colorHDR(1f, 1f, 1f, 1f);

        if (IsToon(family))
        {
            var toon = slot.AttachComponent<XiexeToonMaterial>();
            toon.AlbedoColor.Value = new colorHDR(mainColor.r, mainColor.g, mainColor.b, mainColor.a);
            toon.AlbedoTexture.Target = Texture(albedo)!;
            if (albedo is { } a)
            {
                toon.MainTextureScale.Value = a.Scale;
                toon.MainTextureOffset.Value = a.Offset;
            }
            toon.NormalMap.Target = Texture(normal)!;
            toon.NormalScale.Value = normalScale;
            toon.EmissionMap.Target = Texture(emissionMap)!;
            toon.EmissionColor.Value = emissionColor;
            var (hue, saturation) = ColorAdjust(mat, family);
            toon.HueShift.Value = hue;
            toon.Saturation.Value = saturation;
            // The toon material has no additive or multiply pass: those become plain alpha blending.
            toon.AlphaMode.Value = alpha switch
            {
                Alpha.Opaque => AlphaMode.Opaque,
                Alpha.Cutout => AlphaMode.Cutout,
                _ => AlphaMode.Blend,
            };
            toon.AlphaClip.Value = cutoff;
            toon.Culling.Value = cull;

            if (Ramp(mat, family) is { Guid: not null } ramp)
                toon.ShadowRamp.Target = Texture(ramp, clamp: true)!;
            else if (family == UnityShaderFamily.ToonStandard)
                toon.ShadowRamp.Target = Texture(new UnityTextureSlot(UnityToonRamps.Flat, float2.One, float2.Zero), clamp: true)!;

            if (Matcap(mat, family) is { } matcap)
            {
                toon.Matcap.Target = Texture(matcap, clamp: true)!;
                var tint = family == UnityShaderFamily.ToonStandard ? new color(1f, 1f, 1f, 1f)
                    : mat.FirstColor("_MatcapColor", "_MatCapColor") ?? new color(1f, 1f, 1f, 1f);
                float intensity = family == UnityShaderFamily.ToonStandard ? mat.Float("_MatcapStrength", 1f)
                    : mat.FirstFloat("_MatcapIntensity", "_MatCapBlend", "_MatcapStrength") ?? 1f;
                toon.MatcapTint.Value = new colorHDR(tint.r * intensity, tint.g * intensity, tint.b * intensity, 1f);
            }

            bool rim = family switch
            {
                UnityShaderFamily.Poiyomi => mat.Toggle("_EnableRimLighting"),
                UnityShaderFamily.LilToon => mat.Toggle("_UseRim"),
                UnityShaderFamily.MToon => (mat.FirstColor("_RimColor") is { } rc) && rc.r + rc.g + rc.b > 0.001f,
                UnityShaderFamily.ToonStandard => mat.ValidKeywords.Contains("USE_RIMLIGHT") && mat.Float("_RimIntensity", 0.5f) > 0f,
                UnityShaderFamily.ToonLit => false,
                UnityShaderFamily.UnityChanToon => mat.Toggle("_RimLight"),
                _ => mat.Toggle("_RimLighting") || mat.Toggle("_UseRimLight"),
            };
            if (rim)
            {
                var rc = (family == UnityShaderFamily.ToonStandard ? mat.FirstColor("_RimColor") : mat.FirstColor("_RimLightColor", "_RimColor"))
                    ?? new color(1f, 1f, 1f, 1f);
                toon.RimColor.Value = new colorHDR(rc.r, rc.g, rc.b, 1f);
                // Our range is where along the silhouette falloff the rim starts, so a THIN rim is a HIGH
                // range: Poiyomi's width reads inverted, lilToon's border already means this. Poiyomi's
                // strength runs to ten for a rim that thin; ours tops out at four for any width.
                switch (family)
                {
                    case UnityShaderFamily.Poiyomi:
                        toon.RimRange.Value = System.Math.Clamp(1f - mat.Float("_RimWidth", 0.8f), 0f, 1f);
                        toon.RimIntensity.Value = System.Math.Clamp(mat.Float("_RimStrength", 1f) * 0.25f, 0f, 1.5f);
                        break;
                    case UnityShaderFamily.LilToon:
                        toon.RimRange.Value = System.Math.Clamp(mat.Float("_RimBorder", 0.5f), 0f, 1f);
                        toon.RimIntensity.Value = 1f;
                        break;
                    case UnityShaderFamily.ToonStandard:
                        toon.RimRange.Value = System.Math.Clamp(1f - mat.Float("_RimRange", 0.3f), 0f, 1f);
                        toon.RimIntensity.Value = System.Math.Clamp(mat.Float("_RimIntensity", 0.5f), 0f, 1.5f);
                        toon.RimSharpness.Value = System.Math.Clamp(mat.Float("_RimSharpness", toon.RimSharpness.Value), 0f, 1f);
                        toon.RimAlbedoTint.Value = System.Math.Clamp(mat.Float("_RimAlbedoTint", 0f), 0f, 1f);
                        break;
                    default:
                        toon.RimIntensity.Value = System.Math.Clamp(mat.FirstFloat("_RimIntensity", "_RimStrength", "_RimLight_Power") ?? 1f, 0f, 1.5f);
                        break;
                }
            }

            // The SDK toon material's metal, gloss and highlight belong to its specular feature and do nothing
            // with it off; sharpness takes the lobe from a soft highlight to a flat dot. Its reflectance and
            // our reflectivity scale a dielectric the same way. The lit shader tints by vertex colour always.
            if (family == UnityShaderFamily.ToonStandard)
            {
                toon.UseVertexColors.Value = mat.Toggle("_VertexColor");
                if (mat.ValidKeywords.Contains("USE_SPECULAR"))
                {
                    toon.Metallic.Value = System.Math.Clamp(mat.Float("_MetallicStrength", 0f), 0f, 1f);
                    toon.Glossiness.Value = System.Math.Clamp(mat.Float("_GlossStrength", 0.5f), 0f, 1f);
                    toon.SpecularIntensity.Value = 1f;
                    toon.SpecularArea.Value = System.Math.Clamp(1f - mat.Float("_SpecularSharpness", 0f), 0f, 1f);
                    toon.Reflectivity.Value = System.Math.Clamp(mat.Float("_Reflectance", 0.5f), 0f, 1f);
                }
                if (mat.ValidKeywords.Contains("USE_OCCLUSION_MAP"))
                    toon.OcclusionMap.Target = Texture(mat.Texture("_OcclusionMap"))!;
            }
            else if (family == UnityShaderFamily.ToonLit)
                toon.UseVertexColors.Value = true;

            // Toon outlines: Poiyomi, lilToon and MToon author theirs in centimetres, UTS in thousandths of
            // the mesh's own unit, which the importer's metres make millimetres. Ours are model-space metres.
            float outlineMetres = family switch
            {
                UnityShaderFamily.Poiyomi => mat.Toggle("_EnableOutlines") ? mat.Float("_LineWidth") * 0.01f : 0f,
                UnityShaderFamily.LilToon => mat.Float("_OutlineWidth") * 0.01f,
                UnityShaderFamily.MToon => mat.Float("_OutlineWidthMode") > 0.5f ? mat.Float("_OutlineWidth") * 0.01f : 0f,
                UnityShaderFamily.UnityChanToon => UtsOutline(mat, shaderName) ? mat.Float("_Outline_Width") * 0.001f : 0f,
                _ => 0f,
            };
            if (outlineMetres > 0f)
            {
                var oc = mat.FirstColor("_LineColor", "_OutlineColor", "_Outline_Color") ?? new color(0f, 0f, 0f, 1f);
                toon.OutlineColor.Value = new colorHDR(oc.r, oc.g, oc.b, oc.a);
                toon.OutlineWidth.Value = outlineMetres;
                if (OutlineMask(mat) is { } mask)
                    toon.OutlineMask.Target = Texture(mask)!;
            }
            return toon;
        }

        if (family == UnityShaderFamily.Unlit)
        {
            var unlit = slot.AttachComponent<UnlitMaterial>();
            unlit.TintColor.Value = new colorHDR(mainColor.r, mainColor.g, mainColor.b, mainColor.a);
            unlit.Texture.Target = Texture(albedo)!;
            if (albedo is { } a)
            {
                unlit.TextureScale.Value = a.Scale;
                unlit.TextureOffset.Value = a.Offset;
            }
            // The unlit material blends additively but has no multiply pass.
            unlit.BlendMode.Value = alpha == Alpha.Multiply ? BlendMode.Alpha : Blend(alpha);
            unlit.AlphaCutoff.Value = cutoff;
            unlit.Culling.Value = cull;
            return unlit;
        }

        if (family == UnityShaderFamily.StandardSpecular)
        {
            var spec = slot.AttachComponent<PBS_Specular>();
            spec.AlbedoColor.Value = new colorHDR(mainColor.r, mainColor.g, mainColor.b, mainColor.a);
            spec.AlbedoTexture.Target = Texture(albedo)!;
            if (albedo is { } a)
            {
                spec.TextureScale.Value = a.Scale;
                spec.TextureOffset.Value = a.Offset;
            }
            var sc = mat.FirstColor("_SpecColor") ?? new color(0.2f, 0.2f, 0.2f, 1f);
            spec.SpecularColor.Value = new colorHDR(sc.r, sc.g, sc.b, mat.FirstFloat("_Glossiness", "_Smoothness") ?? 0.5f);
            spec.SpecularMap.Target = Texture(mat.Texture("_SpecGlossMap"))!;
            spec.NormalMap.Target = Texture(normal)!;
            spec.NormalScale.Value = normalScale;
            spec.EmissiveMap.Target = Texture(emissionMap)!;
            spec.EmissiveColor.Value = emissionColor;
            spec.OcclusionMap.Target = Texture(mat.Texture("_OcclusionMap"))!;
            spec.BlendMode.Value = alpha is Alpha.Additive or Alpha.Multiply ? BlendMode.Alpha : Blend(alpha);
            spec.AlphaCutoff.Value = cutoff;
            spec.Culling.Value = cull;
            return spec;
        }

        var pbs = slot.AttachComponent<PBS_Metallic>();
        pbs.AlbedoColor.Value = new colorHDR(mainColor.r, mainColor.g, mainColor.b, mainColor.a);
        pbs.AlbedoTexture.Target = Texture(albedo)!;
        if (albedo is { } main)
        {
            pbs.TextureScale.Value = main.Scale;
            pbs.TextureOffset.Value = main.Offset;
        }
        // Standard Lite's sliders default to full and scale its map; Standard's map replaces them.
        bool lite = StandardLite(shaderName);
        pbs.Metallic.Value = GammaDecoded(mat.Float("_Metallic", lite ? 1f : 0f));
        pbs.Smoothness.Value = lite ? mat.Float("_Glossiness", 1f) : mat.FirstFloat("_Glossiness", "_Smoothness") ?? 0.5f;
        pbs.MetallicMap.Target = Texture(mat.Texture("_MetallicGlossMap"))!;
        pbs.SlidersScaleMap.Value = lite;
        pbs.NormalMap.Target = Texture(normal)!;
        pbs.NormalScale.Value = normalScale;
        pbs.EmissiveMap.Target = Texture(emissionMap)!;
        pbs.EmissiveColor.Value = emissionColor;
        pbs.OcclusionMap.Target = Texture(mat.Texture("_OcclusionMap"))!;
        pbs.BlendMode.Value = Blend(alpha);
        pbs.AlphaCutoff.Value = cutoff;
        pbs.Culling.Value = cull;
        if (family == UnityShaderFamily.Unknown)
            Logger.Debug($"UnityMaterial: '{mat.Name}' has a shader with no known family; brought in as PBS from its main texture, normal and emission.");
        return pbs;
    }

    // UTS draws its outline from a pass the "NoOutline" shaders leave out and a material can switch off.
    private static bool UtsOutline(UnityMaterialData mat, string? shaderName)
    {
        if ((shaderName ?? "").Contains("NoOutline", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var pass in mat.DisabledPasses)
            if (pass.Contains("Outline", StringComparison.OrdinalIgnoreCase) || pass == "SRPDefaultUnlit")
                return false;
        return true;
    }

    private static BlendMode Blend(Alpha alpha) => alpha switch
    {
        Alpha.Cutout => BlendMode.Cutout,
        Alpha.Blend => BlendMode.Alpha,
        Alpha.Premultiplied => BlendMode.Transparent,
        Alpha.Additive => BlendMode.Additive,
        Alpha.Multiply => BlendMode.Multiply,
        _ => BlendMode.Opaque,
    };
}
