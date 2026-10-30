// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The fluid look, worn through a shipped shader: Goo (glossy, coloured transmission), or Liquid (the
// scene behind, bent and absorbed, the sky and the lights on top). On the Mobile renderer Liquid wears its own
// file, which reads neither the screen nor the depth. The values go to the shader by uniform name. Strands and
// spray never wear this material itself: the renderer builds their edge material from these same values, so a
// strand looks like the goo it came from. CastShadows is read by the renderer.
//
// Absorption and gloss left at their defaults mean each look's own. Both take absorption from the fluid's tint;
// goo colours a shorter path than liquid. A liquid also defaults to a nearly mirror finish.
// Set either to anything else and both looks use it as given. -xlinka
//
// The file and the refraction gate depend on this machine's renderer and tier, which the first fluid hook reports.
// A material that updated before that report picked blind; PickedForThisMachine says so, and the renderer asks
// for an update once the answer is in. -xlinka
[ComponentCategory("Assets/Materials")]
public sealed class FluidMaterial : MaterialProvider
{
    public const string GooShader = "res://Modules/Fluids/Shaders/FluidGoo.gdshader";
    public const string LiquidShader = "res://Modules/Fluids/Shaders/FluidLiquid.gdshader";
    public const string LiquidMobileShader = "res://Modules/Fluids/Shaders/FluidLiquidMobile.gdshader";
    public const string EdgeShader = "res://Modules/Fluids/Shaders/FluidGooEdge.gdshader";
    public const string GooNearShader = "res://Modules/Fluids/Shaders/FluidGooNear.gdshader";
    public const string LiquidNearShader = "res://Modules/Fluids/Shaders/FluidLiquidNear.gdshader";
    public const string LiquidMobileNearShader = "res://Modules/Fluids/Shaders/FluidLiquidMobileNear.gdshader";
    public const string BlobShader = "res://Modules/Fluids/Shaders/FluidBlob.gdshader";

    // The saved default means absorption follows the fluid tint. -xlinka
    public static readonly colorHDR GooAbsorption = new(0.2f, 0.1f, 0.3f, 1f);
    // goo's gloss, and what a liquid left at it wears: still water is close to a mirror
    public const float GooGloss = 0.85f;
    public const float LiquidGloss = 0.97f;

    public readonly Sync<FluidLook> Look = new(FluidLook.Goo);
    // Multiplies the colour the fluid itself carries (each particle, cell and spot has its substance's tint), so
    // white shows the substances as they are.
    public readonly Sync<colorHDR> Tint = new(new colorHDR(1f, 1f, 1f, 1f));
    // extinction per cm; at its default, each look's own (see above)
    public readonly Sync<colorHDR> Absorption = new(GooAbsorption);
    // at its default, each look's own (GooGloss, LiquidGloss)
    public readonly Sync<float> Gloss = new(GooGloss);
    public readonly Sync<float> Clearcoat = new(0.5f);
    public readonly Sync<float> Transmission = new(0.7f);
    // how far the scene behind a liquid shifts, in metres per metre of liquid crossed (about 1 - 1/n; water 0.25)
    public readonly Sync<float> Refraction = new(0.3f);
    public readonly Sync<float> Foam = new(0f);
    public readonly Sync<colorHDR> Emission = new(new colorHDR(0f, 0f, 0f, 1f));
    public readonly Sync<bool> CastShadows = new(false);

    private bool _pickedReported;
    private bool _pickedMobile;
    private FluidGpuTier _pickedTier = FluidGpuTier.None;

    protected override MaterialType MaterialType => MaterialType.Custom;

    // Whether the last update knew this machine's renderer and tier.
    public bool PickedForThisMachine
        => _pickedReported == FluidPlatform.Reported && _pickedMobile == FluidPlatform.IsMobileRenderer
        && _pickedTier == (FluidGpu.Current?.Tier ?? FluidGpuTier.None);

    // The same look with the near-eye cut, for the renderer's eyes-only twin; null for a file that has none.
    public static string? NearVariantOf(string shaderPath) => shaderPath switch
    {
        GooShader => GooNearShader,
        LiquidShader => LiquidNearShader,
        LiquidMobileShader => LiquidMobileNearShader,
        _ => null,
    };

    // The file a look wears on this machine's renderer.
    public static string ShaderFor(FluidLook look, bool mobileRenderer) => look switch
    {
        FluidLook.Liquid => mobileRenderer ? LiquidMobileShader : LiquidShader,
        _ => GooShader,
    };

    // The bend costs one more depth read on top of the screen read every desktop liquid already pays, and the
    // scene's reflection a short march over that depth, so both run from Medium up; Low and the light tiers keep
    // the straight view, the absorption and the sky's reflection.
    public static bool RefractionAllowed(FluidGpuTier tier) => tier >= FluidGpuTier.Medium;

    // What the shaders get for absorption: as set, or zero at the legacy default, meaning from the fluid colour. -xlinka
    public float4 ShaderAbsorption()
    {
        var a = Absorption.Value;
        if (a.Equals(GooAbsorption))
            return float4.Zero;
        return new float4(a.r, a.g, a.b, a.a);
    }

    // What the shaders get for gloss: as set, or at its default the look's own.
    public float ShaderGloss()
        => Look.Value == FluidLook.Liquid && Gloss.Value == GooGloss ? LiquidGloss : Gloss.Value;

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        _pickedReported = FluidPlatform.Reported;
        _pickedMobile = FluidPlatform.IsMobileRenderer;
        _pickedTier = FluidGpu.Current?.Tier ?? FluidGpuTier.None;
        var look = Look.Value;
        asset.SetCustomShader(ShaderFor(look, FluidPlatform.IsMobileRenderer));
        asset.SetColor("tint", Tint.Value);
        asset.SetFloat4("absorption", ShaderAbsorption());
        asset.SetFloat("gloss", ShaderGloss());
        asset.SetFloat("foam", Foam.Value);
        asset.SetColor("emission", Emission.Value);
        if (look == FluidLook.Liquid)
        {
            var tier = FluidGpu.Current?.Tier ?? FluidGpuTier.None;
            if (!FluidPlatform.IsMobileRenderer)
            {
                asset.SetFloat("refraction", RefractionAllowed(tier) ? Refraction.Value : 0f);
                asset.SetFloat("scene_reflection", RefractionAllowed(tier) ? 1f : 0f);
            }
        }
        else
        {
            asset.SetFloat("clearcoat", Clearcoat.Value);
            asset.SetFloat("transmission", Transmission.Value);
        }
    }
}
