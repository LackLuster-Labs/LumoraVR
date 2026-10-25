// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Unlit wireframe: constant-width edge lines drawn over an optional filled face.
//
// The lines come from barycentric coordinates carried in the mesh's vertex COLOR channel. Godot 4
// has no geometry shader and no barycentric fragment builtin, and the mesh upload path carries only
// position/normal/tangent/color/uv0/index, so COLOR is the only per-vertex slot a triangle-local
// coordinate can travel in. Bake it with a procedural mesh's "Bake Wireframe Barycentrics" action,
// which unwelds the triangles first because a shared vertex cannot hold three different corner
// values. With no bake the surface still draws, it just has no wires. -xlinka
[ComponentCategory("Assets/Materials")]
public class WireframeMaterial : MaterialProvider, ICommonMaterial
{
    [Group("Lines")]
    public readonly Sync<colorHDR> LineColor;

    // pixels, held constant by screen-space derivatives
    [Group("Lines")]
    [Range(0.1f, 20f, "0.0")]
    public readonly Sync<float> LineWidth;

    // off means the mesh has no baked barycentrics; only the fill draws
    [Group("Lines")]
    public readonly Sync<bool> UseVertexBarycentric;

    [Group("Fill")]
    public readonly Sync<colorHDR> FillColor;

    [Group("Fill")]
    public readonly AssetRef<TextureAsset> FillTexture;

    [Group("Fill")]
    public readonly Sync<float2> TextureScale;

    [Group("Fill")]
    public readonly Sync<float2> TextureOffset;

    // switches to the depth-test-disabled shader variant; a compile-time render mode in Godot, so it
    // can't be a plain uniform
    [Group("Rendering")]
    public readonly Sync<bool> DrawOverDepth;

    [Group("Rendering")]
    public readonly Sync<int> RenderQueue;

    // On, the lines are LineWidth pixels at any distance. Off, they are Thickness metres in from each edge, so
    // they thin with distance like the mesh does.
    [Group("Lines")]
    public readonly Sync<bool> ScreenSpace;
    [Group("Lines")]
    public readonly Sync<float> Thickness;

    // Toward the silhouette the line and fill swing to the far colours; the exponent pins the swing to the
    // edge.
    [Group("Fresnel")]
    public readonly Sync<bool> UseFresnel;
    [Group("Fresnel")]
    public readonly Sync<float> FresnelExponent;
    [Group("Fresnel")]
    public readonly Sync<colorHDR> LineFarColor;
    [Group("Fresnel")]
    public readonly Sync<colorHDR> FillFarColor;

    // Back faces in the inner colours; off, they take the front ones.
    [Group("Inner")]
    public readonly Sync<bool> UseInnerColors;
    [Group("Inner")]
    public readonly Sync<colorHDR> InnerLineColor;
    [Group("Inner")]
    public readonly Sync<colorHDR> InnerFillColor;
    [Group("Inner")]
    public readonly Sync<colorHDR> InnerLineFarColor;
    [Group("Inner")]
    public readonly Sync<colorHDR> InnerFillFarColor;

    // The fill texture multiplies the lines as well.
    [Group("Fill")]
    public readonly Sync<bool> TextureTintsLines;

    // Both faces and no depth written, as the wireframe always drew, unless these say otherwise. Auto writes
    // depth once the fill is mostly opaque (its alpha at 0.75 or more in any colour it can take).
    [Group("Rendering")]
    public readonly Sync<Culling> Culling;
    [Group("Rendering")]
    public readonly Sync<ZWrite> ZWrite;
    // DEPTH BIAS: toward the camera (negative) or away from it, in depth only.
    [Group("Rendering")]
    public readonly Sync<float> OffsetFactor;
    [Group("Rendering")]
    public readonly Sync<float> OffsetUnits;

    // Whether the last update put a generated shader on in place of a shipped file.
    private bool _generatedVariant;

    protected override MaterialType MaterialType => MaterialType.Wireframe;

    public colorHDR Color
    {
        get => LineColor.Value;
        set => LineColor.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => FillTexture.Target;
        set => FillTexture.Target = value;
    }

    public WireframeMaterial()
    {
        LineColor = new Sync<colorHDR>(this, colorHDR.White);
        LineWidth = new Sync<float>(this, 1.5f);
        UseVertexBarycentric = new Sync<bool>(this, true);
        // Fully transparent fill by default: a wireframe that also paints solid faces is the exception.
        FillColor = new Sync<colorHDR>(this, new colorHDR(0f, 0f, 0f, 0f));
        FillTexture = new AssetRef<TextureAsset>(this);
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);
        DrawOverDepth = new Sync<bool>(this, false);
        RenderQueue = new Sync<int>(this, -1);

