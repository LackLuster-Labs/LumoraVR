// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Any value as text in the form Parse reads back: numbers invariant and exact, a struct's numbers joined
// by commas, a character as itself or by name where it would not show, a date, a span of time, a
// quantity in its base unit, an enum by name.
[Node("Strings/To Text", Description = "Converts a value to text that Parse can read back.",
    Keywords = "stringify, as text, convert, string")]
public sealed class ToText<T> : ValueNode where T : struct
{
    private static readonly ScriptType Type = ScriptTypes.Get(typeof(T));

    public static string Run(T value) => ScriptTextForm.Format(Type, value);
}
