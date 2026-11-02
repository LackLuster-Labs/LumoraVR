// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// A color the material exposes by name, picked like any other color on the material. White until one is
// typed in. Typed on the node, never wired.
[Node("Properties/Color Property", Description = "A color the material exposes by name, picked like any other color on the material.")]
public sealed class ColorProperty : ShaderNode
{
    [return: Port("color")]
    public static color Run([Setting] string name = "Color", [Setting] color value = default) => throw GpuOnly;

    public override void Emit(ShaderNodeContext context)
    {
        if (context.IsWired("value"))
        {
            context.Error("a property's value is typed on it, not wired");
            return;
        }
        var numbers = context.Node.Values.TryGetValue("value", out var set) && set.Kind == GraphValueKind.Numbers && set.Numbers.Length > 0
            ? set.Numbers
            : new[] { 1.0, 1.0, 1.0, 1.0 };
        var name = context.Uniform(GlslNames.FromText(context.Text("name"), $"property_{context.Prefix[1..]}"), ShaderType.Color,
            "source_color", GlslLiteral.Numbers(ShaderType.Color, numbers));
        context.Output("color", name);
    }
}
