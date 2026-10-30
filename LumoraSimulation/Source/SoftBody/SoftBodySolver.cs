// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Simulation.SoftBody;

// Soft body: a Verlet particle mesh with distance constraints, bending constraints, long-range
// attachment, pressure, shape matching, wind and contact friction. Knows nothing about worlds, slots
// or rendering - it is handed a topology at build time, a delta time and an anchor space per step, and
// it produces particle positions.
//
// One step, in order:
//   1. accumulate per-triangle wind on the air velocity relative to the cloth, projected on each
//      triangle's facing and spread over each particle's share of the rest area
//   2. Verlet integrate the free particles; pinned ones are rebuilt from the anchor space
//   3. one bounded pressure push toward the rest volume
//   4. N relaxation passes over the stretch edges, then the bend edges
//   5. long-range attachment clamp against the nearest pin
//   6. shape matching toward the rest shape, turned with a free body's own spin
//   7. one collision pass, ground last, with a friction-aware contact response
//   8. sleep check: particles slow for long enough get the residual velocity zeroed
//
// Every constant and ordering choice in here was tuned against real cloth and jelly and is load
// bearing. The reasons are in the comments at each step; they are not decoration. -xlinka
public sealed partial class SoftBodySolver
{
    internal struct Particle
    {
        public float3 Pos;
        public float3 Prev;
        public bool Pinned;

        // For pinned particles: rest offset in anchor-space, so they follow the anchor.
        public float3 RestLocal;
    }

    private struct Edge
    {
        public int A;
        public int B;
        public float Rest;

        // Share of the correction each end takes, fixed at build from the pin state: a free-free edge
        // splits it in half, a pinned end takes none. Baked here so the inner loop is one sqrt, one
        // divide and two scaled adds with nothing to look up. Edges with both ends pinned never enter
        // the list at all. -xlinka
        public float Ka;
        public float Kb;
    }

    private Particle[]? _particles;
    private Edge[]? _edges;
    private Edge[]? _bendEdges;       // cross-edge constraints over each pair of adjacent triangles
    private int[]? _restIndices;
    private float3[]? _restOffsets;   // each rest vertex relative to the rest centroid (shape-match goal)
    private int[]? _lraPin;
    private float[]? _lraRest;
    private float3[]? _windAccum;     // per-particle wind force accumulator, allocated only while wind blows
    private float[]? _restArea;       // each particle's third of every rest triangle it touches
    private byte[]? _slowSteps;       // consecutive steps each particle has spent under the sleep speed
    private float _restVolume;
    private float3[]? _surfaceNormal;   // pressure push direction per particle, allocated only under pressure
    private float[]? _pull;           // per-particle share of the way to its goal taken each step; null = none
    private float3[]? _goal;          // per-particle goal this frame, world space; null = the rest shape through the anchor
    private bool _goalMoved;          // the rest lengths still describe the previous goal
    private float _avgEdge = 0.1f;    // velocity/collision scale, so tuning is mesh-size independent
    private floatQ _shapeRotation = floatQ.Identity;
    private float _lastDt = 1f / 60f;
    private float _stepDt = 1f / 60f;
    private bool _wasSimulating;
    private int _pinnedCount;

    // TUNABLES

    // Spring stiffness toward rest edge lengths (0..1), corrected to be iteration-independent.
    public float Stiffness = 0.6f;

    // Resistance to folding (0..1) via cross-edge constraints over adjacent triangle pairs.
    public float BendStiffness = 0.15f;

    // Share of velocity lost every sixtieth of a second (0 = bouncy, higher = sluggish), so the same
    // value bleeds the same energy per second at 60, 90 or 144 Hz.
    public float Damping = 0.02f;

    // Contact friction (0..1). 1 is full stick on contact; lower keeps tangential sliding.
    public float Friction = 1f;

    // World-space air velocity. A still sheet face-on to it accelerates at its length in m/s^2; a sheet
    // already moving with the air feels nothing, so the same term is the drag that makes it flutter.
    public float3 Wind = float3.Zero;

    // Wind gust strength (0 = perfectly steady, ~0.5 = natural gusting).
    public float WindGustiness = 0.5f;

    // Constraint solver iterations per step.
    public int Iterations = 8;

    // Internal pressure toward the rest volume (0 = floppy cloth, positive = holds a balloon).
    public float Pressure;

    // Shape retention (0..1): how hard particles are pulled back toward the rest shape.
    public float ShapeRetention;

    public float3 Gravity = new(0f, -9.81f, 0f);

    public float ParticleRadius = 0.02f;

    // Flat ground plane height. NaN disables it.
    public float GroundY = float.NaN;

    // Something that can push the body is within reach. Nothing sleeps meanwhile, so a particle resting
    // against a hand still answers the moment the hand moves.
    public bool CollidersNear;

    // Kilograms, spread evenly over the particles. Only contacts read it: what a landing hands to a body it
    // meets, and how two soft bodies share a push. Gravity moves every mass alike.
    public float Mass = 1f;

    // Air coupling, per second: a sheet face-on to the wind accelerates at this times the relative air
    // speed along its facing.
    private const float AirCoupling = 1f;

    // A particle sleeps after this many steps in a row under the sleep speed. A count of steps rather
    // than one slow step, so the turning point of a swing, where the speed passes through zero, does not
    // stop the swing dead. -xlinka
    private const int SleepSteps = 30;
    private const float SleepSpeed = 0.02f;

    // Most of the missing volume the pressure push may put back in a sixtieth of a second.
    private const float PressureGain = 0.4f;

    // STATE

    public bool IsBuilt => _particles != null;

    public int ParticleCount => _particles?.Length ?? 0;

    public int EdgeCount => _edges?.Length ?? 0;

    public int BendEdgeCount => _bendEdges?.Length ?? 0;

    public int PinnedCount => _pinnedCount;

    // Average rest edge length. Every internal threshold is scaled by it.
    public float AverageEdgeLength => _avgEdge;

    // False once every free particle has settled. The host can then skip stepping entirely.
    public bool IsAwake { get; private set; } = true;

    // The fastest any free particle moved in the last step, metres per second.
    public float LastFastestSpeed { get; private set; }

    // World-space position of one particle.
    public float3 GetPosition(int index) => _particles![index].Pos;

    // Every edge has a triangle on each side: the surface has an inside. Its winding faces out when Winding is 1.
    public bool IsClosed { get; private set; }

    internal float Winding { get; private set; } = 1f;

    // One particle's share of Mass, as an inverse; pinned particles never move and take none of a push.
    internal float InverseParticleMass => _particles == null || _particles.Length == 0 ? 0f : _particles.Length / MathF.Max(Mass, 1e-4f);

    internal Particle[]? Particles => _particles;

    internal int[]? Triangles => _restIndices;

    // Where soft contacts want particles moved this substep, gathered over every body this one touches and applied
    // as their mean (ApplyContactMoves), so a point wedged between two jellies is not shoved into one by the other
    // and back again every step.
    private float3[]? _contactMove;
    private float3[]? _contactPrevMove;
    private int[]? _contactHits;
    private int[]? _contactTouched;
    private int _contactTouchedCount;

    internal void AddContactMove(int index, in float3 move, in float3 prevMove)
    {
        int n = _particles!.Length;
        if (_contactHits == null || _contactHits.Length != n)
        {
            _contactMove = new float3[n];
            _contactPrevMove = new float3[n];
            _contactHits = new int[n];
            _contactTouched = new int[System.Math.Min(n, 64)];
            _contactTouchedCount = 0;
        }
        if (_contactHits[index]++ == 0)
        {
            _contactMove![index] = move;
            _contactPrevMove![index] = prevMove;
            if (_contactTouchedCount == _contactTouched!.Length)
                Array.Resize(ref _contactTouched, System.Math.Min(n, _contactTouched.Length * 2));
            _contactTouched[_contactTouchedCount++] = index;
            return;
        }
        _contactMove![index] += move;
        _contactPrevMove![index] += prevMove;
    }

