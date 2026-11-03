// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Turning by first and then by then, as one rotation. A zero rotation counts as no turn, so an unwired
// side leaves the other as it is rather than giving a rotation of no size.
[Node("Math/Rotations/Combine", Description = "Turning by first and then by then, as one rotation.",
    Keywords = "multiply rotation, add rotation, quaternion multiply, rotate rotation")]
public sealed class CombineRotations : ValueNode
{
    [return: Port("rotation")]
    public static floatQ Run([Default(0, 0, 0, 1)] floatQ first, [Default(0, 0, 0, 1)] floatQ then) => Turns.Unit(then) * Turns.Unit(first);
}
