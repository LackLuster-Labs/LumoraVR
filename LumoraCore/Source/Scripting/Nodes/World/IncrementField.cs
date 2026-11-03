// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Numerics;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Adds one to a number field and writes it back as the script's author, judged like Set Field, whether the
// field is bound or arrives on a wire. value is what was written; written is false when there was no field
// or the write was refused, which is counted. Whole numbers wrap past their largest value the way they do in
// math nodes.
[Node("World/Fields/Increment Field", Capability.WorldWrite, Description = "Adds one to a number field and reports whether the change was allowed and written.",
    Keywords = "add one, plus one, counter, count up")]
public sealed class IncrementField<T> : ActionNode where T : struct, INumber<T>
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
        var next = unchecked(field.Value + T.One);
        ScriptWrites.Set(ctx, field, next);
        written = true;
        return next;
    }
}
