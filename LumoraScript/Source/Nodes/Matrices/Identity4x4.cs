// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The 4x4 matrix that changes nothing it is multiplied with: no move, no turn, a scale of one.
[Node("Math/Matrices/Identity 4x4", Description = "The 4x4 matrix that changes nothing it is multiplied with: no move, no turn, a scale of one.")]
public sealed class Identity4x4 : ValueNode
{
    [return: Port("matrix")] public static float4x4 Run() => float4x4.Identity;
}
