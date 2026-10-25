// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// A depth camera's frame rebuilt in 3D: put it on a grid mesh and every vertex is pushed out along its own ray
// as far as DepthTexture says, coloured from ColorTexture at the same spot. A depth sensor's feed, a captured
// RGBD photo, a hologram of a room.
//
// Depth runs 0..1 out of the picture, eased and then spread from DepthFrom to DepthTo metres. NearClip and
// FarClip cut on that 0..1, and where neighbouring depths jump by more than DiscardThreshold (sampled
// DiscardOffset apart in UV) the grid tears, so a person in front of a wall is not joined to it by a sheet.
// FieldOfView is in degrees across and down. The mesh's own positions are never used, only its UVs.
//
// Unlit and drawn from both sides. The renderer still culls by the flat grid's bounds, so give the grid a
// size near what it projects into. -xlinka
[ComponentCategory("Assets/Materials")]
public class DepthProjectionMaterial : MaterialProvider
{
    public readonly AssetRef<TextureAsset> ColorTexture;
    public readonly Sync<float2> ColorTextureScale;
    public readonly Sync<float2> ColorTextureOffset;

    public readonly AssetRef<TextureAsset> DepthTexture;
    public readonly Sync<float2> DepthTextureScale;
    public readonly Sync<float2> DepthTextureOffset;
    public readonly Sync<DepthEncoding> DepthEncoding;

    public readonly Sync<float> DepthFrom;
    public readonly Sync<float> DepthTo;
    public readonly Sync<float2> FieldOfView;
    [Range(0f, 1f, "0.000")]
    public readonly Sync<float> NearClip;
    [Range(0f, 1f, "0.000")]
    public readonly Sync<float> FarClip;
    public readonly Sync<float> DiscardThreshold;
    public readonly Sync<float> DiscardOffset;

    [Group("Rendering")]
    public readonly Sync<BlendMode> BlendMode;
    // Auto writes depth when opaque or cutout.
    public readonly Sync<ZWrite> ZWrite;
    public readonly Sync<int> RenderQueue;

    private const string ShippedFile = "res://Shaders/Mat_DepthProjection.gdshader";

    // Index (int)blend * 2 + (writes depth ? 1 : 0); empty where the shipped file is that state.
    private static readonly string[] Variants = BuildVariants();

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public DepthProjectionMaterial()
    {
        ColorTexture = new AssetRef<TextureAsset>(this);
        ColorTextureScale = new Sync<float2>(this, float2.One);
        ColorTextureOffset = new Sync<float2>(this, float2.Zero);

        DepthTexture = new AssetRef<TextureAsset>(this);
        DepthTextureScale = new Sync<float2>(this, float2.One);
        DepthTextureOffset = new Sync<float2>(this, float2.Zero);
        DepthEncoding = new Sync<DepthEncoding>(this, Assets.DepthEncoding.Grayscale);

        DepthFrom = new Sync<float>(this, 0f);
        DepthTo = new Sync<float>(this, 1f);
        FieldOfView = new Sync<float2>(this, new float2(60f, 33.75f));
        NearClip = new Sync<float>(this, 0f);
        FarClip = new Sync<float>(this, 1f);
        DiscardThreshold = new Sync<float>(this, 0.01f);
        DiscardOffset = new Sync<float>(this, 0.01f);

        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        var blend = BlendMode.Value;
        asset.SetBlendMode(blend);
        bool depth = ZWrite.Value == Assets.ZWrite.On || (ZWrite.Value == Assets.ZWrite.Auto && !Blended(blend));
        int index = (int)blend * 2 + (depth ? 1 : 0);
        string source = index >= 0 && index < Variants.Length ? Variants[index] : string.Empty;
        if (source.Length > 0)
            asset.SetCustomShaderSource(source);
        else
            asset.SetCustomShader(ShippedFile);

        asset.SetTexture("ColorTexture", ColorTexture.Asset);
        asset.SetTexture("DepthTexture", DepthTexture.Asset);
        asset.SetFloat4("ColorTextureTransform", Transform(ColorTextureScale.Value, ColorTextureOffset.Value));
        asset.SetFloat4("DepthTextureTransform", Transform(DepthTextureScale.Value, DepthTextureOffset.Value));
        asset.SetInt("DepthEncoding", (int)DepthEncoding.Value);
        asset.SetFloat("DepthFrom", DepthFrom.Value);
        asset.SetFloat("DepthTo", DepthTo.Value);
        asset.SetFloat2("FieldOfView", FieldOfView.Value * (MathF.PI / 180f));
        asset.SetFloat("NearClip", NearClip.Value);
        asset.SetFloat("FarClip", FarClip.Value);
        asset.SetFloat("DiscardThreshold", DiscardThreshold.Value);
        asset.SetFloat("DiscardOffset", DiscardOffset.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);
    }

    private static float4 Transform(float2 scale, float2 offset) => new(scale.x, scale.y, offset.x, offset.y);

    private static bool Blended(BlendMode blend) => blend is Assets.BlendMode.Alpha or Assets.BlendMode.Transparent
        or Assets.BlendMode.Additive or Assets.BlendMode.Multiply;

    // Opaque and cutout are the shipped file when they write depth; there is no alpha test to cut with. Alpha
    // fades by the colour texture's alpha, Transparent takes it as premultiplied, additive and multiply ignore
    // it.
    private static string[] BuildVariants()
    {
        var blends = Enum.GetValues<BlendMode>();
        var variants = new string[blends.Length * 2];
        foreach (var blend in blends)
        {
            for (int depthBit = 0; depthBit < 2; depthBit++)
            {
                bool depth = depthBit == 1;
                bool blended = Blended(blend);
                string text = string.Empty;
                if (blended || !depth)
                {
                    string mode = blend switch
                    {
                        Assets.BlendMode.Transparent => "blend_premul_alpha",
                        Assets.BlendMode.Additive => "blend_add",
                        Assets.BlendMode.Multiply => "blend_mul",
                        _ => "blend_mix",
                    };
                    string depthMode = depth ? (blended ? ", depth_draw_always" : string.Empty) : ", depth_draw_never";
                    string define = blend switch
                    {
                        Assets.BlendMode.Alpha or Assets.BlendMode.Transparent => "#define DEPTH_PROJECTION_ALPHA\n",
                        Assets.BlendMode.Additive or Assets.BlendMode.Multiply => "#define DEPTH_PROJECTION_ALPHA_ONE\n",
                        _ => string.Empty,
                    };
                    text = "shader_type spatial;\nrender_mode unshaded, fog_disabled, " + mode + ", cull_disabled" + depthMode + ";\n"
                        + define + "#include \"res://Shaders/DepthProjection_Core.gdshaderinc\"\n";
                }
                variants[(int)blend * 2 + depthBit] = text;
            }
        }
        return variants;
    }
}
