// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

[Node("Math/Vectors/Cross", Description = "Returns a vector perpendicular to both input vectors, with direction determined by their order.")]
public sealed class Cross : ValueNode
{
    public static float3 Run(float3 a, float3 b) => float3.Cross(a, b);
}
