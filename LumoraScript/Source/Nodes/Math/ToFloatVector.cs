// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Each lane of an int vector as a float. Past 16777216 a float cannot hold every whole number, and a
// lane lands on the nearest one it can.
[Node("Math/Conversion/To Float Vector", Description = "Each lane of an int vector as a float.",
    Keywords = "cast, convert, number type")]
public sealed class ToFloatVector : ValueNode
{
    public static float2 Run(int2 value) => new(value.x, value.y);
    public static float3 Run(int3 value) => new(value.x, value.y, value.z);
    public static float4 Run(int4 value) => new(value.x, value.y, value.z, value.w);
}
