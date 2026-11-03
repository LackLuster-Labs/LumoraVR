// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Variables;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Writes a named variable through the scope above slot, into every component that holds it, as the
// script's author. Each holder is judged first and none is written unless all may be. found is false when
// nothing of that name and type is there; written is false as well when every holder is driven or read
// only. A refusal is counted and leaves both false. The call fails whenever written is false.
[Node("Variables/Scoped/Write Variable", Capability.WorldWrite, Description = "Writes a named variable through the scope above slot, into every component that holds it, as the script's author.",
    Keywords = "set variable, write var, scoped variable")]
public sealed class WriteScopedVariable<T> : ActionNode
{
    public static bool IsValidGenericType => ScriptScopedVariables.CanHold(typeof(T));

    [return: Port("written")]
    public static bool Run(ScriptContext ctx, Slot? slot, string? path, T value, out bool found)
    {
        found = false;
        if (!ScriptScopedVariables.TryFind(ctx, slot, path, out var scope, out var name, out _))
            return false;
        var result = ScriptScopedVariables.Write(ctx, scope, name, value);
        found = result is not (VariableWriteResult.NotFound or VariableWriteResult.TypeMismatch);
        if (result == VariableWriteResult.Success)
            return true;
        ctx.Fail(ScriptScopedVariables.Unwritten(result, name));
        return false;
    }
}
