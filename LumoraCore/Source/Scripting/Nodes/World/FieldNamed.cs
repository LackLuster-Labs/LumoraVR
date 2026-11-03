// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A component's value field by its member name, exact and case sensitive, as the inspector lists it: Size on a
// text renderer, Enabled on any component. The field carries on into Get Field, Set Field, Drive Field and the
// rest, which judge every write as the author's own. Nothing when the component has no field of that name and
// type, or one that is host-only, hidden from the inspector, on an item seal, or a reference (Reference Named
// has those).
[Node("World/Fields/Field Named", Capability.WorldRead, Description = "A component's value field by its member name, exact and case sensitive, as the inspector lists it: Size on a text renderer, Enabled on any component.",
    Keywords = "field by name, property by name, member")]
[Evaluates(Evaluation.Tree)]
public sealed class FieldNamed<T> : ReadNode
{
    [return: Port("field")]
    public static IField<T>? Run(ScriptContext ctx, Component? component, string? name)
    {
        if (!ScriptReads.Visible(ctx, component) || string.IsNullOrEmpty(name))
            return null;
        int index = ScriptComponents.Find(component!, name);
        return index >= 0 && ScriptComponents.IsValueField(component!, index, out var field, out _) ? field as IField<T> : null;
    }
}
