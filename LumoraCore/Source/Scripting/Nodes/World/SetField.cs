// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Writes a field as the script's author, judged by the gate like the author's own write. written is false
// when there was no field or the write was refused, which is counted; the call fails then as well.
[Node("World/Fields/Set Field", Capability.WorldWrite, Description = "Writes a field as the script's author, judged by the gate like the author's own write. written is false when there was no field or the write was refused, which is counted; the call fails then as well.",
    Keywords = "set member, assign, change property")]
public sealed class SetField<T> : ActionNode
{
    public static void Run(ScriptContext ctx, IField<T>? field, T value, out bool written)
    {
        written = false;
        if (field == null || field.IsDestroyed)
        {
            ctx.Fail(field == null ? "no field" : "the field is gone");
            return;
        }
        ScriptWrites.Set(ctx, field, value);
        written = true;
    }
}
