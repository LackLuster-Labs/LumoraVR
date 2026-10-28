// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;

namespace Lumora.Core.Assets;

// Shader source for the render states a material's shipped variant files do not cover.
//
// Depth write, depth test, a depth bias and billboard geometry are all compile-time render_modes or
// vertex stages in a Godot shader, never uniforms. The unlit, fresnel and specular materials already ship
// one file per blend and cull; multiplying those by every depth combination would be hundreds of files
// nobody loads. So a material whose state a shipped file covers keeps that file, exactly as before, and
// one that leaves the covered set gets a few lines of source naming its render modes over the same shared
// body, compiled once per distinct text however many materials wear it. -xlinka
internal static class MaterialVariantSource
{
    private const string Folder = "res://Shaders/";

    // Every material update asks again for its file name and its generated source, and both were put together
    // fresh each time, so a scrolling or pulsing material built the same strings every frame. Each distinct
    // answer is kept once. -xlinka
    private static readonly ConcurrentDictionary<long, string?> Known = new();

    private static long Key(int kind, int a, int b = 0, int c = 0, int d = 0, int e = 0)
        => kind | ((long)(a & 0xFF) << 4) | ((long)(b & 0xFF) << 12) | ((long)(c & 0xFF) << 20) | ((long)(d & 0xFF) << 28) | ((long)(e & 0xFF) << 36);

    // Puts the variant on: the generated source when there is one, else the shipped file, but only when
    // this material had replaced it, so a material that never left the shipped set is never touched.
    // Returns whether a generated variant is on now.
    public static bool Apply(MaterialAsset asset, string? source, string shippedFile, bool generatedBefore)
    {
        if (source != null)
        {
            asset.SetCustomShaderSource(source);
            return true;
        }
        if (generatedBefore)
            asset.SetCustomShader(shippedFile);
        return false;
    }

    // ---------------------------------------------------------------- unlit
    //
    // Two files: additive, and everything else through the plain one, culling ignored in both. That is
    // what unlit content has always drawn with, so the variants keep it rather than change how a
    // multiply or a back-culled unlit surface looks in an existing world.

    private const string UnlitAdditiveFile = Folder + "Unlit_Additive.gdshader";
    private const string UnlitPlainFile = Folder + "Unlit.gdshader";

    public static string UnlitFile(BlendMode blend)
        => blend == BlendMode.Additive ? UnlitAdditiveFile : UnlitPlainFile;

    public static string? Unlit(BlendMode blend, ZWrite zWrite, bool depthOffset, bool billboard)
    {
        long key = Key(1, (int)blend, (int)zWrite, depthOffset ? 1 : 0, billboard ? 1 : 0);
        if (!Known.TryGetValue(key, out var source))
            Known[key] = source = BuildUnlit(blend, zWrite, depthOffset, billboard);
        return source;
    }

