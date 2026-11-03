// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// How much value has changed since the frame before it was last worked out: this value minus last
// frame's for numbers, vectors and colors, and for a rotation the turn from last frame's to this. Unlike
// Change Since Last Run (the Delta class) it needs no flow, so a drive can use it, and worked out again in
// the same frame it gives the same change. The first time the change is none. Filed as Delta, the name
// people look for it by; the class name stays, so saved graphs keep it. -xlinka
[Node("Math/Delta", Description = "How much a value changed since last frame, with no flow needed: this value minus last frame's, or for a rotation the turn from last frame's to this. None the first time.",
    Keywords = "frame change, change since last frame, difference, per frame")]
public sealed class FrameChange<T> : MemoryNode
{
    public static bool IsValidGenericType
        => typeof(T) == typeof(float) || typeof(T) == typeof(double) || typeof(T) == typeof(int) || typeof(T) == typeof(long)
            || typeof(T) == typeof(float2) || typeof(T) == typeof(float3) || typeof(T) == typeof(float4) || typeof(T) == typeof(int2)
            || typeof(T) == typeof(int3) || typeof(T) == typeof(int4) || typeof(T) == typeof(color) || typeof(T) == typeof(colorHDR)
            || typeof(T) == typeof(TimeSpan) || typeof(T) == typeof(floatQ);

    public override void Build(FlowBuilder f)
    {
        bool rotation = typeof(T) == typeof(floatQ);
        f.FrameDelta(f.Input<T>("value"), f.Output<T>("delta"), rotation ? nameof(FromToRotation) : nameof(Subtract), previousFirst: rotation);
    }
}
