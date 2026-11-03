// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Text read as a value of the node's type: the form To Text writes, and looser ones too, such as "1 2 3"
// for an int3, "5 ft 3 in" for a distance or "90s" for a span of time. parsed is false, and the value the
// type's default, when the text is not one.
[Node("Strings/Parse", Description = "Reads text as the selected value type and reports whether parsing succeeded.",
    Keywords = "from text, from string, read value, convert")]
public sealed class Parse<T> : ValueNode where T : struct
{
    private static readonly ScriptType Type = ScriptTypes.Get(typeof(T));

    [return: Port("value")]
    public static T Run(string? text, out bool parsed)
    {
        parsed = ScriptTextForm.TryParse(Type, text, out var value) && value is T;
        return parsed ? (T)value! : default;
    }
}
