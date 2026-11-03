// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether an object input holds nothing: unbound, gone, or never set. A number is never null.
[Node("Logic/Is Null", Description = "Whether an object input holds nothing: unbound, gone, or never set.",
    Keywords = "null check, exists, missing, valid")]
public sealed class IsNull<T> : ValueNode
{
    [return: Port("null")]
    public static bool Run(T? value) => value is null;
}
