// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Shading;

// A number the material exposes by name, so it can be changed on the material without a recompile.
// With min below max it gets a slider over that range. Its values are typed on the node, never wired.
[Node("Properties/Float Property", Description = "A number the material exposes by name, so it can be changed on the material without a recompile.")]
public sealed class FloatProperty : ShaderNode
{
    public static float Run([Setting] string name = "Amount", [Setting] float value = 0f,
        [Setting] float min = 0f, [Setting] float max = 1f) => throw GpuOnly;

    public override void Emit(ShaderNodeContext context)
    {
        if (context.IsWired("value") || context.IsWired("min") || context.IsWired("max"))
        {
            context.Error("a property's value and range are typed on it, not wired");
            return;
        }
        double min = context.Number("min", 0), max = context.Number("max", 1);
        var hints = min < max ? $"hint_range({Arg(min)}, {Arg(max)})" : string.Empty;
        var name = context.Uniform(GlslNames.FromText(context.Text("name"), $"property_{context.Prefix[1..]}"), ShaderType.Float, hints,
            Arg(context.Number("value", 0)));
        context.Output("result", name);
    }

    private static string Arg(double value)
    {
        var text = GlslLiteral.Float(value);
        return text.StartsWith('(') ? text[1..^1] : text;
    }
}
