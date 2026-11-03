// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A value that lives as long as the script instance: neither saved nor synced. Lowered to a global.
// "name" is what a live graph swap matches it by, "initial" its starting value.
[Node("Variables/Stored Value", Description = "A value that lives as long as the script instance: neither saved nor synced.",
    Keywords = "variable, store, memory, state, remember, keep")]
public sealed class StoredValue<T> : ScriptNode
{
}
