// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Godot.Hooks;

// What someone else's toon-shaded avatar wears when this viewer has their shaders simplified: the small
// shader in Shaders/AvatarFallback.gdshaderinc, filled from the toon material. Its main texture and colour,
// its cutout or blend and cull, a two-band shade read from its own lighting mode (by the light's brightness
// alone where the toon greys its lights, so a coloured sun does not dye the avatar), its first glow, its
// first matcap and the UDIM tiles it hides. One per toon material, updated in place, so a surface keeps the
// same material across changes. A shader per render state, a handful in all. -xlinka
public static class AvatarFallbackMaterial
{
    private const string Include = "res://Shaders/AvatarFallback.gdshaderinc";

    private enum Blend { Opaque, Cutout, Mix, Premul, Add, Mul, Mul2x }

    private sealed class Entry
    {
        public readonly ShaderMaterial Material = new();
        public Inputs Applied;
        public bool Built;
    }

    private readonly record struct Inputs(
        int Cull, Blend Blend,
        Texture2D? Main, Color MainColor, Vector4 MainSt, Vector4 UvPick,
        Texture2D? AlphaMask, Vector4 AlphaMaskSt, Vector4 AlphaMaskChannels, Vector4 AlphaRule, float Cutoff, float AlphaOne, float Premultiply,
        Vector3 Ambient, Vector3 Dark, Vector4 Band, Vector4 LightRule, float Unlit,
        Texture2D? GlowMap, Texture2D? GlowMask, Color GlowColor, Vector4 GlowMapSt, Vector4 GlowMaskSt, Vector4 GlowMaskChannels, Vector4 GlowRule,
        Texture2D? Matcap, Color MatcapColor, Vector4 MatcapSt, Vector4 MatcapUv, Vector4 MatcapBlend, Vector4 MatcapMore,
        int UdimHide);

    private static readonly ConditionalWeakTable<PoiyomiMaterial, Entry> Cache = new();
    private static readonly Dictionary<(int, Blend), Shader> Shaders = new();

    public static Material For(PoiyomiMaterial source)
    {
        source.ShownSimplified = true;
        var entry = Cache.GetValue(source, _ => new Entry());
        var inputs = Read(source);
        if (!entry.Built || inputs != entry.Applied)
        {
            Apply(entry.Material, inputs, entry.Built ? entry.Applied : (Inputs?)null);
            entry.Applied = inputs;
            entry.Built = true;
        }
        return entry.Material;
    }

    private static Inputs Read(PoiyomiMaterial m)
    {
        var color = m.GetVector("_Color");

        // The toon shader's own reading of the blend factors. A fade it forces opaque draws opaque, and so
        // does a cutout: forcing the alpha to one leaves nothing under the cutoff.
        int src = (int)m.GetFloat("_SrcBlend"), dst = (int)m.GetFloat("_DstBlend");
        float mode = m.GetFloat("_Mode");
        bool forceOpaque = m.GetFloat("_AlphaForceOpaque") != 0f;
        var blend = (src, dst) switch
        {
            (1, 0) => mode is 1f or 9f ? Blend.Cutout : Blend.Opaque,
            (1, 10) => Blend.Premul,
            (5, 1) or (1, 1) or (4, 1) or (6, 1) => Blend.Add,
            (2, 0) or (0, 3) => Blend.Mul,
            (2, 3) or (3, 2) => Blend.Mul2x,
            _ => Blend.Mix,
        };
        if (forceOpaque && blend is Blend.Cutout or Blend.Mix or Blend.Premul)
            blend = Blend.Opaque;

        var alphaMask = TextureOf(m, "_AlphaMask");
        float maskMode = alphaMask != null ? m.GetFloat("_MainAlphaMaskMode") : 0f;
        var alphaRule = new Vector4(maskMode, m.GetFloat("_AlphaMod"), m.GetFloat("_MainIgnoreTexAlpha"), m.GetFloat("_AlphaMaskInvert"));

        var (ambient, dark, band, cap) = Shading(m);
        bool additive = m.GetFloat("_LightingAdditiveEnable") != 0f;
        var lightRule = new Vector4(
            m.GetFloat("_LightingMonochromatic"), m.GetFloat("_LightingAdditiveMonochromatic"), cap,
            !additive ? 0f : m.GetFloat("_LightingAdditiveLimited") != 0f ? m.GetFloat("_LightingAdditiveLimit") : 1e4f);
        band.Z = m.GetFloat("_LightingCastedShadows");
        band.W = m.GetFloat("_LightingAdditivePassthrough");

        var glow = PickGlow(m);
        var matcap = PickMatcap(m);

        int cull = (int)m.GetFloat("_Cull");
        return new Inputs(
            cull is 0 or 1 ? cull : 2, blend,
            TextureOf(m, "_MainTex"), new Color(color.x, color.y, color.z, color.w), Tiling(m.GetVector("_MainTex_ST")),
            new Vector4(SecondUv(m, "_MainTexUV"), SecondUv(m, "_AlphaMaskUV"), glow.MapUv, glow.MaskUv),
            alphaMask, Tiling(m.GetVector("_AlphaMask_ST")),
            new Vector4(m.GetFloat("_AlphaMaskR"), m.GetFloat("_AlphaMaskG"), m.GetFloat("_AlphaMaskB"), m.GetFloat("_AlphaMaskA")),
            alphaRule, m.GetFloat("_Cutoff"), (src, dst) is (1, 1) or (4, 1) or (6, 1) || forceOpaque ? 1f : 0f,
            m.GetFloat("_AlphaPremultiply") != 0f ? 1f : 0f,
            ambient, dark, band, lightRule, m.GetFloat("_ShadingEnabled") == 0f ? 1f : 0f,
            glow.Map, glow.Mask, glow.Color, glow.MapSt, glow.MaskSt, glow.MaskChannels, glow.Rule,
            matcap.Texture, matcap.Color, matcap.St, matcap.Uv, matcap.Blend, matcap.More,
            UdimHide(m));
    }

