// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A value as JSON: a number, true or false, text, an array for a vector, list or other struct, an object for
// a dictionary. JSON has no NaN or infinity, so a number that is not finite is null.
[Node("Data/JSON/Values/From Value", Description = "A value as JSON: a number, true or false, text, an array for a vector, list or other struct, an object for a dictionary.")]
public sealed class JsonFromValue<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    [return: Port("json")]
    public static ScriptJson Run(ScriptContextBase ctx, T value) => ScriptJsonConvert.From(ctx.Collections, value);
}
