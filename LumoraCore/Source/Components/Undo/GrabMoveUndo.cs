// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Components.Interaction;

namespace Lumora.Core.Components;

// A carry captures its original parent before entering the hand, then its final pose after release
// receivers have run. Objects released together share a step, without leaving a history scope open
// across frames. A drop surface may finish placing later; its settled pose belongs to the same move.
internal sealed class GrabMoveUndo
{
    private const float PoseTolerance = 1e-5f;
    private static readonly ConditionalWeakTable<World, List<GrabMoveUndo>> Pending = new();

    private readonly Grabbable _grab;
    private readonly UndoManager _history;
    private readonly SlotTransformUndoBatch _pose;
    private SlotTransformUndoBatch? _atRelease;
    private int _releasedRevision;

    private GrabMoveUndo(Grabbable grab, UndoManager history, SlotTransformUndoBatch pose)
    {
        _grab = grab;
        _history = history;
        _pose = pose;
    }

    public static GrabMoveUndo? Begin(Grabbable grab, Grabber hand)
    {
        var world = grab.World;
        if (world?.LocalUser is not { } user || !ReferenceEquals(hand.OwningUser, user)
            || grab.DestroyOnRelease.Value
            || user.Root?.Slot?.GetComponentInChildren<UndoManager>() is not { IsDestroyed: false } history
            || SlotTransformUndoBatch.Begin(grab.Slot, UndoLocale.Move) is not { } pose)
            return null;
        return new GrabMoveUndo(grab, history, pose);
    }

    public void Released()
    {
        var world = _grab.World;
        if (world == null || _grab.IsDestroyed || _grab.IsGrabbed)
            return;
        _releasedRevision = _grab.GrabRevision;
        // Keep this endpoint if the object is picked up again before deferred receivers finish.
        _atRelease = _pose.Commit(PoseTolerance);
        var pending = Pending.GetValue(world, static _ => new List<GrabMoveUndo>());
        pending.Add(this);
        if (pending.Count == 1)
        {
            world.RunSynchronously(() =>
            {
                var released = pending.ToArray();
                pending.Clear();
                Finish(world, released);
            });
        }
    }

    private bool IsCurrent => !_grab.IsDestroyed && _grab.GrabRevision == _releasedRevision && !_grab.IsGrabbed;

    private static void Finish(World world, GrabMoveUndo[] released)
    {
        if (world.IsDestroyed)
            return;
        foreach (var move in released)
        {
            if (!move._history.IsDestroyed && move.IsCurrent && move._grab.IsBeingPlaced)
            {
                world.RunInUpdates(1, () => Finish(world, released));
                return;
            }
        }

        var records = new Dictionary<UndoManager, List<IUndoBatch>>();
        var graveyard = UndoGraveyard.Find(world);
        foreach (var move in released)
        {
            var slot = move._grab.Slot;
            if (move._history.IsDestroyed || move._grab.IsDestroyed || slot == null || slot.IsDestroyed
                || UndoTargets.ContainsWornAvatar(slot)
                || (graveyard != null && slot.IsDescendantOf(graveyard)))
                continue;
            var batch = move.IsCurrent ? move._pose.Commit(PoseTolerance) : move._atRelease;
            if (batch == null)
                continue;
            if (!records.TryGetValue(move._history, out var steps))
                records.Add(move._history, steps = new List<IUndoBatch>());
            steps.Add(batch);
        }
        foreach (var pair in records)
            pair.Key.Record(CompositeUndoBatch.Combine(UndoLocale.Move, pair.Value));
    }
}
