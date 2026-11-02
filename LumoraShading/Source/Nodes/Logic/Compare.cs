// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Shading;

// Tests a against b. Equal allows for the rounding a GPU does, within 0.00001.
[Node("Logic/Compare", Description = "Tests a against b.")]
public sealed class Compare : ShaderNode
{
    public static bool Run(float a, float b, [Setting] CompareMode mode = CompareMode.Greater) => throw GpuOnly;

    public override void Emit(ShaderNodeContext context)
    {
        var a = context.Input("a").Code;
        var b = context.Input("b").Code;
        context.Output("result", context.Option<CompareMode>("mode") switch
        {
            CompareMode.Less => $"({a} < {b})",
            CompareMode.LessOrEqual => $"({a} <= {b})",
            CompareMode.GreaterOrEqual => $"({a} >= {b})",
            CompareMode.Equal => $"(abs({a} - {b}) <= 0.00001)",
            CompareMode.NotEqual => $"(abs({a} - {b}) > 0.00001)",
            _ => $"({a} > {b})",
        });
    }
}
