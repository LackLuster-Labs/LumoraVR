// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Simulation.SoftBody;

// FACE SAMPLES. A body meets the world through its particles alone, and a particle is a vertex of the mesh it was
// built from: a bevelled box's flat top is one quad with a particle at each corner and nothing between them. A 25 cm
// box set on a 46 cm jelly cube fitted inside those four corners, rested on their edges for a moment, pushed them
// aside and sank into the jelly; dropped from 1.5 m it fell straight through the face. So across any face wider than
// a contact can bridge, points spread over the triangle are asked about too, each moving the three corners it sits
// between. They are only asked where the host has marked a body that gives as near (the zones), since against the
// static world a face lying between its corners was never a problem: the floor and a draped box are larger than the
// gap. -xlinka
public sealed partial class SoftBodySolver
{
    // Three particles and three weights per sample.
    private int[]? _sampleCorners;
    private float3[]? _sampleWeights;
    private int[]? _sampledTriangles;
    private float _sampleSpacing;

    public int FaceSampleCount { get; private set; }

    // World boxes round the bodies that give, marked by the host for this frame's steps.
    private float3[] _zoneMin = new float3[2];
    private float3[] _zoneMax = new float3[2];
    private int _zoneCount;

    public void ClearSampleZones() => _zoneCount = 0;

    public void AddSampleZone(in float3 min, in float3 max)
    {
        if (_zoneCount == _zoneMin.Length)
        {
            Array.Resize(ref _zoneMin, _zoneCount * 2);
            Array.Resize(ref _zoneMax, _zoneCount * 2);
        }
        _zoneMin[_zoneCount] = min;
        _zoneMax[_zoneCount] = max;
        _zoneCount++;
    }

    public int SampleZoneCount => _zoneCount;

    // Lays the samples about spacing apart, no more than four for every particle (spacing grows until they fit),
    // and keeps them while the triangles and spacing stay. Returns how many there are.
    public int EnsureFaceSamples(float spacing)
    {
        var triangles = _restIndices;
        var particles = _particles;
        if (triangles == null || particles == null || !(spacing > 0f) || !float.IsFinite(spacing))
        {
            FaceSampleCount = 0;
            return 0;
        }
        if (ReferenceEquals(_sampledTriangles, triangles) && MathF.Abs(spacing - _sampleSpacing) <= _sampleSpacing * 0.1f)
            return FaceSampleCount;

        _sampledTriangles = triangles;
        _sampleSpacing = spacing;
        int limit = particles.Length * 4;
        float laid = spacing;
        int count = LayFaceSamples(laid, null, null);
        for (int tries = 0; count > limit && tries < 8; tries++)
        {
            laid *= 1.5f;
            count = LayFaceSamples(laid, null, null);
        }
        if (count > limit)
            count = 0;
        if (_sampleWeights == null || _sampleWeights.Length < count)
        {
            _sampleCorners = new int[count * 3];
            _sampleWeights = new float3[count];
        }
        if (count > 0)
            LayFaceSamples(laid, _sampleCorners, _sampleWeights);
        FaceSampleCount = count;
        return count;
    }

