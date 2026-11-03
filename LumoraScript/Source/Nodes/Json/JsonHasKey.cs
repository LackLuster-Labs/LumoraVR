// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the object has key, whatever is under it, null included.
[Node("Data/JSON/Objects/Has Key", Description = "Whether the object has key, whatever is under it, null included.")]
public sealed class JsonHasKey : ValueNode
{
    [return: Port("has")]
    public static bool Run(ScriptJsonObject? @object, string? key) => @object != null && key != null && @object.ContainsKey(key);
}
