// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// One value, set on its card, for as many consumers as are wired to its result. Its value is a setting,
// so another node cannot turn it into a relay.
[Node("Variables/Constant", Description = "One value, set on its card, for as many consumers as are wired to its result.",
    Keywords = "literal, number, fixed value, input value")]
public sealed class Constant<T> : ScriptNode
{
}
