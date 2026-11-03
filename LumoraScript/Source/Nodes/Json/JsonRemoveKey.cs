// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The object without key; removed is false, and the object as it was, when it had no such key. The object
// given is not changed.
[Node("Data/JSON/Objects/Remove Key", Description = "The object without key; removed is false, and the object as it was, when it had no such key.")]
public sealed class JsonRemoveKey : ReadNode
{
    public static ScriptJsonObject Run(ScriptContextBase ctx, ScriptJsonObject? @object, string? key, out bool removed)
    {
        removed = false;
        if (@object == null)
            return ScriptJsonObject.Empty;
        return key == null ? @object : @object.Remove(ctx.Collections, key, out removed);
    }
}
