// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Numerics;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Takes one from a number field and writes it back, like Increment Field. Whole numbers wrap below their
// smallest value.
[Node("World/Fields/Decrement Field", Capability.WorldWrite, Description = "Takes one from a number field and writes it back, like Increment Field.",
    Keywords = "minus one, subtract one, count down")]
public sealed class DecrementField<T> : ActionNode where T : struct, INumber<T>
{
    [return: Port("value")]
    public static T Run(ScriptContext ctx, IField<T>? field, out bool written)
    {
        written = false;
        if (field == null || field.IsDestroyed)
        {
            ctx.Fail(field == null ? "no field" : "the field is gone");
            return default;
        }
        var next = unchecked(field.Value - T.One);
        ScriptWrites.Set(ctx, field, next);
        written = true;
        return next;
    }
}