    // The toon shades in two parts: ALBEDO carries how it shades a surface no light reaches (all the
    // engine's ambient sees), and each light adds its own share over that. Here each lighting mode comes
    // down to that ambient share, the dark side's share of a light and the band between dark and lit,
    // as the toon works them out with a unit light. The main lights' cap rides along. -xlinka
    private static (Vector3 Ambient, Vector3 Dark, Vector4 Band, float Cap) Shading(PoiyomiMaterial m)
    {
        var one = Vector3.One;
        float strength = m.GetFloat("_ShadowStrength");
        bool ignoreAmbient = m.GetFloat("_LightingIgnoreAmbientColor") != 0f;
        var shadow = Linear(m.GetVector("_LightingShadowColor"));
        float cap = m.GetFloat("_LightingCapEnabled") != 0f ? m.GetFloat("_LightingCap") : 1e4f;
        // The usual toon answer for the modes that keep their shadow colour in _LightingShadowColor.
        var dark = ignoreAmbient ? one.Lerp(shadow, strength) : one * (1f - strength);
        var ambient = one.Lerp(shadow, strength);
        var band = new Vector4(0.45f, 0.55f, 0f, 0f);

        switch ((int)m.GetFloat("_LightingMode"))
        {
            case 0:
            {
                // Texture ramp: its dark end taken as black where a ramp is set, unless the offset lifts the
                // whole ramp past its step.
                float offset = m.GetFloat("_ShadowOffset");
                float r = MathF.Max(0f, 1f - strength * (m.GetTexture("_ToonRamp") != null && offset < 0.5f ? 1f : 0f));
                var shared = shadow * (r * (1f - r)) + one * r;
                dark = ignoreAmbient ? shared : one * r;
                ambient = ignoreAmbient ? shared : shadow * (1f - r) + one * r;
                band = Band(0.4f - offset, 0.6f - offset);
                break;
            }
            case 1:
            {
                // Multilayer: the first shadow colour on the dark side, all three layers where no light reaches.
                var c1 = m.GetVector("_ShadowColor");
                var c2 = m.GetVector("_Shadow2ndColor");
                var c3 = m.GetVector("_Shadow3rdColor");
                var first = c1.w > 0f ? Linear(c1) : one;
                var deep = first;
                if (c2.w > 0f)
                    deep = deep.Lerp(Linear(c2), MathF.Min(c2.w, 1f));
                if (c3.w > 0f)
                    deep = deep.Lerp(Linear(c3), MathF.Min(c3.w, 1f));
                dark = ignoreAmbient ? one.Lerp(first, strength) : one * (1f - strength);
                ambient = one.Lerp(deep, strength);
                float border = m.GetFloat("_ShadowBorder"), blur = m.GetFloat("_ShadowBlur");
                band = Band(border - blur * 0.5f, border + blur * 0.5f);
                break;
            }
            case 2:
                band = Band(1f - m.GetFloat("_LightingGradientEnd"), 1f - m.GetFloat("_LightingGradientStart"));
                break;
            case 4:
            {
                var shade = Linear(m.GetVector("_1st_ShadeColor"));
                dark = ambient = one.Lerp(shade, strength);
                float step = m.GetFloat("_BaseColor_Step");
                band = Band(step - m.GetFloat("_BaseShade_Feather"), step);
                break;
            }
            case 5:
                // Flat: the light's colour, no direction.
                dark = ambient = one;
                break;
            case 6:
            case 7:
                // Realistic and cloth: Lambert, and no cap.
                dark = Vector3.Zero;
                ambient = one;
                band = new Vector4(0.5f, 1f, 0f, 0f);
                cap = 1e4f;
                break;
        }
        ambient = ambient.Clamp(new Vector3(1e-3f, 1e-3f, 1e-3f), new Vector3(16f, 16f, 16f));
        return (ambient, dark, band, cap);
    }

