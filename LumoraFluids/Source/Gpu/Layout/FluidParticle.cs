// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// One particle, in the cell-sorted A/B arrays the sort ping-pongs. Volume is m3 and drives density, so thinned
// strand particles and merged ones stay correct. Material packs substance, system, frame and origin indices (shifts
// in FluidLayout); Phase carries the phase, flags and neighbour count. Bonds and anchors never point at a slot:
// they hold stable ids, resolved through the id-to-slot map the sort rewrites.
// std430, 80 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidParticle
{
    public const int Size = 80;

    // m, in the particle's frame
    public float3 Position;
    // m3
    public float Volume;
    // m, last substep
    public float3 Previous;
    // kg
    public float Mass;
    // m/s
    public float3 Velocity;
    // 0..1
    public float Wetness;
    public uint Phase;
    public uint Material;
    // RGBA8
    public uint Tint;
    public uint Region;
    // s
    public float Age;
    // s inside StickDistance of a surface
    public float BandTime;
    // s, the fade's time constant while FADING
    public float FadeTime;
    public uint Reserved1;

    // Old names, kept until every user has moved over; they add no bytes.
    [global::System.Obsolete("Renamed FadeTime.")]
    public uint Reserved0 { readonly get => global::System.BitConverter.SingleToUInt32Bits(FadeTime); set => FadeTime = global::System.BitConverter.UInt32BitsToSingle(value); }
}
