// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Globalization;

namespace Lumora.Core.Assets;

// The render_mode and stencil_mode words a generated material variant is written with, so every material that
// names its own source spells one state the same way.
internal static class RenderModeText
{
    public static bool IsBlended(BlendMode blend)
        => blend is BlendMode.Alpha or BlendMode.Transparent or BlendMode.Additive or BlendMode.Multiply;

    public static string Cull(Culling culling) => culling switch
    {
        Culling.Front => "cull_front",
        Culling.None => "cull_disabled",
        _ => "cull_back"
    };

    // For a pass that writes ALPHA. Godot's add weighs the colour by its alpha; Transparent is premultiplied.
    public static string Blend(BlendMode blend) => blend switch
    {
        BlendMode.Additive => "blend_add",
        BlendMode.Multiply => "blend_mul",
        BlendMode.Transparent => "blend_premul_alpha",
        _ => "blend_mix"
    };

    // Godot compares depth one way or turns the test off. Less and LessOrEqual are its normal test, Greater and
    // GreaterOrEqual the inverted one, Always and Disabled no test. Equal and NotEqual have no counterpart and
    // keep the normal test; Never is the caller's to draw as nothing.
    public static string DepthTest(ZTest test) => test switch
    {
        ZTest.Always or ZTest.Disabled => "depth_test_disabled",
        ZTest.Greater or ZTest.GreaterOrEqual => "depth_test_inverted",
        _ => string.Empty
    };

    // Empty when the test always passes and nothing is written. Godot's stencil writes its one compare value
    // wherever the test passes: Replace stores the ID, the other operations what they would leave in a cleared
    // buffer. A test compares ID & read mask, a write stores the value & write mask.
    public static string Stencil(StencilComparison comparison, StencilOperation operation, byte id, byte writeMask, byte readMask)
    {
        if (comparison is StencilComparison.Disabled or StencilComparison.Never)
            return string.Empty;
        bool read = comparison != StencilComparison.Always;
        bool write = operation != StencilOperation.Keep;
        if (!read && !write)
            return string.Empty;
        int value = read ? id & readMask : Stored(operation, id) & writeMask;
        return "stencil_mode " + (read ? "read, " : string.Empty) + (write ? "write, " : string.Empty)
            + Compare(comparison) + ", " + value.ToString(CultureInfo.InvariantCulture) + ";\n";
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
