// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// One of four values by index, 0 to 3; any other index gives the type's empty value. For two
// values by true or false, Conditional.
[Node("Logic/Select", Description = "One of four values by index, 0 to 3; any other index gives the type's empty value.",
    Keywords = "multiplex, mux, switch, pick, choose, by index")]
public sealed class Select<T> : ValueNode
{
    public static T Run(int index, T a, T b, T c, T d) => index switch
    {
        0 => a,
        1 => b,
        2 => c,
        3 => d,
        _ => default!,
    };
}
