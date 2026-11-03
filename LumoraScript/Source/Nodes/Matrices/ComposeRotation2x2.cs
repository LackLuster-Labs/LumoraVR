// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// A turn in the plane, in degrees, counterclockwise from x toward y.
[Node("Math/Matrices/Compose Rotation 2x2", Description = "A turn in the plane, in degrees, counterclockwise from x toward y.")]
public sealed class ComposeRotation2x2 : ValueNode
{
    [return: Port("matrix")]
    public static float2x2 Run(float degrees) => float2x2.Rotation(degrees * (MathF.PI / 180f));
}
