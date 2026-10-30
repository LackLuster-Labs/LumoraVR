// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Lumora.Core.Math;
using Lumora.Simulation.SoftBody;

namespace Lumora.Core.Components;

// One per world: every soft body that steps this frame, stepped together. The constraint half of a
// substep is arithmetic on a body's own particles, so the bodies solve side by side on the pool; the
// contact half asks the world's physics and stays on the world thread. Substep by substep each body
// still solves and then collides in its own order, so it moves exactly as it did stepped alone. The
// flush runs inside the same synchronous drain the bodies queued from, so nothing is a frame late. -xlinka
//
// SOFT BODIES MEET HERE TOO. Between the two halves of every substep, each pair of stepping bodies whose
// bounds come near each other is handed to SoftBodyContacts, which touches nothing outside the box where
// the two overlap. Pairs are found once a frame from the bounds each body took when it snapshotted its
// colliders, so a room of soft bodies apart from each other pays for a comparison of boxes and nothing
// else. A resting body leaves a note of where it lies: one stepping near it either pulls it into the frame's
// step or meets it held still (see PullInResting). Pulled in, it steps without being woken, keeps its
// particles' sleep counts, and drops back out as soon as nothing near it moves. -xlinka
internal sealed class SoftBodyStepBatch
{
    private static readonly ConditionalWeakTable<World, SoftBodyStepBatch> Batches = new();

    private readonly World _world;
    private readonly List<SquishyBody> _pending = new();
    private readonly List<SquishyBody> _resting = new();
    private readonly Action _flush;
    private readonly ParticleStepScheduler _scheduler;
    private readonly SoftBodyContacts _contacts = new();
    private readonly List<(int A, int B)> _pairs = new();
    private readonly List<SquishyBody> _held = new();
    private readonly List<(int A, int H)> _heldPairs = new();
    private SquishyBody[] _running = Array.Empty<SquishyBody>();
    private int _runningCount;
    private int _substep;
    private ulong _frame = ulong.MaxValue;
    private ulong _restingFrame = ulong.MaxValue;
    private bool _scheduled;

    private SoftBodyStepBatch(World world)
    {
        _world = world;
        _flush = Flush;
        _scheduler = new ParticleStepScheduler(SolveOne);
    }

    public static SoftBodyStepBatch For(World world)
        => Batches.GetValue(world, static w => new SoftBodyStepBatch(w));

    // Soft contacts resolved in the last frame's flush, and how long they took, for the inspector and tests.
    public int LastSoftContacts { get; private set; }

    public double LastSoftContactMilliseconds { get; private set; }

    public void Enqueue(SquishyBody body, ulong frame)
    {
        // A batch left over from an earlier frame means its drain never came back for it; settle it
        // rather than leave those bodies a frame behind.
        if (_scheduled && frame != _frame && _pending.Count > 0)
            Flush();
        _frame = frame;
        if (!_pending.Contains(body))
            _pending.Add(body);
        if (!_scheduled)
        {
            _scheduled = true;
            _world.RunSynchronously(_flush);
        }
    }

    // A body at rest this frame, in case something stepping comes near it.
    public void NoteResting(SquishyBody body, ulong frame)
    {
        if (frame != _restingFrame)
        {
            _resting.Clear();
            _restingFrame = frame;
        }
        _resting.Add(body);
    }

    public void Remove(SquishyBody body)
    {
        _pending.Remove(body);
        _resting.Remove(body);
    }

    private void Flush()
    {
        _scheduled = false;
        int count = _pending.Count;
        if (count == 0)
            return;
        if (_running.Length < count + _resting.Count)
            _running = new SquishyBody[System.Math.Max(count + _resting.Count, 8)];
        _pending.CopyTo(_running, 0);
        _pending.Clear();
        _runningCount = count;

        long start = Stopwatch.GetTimestamp();
        if (_restingFrame == _frame)
            PullInResting();
        _resting.Clear();
        count = _runningCount;
        FindPairs();

        int substeps = 0;
        for (int i = 0; i < count; i++)
            substeps = System.Math.Max(substeps, _running[i].BatchedSteps);
        // A world past Running is torn down on this thread a few steps later; no pool work into that.
        bool pooled = count > 1 && _world.State == World.WorldState.Running;
        int softContacts = 0;
        long softTicks = 0;
        try
        {
            for (int s = 0; s < substeps; s++)
            {
                _substep = s;
                if (pooled)
                    _scheduler.Run(count);
                else
                    for (int i = 0; i < count; i++)
                        SolveOne(i);
                if (_pairs.Count > 0 || _heldPairs.Count > 0)
                {
                    long contactStart = Stopwatch.GetTimestamp();
                    for (int p = 0; p < _pairs.Count; p++)
                    {
                        var a = _running[_pairs[p].A];
                        var b = _running[_pairs[p].B];
                        if (s < a.BatchedSteps && s < b.BatchedSteps && !a.IsDestroyed && !b.IsDestroyed)
                            softContacts += _contacts.Collide(a.Solver, b.Solver);
                    }
                    for (int p = 0; p < _heldPairs.Count; p++)
                    {
                        var a = _running[_heldPairs[p].A];
                        var h = _held[_heldPairs[p].H];
                        if (s < a.BatchedSteps && !a.IsDestroyed && !h.IsDestroyed)
                            softContacts += _contacts.Collide(a.Solver, h.Solver, bHeld: true);
                    }
                    for (int i = 0; i < count; i++)
                        _running[i].Solver.ApplyContactMoves();
                    softTicks += Stopwatch.GetTimestamp() - contactStart;
                }
                for (int i = 0; i < count; i++)
                    _running[i].ResolveBatchedStep(s);
            }
        }
        finally
        {
            LastSoftContacts = softContacts;
            LastSoftContactMilliseconds = softTicks * 1000.0 / Stopwatch.Frequency;
            // The budget is about the world thread's wall time, so each body is charged its share of
            // the batch rather than the CPU time it burned on a pool thread.
            double each = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency / count;
            for (int i = 0; i < count; i++)
            {
                _running[i].FinishBatchedStep(each);
                _running[i] = null!;
            }
            _runningCount = 0;
            _held.Clear();
            _heldPairs.Clear();
        }
    }

