// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The 2x2 matrix that changes nothing it is multiplied with.
[Node("Math/Matrices/Identity 2x2", Description = "The 2x2 matrix that changes nothing it is multiplied with.")]
public sealed class Identity2x2 : ValueNode
{
    [return: Port("matrix")] public static float2x2 Run() => float2x2.Identity;
}
