// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Removes every variable in the scope of that name reached from slot, the nearest scope that takes
// unnamed variables when the name is empty, and answers how many went. Removed like Delete Variable, all
// judged before any goes. found is false when no scope answers.
[Node("Variables/Scoped/Clear Scope", Capability.WorldStructure, Description = "Removes every variable in the scope of that name reached from slot, the nearest scope that takes unnamed variables when the name is empty, and answers how many went.",
    Keywords = "clear variables, remove variables")]
public sealed class ClearScope : ActionNode
{
    [return: Port("cleared")]
    public static int Run(ScriptContext ctx, Slot? slot, string? scope, out bool found)
        => ScriptScopedVariables.Clear(ctx, slot, scope, null, out found);
}
