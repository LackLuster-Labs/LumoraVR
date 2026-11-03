// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Degrees about x, y and z, the angles From Euler takes back. The rotation is read at length one, so a
// scaled one gives the angles of the turn it stands for.
[Node("Math/Rotations/To Euler", Description = "Degrees about x, y and z.")]
public sealed class ToEuler : ValueNode
{
    [return: Port("degrees")]
    public static float3 Run([Default(0, 0, 0, 1)] floatQ rotation) => Turns.Unit(rotation).ToEuler() * (180f / MathF.PI);
}
