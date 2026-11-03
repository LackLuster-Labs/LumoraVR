// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether two JSON values hold the same thing: numbers by value, so 1 and 1.0 are equal, text exactly,
// arrays item by item, objects key by key whatever their order. No value equals only no value.
[Node("Data/JSON/Values/Equals", Description = "Whether two JSON values hold the same thing: numbers by value, so 1 and 1.0 are equal, text exactly, arrays item by item, objects key by key whatever their order.")]
public sealed class JsonEquals : ValueNode
{
    [return: Port("equal")]
    public static bool Run(ScriptJson? a, ScriptJson? b) => a == null ? b == null : a.Equals(b);
}
