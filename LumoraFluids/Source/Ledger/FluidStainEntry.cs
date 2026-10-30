// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Packs and reads one entry of FluidSystem.Stains, the host's sparse grid of stains on world surfaces. A stain lives in
// a coarse cell of FluidStainPool.CellSize in the system slot's space and remembers where in that cell it lies:
//   x  ix (16, signed) | iz (16, signed)
//   y  iy (16, signed) | place in the cell, 5 bits an axis, from bit 16 (x, then y, then z)
//   z  normal, octahedral 8 + 8 | amount 0..65535 (65535 = fully stained)
//   w  radius in mm (12) | age in 16 s steps (8) | substance (4) | flags (4) | spare (4)
// StainTint at the same index is RGBA8 with r in the low byte; 0 means the substance's own tint. An entry with amount
// 0 is free. -xlinka
public static class FluidStainEntry
{
    public const int AmountMax = 65535;
    public const int PlaceSteps = 32;
    public const float AgeStep = 16f;

    public static int4 Pack(float3 local, float3 normal, float radius, float amount, int substance, int age = 0, int flags = 0)
    {
        float cell = FluidStainPool.CellSize;
        int ix = (int)MathF.Floor(local.x / cell);
        int iy = (int)MathF.Floor(local.y / cell);
        int iz = (int)MathF.Floor(local.z / cell);
        int px = Place(local.x / cell - ix);
        int py = Place(local.y / cell - iy);
        int pz = Place(local.z / cell - iz);
        var oct = OctEncode(normal);
        int a = Math.Clamp((int)MathF.Round(amount * AmountMax), 0, AmountMax);
        int r = Math.Clamp((int)MathF.Round(radius * 1000f), 0, 4095);
        return new int4(
            (Math.Clamp(ix, short.MinValue, short.MaxValue) & 0xFFFF) | (Math.Clamp(iz, short.MinValue, short.MaxValue) << 16),
            (Math.Clamp(iy, short.MinValue, short.MaxValue) & 0xFFFF) | (px << 16) | (py << 21) | (pz << 26),
            (oct.x & 255) | ((oct.y & 255) << 8) | (a << 16),
            r | ((age & 255) << 12) | ((substance & 15) << 20) | ((flags & 15) << 24));
    }

    private static int Place(float f) => Math.Clamp((int)MathF.Floor(f * PlaceSteps), 0, PlaceSteps - 1);

    public static float Amount(int4 stain) => (int)((uint)stain.z >> 16) / (float)AmountMax;

    public static bool IsFree(int4 stain) => ((uint)stain.z >> 16) == 0;

    // Where the stain lies, in the system slot's space.
    public static float3 Centre(int4 stain)
    {
        float cell = FluidStainPool.CellSize;
        int ix = (short)(stain.x & 0xFFFF);
        int iz = stain.x >> 16;
        int iy = (short)(stain.y & 0xFFFF);
        float px = (((stain.y >> 16) & 31) + 0.5f) / PlaceSteps;
        float py = (((stain.y >> 21) & 31) + 0.5f) / PlaceSteps;
        float pz = (((stain.y >> 26) & 31) + 0.5f) / PlaceSteps;
        return new float3((ix + px) * cell, (iy + py) * cell, (iz + pz) * cell);
    }

    public static float3 Normal(int4 stain) => OctDecode(stain.z & 255, (stain.z >> 8) & 255);

    // m
    public static float Radius(int4 stain) => (stain.w & 4095) * 0.001f;

    // s
    public static float Age(int4 stain) => ((stain.w >> 12) & 255) * AgeStep;

    public static int Substance(int4 stain) => (stain.w >> 20) & 15;

    public static int Flags(int4 stain) => (stain.w >> 24) & 15;

    // The coarse cell a stain sits in; two stains in one cell are the same stain.
    public static (int X, int Y, int Z) Key(int4 stain) => ((short)(stain.x & 0xFFFF), (short)(stain.y & 0xFFFF), stain.x >> 16);

    // Everything a renderer needs for entry i of a system, in world space: centre, normal, radius, tint and age.
    public static bool Read(FluidSystem system, int index, out float3 centre, out float3 normal, out float radius, out colorHDR tint, out float age, out float amount)
    {
        centre = default;
        normal = float3.Up;
        radius = 0f;
        tint = colorHDR.White;
        age = 0f;
        amount = 0f;
        if (system?.Slot == null || index < 0 || index >= system.Stains.Count)
            return false;
        var stain = system.Stains[index];
        if (IsFree(stain))
            return false;
        var slot = system.Slot;
        centre = slot.LocalPointToGlobal(Centre(stain));
        normal = (slot.GlobalRotation * Normal(stain)).Normalized;
        radius = Radius(stain) * MathF.Abs(slot.GlobalScale.x);
        age = Age(stain);
        amount = Amount(stain);
        int packed = index < system.StainTint.Count ? system.StainTint[index] : 0;
        tint = packed != 0 ? Unpack((uint)packed) : system.TintOf(Substance(stain));
        return true;
    }

    private static colorHDR Unpack(uint v)
        => new((v & 255) / 255f, ((v >> 8) & 255) / 255f, ((v >> 16) & 255) / 255f, ((v >> 24) & 255) / 255f);

    private static (int x, int y) OctEncode(float3 n)
    {
        float len = MathF.Abs(n.x) + MathF.Abs(n.y) + MathF.Abs(n.z);
        if (len < 1e-9f)
            n = float3.Up;
        else
            n = n / len;
        float ox = n.x, oy = n.y;
        if (n.z < 0f)
        {
            ox = (1f - MathF.Abs(n.y)) * (n.x >= 0f ? 1f : -1f);
            oy = (1f - MathF.Abs(n.x)) * (n.y >= 0f ? 1f : -1f);
        }
        return ((int)MathF.Round((ox * 0.5f + 0.5f) * 255f), (int)MathF.Round((oy * 0.5f + 0.5f) * 255f));
    }

    private static float3 OctDecode(int ex, int ey)
    {
        float fx = ex / 255f * 2f - 1f;
        float fy = ey / 255f * 2f - 1f;
        var n = new float3(fx, fy, 1f - MathF.Abs(fx) - MathF.Abs(fy));
        float t = Math.Clamp(-n.z, 0f, 1f);
        n = new float3(n.x + (n.x >= 0f ? -t : t), n.y + (n.y >= 0f ? -t : t), n.z);
        return n.Normalized;
    }
}
