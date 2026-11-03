// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Removes a named variable of this type from the scope a path reaches from slot: every component that
// holds its value goes, the ones that only follow it stay. Each is judged by the gate as the author before
// any goes, and none from the world root, a protected slot, or someone's body or hands. deleted is false
// when there was nothing of that name and type.
[Node("Variables/Scoped/Delete Variable", Capability.WorldStructure, Description = "Removes a named variable of this type from the scope a path reaches from slot: every component that holds its value goes, the ones that only follow it stay.",
    Keywords = "remove variable")]
public sealed class DeleteScopedVariable<T> : ActionNode
{
    public static bool IsValidGenericType => ScriptScopedVariables.CanHold(typeof(T));

    [return: Port("deleted")]
    public static bool Run(ScriptContext ctx, Slot? slot, string? path) => ScriptScopedVariables.Delete<T>(ctx, slot, path);
}
