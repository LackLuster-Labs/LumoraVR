// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Simulation.SoftBody;

// SOFT AGAINST SOFT.
//
// Two bodies meet where a particle of one comes within a particle radius of a triangle of the other. Points against
// triangles, both ways round, rather than particles against particles: particles sit an edge apart and a radius wide,
// so two jellies made of them pass through each other's gaps, while a surface has none. A closed surface pushes a
// point out of its inside; an open sheet pushes it back to the side it came from, judged on where both were when the
// step began. Each point answers to the one triangle nearest it: every triangle a point is behind would push it out
// through its own face, and from inside a ball that is every direction at once.
//
// Only points ever move, and both sides are worked out from the same moment before either moves. A point takes its
// share of the way out by mass (one over its mass against the surface's where it touches), and the other body's points
// facing it take theirs in the other half of the pass, so a heavy jelly squashes a light one and not the other way
// round, and nothing is pushed twice in one pass. Moving the triangles' corners too was tried: a corner can be nearest
// to a dozen points at once (the pole of a ball landing on another), was shoved a dozen times its share, and the bottom
// of the ball crumpled inside out. -xlinka
//
// Cost: only the box where the two bodies' bounds overlap is looked at. The triangles of one that reach into it go into
// a small grid over that box and the other's points inside it look them up, and a point looks only as far as the
// contact distance plus how far it moved this step, so two jellies resting together pay for their contact patch and
// nothing else.
public sealed class SoftBodyContacts
{
    private int[] _triangles = new int[64];
    private float3[] _triangleMin = new float3[64];
    private float3[] _triangleMax = new float3[64];
    private int _triangleCount;
    private int[] _points = new int[64];
    private int _pointCount;

    private int[] _cellStart = new int[2];
    private int[] _cellCursor = new int[2];
    private int[] _cellItems = new int[64];
    private float3 _gridMin;
    private float _cellInverse;
    private int _nx, _ny, _nz;

    // Where every touching point goes, gathered from both sides before any of it is applied.
    private struct Move
    {
        public int Index;
        public bool Second;
        public float3 Pos;
        public float3 Prev;
    }

    private Move[] _moves = new Move[64];
    private int _moveCount;

    // Fewest point and triangle pairs worth a grid for; under it every point simply tries every triangle.
    private const int BruteForcePairs = 2048;

    private const int MostCells = 16384;

    // Contacts resolved by the last Collide.
    public int LastContacts { get; private set; }

    // Whether two bodies' current bounds come within the given distance of each other.
    public static bool Near(SoftBodySolver a, SoftBodySolver b, float distance)
    {
        var pa = a.Particles;
        var pb = b.Particles;
        if (pa == null || pb == null || pa.Length == 0 || pb.Length == 0)
            return false;
        Bounds(pa, out var amin, out var amax);
        Bounds(pb, out var bmin, out var bmax);
        return amin.x <= bmax.x + distance && bmin.x <= amax.x + distance
            && amin.y <= bmax.y + distance && bmin.y <= amax.y + distance
            && amin.z <= bmax.z + distance && bmin.z <= amax.z + distance;
    }

