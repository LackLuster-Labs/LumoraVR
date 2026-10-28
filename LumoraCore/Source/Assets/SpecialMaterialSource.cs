// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Lumora.Core.Assets;

// Shader source for the unlit special materials: the distance lerp, the UV rect, the circle segment and the
// volume texture.
//
// Blending, depth writing and testing, culling, a depth bias and the stencil are all compile-time in a Godot
// shader. Each of these materials ships one file for its default state, which is what nearly every one of them
// wears and which compiles with the project; any other state gets a few lines of source naming its render modes
// over the same body, compiled once per distinct text however many materials wear it. -xlinka
internal static class SpecialMaterialSource
{
    // The generated source when there is one, else the shipped file.
    public static void Wear(MaterialAsset asset, string? source, string shippedFile)
    {
        if (source != null)
            asset.SetCustomShaderSource(source);
        else
            asset.SetCustomShader(shippedFile);
    }

    public static bool IsBlended(BlendMode blend) => blend is BlendMode.Alpha or BlendMode.Transparent
        or BlendMode.Additive or BlendMode.Multiply;

    // An unlit body under a render state. The body reads TRANSPARENT_PASS (write ALPHA), BLEND_ADDITIVE (add the
    // colour whatever its alpha), ALPHA_CUTOUT, ZTEST_NEVER and DEPTH_OFFSET, and whatever the caller adds.
    //
    // Alpha mixes by alpha, Transparent is premultiplied, Additive adds and Multiply multiplies what is behind;
    // Opaque and Cutout stay in the opaque pass. A blended surface writes no depth unless ZWrite says On.
    public static string Unlit(string body, BlendMode blend, ZWrite zWrite, ZTest zTest, Culling culling,
        bool depthOffset, string? stencil = null, string? define = null)
    {
        bool blended = IsBlended(blend);
        var sb = new StringBuilder(320);
        sb.Append("shader_type spatial;\nrender_mode unshaded, ");
        sb.Append(blend switch
        {
            BlendMode.Transparent => "blend_premul_alpha",
            BlendMode.Additive => "blend_add",
            BlendMode.Multiply => "blend_mul",
            _ => "blend_mix",
        });

        string depth = zWrite switch
        {
            ZWrite.On => blended ? "depth_draw_always" : string.Empty,
            ZWrite.Off => "depth_draw_never",
            _ => blended ? "depth_draw_never" : string.Empty,
        };
        if (depth.Length > 0)
            sb.Append(", ").Append(depth);

        // Godot tests depth one way, the other way round, or not at all. Equal and NotEqual have no counterpart
        // and keep the normal test.
        string test = zTest switch
        {
            ZTest.Always or ZTest.Disabled => "depth_test_disabled",
            ZTest.Greater or ZTest.GreaterOrEqual => "depth_test_inverted",
            _ => string.Empty,
        };
        if (test.Length > 0)
            sb.Append(", ").Append(test);
        sb.Append(", ").Append(CullMode(culling)).Append(";\n");

        if (stencil != null)
            sb.Append(stencil);
        if (blended)
            sb.Append("#define TRANSPARENT_PASS\n");
        if (blend == BlendMode.Additive)
            sb.Append("#define BLEND_ADDITIVE\n");
        if (blend == BlendMode.Cutout)
            sb.Append("#define ALPHA_CUTOUT\n");
        if (zTest == ZTest.Never)
            sb.Append("#define ZTEST_NEVER\n");
        if (depthOffset)
            sb.Append("#define DEPTH_OFFSET\n");
        if (define != null)
            sb.Append("#define ").Append(define).Append('\n');

        sb.Append("#include \"res://Shaders/").Append(body).Append(".gdshaderinc\"\n");
        return sb.ToString();
    }

    // The stencil line for a comparison and an operation, or null when the stencil is left alone. Godot tests
    // against one value and writes that same value, so an operation that stores anything else is taken as
    // storing what it leaves in a cleared buffer, and the read and write masks fold into the value.
    public static string? Stencil(StencilComparison comparison, StencilOperation operation, byte id, byte readMask,
        byte writeMask)
    {
        bool enabled = comparison is not (StencilComparison.Disabled or StencilComparison.Never);
        bool read = enabled && comparison != StencilComparison.Always;
        bool write = enabled && operation != StencilOperation.Keep;
        if (!read && !write)
            return null;

        int value = read ? id & readMask : StoredValue(operation, id) & writeMask;
        var sb = new StringBuilder("stencil_mode ", 64);
        if (read)
            sb.Append("read, ");
        if (write)
            sb.Append("write, ");
        sb.Append(CompareMode(comparison)).Append(", ").Append(value.ToString(CultureInfo.InvariantCulture)).Append(";\n");
        return sb.ToString();
    }

    public static string CullMode(Culling culling) => culling switch
    {
        Culling.Front => "cull_front",
        Culling.None => "cull_disabled",
        _ => "cull_back"
    };

    private static int StoredValue(StencilOperation operation, byte id) => operation switch
    {
        StencilOperation.Zero => 0,
        StencilOperation.IncrementSaturate or StencilOperation.IncrementWrap => 1,
        StencilOperation.DecrementSaturate => 0,
        StencilOperation.DecrementWrap or StencilOperation.Invert => 255,
        _ => id
    };

    private static string CompareMode(StencilComparison comparison) => comparison switch
    {
        StencilComparison.Less => "compare_less",
        StencilComparison.Equal => "compare_equal",
        StencilComparison.LessOrEqual => "compare_less_or_equal",
        StencilComparison.Greater => "compare_greater",
        StencilComparison.NotEqual => "compare_not_equal",
        StencilComparison.GreaterOrEqual => "compare_greater_or_equal",
        _ => "compare_always"
    };
}
