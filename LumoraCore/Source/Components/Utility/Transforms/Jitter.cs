// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.Utility;

// Drives this slot's position and rotation with a slow random wander around its resting pose.
//
// Smooth noise, not a per-frame random walk: the offset is a pure function of the shared clock and the
// seed, so every peer sees the same wander and nothing has to be replicated per frame. A random walk
// would diverge immediately and could never be re-derived by a joining peer. -xlinka
[ComponentCategory("Utility/Transforms")]
public class Jitter : Component
{
    // How far the position wanders, per axis, in the slot's parent space.
    public readonly Sync<float3> PositionMagnitude;

    // How far the rotation wanders, per axis, in degrees.
    public readonly Sync<float3> RotationMagnitude;

    // Wanders per second, per axis.
    public readonly Sync<float3> Speed;

    // Changes the pattern without changing its character.
    public readonly Sync<int> Seed;

    // Moves each axis along its pattern, in wanders. Jitters on one seed set a little apart in phase move as one
    // wave passing through them, which is how a row of strips sways like a single flag.
    public readonly Sync<float3> Phase;

    // The rotation wanders about the parent's axes rather than the slot's own.
    public readonly Sync<bool> TurnAboutParent;

    // The position the wander is applied on top of.
    public readonly Sync<float3> BasePosition;

    // The rotation the wander is applied on top of.
    public readonly Sync<floatQ> BaseRotation;

    // Defaults to this slot's local position.
    public readonly FieldDrive<float3> Position;

    // Defaults to this slot's local rotation.
    public readonly FieldDrive<floatQ> Rotation;

    public Jitter()
    {
        PositionMagnitude = new Sync<float3>(this, float3.Zero);
        RotationMagnitude = new Sync<float3>(this, new float3(5f, 5f, 5f));
        Speed = new Sync<float3>(this, float3.One);
        Seed = new Sync<int>(this, 0);
        Phase = new Sync<float3>(this, float3.Zero);
        TurnAboutParent = new Sync<bool>(this, false);
        BasePosition = new Sync<float3>(this, float3.Zero);
        BaseRotation = new Sync<floatQ>(this, floatQ.Identity);
        Position = new FieldDrive<float3>(this) { LocalValueOnly = true };
        Rotation = new FieldDrive<floatQ>(this) { LocalValueOnly = true };
    }

    public override void OnAttach()
    {
        base.OnAttach();
        BasePosition.Value = Slot.LocalPosition.Value;
        BaseRotation.Value = Slot.LocalRotation.Value;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (Position.ShouldApplyDefault)
            Position.DriveTarget(Slot.LocalPosition);
        if (Rotation.ShouldApplyDefault)
            Rotation.DriveTarget(Slot.LocalRotation);
    }

    public override void OnUpdate(float delta)
    {
        double now = UtilityClock.Seconds(World);
        var speed = Speed.Value;
        var phase = Phase.Value;
        int seed = Seed.Value;

        var wander = new float3(
            SmoothNoise(now * speed.x + phase.x, seed),
            SmoothNoise(now * speed.y + phase.y, seed + 8191),
            SmoothNoise(now * speed.z + phase.z, seed + 16381));

        if (Position.IsLinkValid)
            Position.SetValue(BasePosition.Value + wander * PositionMagnitude.Value);

        if (Rotation.IsLinkValid)
        {
            var degrees = wander * RotationMagnitude.Value;
            var turn = floatQ.Euler(degrees * (float)(System.Math.PI / 180.0));
            Rotation.SetValue(TurnAboutParent.Value ? turn * BaseRotation.Value : BaseRotation.Value * turn);
        }
    }

    // Gradient noise: every whole step of the input crosses zero on a slope hashed from the seed, and the curve
    // eases from one slope into the next. Eased random levels stall flat at every step, which reads as a wander
    // pausing; this never does. Deterministic from the seed alone and continuous, which is what separates a
    // wander from a twitch. Scaled so the swing peaks at about the magnitude, now and then a little past it,
    // and spends most of its time within a third of it. -xlinka
    private static float SmoothNoise(double t, int seed)
    {
        long cell = (long)System.Math.Floor(t);
        float fraction = (float)(t - cell);
        float eased = fraction * fraction * fraction * (fraction * (fraction * 6f - 15f) + 10f);
        float a = Hash(cell, seed) * fraction;
        float b = Hash(cell + 1, seed) * (fraction - 1f);
        return (a + (b - a) * eased) * NoiseScale;
    }

    private const float NoiseScale = 2.5f;

    private static float Hash(long cell, int seed)
    {
        ulong h = (ulong)cell * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)seed * 0xBF58476D1CE4E5B9UL;
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 29;
        // Top 24 bits are the best mixed, and 2^24 is exactly representable in a float.
        return (h >> 40) / (float)(1 << 23) - 1f;
    }
}