    // The soft contacts gathered since the last call, each particle moved by their mean.
    public void ApplyContactMoves()
    {
        var particles = _particles;
        var hits = _contactHits;
        if (particles == null || hits == null || _contactTouchedCount == 0)
            return;
        for (int k = 0; k < _contactTouchedCount; k++)
        {
            int i = _contactTouched![k];
            float share = 1f / hits[i];
            particles[i].Pos += _contactMove![i] * share;
            particles[i].Prev += _contactPrevMove![i] * share;
            hits[i] = 0;
        }
        _contactTouchedCount = 0;
    }

    // Wake a sleeping body, e.g. because something that can disturb it has moved. The slow counts start
    // over so it does not drop straight back to sleep on its first quiet step. Called by the host once per
    // frame it steps a dormant body, never per substep: a body stepped twice a frame that fell asleep on
    // the first substep would have its count cleared on the second, every time, and never go dormant.
    public void Wake()
    {
        if (!IsAwake && _slowSteps != null)
            Array.Clear(_slowSteps, 0, _slowSteps.Length);
        IsAwake = true;
    }

    // Forget the accumulated velocity, so the next step starts from rest without a launch.
    public void ResetVelocities() => _wasSimulating = false;

    // Put every particle back on its rest position through the anchor, at rest. Used when a body
    // resumes after a gap it sat out - a distance pause, a disabled frame, a world that was in the
    // background. Carrying on from the stored pose would be a teleport: the anchor may be fifty metres
    // and a minute away by now, and the pins would snap to it while the free particles tried to catch
    // up from wherever they were parked, which reads as the garment being fired at its owner. -xlinka
    public void ResetToRest(ISoftBodySpace space)
    {
        var particles = _particles;
        if (particles == null)
            return;

        var goal = _goal;
        for (int i = 0; i < particles.Length; i++)
        {
            var world = goal != null ? goal[i] : space.LocalPointToGlobal(particles[i].RestLocal);
            particles[i].Pos = world;
            particles[i].Prev = world;
        }
        if (_slowSteps != null)
            Array.Clear(_slowSteps, 0, _slowSteps.Length);
        _shapeRotation = floatQ.Identity;
        _wasSimulating = false;
        IsAwake = true;
    }

    // BUILD

    // Build the constraint sets from a triangle mesh. Vertices above pinAboveLocalY in anchor-local
    // space are pinned. The particle order matches the input vertex order, so the host can write
    // results straight back onto the same mesh.
    public void Build(float3[] localPositions, int[] indices, float pinAboveLocalY, ISoftBodySpace space)
        => Build(localPositions, indices, pinAboveLocalY, null, space);

    // The same, with a hold per particle on top of the pin line: 1 pins it, anything between 0 and 1 is the share of
    // the way back to its goal it is pulled every step. The goal is its rest spot through the anchor, or whatever
    // SetGoals last handed in.
    public void Build(float3[] localPositions, int[] indices, float pinAboveLocalY, float[]? hold, ISoftBodySpace space)
    {
        ReleaseCompute();
        int n = localPositions.Length;

        _particles = new Particle[n];
        _pinnedCount = 0;
        _pull = null;
        _goal = null;
        _goalMoved = false;
        for (int i = 0; i < n; i++)
        {
            var world = space.LocalPointToGlobal(localPositions[i]);
            bool pinned = localPositions[i].y > pinAboveLocalY;
            if (!pinned && hold != null && i < hold.Length)
            {
                if (hold[i] >= 1f)
                {
                    pinned = true;
                }
                else if (hold[i] > 0f)
                {
                    _pull ??= new float[n];
                    _pull[i] = hold[i];
                }
            }
            if (pinned)
                _pinnedCount++;
            _particles[i] = new Particle
            {
                Pos = world,
                Prev = world,
                Pinned = pinned,
                RestLocal = localPositions[i],
            };
        }

        // Unique undirected edges from the triangle list = the distance-constraint springs.
        var edgeSet = new Dictionary<long, Edge>();
        void AddEdge(int a, int b)
        {
            if (a == b) return;
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (edgeSet.ContainsKey(key)) return;
            edgeSet[key] = MakeEdge(a, b);
        }
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            AddEdge(indices[t], indices[t + 1]);
            AddEdge(indices[t + 1], indices[t + 2]);
            AddEdge(indices[t + 2], indices[t]);
        }

        // The average is taken over EVERY unique edge, pinned rows included, because every threshold in
        // the solver hangs off it and it has to mean the same thing whatever gets pinned. The solve list
        // then drops the edges that cannot move anything.
        float edgeSum = 0f;
        foreach (var edge in edgeSet.Values)
            edgeSum += edge.Rest;
        _avgEdge = edgeSet.Count > 0 ? MathF.Max(edgeSum / edgeSet.Count, 1e-3f) : 0.1f;
        _edges = CollectSolvable(edgeSet.Values);

