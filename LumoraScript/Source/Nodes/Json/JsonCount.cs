// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// How many members an object has or items an array has; 0 for any other value, or none.
[Node("Data/JSON/Count", Description = "How many members an object has or items an array has; 0 for any other value, or none.",
    Keywords = "length, size, item count, key count")]
public sealed class JsonCount : ValueNode
{
    [return: Port("count")]
    public static int Run(ScriptJson? json) => json?.Count ?? 0;
}
