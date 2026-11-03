// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A random point on the straight line from a to b, every point of it as likely: one draw shared by all
// lanes, where Random Vector draws each lane on its own.
[Node("Math/Random/Random Lerp", Description = "A random point on the straight line from a to b, every point of it as likely: one draw shared by all lanes, where Random Vector draws each lane on its own.",
    Keywords = "random between, random point on line")]
public sealed class RandomLerp : ReadNode
{
    public static float2 Run(ScriptContextBase ctx, [Default(0)] float2 a, [Default(1)] float2 b) => a + (b - a) * ctx.Random.NextSingle();
    public static float3 Run(ScriptContextBase ctx, [Default(0)] float3 a, [Default(1)] float3 b) => a + (b - a) * ctx.Random.NextSingle();
    public static float4 Run(ScriptContextBase ctx, [Default(0)] float4 a, [Default(1)] float4 b) => a + (b - a) * ctx.Random.NextSingle();
}
