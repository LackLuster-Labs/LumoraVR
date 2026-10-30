// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// One entry pair on a person's release ring. a = kind 8 | body part 8 | substance 4 | Touched index 4, seed, litres,
// packed velocity; b = world point xyz, cell. For a Pickup, System is the Touched index of the system the goo came from
// and Cell its ledger index there, so the keeper and the host know exactly which cell gave it up. -xlinka
public readonly struct FluidRelease
{
    public const int NoCell = -1;

    public readonly FluidReleaseKind Kind;
    public readonly float3 Point;
    public readonly float3 Velocity;
    public readonly float Litres;
    public readonly BodyNode Part;
    public readonly int Substance;
    public readonly int Seed;
    public readonly int System;
    public readonly int Cell;

    public FluidRelease(FluidReleaseKind kind, float3 point, float3 velocity, float litres, BodyNode part, int substance, int seed)
        : this(kind, point, velocity, litres, part, substance, seed, 0, NoCell)
    {
    }

    public FluidRelease(FluidReleaseKind kind, float3 point, float3 velocity, float litres, BodyNode part, int substance, int seed, int system, int cell)
    {
        Kind = kind;
        Point = point;
        Velocity = velocity;
        Litres = litres;
        Part = part;
        Substance = substance;
        Seed = seed;
        System = system;
        Cell = cell;
    }

    // Everything but a Pickup takes goo off the person and puts it into the world.
    public bool IsOffPerson => Kind != FluidReleaseKind.Pickup;

    public static FluidRelease Read(int4 a, int4 b)
        => new((FluidReleaseKind)(a.x & 255),
            new float3(FluidRings.Float(b.x), FluidRings.Float(b.y), FluidRings.Float(b.z)),
            FluidRings.UnpackVelocity(a.w), FluidRings.Float(a.z), (BodyNode)((a.x >> 8) & 255),
            (a.x >> 16) & 15, a.y, (a.x >> 20) & 15, b.w);

    public void Write(out int4 a, out int4 b)
    {
        a = new int4(((int)Kind & 255) | (((int)Part & 255) << 8) | ((Substance & 15) << 16) | ((System & 15) << 20),
            Seed, FluidRings.Bits(Litres), FluidRings.PackVelocity(Velocity));
        b = new int4(FluidRings.Bits(Point.x), FluidRings.Bits(Point.y), FluidRings.Bits(Point.z), Cell);
    }
}
