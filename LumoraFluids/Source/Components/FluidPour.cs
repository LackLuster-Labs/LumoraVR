// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A held tool's live pour, as one hand's pair in FluidPerson.Pour. a = tool RefID low and high, flow in uL/s 28 |
// mode 4, seed; b = start time (session seconds, double bits low and high), pour number, 0. A zero tool is no pour.
// Flow is fixed point so a slider wiggle that changes nothing visible sends nothing. -xlinka
public readonly struct FluidPour
{
    public readonly ulong Tool;
    public readonly float Flow;
    public readonly FluidPourMode Mode;
    public readonly double StartedAt;
    public readonly int Seed;
    public readonly int Number;

    public FluidPour(ulong tool, float flow, FluidPourMode mode, double startedAt, int seed, int number)
    {
        Tool = tool;
        Flow = flow;
        Mode = mode;
        StartedAt = startedAt;
        Seed = seed;
        Number = number;
    }

    public bool IsPouring => Tool != 0 && Flow > 0f;

    public static FluidPour Read(int4 a, int4 b)
        => new(FluidRings.Join(a.x, a.y), (a.z & 0x0FFFFFFF) * 1e-6f, (FluidPourMode)((uint)a.z >> 28),
            FluidRings.JoinDouble(b.x, b.y), a.w, b.z);

    public void Write(out int4 a, out int4 b)
    {
        int flow = (int)Math.Clamp(MathF.Round(Flow * 1e6f), 0f, 0x0FFFFFFF);
        a = new int4(FluidRings.Low(Tool), FluidRings.High(Tool), flow | (((int)Mode & 15) << 28), Seed);
        b = new int4(FluidRings.Low(StartedAt), FluidRings.High(StartedAt), Number, 0);
    }
}
