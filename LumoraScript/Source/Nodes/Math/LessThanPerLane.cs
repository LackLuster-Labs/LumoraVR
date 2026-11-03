// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Each lane of a against the same lane of b, one flag per lane. A NaN lane is false.
[Node("Logic/Comparison/Per Component/Less Than", Description = "Each lane of a against the same lane of b, one flag per lane.",
    Keywords = "per component, per lane, componentwise, vector compare")]
public sealed class LessThanPerLane : ValueNode
{
    public static bool2 Run(float2 a, float2 b) => new(a.x < b.x, a.y < b.y);
    public static bool3 Run(float3 a, float3 b) => new(a.x < b.x, a.y < b.y, a.z < b.z);
    public static bool4 Run(float4 a, float4 b) => new(a.x < b.x, a.y < b.y, a.z < b.z, a.w < b.w);
    public static bool2 Run(int2 a, int2 b) => new(a.x < b.x, a.y < b.y);
    public static bool3 Run(int3 a, int3 b) => new(a.x < b.x, a.y < b.y, a.z < b.z);
    public static bool4 Run(int4 a, int4 b) => new(a.x < b.x, a.y < b.y, a.z < b.z, a.w < b.w);
}
