// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Writes a stored value. "var" names the StoredValue node it writes.
[Node("Variables/Write Value", Description = "Writes a stored value. \"var\" names the StoredValue node it writes.",
    Keywords = "set variable, assign, store, write variable")]
public sealed class WriteValue<T> : ScriptNode
{
}
