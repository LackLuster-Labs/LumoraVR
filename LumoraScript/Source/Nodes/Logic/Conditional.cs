// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// One value or the other. Both inputs are computed either way; to skip work, branch with If.
[Node("Logic/Conditional", Description = "Chooses between two values according to a boolean condition.",
    Keywords = "ternary, if else, choose, pick, switch, either")]
public sealed class Conditional<T> : ValueNode
{
    public static T Run(bool condition, T whenTrue, T whenFalse) => condition ? whenTrue : whenFalse;
}
