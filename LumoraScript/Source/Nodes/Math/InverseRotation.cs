// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The rotation that undoes this one.
[Node("Math/Rotations/Inverse", Description = "The rotation that undoes this one.",
    Keywords = "invert rotation, undo rotation, conjugate")]
public sealed class InverseRotation : ValueNode
{
    [return: Port("inverse")]
    public static floatQ Run([Default(0, 0, 0, 1)] floatQ rotation) => rotation.Inverse;
}