        ScreenSpace = new Sync<bool>(this, true);
        Thickness = new Sync<float>(this, 0.001f);
        UseFresnel = new Sync<bool>(this, false);
        FresnelExponent = new Sync<float>(this, 1f);
        LineFarColor = new Sync<colorHDR>(this, colorHDR.White);
        FillFarColor = new Sync<colorHDR>(this, new colorHDR(1f, 1f, 1f, 0f));
        UseInnerColors = new Sync<bool>(this, false);
        InnerLineColor = new Sync<colorHDR>(this, new colorHDR(0f, 0f, 0f, 0.73f));
        InnerFillColor = new Sync<colorHDR>(this, new colorHDR(0f, 0f, 0f, 0f));
        InnerLineFarColor = new Sync<colorHDR>(this, new colorHDR(1f, 1f, 1f, 0.73f));
        InnerFillFarColor = new Sync<colorHDR>(this, new colorHDR(1f, 1f, 1f, 0f));
        TextureTintsLines = new Sync<bool>(this, false);
        Culling = new Sync<Culling>(this, Assets.Culling.None);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Off);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        // The hook swaps between the two shipped files on DrawOverDepth, so that goes only while one of them is
        // on; a generated variant names its own depth test.
        bool depthOffset = OffsetFactor.Value != 0f || OffsetUnits.Value != 0f;
        string? source = VariantSource(Culling.Value, WritesDepth(), DrawOverDepth.Value, depthOffset);
        _generatedVariant = MaterialVariantSource.Apply(asset, source,
            DrawOverDepth.Value ? "res://Shaders/Mat_WireframeOverlay.gdshader" : "res://Shaders/Mat_Wireframe.gdshader",
            _generatedVariant);
        if (!_generatedVariant)
            asset.SetBool("DrawOverDepth", DrawOverDepth.Value);

        asset.SetBool("ScreenSpace", ScreenSpace.Value);
        asset.SetFloat("Thickness", Thickness.Value);
        asset.SetBool("UseFresnel", UseFresnel.Value);
        asset.SetFloat("FresnelExponent", FresnelExponent.Value);
        asset.SetColor("LineFarColor", LineFarColor.Value);
        asset.SetColor("FillFarColor", FillFarColor.Value);
        asset.SetBool("UseInnerColors", UseInnerColors.Value);
        asset.SetColor("InnerLineColor", InnerLineColor.Value);
        asset.SetColor("InnerFillColor", InnerFillColor.Value);
        asset.SetColor("InnerLineFarColor", InnerLineFarColor.Value);
        asset.SetColor("InnerFillFarColor", InnerFillFarColor.Value);
        asset.SetBool("TextureTintsLines", TextureTintsLines.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);

        asset.SetColor("LineColor", LineColor.Value);
        asset.SetFloat("LineWidth", LineWidth.Value);
        asset.SetBool("UseVertexBarycentric", UseVertexBarycentric.Value);

        asset.SetColor("FillColor", FillColor.Value);
        asset.SetTexture("FillTexture", FillTexture.Asset);
        asset.SetBool("UseFillTexture", FillTexture.Asset != null);
        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);

        asset.SetInt("RenderQueue", RenderQueue.Value);
    }

    private bool WritesDepth()
    {
        if (ZWrite.Value != Assets.ZWrite.Auto)
            return ZWrite.Value == Assets.ZWrite.On;
        float fill = FillColor.Value.a;
        if (UseFresnel.Value)
            fill = System.Math.Max(fill, FillFarColor.Value.a);
        if (UseInnerColors.Value)
        {
            fill = System.Math.Max(fill, InnerFillColor.Value.a);
            if (UseFresnel.Value)
                fill = System.Math.Max(fill, InnerFillFarColor.Value.a);
        }
        return fill >= 0.75f;
    }

    // Null while a shipped file covers the state: both faces, no depth written, no bias.
    private static string? VariantSource(Culling culling, bool writesDepth, bool overDepth, bool depthOffset)
    {
        if (culling == Assets.Culling.None && !writesDepth && !depthOffset)
            return null;
        return "shader_type spatial;\nrender_mode unshaded, blend_mix, "
            + (writesDepth ? "depth_draw_always" : "depth_draw_never")
            + (overDepth ? ", depth_test_disabled" : string.Empty)
            + ", " + RenderModeText.Cull(culling) + ";\n"
            + (depthOffset ? "#define DEPTH_OFFSET\n" : string.Empty)
            + "#include \"res://Shaders/Wireframe_Core.gdshaderinc\"\n";
    }
}
