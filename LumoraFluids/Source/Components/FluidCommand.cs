// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Fluids;

// One entry pair on a system's command ring. a = kind 8 | origin 8 | substance 4, seed, litres, radius (or seconds for
// a fade); b = point xyz (system local), packed velocity. Origin AnyOrigin acts on every origin. -xlinka
public readonly struct FluidCommand
{
    public const int AnyOrigin = 255;

    public readonly FluidCommandKind Kind;
    public readonly int Origin;
    public readonly float3 Point;
    public readonly float Radius;
    public readonly float Litres;
    public readonly int Substance;
    public readonly int Seed;
    public readonly float3 Velocity;

    public FluidCommand(FluidCommandKind kind, int origin, float3 point, float radius, float litres, int substance, int seed)
        : this(kind, origin, point, radius, litres, substance, seed, default)
    {
    }

    public FluidCommand(FluidCommandKind kind, int origin, float3 point, float radius, float litres, int substance, int seed, float3 velocity)
    {
        Kind = kind;
        Origin = origin;
        Point = point;
        Radius = radius;
        Litres = litres;
        Substance = substance;
        Seed = seed;
        Velocity = velocity;
    }

    public bool AnyOriginMatches => Origin == AnyOrigin;

    public static FluidCommand Read(int4 a, int4 b)
        => new((FluidCommandKind)(a.x & 255), (a.x >> 8) & 255,
            new float3(FluidRings.Float(b.x), FluidRings.Float(b.y), FluidRings.Float(b.z)),
            FluidRings.Float(a.w), FluidRings.Float(a.z), (a.x >> 16) & 15, a.y, FluidRings.UnpackVelocity(b.w));

    public void Write(out int4 a, out int4 b)
    {
        a = new int4(((int)Kind & 255) | ((Origin & 255) << 8) | ((Substance & 15) << 16), Seed, FluidRings.Bits(Litres), FluidRings.Bits(Radius));
        b = new int4(FluidRings.Bits(Point.x), FluidRings.Bits(Point.y), FluidRings.Bits(Point.z), FluidRings.PackVelocity(Velocity));
    }

    // The GPU's form of it, for the commands kernel (FluidGpuOp has the field meanings). system is the system's index
    // in the frame block; particles is how many the emit makes at this machine's spacing.
    public FluidGpuCommand ToGpu(uint system, uint particles = 0)
    {
        var c = new FluidGpuCommand { System = system, Seed = (uint)Seed };
        switch (Kind)
        {
            case FluidCommandKind.Emit:
            case FluidCommandKind.Burst:
                c.Op = (uint)(Kind == FluidCommandKind.Emit ? FluidGpuOp.Emit : FluidGpuOp.Burst);
                c.Count = particles;
                c.A = new float4(Point.x, Point.y, Point.z, Litres);
                c.B = new float4(Velocity.x, Velocity.y, Velocity.z, 0f);
                c.C = new float4(0f, Origin, Substance, Radius);
                break;
            case FluidCommandKind.Remove:
                c.Op = (uint)FluidGpuOp.Remove;
                c.Count = (uint)Origin;
                c.A = new float4(Point.x, Point.y, Point.z, Radius);
                c.B = new float4(Litres, 0f, 0f, 0f);
                break;
            case FluidCommandKind.ClearAll:
                c.Op = (uint)FluidGpuOp.ClearAll;
                break;
            case FluidCommandKind.ClearOrigin:
                c.Op = (uint)FluidGpuOp.ClearOrigin;
                c.Count = (uint)Origin;
                break;
            case FluidCommandKind.Evaporate:
                c.Op = (uint)FluidGpuOp.Evaporate;
                c.Count = (uint)Origin;
                c.A = new float4(Litres, 0f, 0f, 0f);
                break;
            case FluidCommandKind.FadeOrigin:
                c.Op = (uint)FluidGpuOp.FadeOrigin;
                c.Count = (uint)Origin;
                c.A = new float4(Radius, 0f, 0f, 0f);
                break;
            case FluidCommandKind.Settle:
                c.Op = (uint)FluidGpuOp.Settle;
                break;
            default:
                // Epoch is read on the world thread (drop the unanchored bulk and reseed from cells), not a kernel op.
                c.Op = (uint)FluidGpuOp.None;
                break;
        }
        return c;
    }
}
