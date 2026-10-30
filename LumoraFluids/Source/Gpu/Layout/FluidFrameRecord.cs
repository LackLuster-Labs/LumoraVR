// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A simulation frame: the world (index 0) or a container. The pose now and last frame move colliders and proxies
// into the frame's space; the three terms give the fictitious accelerations a swung cup's liquid feels.
// std430, 128 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidFrameRecord
{
    public const int Size = 128;

    public floatQ Rotation;
    // m
    public float3 Position;
    public float Scale;
    public floatQ LastRotation;
    // m
    public float3 LastPosition;
    public float LastScale;
    // m/s2
    public float3 LinearAcceleration;
    public uint System;
    // rad/s
    public float3 AngularVelocity;
    public uint Flags;
    // rad/s2
    public float3 AngularAcceleration;
    // the container's index, on a FLUID_FRAME_RECORD_CONTAINER frame
    public uint Container;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
    public uint Reserved4;

    // Old names, kept until every user has moved over; they add no bytes.
    [global::System.Obsolete("Renamed Container.")]
    public uint Reserved0 { readonly get => Container; set => Container = value; }
}
