// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Makes a named variable in the scope a path reaches from slot, holding value: a variable component on
// the scope's own slot, or on slot itself with onSlot. It is synced like any variable, and saved with the
// world when saved is set and the script is the world's own. exists is true when one of that name and type
// was already there, and nothing is made.
//
// Only a variable component is ever made, and only on a slot the script may change: world content for the
// world's scripts, the author's own things for a person's (made as theirs, never saved), local things
// under its own object for a Local script. Never on the world root, a protected slot, or anything in
// someone's body or hands; the gate is asked as the author first. Making many is warned about, never
// refused.
[Node("Variables/Scoped/Create Variable", Capability.WorldStructure, Description = "Makes a named variable in the scope a path reaches from slot, holding value: a variable component on the scope's own slot, or on slot itself with onSlot.",
    Keywords = "add variable, new variable, make variable")]
public sealed class CreateScopedVariable<T> : ActionNode
{
    public static bool IsValidGenericType => ScriptScopedVariables.CanHold(typeof(T));

    [return: Port("created")]
    public static bool Run(ScriptContext ctx, Slot? slot, string? path, T value, out bool exists, bool onSlot = false, bool saved = true)
        => ScriptScopedVariables.Create(ctx, slot, path, value, onSlot, saved, out exists);
}
