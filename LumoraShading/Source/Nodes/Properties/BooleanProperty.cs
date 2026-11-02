// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Shading;

// An on/off switch the material exposes by name, for a Branch to pick with. Typed on the node, never
// wired.
[Node("Properties/Boolean Property", Description = "An on/off switch the material exposes by name, for a Branch to pick with.")]
public sealed class BooleanProperty : ShaderNode
{
    [return: Port("on")]
    public static bool Run([Setting] string name = "Toggle", [Setting] bool value = false) => throw GpuOnly;

    public override void Emit(ShaderNodeContext context)
    {
        if (context.IsWired("value"))
        {
            context.Error("a property's value is typed on it, not wired");
            return;
        }
        var value = context.ConstantValue("value", ShaderType.Bool)?.Code ?? "false";
        var name = context.Uniform(GlslNames.FromText(context.Text("name"), $"property_{context.Prefix[1..]}"), ShaderType.Bool,
            string.Empty, value);
        context.Output("on", name);
    }
}
