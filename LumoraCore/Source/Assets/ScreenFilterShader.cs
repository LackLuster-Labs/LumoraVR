// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// The shader a screen filter wears (Gamma, Grayscale, Invert, Pixelate, Posterize, Depth).
//
// Blend, culling, the clip rect and the colour mask are uniforms (ScreenFilter_Raster), so the usual state is
// one shipped file per filter, Mat_<Name>. Depth write, depth test and the stencil are compile-time render
// modes; a filter that changes any of them gets a few lines of source naming them over the filter's body,
// ScreenFilter_<Name>, compiled once per distinct text.
//
// Godot's stencil writes its one compare value wherever the test passes. Replace stores StencilID; the other
// operations store what they would leave in a cleared buffer. The masks fold into that value: a test compares
// StencilID & StencilReadMask, a write stores the value & StencilWriteMask. A Never test, of depth or stencil,
// draws nothing. -xlinka
internal static class ScreenFilterShader
{
    private readonly record struct VariantKey(string Name, bool WritesDepth, bool ShippedWritesDepth, ZTest ZTest,
        StencilComparison Comparison, StencilOperation Operation, byte Id, byte WriteMask, byte ReadMask);

    private static readonly ConcurrentDictionary<string, string> Files = new();
    private static readonly ConcurrentDictionary<VariantKey, string> Sources = new();

    // opaqueWritesDepth: whether ZWrite.Auto writes depth on an opaque or cutout filter, which is also the
    // depth state the shipped file was written for.
    public static void Apply(MaterialAsset asset, string name, in ScreenFilterState state, int defaultQueue, bool opaqueWritesDepth)
    {
        asset.SetBlendMode(state.Blend);
        asset.SetCulling(state.Culling);

        bool blended = state.Blend is BlendMode.Alpha or BlendMode.Transparent or BlendMode.Additive or BlendMode.Multiply;
        bool writesDepth = state.ZWrite switch
        {
            ZWrite.On => true,
            ZWrite.Off => false,
            _ => opaqueWritesDepth && !blended,
        };

        var key = new VariantKey(name, writesDepth, opaqueWritesDepth, state.ZTest, state.StencilComparison,
            state.StencilOperation, state.StencilID, state.StencilWriteMask, state.StencilReadMask);
        string source = Sources.GetOrAdd(key, static k => Build(k));
        if (source.Length > 0)
            asset.SetCustomShaderSource(source);
        else
            asset.SetCustomShader(Files.GetOrAdd(name, static n => $"res://Shaders/Mat_{n}.gdshader"));

        var rect = state.Rect;
        asset.SetBool("RectClip", state.RectClip);
        asset.SetFloat4("Rect", new float4(rect.xMin, rect.yMin, rect.xMax, rect.yMax));
        asset.SetInt("ColorMask", (int)state.ColorMask);
        asset.SetFloat("RenderQueue", state.RenderQueue < 0 ? defaultQueue : state.RenderQueue);
    }

    // Empty when the shipped file already is this state.
    private static string Build(in VariantKey key)
    {
        bool never = key.ZTest == ZTest.Never || key.Comparison == StencilComparison.Never;
        string test = key.ZTest switch
        {
            ZTest.Always or ZTest.Disabled => "depth_test_disabled",
            ZTest.Greater or ZTest.GreaterOrEqual => "depth_test_inverted",
            _ => string.Empty,
        };
        bool stencil = key.Comparison is not (StencilComparison.Disabled or StencilComparison.Never);
        bool read = stencil && key.Comparison != StencilComparison.Always;
        bool write = stencil && key.Operation != StencilOperation.Keep;
        if (!never && key.WritesDepth == key.ShippedWritesDepth && test.Length == 0 && !read && !write)
            return string.Empty;

        var sb = new StringBuilder(256);
        sb.Append("shader_type spatial;\nrender_mode unshaded, fog_disabled, blend_mix, cull_disabled, ");
        sb.Append(key.WritesDepth ? "depth_draw_always" : "depth_draw_never");
        if (test.Length > 0)
            sb.Append(", ").Append(test);
        sb.Append(";\n");

        if (read || write)
        {
            int value = read ? key.Id & key.ReadMask : Stored(key.Operation, key.Id) & key.WriteMask;
            sb.Append("stencil_mode ");
            if (read)
                sb.Append("read, ");
            if (write)
                sb.Append("write, ");
            sb.Append(Compare(key.Comparison)).Append(", ")
                .Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(";\n");
        }

        if (never)
            sb.Append("#define SCREEN_FILTER_NEVER\n");
        sb.Append("#include \"res://Shaders/ScreenFilter_").Append(key.Name).Append(".gdshaderinc\"\n");
        return sb.ToString();
    }

    private static int Stored(StencilOperation operation, byte id) => operation switch
    {
        StencilOperation.Zero or StencilOperation.DecrementSaturate => 0,
        StencilOperation.IncrementSaturate or StencilOperation.IncrementWrap => 1,
        StencilOperation.DecrementWrap or StencilOperation.Invert => 255,
        _ => id
    };

    private static string Compare(StencilComparison comparison) => comparison switch
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