    // One pass of contact between two bodies after both solved their constraints for the same step. Returns how many
    // contacts were found. Nothing moves until each body's ApplyContactMoves, after every pair of the substep. A body
    // that is not stepping this frame can still be met, held still: it takes no share of any push and is not moved.
    public int Collide(SoftBodySolver a, SoftBodySolver b, bool bHeld = false)
    {
        LastContacts = 0;
        var pa = a.Particles;
        var pb = b.Particles;
        if (pa == null || pb == null || pa.Length == 0 || pb.Length == 0 || ReferenceEquals(a, b))
            return 0;

        // One skin between them, not one each: the bigger radius is the gap they rest at.
        float thickness = MathF.Max(MathF.Max(a.ParticleRadius, b.ParticleRadius), 1e-3f);
        Bounds(pa, out var amin, out var amax);
        Bounds(pb, out var bmin, out var bmax);
        var min = new float3(MathF.Max(amin.x, bmin.x), MathF.Max(amin.y, bmin.y), MathF.Max(amin.z, bmin.z)) - new float3(thickness, thickness, thickness);
        var max = new float3(MathF.Min(amax.x, bmax.x), MathF.Min(amax.y, bmax.y), MathF.Min(amax.z, bmax.z)) + new float3(thickness, thickness, thickness);
        if (min.x > max.x || min.y > max.y || min.z > max.z)
            return 0;

        float friction = System.Math.Clamp((a.Friction + b.Friction) * 0.5f, 0f, 1f);
        _moveCount = 0;
        PointsAgainst(a, pa, b, pb, min, max, thickness, friction, second: false, cornersHeld: bHeld);
        if (!bHeld)
            PointsAgainst(b, pb, a, pa, min, max, thickness, friction, second: true, cornersHeld: false);
        // Handed to the bodies, which move by the mean of everything they were asked this substep once every pair
        // is done (SoftBodySolver.ApplyContactMoves).
        for (int i = 0; i < _moveCount; i++)
        {
            ref var move = ref _moves[i];
            var body = move.Second ? b : a;
            var particles = move.Second ? pb : pa;
            ref var p = ref particles[move.Index];
            body.AddContactMove(move.Index, move.Pos - p.Pos, move.Prev - p.Prev);
        }
        LastContacts = _moveCount;
        return _moveCount;
    }

    private void PointsAgainst(SoftBodySolver pointBody, SoftBodySolver.Particle[] points, SoftBodySolver triangleBody,
        SoftBodySolver.Particle[] corners, in float3 min, in float3 max, float thickness, float friction, bool second, bool cornersHeld)
    {
        var triangles = triangleBody.Triangles;
        float pointInverseMass = pointBody.InverseParticleMass;
        if (triangles == null || triangles.Length < 3 || pointInverseMass <= 0f)
            return;

        // How far a point is looked for: touching from outside, or at most an edge deep inside.
        float depthLimit = thickness + triangleBody.AverageEdgeLength;

        // The triangles that reach into the box.
        _triangleCount = 0;
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            var a = corners[triangles[t]].Pos;
            var b = corners[triangles[t + 1]].Pos;
            var c = corners[triangles[t + 2]].Pos;
            var lo = new float3(MathF.Min(a.x, MathF.Min(b.x, c.x)), MathF.Min(a.y, MathF.Min(b.y, c.y)), MathF.Min(a.z, MathF.Min(b.z, c.z)));
            var hi = new float3(MathF.Max(a.x, MathF.Max(b.x, c.x)), MathF.Max(a.y, MathF.Max(b.y, c.y)), MathF.Max(a.z, MathF.Max(b.z, c.z)));
            if (lo.x - depthLimit > max.x || hi.x + depthLimit < min.x
                || lo.y - depthLimit > max.y || hi.y + depthLimit < min.y
                || lo.z - depthLimit > max.z || hi.z + depthLimit < min.z)
                continue;
            if (_triangleCount == _triangles.Length)
            {
                Array.Resize(ref _triangles, _triangles.Length * 2);
                Array.Resize(ref _triangleMin, _triangles.Length);
                Array.Resize(ref _triangleMax, _triangles.Length);
            }
            _triangleMin[_triangleCount] = lo;
            _triangleMax[_triangleCount] = hi;
            _triangles[_triangleCount++] = t;
        }
        if (_triangleCount == 0)
            return;

