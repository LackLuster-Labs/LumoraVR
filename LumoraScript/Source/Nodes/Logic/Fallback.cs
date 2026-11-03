// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// value, or fallback when value holds nothing.
[Node("Logic/Fallback", Description = "Uses the fallback value when the first value is null.",
    Keywords = "coalesce, or else, if null, default")]
public sealed class Fallback<T> : ValueNode
{
    public static T Run(T? value, T fallback) => value is null ? fallback : value;
}