    // A resting body near one stepping now steps too when that one is moving fast enough to matter (a landing, a
    // push); and a resting one near that, so a pile wakes together. Near one that is only settling, it stays at rest
    // and is met held still, as a floor would be: pulled in, a pile where one jelly crept a millimetre a frame kept
    // every jelly in it stepping for good, and never slept. Every RecheckFrames frames it steps once all the same,
    // so one whose support crept out from under it finds out and falls instead of hanging in the air. -xlinka
    private const float LivelySpeed = 0.2f;
    private const ulong RecheckFrames = 15;

    private void PullInResting()
    {
        bool joined = true;
        while (joined && _resting.Count > 0)
        {
            joined = false;
            for (int r = _resting.Count - 1; r >= 0; r--)
            {
                var body = _resting[r];
                if (body.IsDestroyed || !body.TouchesSoftBodies)
                {
                    _resting.RemoveAt(r);
                    continue;
                }
                bool near = false, lively = false;
                for (int i = 0; i < _runningCount && !lively; i++)
                {
                    var other = _running[i];
                    if (!other.TouchesSoftBodies || !BoundsNear(body, other))
                        continue;
                    near = true;
                    lively = other.Solver.LastFastestSpeed > LivelySpeed;
                }
                if (!near)
                    continue;
                if (!lively && _frame - body.RestCheckedFrame < RecheckFrames)
                    continue;
                if (body.JoinStep())
                {
                    body.RestCheckedFrame = _frame;
                    _running[_runningCount++] = body;
                    joined = true;
                }
                _resting.RemoveAt(r);
            }
        }

        // What is left near something stepping is met held.
        for (int r = 0; r < _resting.Count; r++)
        {
            var body = _resting[r];
            if (body.IsDestroyed || !body.TouchesSoftBodies)
                continue;
            int held = -1;
            for (int i = 0; i < _runningCount; i++)
            {
                var other = _running[i];
                if (!other.TouchesSoftBodies || !BoundsNear(body, other))
                    continue;
                if (held < 0)
                {
                    held = _held.Count;
                    _held.Add(body);
                }
                _heldPairs.Add((i, held));
            }
        }
    }

    private void FindPairs()
    {
        _pairs.Clear();
        for (int i = 0; i < _runningCount; i++)
        {
            var a = _running[i];
            if (!a.TouchesSoftBodies)
                continue;
            for (int j = i + 1; j < _runningCount; j++)
            {
                var b = _running[j];
                if (b.TouchesSoftBodies && BoundsNear(a, b))
                    _pairs.Add((i, j));
            }
        }
    }

    // The two bodies' bounds from this frame's snapshot, grown by what either can travel in a frame of steps and
    // the contact distance.
    private static bool BoundsNear(SquishyBody a, SquishyBody b)
    {
        var sa = a.Solver;
        var sb = b.Solver;
        float reach = MathF.Max(sa.ParticleRadius, sb.ParticleRadius) + 2f * (sa.AverageEdgeLength + sb.AverageEdgeLength) + 0.05f;
        var ca = a.FrameBoundsCentre;
        var cb = b.FrameBoundsCentre;
        var ha = a.FrameBoundsSize * 0.5f;
        var hb = b.FrameBoundsSize * 0.5f;
        return MathF.Abs(ca.x - cb.x) <= ha.x + hb.x + reach
            && MathF.Abs(ca.y - cb.y) <= ha.y + hb.y + reach
            && MathF.Abs(ca.z - cb.z) <= ha.z + hb.z + reach;
    }

    private void SolveOne(int index) => _running[index].SolveBatchedStep(_substep);
}
