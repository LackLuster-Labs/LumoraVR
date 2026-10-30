// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// One residue spot, as FluidPerson.Spots and SpotMeta carry it.
//   Spots    float4: xyz in the part bone's local space, w = volume at birth in mL, in 0.05 mL steps (0 = empty).
//   SpotMeta int4:   x = body node; y = tint RGBA8; z = normal oct 8+8, flatten 8, wetness at birth 8;
//                    w = Touched index 8, substance 4, spare 1, birth second 19 (session seconds, wraps at 6 days).
// Drying is never sent: every machine works a spot's current amount out of its birth and the substance's DryTime.
// -xlinka
public readonly struct FluidSpot
{
    public const float QuantumMl = 0.05f;
    public const int BirthWrap = 1 << 19;

    public readonly float3 Local;
    public readonly float BirthMillilitres;
    public readonly BodyNode Node;
    public readonly uint Tint;
    public readonly uint NormalShape;
    public readonly int Touched;
    public readonly int Substance;
    public readonly int BirthSecond;

    public FluidSpot(float3 local, float birthMillilitres, BodyNode node, uint tint, uint normalShape, int touched, int substance, int birthSecond)
    {
        Local = local;
        BirthMillilitres = birthMillilitres;
        Node = node;
        Tint = tint;
        NormalShape = normalShape;
        Touched = touched;
        Substance = substance;
        BirthSecond = birthSecond;
    }

    public bool IsEmpty => !(BirthMillilitres > 0f);

    // Bone-local, unit length.
    public float3 Normal => UnpackNormal(NormalShape & 0xFFFF);

    // 0 round .. 1 a flat film.
    public float Flatten => ((NormalShape >> 16) & 0xFF) / 255f;

    public float Wetness => ((NormalShape >> 24) & 0xFF) / 255f;

    public static FluidSpot Make(float3 local, float millilitres, BodyNode node, uint tint, float3 normal, float flatten, float wetness,
        int touched, int substance, double sessionSeconds)
    {
        uint shape = PackNormal(normal) | (Byte(flatten) << 16) | (Byte(wetness) << 24);
        return new FluidSpot(local, Quantize(millilitres), node, tint, shape, touched & 0xFF, substance & 0xF, BirthOf(sessionSeconds));
    }

    public static FluidSpot Read(float4 spot, int4 meta)
    {
        var local = new float3(spot.x, spot.y, spot.z);
        if (!float.IsFinite(local.x) || !float.IsFinite(local.y) || !float.IsFinite(local.z) || !float.IsFinite(spot.w))
            return default;
        uint w = unchecked((uint)meta.w);
        return new FluidSpot(local, MathF.Max(0f, spot.w), (BodyNode)meta.x, unchecked((uint)meta.y), unchecked((uint)meta.z),
            (int)(w & 0xFF), (int)((w >> 8) & 0xF), (int)(w >> 13));
    }

    public void Write(out float4 spot, out int4 meta)
    {
        spot = new float4(Local.x, Local.y, Local.z, IsEmpty ? 0f : Quantize(BirthMillilitres));
        uint w = (uint)(Touched & 0xFF) | ((uint)(Substance & 0xF) << 8) | ((uint)(BirthSecond & (BirthWrap - 1)) << 13);
        meta = new int4((int)Node, unchecked((int)Tint), unchecked((int)NormalShape), unchecked((int)w));
    }

    // Seconds since birth, through the 19-bit wrap.
    public double AgeAt(double sessionSeconds)
    {
        int now = BirthOf(sessionSeconds);
        int age = (now - BirthSecond) & (BirthWrap - 1);
        return age;
    }

    // The amount left after drying: birth x exp(-age / dryTime). A dry time of 0 never dries.
    public float MillilitresAt(double sessionSeconds, float dryTime)
    {
        if (IsEmpty)
            return 0f;
        if (!(dryTime > 0f))
            return BirthMillilitres;
        return BirthMillilitres * MathF.Exp(-(float)AgeAt(sessionSeconds) / dryTime);
    }

    public FluidSpot With(float3 local, float millilitres, double sessionSeconds)
        => new(local, Quantize(millilitres), Node, Tint, NormalShape, Touched, Substance, BirthOf(sessionSeconds));

    public static int BirthOf(double sessionSeconds)
        => (int)((long)System.Math.Floor(System.Math.Max(0d, sessionSeconds)) & (BirthWrap - 1));

    public static float Quantize(float millilitres)
        => millilitres > 0f ? MathF.Max(QuantumMl, MathF.Round(millilitres / QuantumMl) * QuantumMl) : 0f;

    // Octahedral, 8 bits a component: a spot's normal only steers how it flattens, so 1.4 degrees is plenty.
    public static uint PackNormal(float3 n)
    {
        float l = MathF.Abs(n.x) + MathF.Abs(n.y) + MathF.Abs(n.z);
        if (!(l > 1e-12f))
            return PackNormal(float3.Up);
        float x = n.x / l, y = n.y / l;
        if (n.z < 0f)
        {
            float ox = (1f - MathF.Abs(y)) * (x >= 0f ? 1f : -1f);
            float oy = (1f - MathF.Abs(x)) * (y >= 0f ? 1f : -1f);
            x = ox;
            y = oy;
        }
        return Byte(x * 0.5f + 0.5f) | (Byte(y * 0.5f + 0.5f) << 8);
    }

    public static float3 UnpackNormal(uint packed)
    {
        float x = (packed & 0xFF) / 255f * 2f - 1f;
        float y = ((packed >> 8) & 0xFF) / 255f * 2f - 1f;
        float z = 1f - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0f)
        {
            float ox = (1f - MathF.Abs(y)) * (x >= 0f ? 1f : -1f);
            float oy = (1f - MathF.Abs(x)) * (y >= 0f ? 1f : -1f);
            x = ox;
            y = oy;
        }
        var v = new float3(x, y, z);
        float len = v.Length;
        return len > 1e-6f ? v / len : float3.Up;
    }

    private static uint Byte(float v) => (uint)System.Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
