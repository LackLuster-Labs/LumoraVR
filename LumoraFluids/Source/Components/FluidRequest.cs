// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Fluids;

// One entry pair on a person's request ring. a = kind 8 | substance 4 | number 20, radius (or, for an emit, its packed
// velocity), system RefID low and high; b = point xyz (system local), litres. Number is a pour number for an undo
// and the emit's own pour number for an emit, so the host can credit it like a pour. -xlinka
public readonly struct FluidRequest
{
    public readonly FluidRequestKind Kind;
    public readonly ulong System;
    public readonly float3 Point;
    public readonly float Radius;
    public readonly float Litres;
    public readonly int Number;
    public readonly int Substance;
    public readonly float3 Velocity;

    public FluidRequest(FluidRequestKind kind, ulong system, float3 point, float radius, float litres, int number)
        : this(kind, system, point, radius, litres, number, 0, default)
    {
    }

    public FluidRequest(FluidRequestKind kind, ulong system, float3 point, float radius, float litres, int number, int substance, float3 velocity)
    {
        Kind = kind;
        System = system;
        Point = point;
        Radius = radius;
        Litres = litres;
        Number = number;
        Substance = substance;
        Velocity = velocity;
    }

    public static FluidRequest Read(int4 a, int4 b)
    {
        var kind = (FluidRequestKind)(a.x & 255);
        bool emit = kind == FluidRequestKind.Emit;
        return new FluidRequest(kind, FluidRings.Join(a.z, a.w),
            new float3(FluidRings.Float(b.x), FluidRings.Float(b.y), FluidRings.Float(b.z)),
            emit ? 0f : FluidRings.Float(a.y), FluidRings.Float(b.w), (int)((uint)a.x >> 12), (a.x >> 8) & 15,
            emit ? FluidRings.UnpackVelocity(a.y) : default);
    }

    public void Write(out int4 a, out int4 b)
    {
        int second = Kind == FluidRequestKind.Emit ? FluidRings.PackVelocity(Velocity) : FluidRings.Bits(Radius);
        a = new int4(((int)Kind & 255) | ((Substance & 15) << 8) | ((Number & 0xFFFFF) << 12), second, FluidRings.Low(System), FluidRings.High(System));
        b = new int4(FluidRings.Bits(Point.x), FluidRings.Bits(Point.y), FluidRings.Bits(Point.z), FluidRings.Bits(Litres));
    }
}
