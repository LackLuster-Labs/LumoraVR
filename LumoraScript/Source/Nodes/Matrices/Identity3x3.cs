// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The 3x3 matrix that changes nothing it is multiplied with.
[Node("Math/Matrices/Identity 3x3", Description = "The 3x3 matrix that changes nothing it is multiplied with.")]
public sealed class Identity3x3 : ValueNode
{
    [return: Port("matrix")] public static float3x3 Run() => float3x3.Identity;
}
