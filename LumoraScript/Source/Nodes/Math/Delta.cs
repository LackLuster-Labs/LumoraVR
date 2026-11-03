// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// How much value has changed since the last time flow came through: this value minus the last one for
// numbers, vectors and colors, and for a rotation the turn from the last one to this, so Combine the last one
// with it and you get this one. On On Update it is the change since the last frame. The first time through
// there is no last one and the change is none. Each one keeps its own; a restarted script, or one swapped
// for an edited graph, starts over. Filed as Change Since Last Run: Delta in the browser is Frame Change,
// the one that needs no flow. The class name is what graphs call it, so saved graphs keep this one. -xlinka
[Node("Math/Change Since Last Run", Description = "Returns how much a value changed since flow last reached this node. For rotations, returns the turn between them. Delta gives the change since last frame with no flow.",
    Keywords = "delta, difference")]
public sealed class Delta<T> : FlowNode
{
    public static bool IsValidGenericType
        => typeof(T) == typeof(float) || typeof(T) == typeof(double) || typeof(T) == typeof(int) || typeof(T) == typeof(long)
            || typeof(T) == typeof(uint) || typeof(T) == typeof(ulong) || typeof(T) == typeof(float2) || typeof(T) == typeof(float3)
            || typeof(T) == typeof(float4) || typeof(T) == typeof(int2) || typeof(T) == typeof(int3) || typeof(T) == typeof(int4)
            || typeof(T) == typeof(color) || typeof(T) == typeof(colorHDR) || typeof(T) == typeof(TimeSpan) || typeof(T) == typeof(floatQ);

    public override void Build(FlowBuilder f)
    {
        bool rotation = typeof(T) == typeof(floatQ);
        f.Change(f.Input<T>("value"), f.Output<T>("delta"), f.Out("next"), rotation ? nameof(FromToRotation) : nameof(Subtract),
            previousFirst: rotation);
    }
}
