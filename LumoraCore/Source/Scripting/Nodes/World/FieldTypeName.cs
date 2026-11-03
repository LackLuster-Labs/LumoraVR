// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The script type of a component's value field by its member name, the type Field Named has to be set to for
// it: float, color, string, an enum's name. Empty when Field Named would find nothing, or the field holds a type
// scripts have no name for.
[Node("World/Fields/Field Type", Capability.WorldRead, Description = "The script type of a component's value field by its member name, the type Field Named has to be set to for it: float, color, string, an enum's name.")]
[Evaluates(Evaluation.Tree)]
public sealed class FieldTypeName : ReadNode
{
    [return: Port("type")]
    public static string Run(ScriptContext ctx, Component? component, string? name)
    {
        if (!ScriptReads.Visible(ctx, component) || string.IsNullOrEmpty(name))
            return string.Empty;
        int index = ScriptComponents.Find(component!, name);
        return index >= 0 && ScriptComponents.IsValueField(component!, index, out _, out var type) && ScriptTypes.TryGet(type, out var scriptType)
            ? scriptType.Name : string.Empty;
    }
}