        // BENDING constraints: for every interior edge shared by two triangles, constrain the two OPPOSITE
        // vertices to their rest separation. Stretch edges alone fold like wet paper (zero resistance to
        // creasing); these cross-edges are what give fabric a drape and a jelly its resistance to shearing
        // flat. Same solver as stretch, just a second (softer) edge list. -xlinka
        var opposite = new Dictionary<long, int>();
        var bendList = new List<Edge>();
        int shared = 0, single = 0;
        void AddBend(int a, int b, int opp)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (opposite.TryGetValue(key, out int other))
            {
                if (a != b)
                    shared++;
                if (other != opp)
                    bendList.Add(MakeEdge(other, opp));
            }
            else
            {
                opposite[key] = opp;
                if (a != b)
                    single++;
            }
        }
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            AddBend(indices[t], indices[t + 1], indices[t + 2]);
            AddBend(indices[t + 1], indices[t + 2], indices[t]);
            AddBend(indices[t + 2], indices[t], indices[t + 1]);
        }
        _bendEdges = CollectSolvable(bendList);

        // No edge with a single triangle: the surface has an inside, and another body touching it is pushed out of
        // it rather than back to whichever side it came from.
        IsClosed = single > 0 && shared >= single;

        _restIndices = indices;
        _restArea = new float[n];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = _particles[indices[t]].Pos;
            float third = float3.Cross(_particles[indices[t + 1]].Pos - a, _particles[indices[t + 2]].Pos - a).Length * (0.5f / 3f);
            _restArea[indices[t]] += third;
            _restArea[indices[t + 1]] += third;
            _restArea[indices[t + 2]] += third;
        }
        _slowSteps = new byte[n];
        var restCentroid = Centroid();
        _restVolume = ComputeVolume(indices, restCentroid);
        Winding = SignedVolume(indices, restCentroid) < 0f ? -1f : 1f;
        _restOffsets = new float3[n];
        for (int i = 0; i < n; i++)
            _restOffsets[i] = _particles[i].Pos - restCentroid;

        // Long-range attachment: nearest pin (rest-space distance) per free particle. Rest-space euclidean
        // is an approximation of the true geodesic, but for sheets/blobs it's within a few percent and it
        // never OVER-constrains a straight drop, which is the case that matters visually. -xlinka
        _lraPin = null;
        _lraRest = null;
        var pinIdx = new List<int>();
        for (int i = 0; i < n; i++)
            if (_particles[i].Pinned) pinIdx.Add(i);
        if (pinIdx.Count > 0)
        {
            _lraPin = new int[n];
            _lraRest = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (_particles[i].Pinned) { _lraPin[i] = -1; continue; }
                int best = pinIdx[0];
                float bestSq = float.MaxValue;
                for (int p = 0; p < pinIdx.Count; p++)
                {
                    float dSq = float3.DistanceSquared(localPositions[i], localPositions[pinIdx[p]]);
                    if (dSq < bestSq) { bestSq = dSq; best = pinIdx[p]; }
                }
                _lraPin[i] = best;
                _lraRest[i] = MathF.Sqrt(bestSq);
            }
        }

        _shapeRotation = floatQ.Identity;
        _windAccum = null;
        _lastDt = 1f / 60f;
        _wasSimulating = false;
        IsAwake = true;
    }

    private Edge MakeEdge(int a, int b)
    {
        var particles = _particles!;
        float wa = particles[a].Pinned ? 0f : 1f;
        float wb = particles[b].Pinned ? 0f : 1f;
        float wsum = wa + wb;
        return new Edge
        {
            A = a,
            B = b,
            Rest = float3.Distance(particles[a].Pos, particles[b].Pos),
            Ka = wsum > 0f ? wa / wsum : 0f,
            Kb = wsum > 0f ? wb / wsum : 0f,
        };
    }

    // A pose to follow, one world point per particle, taken until the next call. Pinned particles sit on it, every
    // free one is pulled toward it by its hold, and lengths, the pin distances and the volume are measured off it,
    // so a skin that stretches as it bends is not fought back to the shape it had when the body was built. The
    // rigid shape match stands aside while a pose is followed; fold ShapeRetention into the holds instead.
    public void SetGoals(float3[] worldGoals)
    {
        var particles = _particles;
        if (particles == null || worldGoals.Length < particles.Length)
            return;
        if (_goal == null || _goal.Length != particles.Length)
            _goal = new float3[particles.Length];
        Array.Copy(worldGoals, _goal, particles.Length);
        _goalMoved = true;
    }

    // Back to the rest shape through the anchor.
    public void ClearGoals()
    {
        _goal = null;
        _goalMoved = false;
    }

    public bool FollowsGoals => _goal != null;

    // Holds can change after a build without touching the topology: 0 frees a particle's pull, anything up to 1
    // pulls it. A particle pinned at build stays pinned.
    public void SetPull(float[]? pull)
    {
        var particles = _particles;
        if (particles == null)
            return;
        if (pull == null)
        {
            _pull = null;
            return;
        }
        if (_pull == null || _pull.Length != particles.Length)
            _pull = new float[particles.Length];
        int n = System.Math.Min(pull.Length, particles.Length);
        for (int i = 0; i < n; i++)
            _pull[i] = System.Math.Clamp(pull[i], 0f, 1f);
    }

    private void RefreshRestFromGoals()
    {
        var goal = _goal!;
        RefreshEdgeRests(_edges, goal);
        RefreshEdgeRests(_bendEdges, goal);

        var lraPin = _lraPin;
        var lraRest = _lraRest;
        if (lraPin != null && lraRest != null)
        {
            for (int i = 0; i < lraPin.Length; i++)
            {
                int p = lraPin[i];
                if (p >= 0)
                    lraRest[i] = float3.Distance(goal[i], goal[p]);
            }
        }

        if (Pressure > 0f && _restIndices != null)
        {
            var sum = float3.Zero;
            for (int i = 0; i < goal.Length; i++)
                sum += goal[i];
            var origin = sum / MathF.Max(goal.Length, 1);
            var idx = _restIndices;
            float vol = 0f;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                var a = goal[idx[t]] - origin;
                var b = goal[idx[t + 1]] - origin;
                var c = goal[idx[t + 2]] - origin;
                vol += float3.Dot(a, float3.Cross(b, c)) / 6f;
            }
            _restVolume = MathF.Abs(vol);
        }
    }

    private static void RefreshEdgeRests(Edge[]? edges, float3[] goal)
    {
        if (edges == null)
            return;
        for (int e = 0; e < edges.Length; e++)
            edges[e].Rest = float3.Distance(goal[edges[e].A], goal[edges[e].B]);
    }

    // Only edges with at least one free end can move anything; the rest are skipped once here instead
    // of once per iteration per step forever.
    private static Edge[] CollectSolvable(IEnumerable<Edge> edges)
    {
        var list = new List<Edge>();
        foreach (var edge in edges)
        {
            if (edge.Ka + edge.Kb > 0f)
                list.Add(edge);
        }
        return list.ToArray();
    }

    // STEP

    // Advance one step. time is a monotonic clock used only for wind gusting, so it only has to be
    // consistent, not absolute.
    public void Step(float dt, double time, ISoftBodySpace space, ISoftBodyCollisionHandler? collision)
    {
        if (SolveConstraints(dt, time, space))
            ResolveContacts(space, collision);
    }

    // The pure half of a step: integration, wind, pressure and every constraint. The anchor is only read
    // through the space it is handed, so a host that hands in a frozen copy of its transform can run this
    // half on any thread. False when there is nothing to step. -xlinka
    public bool SolveConstraints(float dt, double time, ISoftBodySpace space)
    {
        if (_particles == null || _edges == null)
            return false;

        var particles = _particles;
        var edges = _edges;
        dt = System.Math.Clamp(dt, 1e-4f, 0.05f);
        _stepDt = dt;
        // Time-corrected Verlet: (pos - prev) is last frame's displacement, i.e. velocity * LAST dt. Scale
        // by dt/lastDt or a frame-time fluctuation reads as a velocity change and the body visibly pulses
        // with the frame rate. Ratio clamped so one hitch frame can't triple the velocity. -xlinka
        float prevDt = MathF.Max(_lastDt, 1e-4f);
        float dtRatio = System.Math.Clamp(dt / prevDt, 0.5f, 2f);
        _lastDt = dt;

        // Damping is stated per sixtieth of a second and raised to the steps actually taken, or a 120 Hz
        // headset bleeds twice the energy a 60 Hz one does from the same setting.
        float damping = MathF.Pow(1f - System.Math.Clamp(Damping, 0f, 1f), dt * 60f);
        var gravity = Gravity;

        // Speed cap in PER-SECOND terms (equivalent to the old 2-edges-per-frame cap at 60fps). A per-frame
        // cap silently changes terminal velocity with the frame rate. Still the key stability fix: without
        // it a stiff constraint or pressure spike accelerates a particle unboundedly and the whole body
        // stretches to infinity then NaNs out ("disappears").
        float maxMove = _avgEdge * 120f * dt;
        bool wasSimulating = _wasSimulating;
        _wasSimulating = true;

        var goal = _goal;
        if (goal != null && _goalMoved)
        {
            _goalMoved = false;
            RefreshRestFromGoals();
        }

        if (TryCompute(dt, prevDt, damping, dtRatio, maxMove, wasSimulating, time, space))
            return true;

        // WIND: per-triangle force on the air velocity RELATIVE to the triangle, projected on its facing,
        // so a sheet edge-on to the wind catches nothing, a face-on one billows, and a part already moving
        // with the air stops being pushed, which is the drag that turns a lean into a flutter. Each
        // particle's sum is divided by its share of the rest area, so Wind is an acceleration and a finer
        // mesh of the same sheet catches the same breeze. Gusting = two incommensurate sines, never
        // periodic-looking. -xlinka
        var wind = Wind;
        var restArea = _restArea;
        bool hasWind = wind.LengthSquared > 1e-8f && _restIndices != null && restArea != null;
        if (hasWind)
        {
            if (_windAccum == null || _windAccum.Length != particles.Length)
                _windAccum = new float3[particles.Length];
            else
                Array.Clear(_windAccum, 0, _windAccum.Length);

            float t = (float)time;
            float gust = 1f + System.Math.Clamp(WindGustiness, 0f, 2f)
                * (0.6f * MathF.Sin(t * 2.3f) + 0.4f * MathF.Sin(t * 5.9f + 1.7f));
            var w = wind * gust;
            // Verlet velocity is last step's displacement over last step's dt; none on a first step.
            float velScale = wasSimulating ? 1f / (3f * prevDt) : 0f;
            var idx = _restIndices!;
            for (int tri = 0; tri + 2 < idx.Length; tri += 3)
            {
                int i0 = idx[tri], i1 = idx[tri + 1], i2 = idx[tri + 2];
                var a = particles[i0].Pos;
                var n = float3.Cross(particles[i1].Pos - a, particles[i2].Pos - a) * 0.5f;
                float nLen = n.Length;
                if (nLen < 1e-8f) continue;
                var facing = n / nLen;
                var vel = (particles[i0].Pos - particles[i0].Prev
                    + particles[i1].Pos - particles[i1].Prev
                    + particles[i2].Pos - particles[i2].Prev) * velScale;
                // Signed projection kept: wind pushes a face from either side toward its lee.
                var f = facing * (float3.Dot(facing, w - vel) * AirCoupling * nLen * (1f / 3f));
                _windAccum[i0] += f;
                _windAccum[i1] += f;
                _windAccum[i2] += f;
            }
            for (int i = 0; i < particles.Length; i++)
            {
                float area = restArea![i];
                _windAccum[i] = area > 1e-8f ? _windAccum[i] / area : float3.Zero;
            }
        }

        // Verlet integrate free particles; pinned ones ride the anchor (so grabbing/moving carries them).
        for (int i = 0; i < particles.Length; i++)
        {
            if (particles[i].Pinned)
            {
                particles[i].Pos = goal != null ? goal[i] : space.LocalPointToGlobal(particles[i].RestLocal);
                particles[i].Prev = particles[i].Pos;
                continue;
            }
            var pos = particles[i].Pos;
            var vel = (pos - particles[i].Prev) * (damping * dtRatio);
            // First frame after (re)build carries no velocity (avoid a launch); clamp velocity always.
            if (!wasSimulating)
                vel = float3.Zero;
            float vmag = vel.Length;
            if (vmag > maxMove)
                vel = vel * (maxMove / vmag);
            var accel = gravity;
            if (hasWind)
                accel += _windAccum![i];
            particles[i].Prev = pos;
            particles[i].Pos = pos + vel + accel * (dt * dt);
            if (!IsFinite(particles[i].Pos))
            {
                particles[i].Pos = pos;
                particles[i].Prev = pos;
            }
        }

        // Pressure ONCE per frame (not per iteration - that was the explosion): a bounded outward push
        // to restore the rest volume. Ratio + push both clamped so a collapsed body can't blow up.
        //
        // WHY A BALLOON NEVER SLEPT. The push per unit of missing volume is a gain that grows with Pressure and
        // shrinks with size; past about one it overshoots, the next step comes back short, and the balloon pumps
        // itself in a swing that never dies (Pressure 30 on a 0.33 m balloon: the swing grew for twenty seconds,
        // Pressure 15 took twelve to settle). So the push is never more than PressureGain of what would refill
        // the volume in a sixtieth of a second, raised to the step like Damping. And it goes along each point's
        // surface normal, where gas pushes, not out from the centre: from the centre, the flat patch a balloon
        // rests on was shoved sideways along the floor every step and crept outward against friction, which
        // flattened it the harder it was pumped. A resting balloon now sleeps in about four seconds at any
        // Pressure, and a soft one under the cap looks as it did. -xlinka
        float pressure = Pressure;
        if (pressure > 0f && _restVolume > 1e-6f && _restIndices != null)
        {
            var centroid = Centroid();
            float vol = SurfaceNormals(_restIndices, centroid, out float area);
            float ratio = vol > 1e-4f ? System.Math.Clamp(_restVolume / vol, 0.5f, 2f) : 1f;
            if (ratio > 1.001f)
            {
                float push = System.Math.Clamp((ratio - 1f) * pressure * 0.01f, 0f, _avgEdge * 0.5f);
                if (area > 1e-8f)
                {
                    float refill = (_restVolume - vol) / area;
                    float gain = 1f - MathF.Pow(1f - PressureGain, dt * 60f);
                    push = MathF.Min(push, refill * gain);
                }
                var normals = _surfaceNormal!;
                for (int i = 0; i < particles.Length; i++)
                {
                    if (particles[i].Pinned) continue;
                    var outward = normals[i];
                    float d = outward.Length;
                    if (d > 1e-12f)
                        particles[i].Pos += outward * (push / d);
                }
            }
        }

        int iterations = System.Math.Clamp(Iterations, 1, 32);
        // Iteration-independent stiffness: applying k per iteration compounds to 1-(1-k)^N, so the same
        // setting got stiffer every time someone raised Iterations. Solve for the per-iteration value that
        // compounds to the REQUESTED one and tuning finally means what it says. -xlinka
        float stiffness = CorrectedStiffness(Stiffness, iterations);
        float bend = CorrectedStiffness(BendStiffness, iterations);
        var bendEdges = _bendEdges;
        bool solveBend = bend > 0f && bendEdges != null && bendEdges.Length > 0;
        for (int iter = 0; iter < iterations; iter++)
        {
            SolveEdges(particles, edges, stiffness);
            if (solveBend)
                SolveEdges(particles, bendEdges!, bend);
        }

        // LONG-RANGE ATTACHMENT: clamp every free particle to at most its rest distance (small slack) from
        // its nearest pin. Edge relaxation alone ALWAYS sags under gravity - each iteration only fixes one
        // ring of edges - so pinned cloth stretched like taffy. This is a direct, unconditionally stable
        // clamp that makes it read inextensible at any iteration count. One-sided: it never pushes IN, so
        // slack cloth hangs naturally. -xlinka
        if (_lraPin != null && _lraRest != null)
        {
            for (int i = 0; i < particles.Length; i++)
            {
                int p = _lraPin[i];
                if (p < 0) continue;
                float allowed = _lraRest[i] * 1.02f;
                var d = particles[i].Pos - particles[p].Pos;
                float dist = d.Length;
                if (dist > allowed && dist > 1e-6f)
                    particles[i].Pos = particles[p].Pos + d * (allowed / dist);
            }
        }

        // SHAPE MATCHING: pull each free particle toward its rest shape. Stable by construction (the goal
        // is bounded) so it can't run away like pressure did. For a fully-free body the goal shape turns
        // with the body (TurnShape below), so a jelly tumbles and rolls instead of being dragged back to its
        // spawn orientation. Pinned bodies keep the translation-only goal: the pins already define the
        // orientation and fighting them just fights the solver. -xlinka
        float shape = System.Math.Clamp(ShapeRetention, 0f, 1f);
        if (shape > 0f && _restOffsets != null && goal == null)
        {
            var c = Centroid();
            bool rotate = _lraPin == null; // any pins -> translation-only
            if (rotate)
                TurnShape(particles, _restOffsets, c);
            var rot = _shapeRotation;
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i].Pinned) continue;
                var target = c + (rotate ? rot * _restOffsets[i] : _restOffsets[i]);
                particles[i].Pos += (target - particles[i].Pos) * shape;
            }
        }

        // HOLDS: each held particle takes its own share of the way to its goal, after everything else, so a vertex
        // half weighted to a pinned bone sits half as loose as one that is not.
        var pull = _pull;
        if (pull != null)
        {
            for (int i = 0; i < particles.Length; i++)
            {
                float k = pull[i];
                if (k <= 0f || particles[i].Pinned)
                    continue;
                var target = goal != null ? goal[i] : space.LocalPointToGlobal(particles[i].RestLocal);
                particles[i].Pos += (target - particles[i].Pos) * k;
            }
        }
        return true;
    }

    // The half that touches the world: the host's collision callback, the ground plane, contact friction
    // and the sleep test. Runs on the thread that owns the host, after SolveConstraints for the same step.
    public void ResolveContacts(ISoftBodySpace space, ISoftBodyCollisionHandler? collision)
    {
        var particles = _particles;
        if (particles == null || _edges == null)
            return;
        float dt = _stepDt;
        float radius = MathF.Max(ParticleRadius, 0f);
        float groundY = GroundY;
        bool hasGround = !float.IsNaN(groundY);
        _bulkPush = float3.Zero;
        _bulkTurn = float3.Zero;
        _bulkReady = false;
        _backedCount = 0;
        _inwardReady = false;
        _heldUp = SupportDirection();
        _supportSum = float3.Zero;
        _supportCount = 0;
        // The body's own motion is read before any contact changes it, while something that gives was touched
        // lately; the first touch after a quiet spell reads it when it comes.
        if (_givingRecently > 0)
        {
            _givingRecently--;
            if (_goal == null && ShapeRetention > 0f)
                PrepareBulk();
        }

        // ONE collision pass after the constraints settle: the host's colliders first, then the ground
        // plane LAST (so it always wins - the body can never sink through it). Any particle that was
        // pushed has its velocity KILLED (Prev = Pos): that's what lets it SETTLE and rest instead of
        // endlessly re-penetrating and jittering. -xlinka
        for (int i = 0; i < particles.Length; i++)
        {
            if (particles[i].Pinned)
                continue;
            var pos = particles[i].Pos;
            // NaN safety net: a runaway particle whose position went non-finite would slip past the
            // ground check (NaN < groundY is false) and vanish/fall through. Reset it onto the ground
            // (or its last good spot) so the body can never leak through the floor. -xlinka
            if (!IsFinite(pos))
            {
                pos = IsFinite(particles[i].Prev) ? particles[i].Prev
                    : _goal != null ? _goal[i] : space.LocalPointToGlobal(particles[i].RestLocal);
                if (hasGround && pos.y < groundY + radius)
                    pos.y = groundY + radius;
                particles[i].Pos = pos;
                particles[i].Prev = pos;
                continue;
            }
            bool hitSurface = false;
            bool hitGround = false;
            var posBefore = pos;

            if (collision != null && collision.ResolveParticle(i, ref pos, radius))
                hitSurface = true;

            if (hasGround && pos.y - radius < groundY)
            {
                pos.y = groundY + radius;
                hitSurface = true;
                hitGround = true;
            }

            particles[i].Pos = pos;
            if (hitSurface)
            {
                if (!hitGround && collision != null)
                {
                    var push = pos - posBefore;
                    float pushLength = push.Length;
                    if (pushLength > 1e-6f)
                    {
                        var normal = push / pushLength;
                        if (collision.MovableContact(i, pos - normal * radius, normal, out var surfaceVelocity, out float bodyInverseMass, out bool bodyResting, out int body)
                            && bodyInverseMass > 0f
                            && RespondMovable(i, posBefore, pos, normal, pushLength, radius, surfaceVelocity, bodyInverseMass, bodyResting, body, collision, dt))
                            continue;
                    }
                }

                // Something that holds still: what the whole body stands on (GROUNDED).
                var held = pos - posBefore;
                float heldLength = held.Length;
                if (heldLength > 1e-6f)
                {
                    _supportSum += held * (1f / heldLength);
                    _supportCount++;
                }

                // STATIC FRICTION. The push-out only ever moves a particle along the normal, so whatever this
                // step carried it along the surface stayed, contact or not: gravity alone slid every resting
                // particle downhill by its tangential share of g*dt^2 each step, and a sheet laid over a ball crept
                // off it in a few seconds whatever Friction said, never sleeping on the way. A contact now holds
                // the step's slide outright while it is within Friction times how deep it was pushed, and trims
                // it by that much past it, so cloth rests on a slope up to the angle its friction can bear and
                // slides past it. -xlinka
                float grip = System.Math.Clamp(Friction, 0f, 1f);
                var pushed = pos - posBefore;
                float depth = pushed.Length;
                if (grip > 0f && depth > 1e-6f)
                {
                    var normal = pushed / depth;
                    var step = pos - particles[i].Prev;
                    var slide = step - normal * float3.Dot(step, normal);
                    float slideLength = slide.Length;
                    float hold = grip * depth;
                    if (slideLength > 1e-7f)
                    {
                        pos -= slideLength <= hold ? slide : slide * (hold / slideLength);
                        particles[i].Pos = pos;
                    }
                }

                // Contact response: kill the velocity INTO the surface, keep the tangential part scaled by
                // (1 - friction). Friction 1 = full stick (Prev = Pos, settles instantly); lower lets cloth
                // slide down slopes instead of gluing to the first thing it touches. The push-out direction
                // doubles as the surface normal. -xlinka
                float friction = System.Math.Clamp(Friction, 0f, 1f);
                if (friction >= 1f)
                {
                    particles[i].Prev = pos;
                }
                else
                {
                    var vel = posBefore - particles[i].Prev;
                    var push = pos - posBefore;
                    float pushLen = push.Length;
                    if (pushLen > 1e-6f)
                    {
                        var n = push / pushLen;
                        var tangential = vel - n * float3.Dot(vel, n);
                        particles[i].Prev = pos - tangential * (1f - friction);
                    }
                    else
                    {
                        particles[i].Prev = pos;
                    }
                }
            }
        }

        if (collision != null && _zoneCount > 0 && FaceSampleCount > 0)
            ResolveFaceSamples(collision, radius, dt);

        if (_backedCount > 0 && collision != null)
            SettleBacked(collision);
        if (_bulkPush != float3.Zero || _bulkTurn != float3.Zero)
            HoldBulkOnSupport();

        // What the bodies this step pushed back on handed the whole of this one (see RespondMovable): every free
        // particle takes it, the ones that touched included, as the body's own move and turn.
        if (_bulkPush != float3.Zero || _bulkTurn != float3.Zero)
        {
            float cap = _avgEdge * 120f;
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i].Pinned)
                    continue;
                var change = BulkVelocityAt(particles[i].Pos);
                if (change.LengthSquared > cap * cap)
                    change = change.Normalized * cap;
                particles[i].Prev -= change * dt;
            }
        }

        // SLEEP: a particle that has stayed slow for SleepSteps steps in a row is at rest - zero its
        // residual velocity so it STOPS instead of buzzing forever. This is why real cloth settles;
        // without it the constraint + gravity equilibrium oscillates indefinitely (the endless jiggle).
        // When EVERY free particle is asleep the whole body goes dormant and stops costing anything.
        //
        // The speed is per second, so the same cloth settles alike at 60 and 144 Hz, and small: zeroing
        // anything under 1.2 edges a second on the spot was a cut a breeze or the pull back after a push
        // never got past, so a shoved curtain crept home over twenty seconds instead of swinging. Wind and
        // anything within reach keep every particle awake. -xlinka
        var slow = _slowSteps;
        bool canSleep = slow != null && !CollidersNear && Wind.LengthSquared <= 1e-8f;
        float sleep = MathF.Min(SleepSpeed, _avgEdge * 0.25f) * dt;
        float sleepSq = sleep * sleep;
        int awake = 0;
        float fastestSq = 0f;
        for (int i = 0; i < particles.Length; i++)
        {
            if (particles[i].Pinned)
                continue;
            float stepSq = float3.DistanceSquared(particles[i].Pos, particles[i].Prev);
            if (stepSq > fastestSq)
                fastestSq = stepSq;
            if (canSleep && stepSq < sleepSq)
            {
                if (slow![i] < SleepSteps)
                    slow[i]++;
                if (slow[i] >= SleepSteps)
                {
                    particles[i].Prev = particles[i].Pos;
                    continue;
                }
            }
            else if (slow != null)
            {
                slow[i] = 0;
            }
            awake++;
        }
        IsAwake = awake > 0;
        LastFastestSpeed = dt > 0f ? MathF.Sqrt(fastestSq) / dt : 0f;
    }

    // A CONTACT THAT GIVES.
    //
    // Against the world a contact stops the particle: the surface cannot move, so all the particle had going into it
    // is simply lost. A body that can be pushed has to take that momentum, or a jelly lands on a light box and the
    // box never feels it. So the two share it as two masses colliding without bounce: the approach speed along the
    // normal goes, split by how easily each side gives (the body's give at that point includes its turning), and
    // friction takes the sideways part in the surface's own frame, up to Friction times the normal share. Whatever
    // the particle loses the body is handed through Push, which the host adds to its picture of that body: the
    // particle's own part at once, so the next particle to land meets a box that is already moving and pays less, and
    // what the whole jelly stood behind it with once every touch has asked (EVEN SPLIT). Nothing here can add energy:
    // the push-out moves the particle to the surface without giving it speed, and the impulses only ever close the
    // gap between the two velocities. -xlinka
    //
    // WHAT STANDS BEHIND A TOUCHED PARTICLE. Alone, a particle is the body's mass over its particle count, a gram of a
    // thousand-particle jelly, and that was all a box landing on it ever met: a 0.3 kg box dropped 0.6 m went through
    // the skin of a 1 kg jelly as if it were not there, and one set down gently sank 17 cm. But the shape matching
    // holds every particle to the body's own frame, ShapeRetention of the way each step, so a touched particle is
    // backed by that share of everything else. Its mass here is its own plus ShapeRetention of the rest, and what the
    // touch hands it splits the same way: its own share to it, the rest to the whole body, which moves and turns as
    // one about its centre (_bulkPush, _bulkTurn), so momentum and its moment both still add up. A firm jelly catches
    // a light box and throws it back up; cloth, with no shape to hold, is as light as ever; a 40 kg box still meets at
    // most the jelly's own kilogram and squashes it.
    //
    // The backing is for a load the shape carries: a body pressing down on the jelly, the touch facing the way gravity
    // pulls, or one driven into it from the side or above. Something the jelly lies on carries the jelly's weight
    // where each part of it lies, so the jelly meets it particle by particle. Backed there too, a light box hanging
    // over a ledge with a 2 kg jelly landing on its overhang was held level: the moment it began to tip, its inner side
    // rose into the jelly and met all of it, while the jelly coming down on the overhang only ever met it a particle
    // at a time, and the box never went over.
    //
    // A body asleep that the touch would barely move is the world for the moment: the host lets such a push go so a
    // box resting on a jelly can sleep, and the jelly must not be bounced off it as if it had given. So is one the
    // jelly only leans on, closing on it slower than RestingHit: a box asleep on the floor with a jelly settled
    // against its side took that press every frame, was woken by it, could not move for the floor, was put back to
    // sleep, and the jelly beside it never slept. A jelly thrown or dropped onto a sleeping box comes in faster than
    // that and still knocks it. -xlinka
    private float3 _bulkPush;
    private float3 _bulkTurn;

    // The body's centre, the inverse of its inertia about it, and how it moves as a whole (the mean of its
    // particles' motion and their turn about that centre), taken once a step when its contacts need them.
    private bool _bulkReady;
    private float3 _bulkCentre;
    private float _ia, _ib, _ic, _id, _ie, _if;   // symmetric inverse inertia: xx, yy, zz, xy, xz, yz
    private float3 _bodyLinear;
    private float3 _bodyTurn;
    private int _givingRecently;

    private const float RestingWake = 0.05f;
    private const float RestingHit = 1f;

    // EVEN SPLIT. Each touch reckons its share for the whole body as though it alone met the other one, and all of
    // them are settled together once the step's contacts are done, every body's split evenly among the points that
    // touched it. Settled one after another instead, the first particle in the list stopped the whole jelly against
    // the box and every one after it found nothing left to stop, so the load sat on whichever particle happened to
    // come first: a box resting on a jelly was held up by one corner every step, tipped, and slid off. Split evenly a
    // flat touch carries it flat, and since each share is the mean of what the touches asked for, never their sum,
    // the settling cannot hand out more than one touch would have. -xlinka
    private struct BackedTouch
    {
        public int Body;
        public float3 Point;
        public float3 Arm;
        public float3 Impulse;
    }

    private BackedTouch[] _backed = new BackedTouch[8];
    private int _backedCount;

    private void SettleBacked(ISoftBodyCollisionHandler collision)
    {
        var touches = _backed;
        int n = _backedCount;
        _backedCount = 0;
        float inverseMass = 1f / MathF.Max(Mass, 1e-4f);
        for (int k = 0; k < n; k++)
        {
            int body = touches[k].Body;
            int count = 0;
            for (int m = 0; m < n; m++)
            {
                if (touches[m].Body == body)
                    count++;
            }
            var share = touches[k].Impulse * (1f / count);
            collision.Push(body, -share, touches[k].Point);
            _bulkPush += share * inverseMass;
            _bulkTurn += ApplyBulkInertia(float3.Cross(touches[k].Arm, share));
        }
    }

    // GROUNDED. A jelly standing on the floor cannot move into it as a whole: what a box presses into it goes through
    // the jelly to whatever holds it up. Handed to the whole body instead, the jelly sank under the box, the floor
    // stopped it a bottom particle at a time, and a 0.3 kg box dropped 1.5 m rode it 25 cm down before it stopped; a
    // physics tick late, it ended inside the jelly with the skin closed over it. So the part of the whole body's push
    // that goes into what it rests on this step is dropped, and so is the tip that would roll it into it; the dent and
    // the spring back are still the particles' own, and a push along the floor still moves it. -xlinka
    private float3 _supportSum;
    private int _supportCount;

    //
    // Held that way, the body's own motion along the support is not the body moving: its bottom stays where it is, so
    // its centre rising is the jelly springing back out of a dent, and that meets a box the way the particles doing
    // it meet it, alone. Counted as the whole body behind the touch with the floor behind that, the face springing
    // back threw the box up a centimetre every few frames, the floor took the jelly's recoil, and a box resting on it
    // bounced for as long as anyone watched. (_heldUp, MeetMovable)
    private float3 _heldUp;

    private float3 SupportDirection()
    {
        if (_supportCount == 0)
            return float3.Zero;
        float length = _supportSum.Length;
        // Held from every side at once (wedged), the sum says nothing about which way is into the support.
        if (!(length > _supportCount * 0.3f))
            return float3.Zero;
        return _supportSum * (1f / length);
    }

    private void HoldBulkOnSupport()
    {
        var up = SupportDirection();
        if (up == float3.Zero)
            return;
        float into = float3.Dot(_bulkPush, up);
        if (into < 0f)
            _bulkPush -= up * into;
        _bulkTurn = up * float3.Dot(_bulkTurn, up);
    }

    // How the body turns as a whole, as the last step its contacts with a body that gives read it. Standing on
    // something it can only spin about the way it is held up; any other turn its particles show is the shape giving.
    public float3 Spin => _heldUp != float3.Zero ? _heldUp * float3.Dot(_bodyTurn, _heldUp) : _bodyTurn;

    private void PrepareBulk()
    {
        if (_bulkReady)
            return;
        _bulkReady = true;
        var particles = _particles!;
        _bulkCentre = Centroid();
        float m = Mass / MathF.Max(particles.Length, 1);
        float toSpeed = _stepDt > 0f ? 1f / _stepDt : 0f;
        float xx = 0f, yy = 0f, zz = 0f, xy = 0f, xz = 0f, yz = 0f;
        var linear = float3.Zero;
        var moment = float3.Zero;
        for (int i = 0; i < particles.Length; i++)
        {
            var r = particles[i].Pos - _bulkCentre;
            xx += r.y * r.y + r.z * r.z;
            yy += r.x * r.x + r.z * r.z;
            zz += r.x * r.x + r.y * r.y;
            xy -= r.x * r.y;
            xz -= r.x * r.z;
            yz -= r.y * r.z;
            var v = (particles[i].Pos - particles[i].Prev) * toSpeed;
            linear += v;
            moment += float3.Cross(r, v);
        }
        _bodyLinear = linear * (1f / MathF.Max(particles.Length, 1));
        _bodyTurn = float3.Zero;
        xx *= m; yy *= m; zz *= m; xy *= m; xz *= m; yz *= m;
        float c00 = yy * zz - yz * yz, c01 = xz * yz - xy * zz, c02 = xy * yz - yy * xz;
        float c11 = xx * zz - xz * xz, c12 = xy * xz - xx * yz, c22 = xx * yy - xy * xy;
        float det = xx * c00 + xy * c01 + xz * c02;
        if (MathF.Abs(det) < 1e-20f || !float.IsFinite(det))
        {
            _ia = _ib = _ic = _id = _ie = _if = 0f;
            return;
        }
        float inv = 1f / det;
        _ia = c00 * inv; _ib = c11 * inv; _ic = c22 * inv; _id = c01 * inv; _ie = c02 * inv; _if = c12 * inv;
        _bodyTurn = ApplyBulkInertia(moment * m);
        if (!IsFinite(_bodyLinear) || !IsFinite(_bodyTurn))
        {
            _bodyLinear = float3.Zero;
            _bodyTurn = float3.Zero;
        }
    }

    private float3 ApplyBulkInertia(in float3 v)
        => new(_ia * v.x + _id * v.y + _ie * v.z, _id * v.x + _ib * v.y + _if * v.z, _ie * v.x + _if * v.y + _ic * v.z);

    private float3 BulkVelocityAt(in float3 point) => _bulkPush + float3.Cross(_bulkTurn, point - _bulkCentre);

    private bool RespondMovable(int i, in float3 posBefore, float3 pos, in float3 normal, float depth, float radius,
        in float3 surfaceVelocity, float bodyInverseMass, bool bodyResting, int body, ISoftBodyCollisionHandler collision, float dt)
    {
        var particles = _particles!;
        float particleInverseMass = InverseParticleMass;
        if (particleInverseMass <= 0f)
            return false;
        var prev = particles[i].Prev;
        var velocity = (posBefore - prev) * (1f / dt);
        if (!MeetMovable(posBefore, velocity, normal, particleInverseMass, surfaceVelocity, bodyInverseMass, bodyResting,
                out var alone, out var backed, out float bulkShare, out var arm))
            return false;

        // The same static friction as against the world, measured against what the surface itself did this step,
        // so a jelly sitting on a box that slides goes with it.
        float grip = System.Math.Clamp(Friction, 0f, 1f);
        if (grip > 0f)
        {
            var step = pos - prev - surfaceVelocity * dt;
            var slide = step - normal * float3.Dot(step, normal);
            float slideLength = slide.Length;
            float hold = grip * depth;
            if (slideLength > 1e-7f)
                pos -= slideLength <= hold ? slide : slide * (hold / slideLength);
        }
        particles[i].Pos = pos;

        // The particle's own share now; the whole body's once every touch has asked (SettleBacked).
        var own = alone + backed * (1f - bulkShare);
        var point = pos - normal * radius;
        if (own != float3.Zero)
        {
            collision.Push(body, -own, point);
            velocity += own * particleInverseMass;
        }
        DeferBacked(body, point, arm, backed * bulkShare);
        particles[i].Prev = pos - velocity * dt;
        return true;
    }

    // What a point of this body moving at velocity, giving ownInverse on its own, and a body that gives settle on
    // (A CONTACT THAT GIVES): the impulse on the point that it meets alone, the part it meets with the whole body
    // behind it, and the share of that part the whole body takes. Of what the point takes, alone plus the rest of the
    // backed part changes its own speed. False for a sleeping body the touch would barely move.
    private bool MeetMovable(in float3 point, in float3 velocity, in float3 normal, float ownInverse, in float3 surfaceVelocity,
        float bodyInverseMass, bool bodyResting, out float3 alone, out float3 backed, out float bulkShare, out float3 arm)
    {
        alone = float3.Zero;
        backed = float3.Zero;
        bulkShare = 0f;
        arm = float3.Zero;
        float grip = System.Math.Clamp(Friction, 0f, 1f);
        var relative = velocity - surfaceVelocity;
        float approach = float3.Dot(relative, normal);

        // How much of the body stands behind this touch. Pressing down: all of ShapeRetention. Driven in: the part of
        // the closing speed the other body brought, since what the particle carries at the surface on its own meets
        // it alone and the rest of the jelly follows a step at a time through the shape; and none of that from below.
        float retention = _goal == null ? System.Math.Clamp(ShapeRetention, 0f, 1f) : 0f;
        _givingRecently = 3;
        float backing = 0f;
        if (retention > 0f)
        {
            float g = Gravity.Length;
            float down = g > 1e-6f ? float3.Dot(normal, Gravity) / g : 0f;
            float brought = MathF.Max(float3.Dot(surfaceVelocity, normal), 0f);
            float carried = MathF.Max(-float3.Dot(velocity, normal), 0f);
            float driven = brought > 0f ? brought / (brought + carried) : 0f;
            backing = retention * MathF.Max(MathF.Max(down, 0f), driven * (1f - MathF.Max(-down, 0f)));
        }

        float own = 1f / ownInverse;
        float giveInverse = 1f / (own + backing * MathF.Max(Mass - own, 0f));
        // The share of an impulse the whole body takes, and how much more the touched point gives for the body
        // turning under it.
        bulkShare = backing * Mass * giveInverse;
        float pointGive = giveInverse;
        float bodyClosing = 0f;
        if (backing > 0f)
        {
            PrepareBulk();
            arm = point - _bulkCentre;
            var turnArm = float3.Cross(arm, normal);
            pointGive += bulkShare * float3.Dot(turnArm, ApplyBulkInertia(turnArm));
            var bodyLinear = _bodyLinear;
            var bodyTurn = _bodyTurn;
            if (_heldUp != float3.Zero)
            {
                bodyLinear -= _heldUp * float3.Dot(bodyLinear, _heldUp);
                bodyTurn = _heldUp * float3.Dot(bodyTurn, _heldUp);
            }
            var bodyAt = bodyLinear + float3.Cross(bodyTurn, arm);
            bodyClosing = MathF.Max(-float3.Dot(bodyAt - surfaceVelocity, normal), 0f);
        }

        // Only the closing the jelly makes as a body meets the backing; what this particle does on its own, the
        // shape springing it out of a dent, meets the particle alone. Backed too, a particle the shape flung out of
        // a deep dent at a few metres a second landed on the box with the whole jelly behind it and threw a 0.3 kg
        // box dropped 1.5 m back up as high as it fell. The jelly springing back as a body still comes through: its
        // centre rising is the body's own motion.
        if (approach < 0f)
        {
            float closing = -approach;
            float bodyPart = MathF.Min(bodyClosing, closing);
            float backedShare = 1f / (pointGive + bodyInverseMass);
            float aloneShare = 1f / (ownInverse + bodyInverseMass);
            float backedAlong = bodyPart * backedShare;
            float aloneAlong = (closing - bodyPart) * aloneShare;
            backed = normal * backedAlong;
            alone = normal * aloneAlong;
            var sideways = relative - normal * approach;
            float sidewaysLength = sideways.Length;
            if (sidewaysLength > 1e-6f && grip > 0f)
            {
                float frictionShare = backedAlong > 0f ? backedShare : aloneShare;
                var drag = sideways * (MathF.Min(sidewaysLength * frictionShare, grip * (backedAlong + aloneAlong)) / sidewaysLength);
                if (backedAlong > 0f)
                    backed -= drag;
                else
                    alone -= drag;
            }
        }
        return !(bodyResting && (-approach < RestingHit || (alone + backed).Length * bodyInverseMass < RestingWake));
    }

    private void DeferBacked(int body, in float3 point, in float3 arm, in float3 impulse)
    {
        if (impulse == float3.Zero)
            return;
        if (_backedCount == _backed.Length)
            Array.Resize(ref _backed, _backed.Length * 2);
        _backed[_backedCount++] = new BackedTouch { Body = body, Point = point, Arm = arm, Impulse = impulse };
    }

    // OUTPUT

    // Convert every particle back into anchor-local space. Returns true if anything moved by more than
    // a hundredth of an average edge since the last call, so a settled body can skip its mesh upload -
    // the single most expensive thing a host does with this data. -xlinka
    public bool WriteLocalPositions(ISoftBodySpace space, float3[] target, ref float3[]? lastUploaded)
    {
        var particles = _particles;
        if (particles == null || target.Length < particles.Length)
            return false;

        int n = particles.Length;
        float eps = _avgEdge * 0.01f;
        float epsSq = eps * eps;
        bool changed = lastUploaded == null || lastUploaded.Length != n;
        for (int i = 0; i < n; i++)
        {
            var local = space.GlobalPointToLocal(particles[i].Pos);
            target[i] = local;
            if (!changed && float3.DistanceSquared(local, lastUploaded![i]) > epsSq)
                changed = true;
        }
        if (!changed)
            return false;

        if (lastUploaded == null || lastUploaded.Length != n)
            lastUploaded = new float3[n];
        Array.Copy(target, lastUploaded, n);
        return true;
    }

    // The body's world-space bounds grown by padding.
    public void ComputeBounds(float padding, out float3 center, out float3 size)
    {
        var particles = _particles;
        if (particles == null || particles.Length == 0)
        {
            center = float3.Zero;
            size = float3.Zero;
            return;
        }
        var min = particles[0].Pos;
        var max = min;
        for (int i = 1; i < particles.Length; i++)
        {
            var p = particles[i].Pos;
            if (!IsFinite(p)) continue;
            min.x = MathF.Min(min.x, p.x); min.y = MathF.Min(min.y, p.y); min.z = MathF.Min(min.z, p.z);
            max.x = MathF.Max(max.x, p.x); max.y = MathF.Max(max.y, p.y); max.z = MathF.Max(max.z, p.z);
        }
        var pad = new float3(padding, padding, padding);
        min -= pad;
        max += pad;
        center = (min + max) * 0.5f;
        size = max - min;
    }

    // INTERNALS

    private float3 Centroid()
    {
        var particles = _particles!;
        var sum = float3.Zero;
        for (int i = 0; i < particles.Length; i++)
            sum += particles[i].Pos;
        return sum / MathF.Max(particles.Length, 1);
    }

    // Signed volume via the divergence theorem, tetrahedra from a LOCAL origin (the centroid) so the
    // sum stays numerically stable regardless of world position. -xlinka
    private float ComputeVolume(int[] indices, float3 origin) => MathF.Abs(SignedVolume(indices, origin));

    private float SignedVolume(int[] indices, float3 origin)
    {
        var particles = _particles!;
        float vol = 0f;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = particles[indices[t]].Pos - origin;
            var b = particles[indices[t + 1]].Pos - origin;
            var c = particles[indices[t + 2]].Pos - origin;
            vol += float3.Dot(a, float3.Cross(b, c)) / 6f;
        }
        return vol;
    }

    // Outward area-weighted normal at every particle into _surfaceNormal, the enclosed volume and the surface
    // area, in one pass over the triangles. The winding may face either way; the volume's sign says which.
    private float SurfaceNormals(int[] indices, float3 origin, out float area)
    {
        var particles = _particles!;
        if (_surfaceNormal == null || _surfaceNormal.Length != particles.Length)
            _surfaceNormal = new float3[particles.Length];
        var normals = _surfaceNormal;
        Array.Clear(normals, 0, normals.Length);
        float vol = 0f;
        float twiceArea = 0f;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            var a = particles[i0].Pos - origin;
            var b = particles[i1].Pos - origin;
            var c = particles[i2].Pos - origin;
            vol += float3.Dot(a, float3.Cross(b, c)) / 6f;
            var n = float3.Cross(b - a, c - a);
            twiceArea += n.Length;
            normals[i0] += n;
            normals[i1] += n;
            normals[i2] += n;
        }
        if (vol < 0f)
        {
            for (int i = 0; i < normals.Length; i++)
                normals[i] = -normals[i];
        }
        area = twiceArea * 0.5f;
        return MathF.Abs(vol);
    }


    private static bool IsFinite(float3 v)
        => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    // Per-iteration stiffness that COMPOUNDS to the requested value over N iterations: k' = 1-(1-k)^(1/N).
    private static float CorrectedStiffness(float k, int iterations)
    {
        k = System.Math.Clamp(k, 0f, 1f);
        if (k <= 0f || k >= 1f || iterations <= 1)
            return k;
        return 1f - MathF.Pow(1f - k, 1f / iterations);
    }

    // One relaxation pass over an edge list. Weighted position-based dynamics: correction split by
    // the per-end shares baked at build, pinned = 0 -> immovable. Shared by the stretch and bend lists.
    // The shares are exact halves or whole, so the arithmetic lands on the same bits as splitting by
    // the inverse-mass sum did. -xlinka
    private static void SolveEdges(Particle[] particles, Edge[] edges, float stiffness)
    {
        for (int e = 0; e < edges.Length; e++)
        {
            ref var edge = ref edges[e];
            ref var a = ref particles[edge.A];
            ref var b = ref particles[edge.B];
            var delta = b.Pos - a.Pos;
            float dist = delta.Length;
            if (dist < 1e-6f)
                continue;
            var corr = delta * ((dist - edge.Rest) / dist * stiffness);
            a.Pos += corr * edge.Ka;
            b.Pos -= corr * edge.Kb;
        }
    }

    // Best-fit rotation of the rest shape onto the current pose (shape matching), extracted from the
    // covariance between current offsets and rest offsets. Quaternion iteration instead of a matrix polar
    // decomposition (we have no 3x3 type): rotate the basis by the residual torque between the rotated
    // rest axes and the covariance columns until they align. Warm-started from last frame's rotation, so
    // it converges in 1-2 iterations during smooth motion. Guaranteed-orthonormal output by construction
    // (it's always a quaternion), which is the property that makes shape matching unconditionally stable. -xlinka
    // HOW THE SHAPE TURNS.
    //
    // The goal used to take the rotation that best fits the particles as they stand, every step. Nothing in that asks
    // whether the body actually turned: a jelly cube with another resting on it near an edge sheared a little under the
    // weight, the best fit tilted with the shear, the goal tilted with the fit, the weight pressed harder on the low
    // side and the cube went over, as if it had been toppled. Two jellies would not stay stacked, and a box resting on
    // one leaned until it slid off. So the goal turns by what the particles are doing instead: their angular momentum
    // about the centroid, through the inertia of where they stand, is this step's spin, and the goal turns by that. A
    // body that tumbles, rolls or is spun turns as before; one that only bends does not. The fit still pulls the
    // turning back toward it by ShapeFitPull a sixtieth of a second, so the goal cannot wander off a body that has
    // squashed in some lopsided way. -xlinka
    private const float ShapeFitPull = 0.05f;

    private void TurnShape(Particle[] particles, float3[] restOffsets, float3 centroid)
    {
        var spin = float3.Zero;
        float ixx = 0f, iyy = 0f, izz = 0f, ixy = 0f, ixz = 0f, iyz = 0f;
        for (int i = 0; i < particles.Length; i++)
        {
            var r = particles[i].Pos - centroid;
            spin += float3.Cross(r, particles[i].Pos - particles[i].Prev);
            ixx += r.y * r.y + r.z * r.z;
            iyy += r.x * r.x + r.z * r.z;
            izz += r.x * r.x + r.y * r.y;
            ixy -= r.x * r.y;
            ixz -= r.x * r.z;
            iyz -= r.y * r.z;
        }

        // This step's turn: the inertia tensor solved against the summed angular momentum, by cofactors.
        float c00 = iyy * izz - iyz * iyz, c01 = ixz * iyz - ixy * izz, c02 = ixy * iyz - iyy * ixz;
        float c11 = ixx * izz - ixz * ixz, c12 = ixy * ixz - ixx * iyz, c22 = ixx * iyy - ixy * ixy;
        float det = ixx * c00 + ixy * c01 + ixz * c02;
        var turned = _shapeRotation;
        if (MathF.Abs(det) > 1e-12f)
        {
            var turn = new float3(
                c00 * spin.x + c01 * spin.y + c02 * spin.z,
                c01 * spin.x + c11 * spin.y + c12 * spin.z,
                c02 * spin.x + c12 * spin.y + c22 * spin.z) * (1f / det);
            float angle = turn.Length;
            if (angle > 1e-7f && float.IsFinite(angle))
                turned = floatQ.AxisAngleRad(turn / angle, MathF.Min(angle, 0.5f)) * turned;
        }

        // The fit, warm-started from where the turning has the goal now, and a small step toward it.
        _shapeRotation = turned;
        ExtractShapeRotation(particles, restOffsets, centroid);
        float pull = 1f - MathF.Pow(1f - ShapeFitPull, _stepDt * 60f);
        var rot = floatQ.Slerp(turned, _shapeRotation, pull);
        float len = MathF.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
        _shapeRotation = len > 1e-6f ? new floatQ(rot.x / len, rot.y / len, rot.z / len, rot.w / len) : turned;
    }

    private void ExtractShapeRotation(Particle[] particles, float3[] restOffsets, float3 centroid)
    {
        // Covariance columns: A_col_j = sum over particles of (p - c) * rest_j.
        float3 a0 = float3.Zero, a1 = float3.Zero, a2 = float3.Zero;
        for (int i = 0; i < particles.Length; i++)
        {
            var p = particles[i].Pos - centroid;
            var q = restOffsets[i];
            a0 += p * q.x;
            a1 += p * q.y;
            a2 += p * q.z;
        }
        if (a0.LengthSquared + a1.LengthSquared + a2.LengthSquared < 1e-12f)
            return; // degenerate (all at centroid) - keep last rotation

        var rot = _shapeRotation;
        for (int iter = 0; iter < 12; iter++)
        {
            var r0 = rot * float3.Right;
            var r1 = rot * float3.Up;
            var r2 = rot * float3.Forward;
            var omega = (float3.Cross(r0, a0) + float3.Cross(r1, a1) + float3.Cross(r2, a2))
                * (1f / (MathF.Abs(float3.Dot(r0, a0) + float3.Dot(r1, a1) + float3.Dot(r2, a2)) + 1e-9f));
            float angle = omega.Length;
            if (angle < 1e-5f)
                break;
            rot = floatQ.AxisAngleRad(omega / angle, angle) * rot;
        }
        // Renormalize: repeated quaternion products drift off unit length and a non-unit rotation SCALES
        // everything it touches.
        float len = MathF.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
        if (len > 1e-6f)
            _shapeRotation = new floatQ(rot.x / len, rot.y / len, rot.z / len, rot.w / len);
    }
}
