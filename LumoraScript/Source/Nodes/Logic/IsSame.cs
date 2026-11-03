// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Lumora.Script;

// Whether a and b are the same: the same object for slots, users and the like, the same value for
// anything else.
[Node("Logic/Is Same", Description = "Whether a and b are the same: the same object for slots, users and the like, the same value for anything else.",
    Keywords = "equals, same object, identity, compare")]
public sealed class IsSame<T> : ValueNode
{
    [return: Port("same")]
    public static bool Run(T? a, T? b) => EqualityComparer<T>.Default.Equals(a!, b!);
}
