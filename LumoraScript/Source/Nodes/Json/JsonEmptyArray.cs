// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// An array with nothing in it, to add items to.
[Node("Data/JSON/Arrays/Empty Array", Description = "An array with nothing in it, to add items to.")]
public sealed class JsonEmptyArray : ValueNode
{
    [return: Port("array")]
    public static ScriptJsonArray Run() => ScriptJsonArray.Empty;
}