        // The free points inside it.
        _pointCount = 0;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i].Pinned)
                continue;
            var p = points[i].Pos;
            if (p.x < min.x || p.x > max.x || p.y < min.y || p.y > max.y || p.z < min.z || p.z > max.z)
                continue;
            if (_pointCount == _points.Length)
                Array.Resize(ref _points, _points.Length * 2);
            _points[_pointCount++] = i;
        }
        if (_pointCount == 0)
            return;

        _closed = triangleBody.IsClosed;
        _winding = triangleBody.Winding;
        float cornerInverseMass = cornersHeld ? 0f : triangleBody.InverseParticleMass;

        // A point looks as far as the contact distance plus how far it moved this step: it can only be deeper in than
        // it travelled, so one at rest on the other body takes nothing deeper than the skin for a contact. Every
        // triangle is listed in each cell within the deepest reach of it, so one cell is all any point looks in. The
        // other half of the pass catches a surface that moved onto a still point.
        bool grid = (long)_triangleCount * _pointCount > BruteForcePairs;
        if (grid)
            BuildGrid(min, max, MathF.Max(triangleBody.AverageEdgeLength, thickness * 2f), depthLimit);
        for (int k = 0; k < _pointCount; k++)
        {
            int index = _points[k];
            ref var p = ref points[index];
            var at = p.Pos;
            float reach = MathF.Min(thickness + (at - p.Prev).Length, depthLimit);
            _best = -1;
            _bestSq = reach * reach;
            if (!grid)
            {
                Nearest(at, corners, triangles, 0, _triangleCount, false);
            }
            else
            {
                int cell = CellOf(at);
                Nearest(at, corners, triangles, _cellStart[cell], _cellStart[cell + 1], true);
            }
            if (_best < 0)
                continue;
            int t = _best;
            if (Resolve(in p, pointInverseMass, corners, triangles[t], triangles[t + 1], triangles[t + 2], cornerInverseMass,
                    thickness, friction, depthLimit, out var pos, out var prev))
            {
                if (_moveCount == _moves.Length)
                    Array.Resize(ref _moves, _moves.Length * 2);
                _moves[_moveCount++] = new Move { Index = index, Second = second, Pos = pos, Prev = prev };
            }
        }
    }

    // The nearest of a run of candidates to a point, if nearer than the best so far.
    //
    // Two triangles meeting at an edge are equally near anything whose nearest point is on that edge, and which of them
    // answers decides inside from outside: above the top edge of a jelly cube, the side face says the point is behind it
    // and shoves it sideways off the cube. On a tie the face the point is most in front of wins; outside a corner that
    // is always a face it is truly in front of. -xlinka
    private int _best;
    private float _bestSq;
    private float _bestAlong;
    private float3 _bestPoint;
    private float _bestU0, _bestU1, _bestU2;
    private bool _closed;
    private float _winding;

    private const float TieTolerance = 1e-3f;

    private void Nearest(in float3 at, SoftBodySolver.Particle[] corners, int[] triangles, int from, int to, bool listed)
    {
        for (int s = from; s < to; s++)
        {
            int j = listed ? _cellItems[s] : s;
            float limit = _bestSq * (1f + TieTolerance) + 1e-12f;
            // Nothing in a triangle's box can be nearer than the box itself.
            var lo = _triangleMin[j];
            var hi = _triangleMax[j];
            float ex = MathF.Max(MathF.Max(lo.x - at.x, at.x - hi.x), 0f);
            float ey = MathF.Max(MathF.Max(lo.y - at.y, at.y - hi.y), 0f);
            float ez = MathF.Max(MathF.Max(lo.z - at.z, at.z - hi.z), 0f);
            if (ex * ex + ey * ey + ez * ez > limit)
                continue;
            int t = _triangles[j];
            var a = corners[triangles[t]].Pos;
            var b = corners[triangles[t + 1]].Pos;
            var c = corners[triangles[t + 2]].Pos;
            var q = ClosestOnTriangle(at, a, b, c, out float b0, out float b1, out float b2);
            float sq = float3.DistanceSquared(at, q);
            if (sq > limit)
                continue;
            var face = float3.Cross(b - a, c - a);
            float faceLength = face.Length;
            if (faceLength < 1e-12f)
                continue;
            float along = float3.Dot(at - q, face) / faceLength;
            along = _closed ? along * _winding : MathF.Abs(along);
            bool nearer = _best < 0 || sq < _bestSq * (1f - TieTolerance) - 1e-12f;
            if (!nearer && along <= _bestAlong)
                continue;
            _bestSq = MathF.Min(sq, _bestSq);
            _bestAlong = along;
            _best = t;
            _bestPoint = q;
            _bestU0 = b0;
            _bestU1 = b1;
            _bestU2 = b2;
        }
    }

    // Where one point goes against its nearest triangle, from the state both sides started this pass in.
    private bool Resolve(in SoftBodySolver.Particle p, float pointInverseMass, SoftBodySolver.Particle[] corners, int i0, int i1, int i2,
        float cornerInverseMass, float thickness, float friction, float depthLimit, out float3 pos, out float3 prev)
    {
        pos = p.Pos;
        prev = p.Prev;
        ref readonly var x0 = ref corners[i0];
        ref readonly var x1 = ref corners[i1];
        ref readonly var x2 = ref corners[i2];
        float b0 = _bestU0, b1 = _bestU1, b2 = _bestU2;
        var q = _bestPoint;

        var a = x0.Pos;
        var face = float3.Cross(x1.Pos - a, x2.Pos - a);
        float faceLength = face.Length;
        if (faceLength < 1e-12f)
            return false;
        face /= faceLength;
        if (_closed)
        {
            face *= _winding;
        }
        else
        {
            // An open sheet has two sides; the point belongs on the one it was on when the step began.
            var pa = x0.Prev;
            var before = float3.Cross(x1.Prev - pa, x2.Prev - pa);
            var q0 = pa * b0 + x1.Prev * b1 + x2.Prev * b2;
            if (float3.Dot(p.Prev - q0, before) < 0f)
                face = -face;
        }

        var d = p.Pos - q;
        float along = float3.Dot(d, face);
        float3 n;
        float overlap;
        if (along >= 0f)
        {
            float distance = d.Length;
            if (distance >= thickness)
                return false;
            n = distance > 1e-6f ? d / distance : face;
            overlap = thickness - distance;
        }
        else
        {
            // Behind the face: inside a closed body, or through a sheet this step. Deeper than the search reached is
            // some other part of the surface's business.
            if (along < -depthLimit)
                return false;
            n = face;
            overlap = thickness - along;
        }

        // The surface's give where it is touched is its corners' by how near the touch is to each; a pinned corner
        // gives nothing. The point's share of the way out is its give against that, half again: an exact share left a
        // resting pair touching by less than the skin, and both sides look out of it only after the step. -xlinka
        float surfaceInverseMass = ((x0.Pinned ? 0f : b0) + (x1.Pinned ? 0f : b1) + (x2.Pinned ? 0f : b2)) * cornerInverseMass;
        float share = MathF.Min(1f, 1.5f * pointInverseMass / (pointInverseMass + surfaceInverseMass));

        // The touch takes out the point's speed along the normal, as the floor does. Handing the other side its
        // momentum instead was tried two ways: sharing the speed by mass left every resting pair trading a few
        // millimetres a step with each other for good, too much to ever sleep, and each side taking the other's
        // speed swapped them back and forth. Sideways it is the floor's rule too: the slide is held while it is
        // under Friction times the overlap and trimmed by that much past it, and what speed is left keeps 1 -
        // Friction of itself. Without that last part a heap of jellies wedged against each other shivered at a
        // few centimetres a second for good and none of it ever slept. -xlinka
        var velocity = p.Pos - p.Prev;
        var sideways = velocity - n * float3.Dot(velocity, n);
        float slideLength = sideways.Length;
        if (friction > 0f && slideLength > 1e-7f)
        {
            float hold = friction * overlap;
            sideways -= sideways * (slideLength <= hold ? 1f : hold / slideLength);
            sideways *= 1f - friction;
        }

        pos = p.Pos + n * (overlap * share);
        prev = pos - sideways;
        return true;
    }

    // A grid over the box, each candidate triangle listed in every cell its bounds grown by reach touch.
    private void BuildGrid(in float3 min, in float3 max, float cell, float reach)
    {
        var size = max - min;
        for (int attempt = 0; ; attempt++)
        {
            _nx = System.Math.Max(1, (int)MathF.Ceiling(size.x / cell));
            _ny = System.Math.Max(1, (int)MathF.Ceiling(size.y / cell));
            _nz = System.Math.Max(1, (int)MathF.Ceiling(size.z / cell));
            if ((long)_nx * _ny * _nz <= MostCells || attempt > 8)
                break;
            cell *= 1.6f;
        }
        _gridMin = min;
        _cellInverse = 1f / cell;
        int cells = _nx * _ny * _nz;
        if (_cellStart.Length < cells + 1)
        {
            _cellStart = new int[cells + 1];
            _cellCursor = new int[cells + 1];
        }
        Array.Clear(_cellStart, 0, cells + 1);

        for (int pass = 0; pass < 2; pass++)
        {
            for (int j = 0; j < _triangleCount; j++)
            {
                var lo = _triangleMin[j] - new float3(reach, reach, reach);
                var hi = _triangleMax[j] + new float3(reach, reach, reach);
                int x0 = Index(lo.x - _gridMin.x, _nx), x1 = Index(hi.x - _gridMin.x, _nx);
                int y0 = Index(lo.y - _gridMin.y, _ny), y1 = Index(hi.y - _gridMin.y, _ny);
                int z0 = Index(lo.z - _gridMin.z, _nz), z1 = Index(hi.z - _gridMin.z, _nz);
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int cellIndex = (z * _ny + y) * _nx + x;
                            if (pass == 0)
                                _cellStart[cellIndex + 1]++;
                            else
                                _cellItems[_cellCursor[cellIndex]++] = j;
                        }
            }
            if (pass == 0)
            {
                for (int i = 0; i < cells; i++)
                    _cellStart[i + 1] += _cellStart[i];
                int total = _cellStart[cells];
                if (_cellItems.Length < total)
                    _cellItems = new int[System.Math.Max(total, _cellItems.Length * 2)];
                Array.Copy(_cellStart, _cellCursor, cells);
            }
        }
    }

    private int Index(float offset, int count) => System.Math.Clamp((int)(offset * _cellInverse), 0, count - 1);

    private int CellOf(in float3 p)
        => (Index(p.z - _gridMin.z, _nz) * _ny + Index(p.y - _gridMin.y, _ny)) * _nx + Index(p.x - _gridMin.x, _nx);

    // Closest point on a triangle and its barycentric weights, by region.
    internal static float3 ClosestOnTriangle(in float3 p, in float3 a, in float3 b, in float3 c, out float u, out float v, out float w)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = float3.Dot(ab, ap), d2 = float3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f)
        {
            u = 1f; v = 0f; w = 0f;
            return a;
        }
        var bp = p - b;
        float d3 = float3.Dot(ab, bp), d4 = float3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3)
        {
            u = 0f; v = 1f; w = 0f;
            return b;
        }
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float t = d1 / (d1 - d3);
            u = 1f - t; v = t; w = 0f;
            return a + ab * t;
        }
        var cp = p - c;
        float d5 = float3.Dot(ab, cp), d6 = float3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6)
        {
            u = 0f; v = 0f; w = 1f;
            return c;
        }
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float t = d2 / (d2 - d6);
            u = 1f - t; v = 0f; w = t;
            return a + ac * t;
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
        {
            float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            u = 0f; v = 1f - t; w = t;
            return b + (c - b) * t;
        }
        float denominator = 1f / (va + vb + vc);
        v = vb * denominator;
        w = vc * denominator;
        u = 1f - v - w;
        return a + ab * v + ac * w;
    }

    private static void Bounds(SoftBodySolver.Particle[] particles, out float3 min, out float3 max)
    {
        min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new float3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < particles.Length; i++)
        {
            var p = particles[i].Pos;
            if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z))
                continue;
            min.x = MathF.Min(min.x, p.x); min.y = MathF.Min(min.y, p.y); min.z = MathF.Min(min.z, p.z);
            max.x = MathF.Max(max.x, p.x); max.y = MathF.Max(max.y, p.y); max.z = MathF.Max(max.z, p.z);
        }
    }
}
