// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The item at index, 0 first, read as the node's type the way Get reads a key. found is false, and the
// value the type's default, when the index is outside the array or the item does not read as that type.
[Node("Data/JSON/Arrays/Get At", Description = "The item at index, 0 first, read as the node's type the way Get reads a key. found is false, and the value the type's default, when the index is outside the array or the item does not read as that type.",
    Keywords = "item, element, index, array get")]
public sealed class JsonGetAt<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    [return: Port("value")]
    public static T? Run(ScriptContextBase ctx, ScriptJsonArray? array, int index, out bool found)
    {
        found = false;
        var item = array?.Get(index);
        if (item == null)
            return default;
        found = ScriptJsonConvert.TryTo<T>(ctx.Collections, item, out var value);
        return value;
    }
}
