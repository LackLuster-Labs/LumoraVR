// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The same direction at length one. A zero vector stays zero.
[Node("Math/Vectors/Normalize", Description = "Keeps a vector's direction while making its length 1. A zero vector has no direction to keep.",
    Keywords = "normalized, unit vector, direction")]
public sealed class Normalize : ValueNode
{
    public static float2 Run(float2 vector) => vector.Normalized;
    public static float3 Run(float3 vector) => vector.Normalized;
    public static float4 Run(float4 vector) => vector.Normalized;
}