    private static Vector4 Band(float from, float to)
    {
        from = Math.Clamp(from, -1f, 1f);
        to = Math.Clamp(to, -1f, 2f);
        if (to - from < 0.02f)
            to = from + 0.02f;
        return new Vector4(from, to, 0f, 0f);
    }

    private readonly record struct Glow(Texture2D? Map, Texture2D? Mask, Color Color, Vector4 MapSt, Vector4 MaskSt, Vector4 MaskChannels, Vector4 Rule, float MapUv, float MaskUv);

    private readonly record struct GlowSlot(string On, string Strength, string Color, string Map, string MapUv, string Mask, string MaskUv,
        string MaskChannel, string MaskInvert, string SharedMask, string BaseAsMap, string Blink, string BlinkMin, string BlinkMax, string Dark, string DarkBright);

    private static GlowSlot Slot(string n, string g) => new(
        "_EnableEmission" + n, "_EmissionStrength" + n, "_EmissionColor" + n, "_EmissionMap" + n, "_EmissionMap" + n + "UV",
        "_EmissionMask" + n, "_EmissionMask" + n + "UV", n.Length == 0 ? "_EmissionMaskChannel" : "_EmissionMask" + n + "Channel",
        "_EmissionMaskInvert" + n, "_EmissionMask" + g + "GlobalMask", "_EmissionBaseColorAsMap" + n,
        "_EmissionBlinkingEnabled" + n, "_EmissiveBlink_Min" + n, "_EmissiveBlink_Max" + n,
        "_EnableGITDEmission" + n, "_GITDEMaxEmissionMultiplier" + n);

    private static readonly GlowSlot[] GlowSlots = { Slot("", "0"), Slot("1", "1"), Slot("2", "2"), Slot("3", "3") };

    // One glow carries over: the first of the toon's four slots that lights. Its map and mask both, so an
    // atlas map with a mask picking the eyes out lights the eyes. A slot cut down by a shared mask this
    // cannot read, with no mask of its own, would light the whole surface, so it is passed over. A blinking
    // glow holds the middle of its blink, and one that fades in the light holds its lit strength. -xlinka
    private static Glow PickGlow(PoiyomiMaterial m)
    {
        foreach (var s in GlowSlots)
        {
            if (m.GetFloat(s.On) == 0f)
                continue;
            var color = m.GetVector(s.Color);
            float strength = m.GetFloat(s.Strength) * color.w;
            if (m.GetFloat(s.Blink) != 0f)
                strength *= (m.GetFloat(s.BlinkMin) + m.GetFloat(s.BlinkMax)) * 0.5f;
            if (m.GetFloat(s.Dark) != 0f)
                strength *= m.GetFloat(s.DarkBright);
            if (strength <= 0f)
                continue;
            var map = TextureOf(m, s.Map);
            var mask = TextureOf(m, s.Mask);
            if (mask == null && m.GetFloat(s.SharedMask) > 0f)
                continue;
            return new Glow(map, mask, new Color(color.x, color.y, color.z, 1f),
                Tiling(m.GetVector(s.Map + "_ST")), Tiling(m.GetVector(s.Mask + "_ST")), Channel(m.GetFloat(s.MaskChannel)),
                new Vector4(strength, m.GetFloat(s.BaseAsMap), m.GetFloat(s.MaskInvert), (map != null ? 1f : 0f) + (mask != null ? 2f : 0f)),
                SecondUv(m, s.MapUv), SecondUv(m, s.MaskUv));
        }
        return new Glow(null, null, new Color(1f, 1f, 1f), Vector4.Zero, Vector4.Zero, Vector4.Zero, Vector4.Zero, 0f, 0f);
    }

