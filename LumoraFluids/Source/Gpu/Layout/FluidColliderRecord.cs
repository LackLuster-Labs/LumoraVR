// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A gathered collider, pose now and last frame so each substep sweeps it. The surface factors come from the nearest
// FluidSurface above it. GridIndex points at its triangle grid (mesh and convex), Body at the impulse table
// (FluidLayout.None for static colliders).
// std430, 144 B; FluidLayout.glsli declares the same bytes. -xlinka
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidColliderRecord
{
    public const int Size = 144;

    public floatQ Rotation;
    // m
    public float3 Position;
    public float Scale;
    public floatQ LastRotation;
    // m
    public float3 LastPosition;
    public float LastScale;
    // m
    public float3 HalfExtents;
    // m
    public float Radius;
    public float Stickiness;
    public float Wetting;
    public float Friction;
    // L/s/m2
    public float Absorb;
    public uint Kind;
    public uint GridIndex;
    public uint Body;
    public uint Frame;
    public uint Flags;
    // anchor slot index for cells keyed to it
    public uint Owner;
    // share of the approach speed a contact gives back along its normal, 0..1
    public float Bounce;
    // Stable collider identity for anchors; zero is an untracked fixture. -xlinka
    public uint Reserved1;
    // Compound shapes accumulate torque about the same world-space body centre. -xlinka
    public float3 BodyCenter;
    public uint Reserved2;

    // Old names, kept until every user has moved over; they add no bytes.
    [global::System.Obsolete("Renamed Bounce.")]
    public uint Reserved0 { readonly get => global::System.BitConverter.SingleToUInt32Bits(Bounce); set => Bounce = global::System.BitConverter.UInt32BitsToSingle(value); }
}
