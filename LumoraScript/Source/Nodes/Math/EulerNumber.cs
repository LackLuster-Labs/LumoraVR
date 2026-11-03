// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// e, 2.7182817: the base of Exp and of the natural Log.
[Node("Math/Constants/E", Description = "E, 2.7182817: the base of Exp and of the natural Log.")]
public sealed class EulerNumber : ValueNode
{
    [return: Port("e")] public static float Run() => MathF.E;
}