    // A grid of barycentric points over every triangle whose longest edge is two spacings or more, corners left out
    // (they are particles) and a point on an edge laid by only one of the two triangles that share it.
    private int LayFaceSamples(float spacing, int[]? corners, float3[]? weights)
    {
        var triangles = _restIndices!;
        var particles = _particles!;
        int count = 0;
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            var pa = particles[a].Pos;
            var pb = particles[b].Pos;
            var pc = particles[c].Pos;
            float longest = MathF.Max(float3.Distance(pa, pb), MathF.Max(float3.Distance(pb, pc), float3.Distance(pc, pa)));
            if (!float.IsFinite(longest))
                continue;
            int n = (int)MathF.Ceiling(longest / spacing);
            if (n < 3)
                continue;
            n = System.Math.Min(n, 16);
            float step = 1f / n;
            for (int i = 0; i <= n; i++)
            {
                for (int j = 0; j <= n - i; j++)
                {
                    int k = n - i - j;
                    if (i == n || j == n || k == n)
                        continue;
                    if ((k == 0 && a > b) || (i == 0 && b > c) || (j == 0 && c > a))
                        continue;
                    if (corners != null)
                    {
                        corners[count * 3] = a;
                        corners[count * 3 + 1] = b;
                        corners[count * 3 + 2] = c;
                        weights![count] = new float3(i * step, j * step, k * step);
                    }
                    count++;
                }
            }
        }
        return count;
    }

    private bool InZone(in float3 p, float reach)
    {
        for (int z = 0; z < _zoneCount; z++)
        {
            var min = _zoneMin[z];
            var max = _zoneMax[z];
            if (p.x >= min.x - reach && p.x <= max.x + reach && p.y >= min.y - reach && p.y <= max.y + reach
                && p.z >= min.z - reach && p.z <= max.z + reach)
                return true;
        }
        return false;
    }

    // Each sample inside a zone is pushed out like a particle (host index: particle count + sample), its corners
    // moved by their weights so the point lands where the push put it, at the speed they had. Against a body that
    // gives it then settles like a particle would (MeetMovable), giving as the three corners together give at that
    // point; against anything else its corners stop as particles on the world stop (StopCorner). Handed the point's
    // own stop as an impulse instead, a corner carrying most of a sample's weight was kicked past still, away from
    // the box, every step, and came back on the shape: the jelly under a box held like the world never slept.
    private void ResolveFaceSamples(ISoftBodyCollisionHandler collision, float radius, float dt)
    {
        var particles = _particles!;
        var corners = _sampleCorners!;
        var weights = _sampleWeights!;
        int first = particles.Length;
        float particleInverse = InverseParticleMass;
        if (particleInverse <= 0f || dt <= 0f)
            return;
        float toSpeed = 1f / dt;
        for (int s = 0; s < FaceSampleCount; s++)
        {
            int a = corners[s * 3], b = corners[s * 3 + 1], c = corners[s * 3 + 2];
            var w = weights[s];
            var p = particles[a].Pos * w.x + particles[b].Pos * w.y + particles[c].Pos * w.z;
            if (!InZone(p, radius))
                continue;
            var before = p;
            if (!collision.ResolveParticle(first + s, ref p, radius))
                continue;
            var push = p - before;
            float length = push.Length;
            if (!(length > 1e-6f) || !float.IsFinite(length))
                continue;
            var normal = push * (1f / length);
            float spread = 1f / (w.x * w.x + w.y * w.y + w.z * w.z);
            var velocity = ((particles[a].Pos - particles[a].Prev) * w.x + (particles[b].Pos - particles[b].Prev) * w.y
                + (particles[c].Pos - particles[c].Prev) * w.z) * toSpeed;
            float ownInverse = particleInverse / spread;

            ShiftCorner(a, push * (w.x * spread));
            ShiftCorner(b, push * (w.y * spread));
            ShiftCorner(c, push * (w.z * spread));

            var own = float3.Zero;
            var surfaceVelocity = float3.Zero;
            bool gives = collision.MovableContact(first + s, p - normal * radius, normal, out surfaceVelocity, out float bodyInverse,
                out bool resting, out int body) && bodyInverse > 0f;
            bool held = true;
            if (gives && MeetMovable(before, velocity, normal, ownInverse, surfaceVelocity, bodyInverse, resting,
                    out var alone, out var backed, out float bulkShare, out var arm))
            {
                held = false;
                own = alone + backed * (1f - bulkShare);
                var point = p - normal * radius;
                if (own != float3.Zero)
                    collision.Push(body, -own, point);
                DeferBacked(body, point, arm, backed * bulkShare);
            }
            else if (!gives)
            {
                surfaceVelocity = float3.Zero;
            }
            if (own != float3.Zero)
            {
                // A push at the point reaches each corner by its weight.
                var change = own * (particleInverse * dt);
                NudgeCorner(a, change * w.x);
                NudgeCorner(b, change * w.y);
                NudgeCorner(c, change * w.z);
            }
            // And a face lying on something stops going into it at the corners too, as a particle on it would, each by
            // how much of the touch it carries. Only the point's own closing was taken out before, and the shape kept
            // pulling the corners into the box beside it every step and the push putting them back: the jelly under a
            // resting box never slowed below the speed of that tug of war and never slept.
            // Held like the world, every corner of a face lying on it stops going into it and, with any grip at all,
            // along it too: the creep is the shape tugging the corners a fraction of a millimetre a step, which the
            // static friction holds outright on a particle lying on the world, and loose it kept the jelly under a box
            // asleep on it awake for good. Against a body that gives, the touch's own friction does it (MeetMovable).
            float slip = held && Friction > 0f ? 0f : 1f;
            StopCorner(a, normal, surfaceVelocity, held ? 1f : MathF.Min(1f, w.x * spread), slip, dt);
            StopCorner(b, normal, surfaceVelocity, held ? 1f : MathF.Min(1f, w.y * spread), slip, dt);
            StopCorner(c, normal, surfaceVelocity, held ? 1f : MathF.Min(1f, w.z * spread), slip, dt);
        }
    }

    private void StopCorner(int index, in float3 normal, in float3 surfaceVelocity, float share, float slip, float dt)
    {
        ref var particle = ref _particles![index];
        if (particle.Pinned || share <= 0f)
            return;
        var velocity = (particle.Pos - particle.Prev) * (1f / dt) - surfaceVelocity;
        float along = float3.Dot(velocity, normal);
        var change = float3.Zero;
        if (along < 0f)
            change -= normal * (along * share);
        if (slip < 1f)
            change -= (velocity - normal * along) * ((1f - slip) * share);
        particle.Prev -= change * dt;
    }

    // Which way is into a closed body at a particle (host index below the particle count) or a face sample (above
    // it): against the mean of the faces round the particle, or the sample's own face. Taken once a contact pass,
    // the first time a host asks. False for an open sheet, which has no inside.
    private float3[]? _inward;
    private bool _inwardReady;

    public bool InwardAt(int index, out float3 inward)
    {
        inward = float3.Zero;
        var particles = _particles;
        var triangles = _restIndices;
        if (!IsClosed || particles == null || triangles == null || index < 0)
            return false;
        if (index >= particles.Length)
        {
            int s = index - particles.Length;
            if (s >= FaceSampleCount)
                return false;
            var a = particles[_sampleCorners![s * 3]].Pos;
            var b = particles[_sampleCorners[s * 3 + 1]].Pos;
            var c = particles[_sampleCorners[s * 3 + 2]].Pos;
            inward = float3.Cross(b - a, c - a) * -Winding;
        }
        else
        {
            if (!_inwardReady)
            {
                _inwardReady = true;
                if (_inward == null || _inward.Length != particles.Length)
                    _inward = new float3[particles.Length];
                Array.Clear(_inward, 0, _inward.Length);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                    var n = float3.Cross(particles[i1].Pos - particles[i0].Pos, particles[i2].Pos - particles[i0].Pos) * -Winding;
                    _inward[i0] += n;
                    _inward[i1] += n;
                    _inward[i2] += n;
                }
            }
            inward = _inward![index];
        }
        float length = inward.Length;
        if (!(length > 1e-12f) || !float.IsFinite(length))
            return false;
        inward *= 1f / length;
        return true;
    }

    // INSIDE. Whether a point is enclosed by this body's closed surface: seen from inside, its triangles cover the whole
    // sphere of directions, from outside they cancel to nothing. And the nearest point of the surface to it, with the
    // outward normal there. For a rigid body that ended up inside the jelly (SquishyBody.FreeTrapped).
    public bool Encloses(in float3 point)
    {
        var particles = _particles;
        var triangles = _restIndices;
        if (!IsClosed || particles == null || triangles == null)
            return false;
        double total = 0.0;
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            var a = particles[triangles[t]].Pos - point;
            var b = particles[triangles[t + 1]].Pos - point;
            var c = particles[triangles[t + 2]].Pos - point;
            double la = a.Length, lb = b.Length, lc = c.Length;
            double above = float3.Dot(a, float3.Cross(b, c));
            double across = la * lb * lc + float3.Dot(a, b) * lc + float3.Dot(b, c) * la + float3.Dot(c, a) * lb;
            total += 2.0 * System.Math.Atan2(above, across);
        }
        return System.Math.Abs(total) > 2.0 * System.Math.PI;
    }

    public bool NearestSurface(in float3 point, out float3 surface, out float3 outward)
    {
        surface = point;
        outward = float3.Zero;
        var particles = _particles;
        var triangles = _restIndices;
        if (!IsClosed || particles == null || triangles == null)
            return false;
        float best = float.MaxValue;
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            var a = particles[triangles[t]].Pos;
            var b = particles[triangles[t + 1]].Pos;
            var c = particles[triangles[t + 2]].Pos;
            var q = SoftBodyContacts.ClosestOnTriangle(point, a, b, c, out _, out _, out _);
            float distance = float3.DistanceSquared(point, q);
            if (distance >= best)
                continue;
            var n = float3.Cross(b - a, c - a) * Winding;
            float length = n.Length;
            if (!(length > 1e-12f))
                continue;
            best = distance;
            surface = q;
            outward = n * (1f / length);
        }
        return best < float.MaxValue;
    }

    private void ShiftCorner(int index, in float3 move)
    {
        ref var particle = ref _particles![index];
        if (particle.Pinned)
            return;
        particle.Pos += move;
        particle.Prev += move;
    }

    // Speeds a corner up by change/dt, without moving it.
    private void NudgeCorner(int index, in float3 change)
    {
        ref var particle = ref _particles![index];
        if (!particle.Pinned)
            particle.Prev -= change;
    }
}
