// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

[Node("Math/Constants/Pi", Description = "Returns pi, the ratio of a circle's circumference to its diameter.")]
public sealed class Pi : ValueNode
{
    [return: Port("pi")] public static float Run() => MathF.PI;
}