    private readonly record struct MatcapPick(Texture2D? Texture, Color Color, Vector4 St, Vector4 Uv, Vector4 Blend, Vector4 More);

    // The toon's first matcap, its texture and blends. One masked by a texture or a shared mask belongs to a
    // part of the surface this cannot pick out, so it is left off rather than spread over the whole. -xlinka
    private static MatcapPick PickMatcap(PoiyomiMaterial m)
    {
        if (m.GetFloat("_MatcapEnable") == 0f || m.GetTexture("_MatcapMask") != null || m.GetFloat("_MatcapMaskGlobalMask") > 0f)
            return new MatcapPick(null, new Color(1f, 1f, 1f), Vector4.Zero, Vector4.Zero, Vector4.Zero, Vector4.Zero);
        var texture = TextureOf(m, "_Matcap");
        float mip = 0f;
        if (texture != null)
            mip = (1f - m.GetFloat("_MatcapSmoothness")) * MathF.Floor(MathF.Log2(MathF.Max(1f, MathF.Max(texture.GetWidth(), texture.GetHeight()))));
        var color = m.GetVector("_MatcapColor");
        int uvMode = (int)m.GetFloat("_MatcapUVMode");
        var st = m.GetVector("_Matcap_ST");
        if (st.x == 0f && st.y == 0f)
            st = new float4(1f, 1f, 0f, 0f);
        return new MatcapPick(texture, new Color(color.x, color.y, color.z, color.w),
            new Vector4(st.x, st.y, st.z, st.w),
            new Vector4(1f, uvMode is >= 0 and <= 3 ? uvMode : 1, m.GetFloat("_MatcapBorder"), MathF.Max(0f, mip)),
            new Vector4(m.GetFloat("_MatcapReplace"), m.GetFloat("_MatcapMultiply"), m.GetFloat("_MatcapAdd"), m.GetFloat("_MatcapScreen")),
            new Vector4(m.GetFloat("_MatcapMixed"), m.GetFloat("_MatcapEmissionStrength"), m.GetFloat("_MatcapBaseColorMix"), m.GetFloat("_MatcapIntensity")));
    }

    // Tiles the toon discards: bit row * 4 + column, bit 16 for UV2.
    private static int UdimHide(PoiyomiMaterial m)
    {
        if (m.GetFloat("_EnableUDIMDiscardOptions") == 0f)
            return 0;
        int bits = 0;
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (m.GetFloat($"_UDIMDiscardRow{row}_{column}") > 0.001f)
                    bits |= 1 << (row * 4 + column);
        if (bits != 0 && SecondUv(m, "_UDIMDiscardUV") != 0f)
            bits |= 1 << 16;
        return bits;
    }

    // The toon counts v up from the bottom, so its tiling's v offset turns over here.
    private static Vector4 Tiling(float4 st)
    {
        if (st.x == 0f && st.y == 0f)
            st = new float4(1f, 1f, 0f, 0f);
        return new Vector4(st.x, st.y, st.z, 1f - st.y - st.w);
    }

    private static float SecondUv(PoiyomiMaterial m, string selector) => m.GetFloat(selector) is >= 0.5f and < 1.5f ? 1f : 0f;

    private static Vector4 Channel(float channel) => (int)channel switch
    {
        1 => new Vector4(0f, 1f, 0f, 0f),
        2 => new Vector4(0f, 0f, 1f, 0f),
        3 => new Vector4(0f, 0f, 0f, 1f),
        _ => new Vector4(1f, 0f, 0f, 0f),
    };

