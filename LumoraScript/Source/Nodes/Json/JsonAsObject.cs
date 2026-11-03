// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A JSON value that is an object, as one; ok is false, and the object nothing, for any other.
[Node("Data/JSON/Values/As Object", Description = "A JSON value that is an object, as one; ok is false, and the object nothing, for any other.")]
public sealed class JsonAsObject : ValueNode
{
    [return: Port("object")]
    public static ScriptJsonObject? Run(ScriptJson? json, out bool ok)
    {
        var obj = json as ScriptJsonObject;
        ok = obj != null;
        return obj;
    }
}
