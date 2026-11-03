// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Each lane on its own from min up to but not including max, the way Random Float draws one number: a
// random point in the box between them.
[Node("Math/Random/Random Vector", Description = "Each lane on its own from min up to but not including max, the way Random Float draws one number: a random point in the box between them.",
    Keywords = "random float2, random float3, random float4, random position")]
public sealed class RandomVector : ReadNode
{
    public static float2 Run(ScriptContextBase ctx, [Default(0)] float2 min, [Default(1)] float2 max)
        => new(RandomFloat.Run(ctx, min.x, max.x), RandomFloat.Run(ctx, min.y, max.y));

    public static float3 Run(ScriptContextBase ctx, [Default(0)] float3 min, [Default(1)] float3 max)
        => new(RandomFloat.Run(ctx, min.x, max.x), RandomFloat.Run(ctx, min.y, max.y), RandomFloat.Run(ctx, min.z, max.z));

    public static float4 Run(ScriptContextBase ctx, [Default(0)] float4 min, [Default(1)] float4 max)
        => new(RandomFloat.Run(ctx, min.x, max.x), RandomFloat.Run(ctx, min.y, max.y), RandomFloat.Run(ctx, min.z, max.z),
            RandomFloat.Run(ctx, min.w, max.w));
}
