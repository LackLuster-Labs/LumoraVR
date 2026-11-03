// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A named variable, through the scope above slot: "Scope/Name" is the nearest scope of that name, "Name"
// alone the nearest one that takes unnamed variables. found is false when nothing of that name and type
// is there, or what holds it relays a host-only member. A slot, user or component the script cannot see
// reads as nothing, found all the same. Self as the slot reads the script's own object's variables.
[Node("Variables/Scoped/Read Variable", Capability.WorldRead, Description = "Finds a named variable in the scope above a slot and reads its value, reporting whether it exists.",
    Keywords = "get variable, read var, scoped variable")]
public sealed class ReadScopedVariable<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptScopedVariables.CanHold(typeof(T));

    [return: Port("value")]
    public static T Run(ScriptContext ctx, Slot? slot, string? path, out bool found)
    {
        found = ScriptScopedVariables.TryRead(ctx, slot, path, out T value);
        return value;
    }
}
