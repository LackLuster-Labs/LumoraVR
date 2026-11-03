// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Degrees about x, y and z, the way a rotation is typed in.
[Node("Math/Rotations/From Euler", Description = "Degrees about x, y and z, the way a rotation is typed in.")]
public sealed class FromEuler : ValueNode
{
    [return: Port("rotation")]
    public static floatQ Run(float3 degrees) => floatQ.Euler(degrees * (MathF.PI / 180f));
}
