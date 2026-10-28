// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Text;

namespace Lumora.Core.Assets;

// Shader source for the generated PBS families (PBS_Surface_Common.gdshaderinc).
//
// Workflow, blending, depth writing and testing and culling are all compile-time in a Godot shader, and these
// families cross them with an effect each, so shipping a file per combination would be hundreds of files
// nobody loads. A few lines naming the render modes over the shared body, compiled once per distinct text,
// cover all of them. -xlinka
public static class PBSSurfaceSource
{
    public static BlendMode BlendFor(AlphaHandling alpha) => alpha switch
    {
        AlphaHandling.AlphaClip => BlendMode.Cutout,
        AlphaHandling.AlphaBlend => BlendMode.Transparent,
        _ => BlendMode.Opaque
    };

    public static bool IsBlended(BlendMode blend) => blend is BlendMode.Alpha or BlendMode.Transparent
        or BlendMode.Additive or BlendMode.Multiply;

    public static string Build(in PBSSurfaceVariant variant) => Build(variant, variant.Culling);

    // The same variant with its culling replaced: a blended surface drawn from both sides is two passes, its
    // back faces first and then its front faces, so the two never sort against each other.
    public static string Build(in PBSSurfaceVariant variant, Culling culling)
    {
        bool blended = IsBlended(variant.Blend);
        var sb = new StringBuilder(320);
        sb.Append("shader_type spatial;\nrender_mode ");
        sb.Append(variant.Blend switch
        {
            BlendMode.Transparent => "blend_premul_alpha",
            BlendMode.Additive => "blend_add",
            BlendMode.Multiply => "blend_mul",
            _ => "blend_mix",
        });

        // A blended surface writes no depth unless asked, and when asked it lays its depth down first, so its
        // own faces sort and anything drawn later is hidden behind it.
        string depth = variant.ZWrite switch
        {
            ZWrite.On => blended ? "depth_prepass_alpha" : string.Empty,
            ZWrite.Off => "depth_draw_never",
            _ => blended ? "depth_draw_never" : string.Empty,
        };
        if (depth.Length > 0)
            sb.Append(", ").Append(depth);

        // Godot tests depth one way, the other way round, or not at all. Equal and NotEqual have no counterpart
        // and keep the normal test.
        string test = variant.ZTest switch
        {
            ZTest.Always or ZTest.Disabled => "depth_test_disabled",
            ZTest.Greater or ZTest.GreaterOrEqual => "depth_test_inverted",
            _ => string.Empty,
        };
        if (test.Length > 0)
            sb.Append(", ").Append(test);
        sb.Append(", ").Append(CullMode(culling)).Append(";\n");

        if (variant.Specular)
            sb.Append("#define PBS_SPECULAR\n");
        if (blended)
            sb.Append("#define TRANSPARENT_PASS\n");
        if (variant.Blend == BlendMode.Transparent)
            sb.Append("#define PREMULTIPLIED\n");
        if (variant.Blend == BlendMode.Cutout)
            sb.Append("#define ALPHA_CLIP\n");
        if (variant.ZTest == ZTest.Never)
            sb.Append("#define ZTEST_NEVER\n");
        if (variant.DepthOffset)
            sb.Append("#define DEPTH_OFFSET\n");

        var features = variant.Features;
        Define(sb, features, PBSSurfaceFeatures.Rim, "PBS_RIM");
        Define(sb, features, PBSSurfaceFeatures.Intersect, "PBS_INTERSECT");
        Define(sb, features, PBSSurfaceFeatures.VertexAlbedo, "PBS_VERTEX_ALBEDO");
        Define(sb, features, PBSSurfaceFeatures.VertexEmissive, "PBS_VERTEX_EMISSIVE");
        Define(sb, features, PBSSurfaceFeatures.VertexReflectance, "PBS_VERTEX_REFLECTANCE");
        Define(sb, features, PBSSurfaceFeatures.Triplanar, "PBS_TRIPLANAR");

        sb.Append("#include \"res://Shaders/").Append(variant.Body).Append(".gdshaderinc\"\n");
        return sb.ToString();
    }

    private static void Define(StringBuilder sb, PBSSurfaceFeatures features, PBSSurfaceFeatures feature, string name)
    {
        if ((features & feature) != 0)
            sb.Append("#define ").Append(name).Append('\n');
    }

    public static string CullMode(Culling culling) => culling switch
    {
        Culling.Front => "cull_front",
        Culling.None => "cull_disabled",
        _ => "cull_back"
    };
}
