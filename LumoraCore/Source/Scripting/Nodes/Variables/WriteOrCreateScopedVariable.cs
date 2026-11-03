// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Variables;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Write Variable, and Create Variable when there is nothing of that name and type to write. written is
// true once the variable holds value either way, created when it was made now. Making it follows Create
// Variable's rules; a variable that is there but will not take the write is left as it is. The call fails
// whenever written is false.
[Node("Variables/Scoped/Write Or Create Variable", Capability.WorldStructure, Description = "Write Variable, and Create Variable when there is nothing of that name and type to write. written is true once the variable holds value either way, created when it was made now.")]
public sealed class WriteOrCreateScopedVariable<T> : ActionNode
{
    public static bool IsValidGenericType => ScriptScopedVariables.CanHold(typeof(T));

    [return: Port("written")]
    public static bool Run(ScriptContext ctx, Slot? slot, string? path, T value, out bool created, bool onSlot = false, bool saved = true)
    {
        created = false;
        if (!ScriptScopedVariables.TryFind(ctx, slot, path, out var scope, out var name, out _))
            return false;
        var result = ScriptScopedVariables.Write(ctx, scope, name, value);
        if (result == VariableWriteResult.Success)
            return true;
        if (result is not (VariableWriteResult.NotFound or VariableWriteResult.TypeMismatch))
        {
            ctx.Fail(ScriptScopedVariables.Unwritten(result, name));
            return false;
        }
        created = ScriptScopedVariables.Create(ctx, slot, path, value, onSlot, saved, out _);
        if (!created)
            ctx.Fail($"the variable '{name}' could not be made");
        return created;
    }
}