    private static string? BuildUnlit(BlendMode blend, ZWrite zWrite, bool depthOffset, bool billboard)
    {
        bool additive = blend == BlendMode.Additive;
        string shippedDepth = additive ? "depth_draw_never" : "depth_prepass_alpha";
        string depth = zWrite switch
        {
            ZWrite.On => "depth_draw_always",
            ZWrite.Off => "depth_draw_never",
            _ => shippedDepth,
        };
        if (depth == shippedDepth && !depthOffset && !billboard)
            return null;

        var sb = new StringBuilder("shader_type spatial;\nrender_mode unshaded, ");
        sb.Append(additive ? "blend_add" : "blend_mix").Append(", ").Append(depth).Append(", cull_disabled");
        if (billboard)
            sb.Append(", skip_vertex_transform");
        sb.Append(";\n");
        if (additive)
            sb.Append("#define UNLIT_ADDITIVE\n");
        if (billboard)
            sb.Append("#define BILLBOARD\n");
        if (depthOffset)
            sb.Append("#define DEPTH_OFFSET\n");
        sb.Append("#include \"res://Shaders/Unlit_Core.gdshaderinc\"\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- fresnel

    public static string FresnelFile(BlendMode blend, Culling culling)
    {
        long key = Key(2, (int)blend, (int)culling);
        if (!Known.TryGetValue(key, out var file))
            Known[key] = file = $"{Folder}Mat_Fresnel{BlendVariant(blend)}{CullVariant(culling)}.gdshader";
        return file!;
    }

    // Godot compares depth one way or turns the test off. Less and LessOrEqual are its normal test, Greater
    // and GreaterOrEqual the inverted one, Always and Disabled no test, and Never draws nothing. Equal and
    // NotEqual have no Godot counterpart and keep the normal test.
    public static string? Fresnel(BlendMode blend, Culling culling, ZWrite zWrite, ZTest zTest, bool depthOffset)
    {
        long key = Key(3, (int)blend, (int)culling, (int)zWrite, (int)zTest, depthOffset ? 1 : 0);
        if (!Known.TryGetValue(key, out var source))
            Known[key] = source = BuildFresnel(blend, culling, zWrite, zTest, depthOffset);
        return source;
    }

    private static string? BuildFresnel(BlendMode blend, Culling culling, ZWrite zWrite, ZTest zTest, bool depthOffset)
    {
        bool transparent = blend is BlendMode.Alpha or BlendMode.Transparent or BlendMode.Additive or BlendMode.Multiply;
        string shippedDepth = !transparent ? string.Empty
            : blend is BlendMode.Alpha or BlendMode.Transparent ? "depth_prepass_alpha" : "depth_draw_never";
        string depth = zWrite switch
        {
            // An opaque surface already writes depth.
            ZWrite.On => transparent ? "depth_draw_always" : string.Empty,
            ZWrite.Off => "depth_draw_never",
            _ => shippedDepth,
        };
        string test = zTest switch
        {
            ZTest.Always or ZTest.Disabled => "depth_test_disabled",
            ZTest.Greater or ZTest.GreaterOrEqual => "depth_test_inverted",
            _ => string.Empty,
        };
        bool never = zTest == ZTest.Never;
        if (depth == shippedDepth && test.Length == 0 && !never && !depthOffset)
            return null;

        var sb = new StringBuilder("shader_type spatial;\nrender_mode unshaded, ");
        sb.Append(blend switch
        {
            BlendMode.Additive => "blend_add",
            BlendMode.Multiply => "blend_mul",
            _ => "blend_mix",
        });
        if (depth.Length > 0)
            sb.Append(", ").Append(depth);
        if (test.Length > 0)
            sb.Append(", ").Append(test);
        sb.Append(", ").Append(CullMode(culling)).Append(";\n");
        if (transparent)
            sb.Append("#define TRANSPARENT_PASS\n");
        if (never)
            sb.Append("#define ZTEST_NEVER\n");
        if (depthOffset)
            sb.Append("#define DEPTH_OFFSET\n");
        sb.Append("#include \"res://Shaders/Fresnel_Core.gdshaderinc\"\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- specular

    // Opaque and cutout ship as files, cutout with its discard compiled in. The blended modes are rare enough on
    // a specular surface to be generated: alpha fades everything, transparent is premultiplied (reflections
    // stay), additive and multiply lay the surface over what is behind it and never write depth.
    public static string SpecularFile(Culling culling, BlendMode blend)
    {
        long key = Key(4, (int)blend, (int)culling);
        if (!Known.TryGetValue(key, out var file))
            Known[key] = file = $"{Folder}Mat_PBS_Specular{(blend == BlendMode.Cutout ? "Cutout" : string.Empty)}{CullVariant(culling)}.gdshader";
        return file!;
    }

    public static string? Specular(Culling culling, BlendMode blend, bool depthOffset)
    {
        long key = Key(5, (int)blend, (int)culling, depthOffset ? 1 : 0);
        if (!Known.TryGetValue(key, out var source))
            Known[key] = source = BuildSpecular(culling, blend, depthOffset);
        return source;
    }

    private static string? BuildSpecular(Culling culling, BlendMode blend, bool depthOffset)
    {
        bool blended = blend is BlendMode.Alpha or BlendMode.Transparent or BlendMode.Additive or BlendMode.Multiply;
        if (!blended && !depthOffset)
            return null;

        var sb = new StringBuilder("shader_type spatial;\nrender_mode ");
        sb.Append(blend switch
        {
            BlendMode.Transparent => "blend_premul_alpha, depth_prepass_alpha",
            BlendMode.Alpha => "blend_mix, depth_prepass_alpha",
            BlendMode.Additive => "blend_add, depth_draw_never",
            BlendMode.Multiply => "blend_mul, depth_draw_never",
            _ => "blend_mix",
        });
        sb.Append(", ").Append(CullMode(culling)).Append(";\n");
        if (blended)
            sb.Append("#define TRANSPARENT_PASS\n");
        if (blend == BlendMode.Transparent)
            sb.Append("#define PREMULTIPLIED\n");
        if (blend == BlendMode.Cutout)
            sb.Append("#define ALPHA_CUTOUT\n");
        if (depthOffset)
            sb.Append("#define DEPTH_OFFSET\n");
        sb.Append("#include \"res://Shaders/PBS_Specular_Core.gdshaderinc\"\n");
        return sb.ToString();
    }

    // The same suffixes the shipped files are named by.
    private static string BlendVariant(BlendMode blend) => blend switch
    {
        BlendMode.Additive => "Add",
        BlendMode.Multiply => "Mul",
        BlendMode.Alpha or BlendMode.Transparent => "Alpha",
        _ => string.Empty
    };

    private static string CullVariant(Culling culling) => culling switch
    {
        Culling.Front => "FrontCull",
        Culling.None => "DoubleSided",
        _ => string.Empty
    };

    private static string CullMode(Culling culling) => culling switch
    {
        Culling.Front => "cull_front",
        Culling.None => "cull_disabled",
        _ => "cull_back"
    };
}
