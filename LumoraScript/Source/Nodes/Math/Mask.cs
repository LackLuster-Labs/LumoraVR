// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Lane by lane: whenTrue's lane where the mask's lane is set, whenFalse's where it is not.
[Node("Math/Vectors/Mask", Description = "Lane by lane: whenTrue's lane where the mask's lane is set, whenFalse's where it is not.",
    Keywords = "select per lane, choose, per component")]
public sealed class Mask : ValueNode
{
    public static float2 Run(bool2 mask, float2 whenTrue, float2 whenFalse)
        => new(mask.x ? whenTrue.x : whenFalse.x, mask.y ? whenTrue.y : whenFalse.y);

    public static float3 Run(bool3 mask, float3 whenTrue, float3 whenFalse)
        => new(mask.x ? whenTrue.x : whenFalse.x, mask.y ? whenTrue.y : whenFalse.y, mask.z ? whenTrue.z : whenFalse.z);

    public static float4 Run(bool4 mask, float4 whenTrue, float4 whenFalse)
        => new(mask.x ? whenTrue.x : whenFalse.x, mask.y ? whenTrue.y : whenFalse.y, mask.z ? whenTrue.z : whenFalse.z,
            mask.w ? whenTrue.w : whenFalse.w);

    public static int2 Run(bool2 mask, int2 whenTrue, int2 whenFalse)
        => new(mask.x ? whenTrue.x : whenFalse.x, mask.y ? whenTrue.y : whenFalse.y);

    public static int3 Run(bool3 mask, int3 whenTrue, int3 whenFalse)
        => new(mask.x ? whenTrue.x : whenFalse.x, mask.y ? whenTrue.y : whenFalse.y, mask.z ? whenTrue.z : whenFalse.z);

    public static int4 Run(bool4 mask, int4 whenTrue, int4 whenFalse)
        => new(mask.x ? whenTrue.x : whenFalse.x, mask.y ? whenTrue.y : whenFalse.y, mask.z ? whenTrue.z : whenFalse.z,
            mask.w ? whenTrue.w : whenFalse.w);
}
