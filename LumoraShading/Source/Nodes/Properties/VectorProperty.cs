// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Shading;

// Two, three or four numbers the material exposes by name. Typed on the node, never wired.
[Node("Properties/Vector Property", Description = "Two, three or four numbers the material exposes by name.")]
public sealed class VectorProperty : ShaderNode
{
    [return: Port("vector")]
    public static float3 Run([Setting] string name = "Vector", [Setting] float3 value = default) => throw GpuOnly;

    [return: Port("vector")]
    public static float2 Run([Setting] string name = "Vector", [Setting] float2 value = default) => throw GpuOnly;

    [return: Port("vector")]
    public static float4 Run([Setting] string name = "Vector", [Setting] float4 value = default) => throw GpuOnly;

    public override void Emit(ShaderNodeContext context)
    {
        if (context.IsWired("value"))
        {
            context.Error("a property's value is typed on it, not wired");
            return;
        }
        ShaderTypes.TryFrom(context.Overload.Outputs[0].Type, out var type);
        var numbers = context.Node.Values.TryGetValue("value", out var set) && set.Kind == GraphValueKind.Numbers && set.Numbers.Length > 0
            ? set.Numbers
            : set?.Kind == GraphValueKind.Number ? new[] { set.Number } : new[] { 0.0 };
        var name = context.Uniform(GlslNames.FromText(context.Text("name"), $"property_{context.Prefix[1..]}"), type, string.Empty,
            GlslLiteral.Numbers(type, numbers));
        context.Output("vector", name);
    }
}
