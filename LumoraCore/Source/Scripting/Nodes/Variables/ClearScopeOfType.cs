// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Clear Scope for the variables of one type only; the rest stay.
[Node("Variables/Scoped/Clear Scope Of Type", Capability.WorldStructure, Description = "Clear Scope for the variables of one type only; the rest stay.")]
public sealed class ClearScopeOfType<T> : ActionNode
{
    public static bool IsValidGenericType => ScriptScopedVariables.CanHold(typeof(T));

    [return: Port("cleared")]
    public static int Run(ScriptContext ctx, Slot? slot, string? scope, out bool found)
        => ScriptScopedVariables.Clear(ctx, slot, scope, typeof(T), out found);
}