    // A toon colour as the toon shader gets it: sRGB in, linear out.
    private static Vector3 Linear(float4 c)
    {
        var l = new Color(c.x, c.y, c.z).SrgbToLinear();
        return new Vector3(l.R, l.G, l.B);
    }

    private static Texture2D? TextureOf(PoiyomiMaterial m, string name)
        => m.GetTexture(name)?.Asset?.Hook is IGodotTexture texture && texture.IsValid ? texture.GodotTexture2D : null;

    private static Shader ShaderFor(int cull, Blend blend)
    {
        if (Shaders.TryGetValue((cull, blend), out var shader) && GodotObject.IsInstanceValid(shader))
            return shader;
        string cullMode = cull switch { 0 => "cull_disabled", 1 => "cull_front", _ => "cull_back" };
        var (modes, defines) = blend switch
        {
            Blend.Cutout => ("", "#define FB_CUTOUT\n"),
            Blend.Mix => ("blend_mix, ", "#define FB_BLEND\n"),
            Blend.Premul => ("blend_premul_alpha, ", "#define FB_BLEND\n"),
            Blend.Add => ("blend_add, ", "#define FB_BLEND\n"),
            Blend.Mul => ("blend_mul, ", "#define FB_BLEND\n"),
            Blend.Mul2x => ("blend_mul, ", "#define FB_BLEND\n#define FB_DOUBLE\n"),
            _ => ("", ""),
        };
        shader = new Shader { Code = $"shader_type spatial;\nrender_mode {modes}{cullMode}, specular_disabled;\n{defines}#include \"{Include}\"\n" };
        Shaders[(cull, blend)] = shader;
        return shader;
    }

    private static void Apply(ShaderMaterial m, in Inputs i, Inputs? was)
    {
        if (was is not { } before || before.Cull != i.Cull || before.Blend != i.Blend)
            m.Shader = ShaderFor(i.Cull, i.Blend);

        m.SetShaderParameter("main_tex", i.Main);
        m.SetShaderParameter("main_color", i.MainColor);
        m.SetShaderParameter("main_st", i.MainSt);
        m.SetShaderParameter("uv_pick", i.UvPick);

        m.SetShaderParameter("alpha_mask", i.AlphaMask);
        m.SetShaderParameter("alpha_mask_st", i.AlphaMaskSt);
        m.SetShaderParameter("alpha_mask_channels", i.AlphaMaskChannels);
        m.SetShaderParameter("alpha_rule", i.AlphaRule);
        m.SetShaderParameter("alpha_cutoff", i.Cutoff);
        m.SetShaderParameter("alpha_one", i.AlphaOne);
        m.SetShaderParameter("premultiply", i.Premultiply);

        m.SetShaderParameter("shade_ambient", i.Ambient);
        m.SetShaderParameter("shade_ambient_inverse", new Vector3(1f / i.Ambient.X, 1f / i.Ambient.Y, 1f / i.Ambient.Z));
        m.SetShaderParameter("shade_dark", i.Dark);
        m.SetShaderParameter("shade_band", i.Band);
        m.SetShaderParameter("light_rule", i.LightRule);
        m.SetShaderParameter("unlit", i.Unlit);

        m.SetShaderParameter("glow_map", i.GlowMap);
        m.SetShaderParameter("glow_mask", i.GlowMask);
        m.SetShaderParameter("glow_color", i.GlowColor);
        m.SetShaderParameter("glow_map_st", i.GlowMapSt);
        m.SetShaderParameter("glow_mask_st", i.GlowMaskSt);
        m.SetShaderParameter("glow_mask_channels", i.GlowMaskChannels);
        m.SetShaderParameter("glow_rule", i.GlowRule);

        m.SetShaderParameter("matcap_tex", i.Matcap);
        m.SetShaderParameter("matcap_color", i.MatcapColor);
        m.SetShaderParameter("matcap_st", i.MatcapSt);
        m.SetShaderParameter("matcap_uv", i.MatcapUv);
        m.SetShaderParameter("matcap_blend", i.MatcapBlend);
        m.SetShaderParameter("matcap_more", i.MatcapMore);

        m.SetShaderParameter("udim_hide", i.UdimHide);
    }
}
