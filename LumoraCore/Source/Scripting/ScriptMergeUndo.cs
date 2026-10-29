// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Scripting;

// One group taken into another's graph, kept in the undo step of the edit that needed their nodes together.
// Its nodes move over and stay where they stood, depth and turn and all; a node a hand holds goes there when
// it is let go of. The emptied group is parked for the step, and its script stops
// with it. Undo takes the nodes back to where they sat and brings the group back; redo takes in whatever the
// group holds by then.
//
// Nothing moves unless every move may be made, since a refusal halfway would leave a graph split. A group
// someone has wired across since is not taken apart again: that undo leaves the two together. A property
// node that comes into a shader graph under a name the graph already uses is renamed in the same step, and
// gets its name back with the undo. -xlinka
internal sealed class ScriptMergeUndo : IUndoBatch, IUndoTargetQuery
{
    private sealed class Move
    {
        public ScriptGraphNode Node = null!;
        public bool Held;
        public float3 Position;
        public floatQ Rotation;
        public float3 Scale;
    }

    private const string PutAwayRefused = "those groups cannot join: you may not put the other group away";

    private readonly ScriptGraphSpace _into;
    private readonly ScriptGraphSpace _from;
    private readonly Slot _group;
    private readonly List<Move> _moves = new();
    private List<IUndoBatch> _renames = new();
    private SlotExistenceUndoBatch? _parked;

    private ScriptMergeUndo(ScriptGraphSpace into, ScriptGraphSpace from, Slot group)
    {
        _into = into;
        _from = from;
        _group = group;
    }

    public string Description => ScriptGraphEditing.JoinStep.Resolve();

    public LocaleText LocalizedDescription => ScriptGraphEditing.JoinStep;

    // Takes the nodes of from into into now, and parks the group from belonged to. Null, with the reason, when
    // anything it would move may not be moved; nothing has changed then.
    public static ScriptMergeUndo? Take(ScriptGraphSpace into, ScriptGraphSpace from, out string? error)
    {
        var world = into.World;
        var group = from.Slot?.Parent;
        if (world == null || group == null || group.IsRootSlot || into.Slot == null)
        {
            error = "those groups cannot join";
            return null;
        }
        var merge = new ScriptMergeUndo(into, from, group);
        error = merge.Refusal(from.Nodes);
        if (error != null)
            return null;
        try
        {
            if (UndoGraveyard.Acquire(world) == null)
            {
                error = PutAwayRefused;
                return null;
            }
        }
        catch (UnauthorizedAccessException)
        {
            error = PutAwayRefused;
            return null;
        }

        merge.TakeIn();
        merge._parked = SlotExistenceUndoBatch.Destroy(world, new[] { group });
        if (merge._parked == null)
        {
            merge.TakeBack();
            error = PutAwayRefused;
            return null;
        }
        return merge;
    }

    public bool Undo()
    {
        if (_parked == null || _into.IsDestroyed || _from.IsDestroyed || _group.IsDestroyed || Tangled())
            return false;
        var back = new List<ScriptGraphNode>();
        foreach (var move in _moves)
        {
            if (!move.Node.IsDestroyed && ReferenceEquals(move.Node.Space, _into))
                back.Add(move.Node);
        }
        if (Refusal(back) != null || !_parked.Undo())
            return false;
        TakeBack();
        return true;
    }

    public bool Redo()
    {
        if (_parked == null || _into.IsDestroyed || _from.IsDestroyed || _group.IsDestroyed || Refusal(_from.Nodes) != null)
            return false;
        TakeIn();
        return _parked.Redo();
    }

    public void OnEvicted() => _parked?.OnEvicted();

    // With no history to keep it in, the parked group goes for good, where whoever joined them may remove it.
    public void Forget()
    {
        try
        {
            _parked?.OnEvicted();
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public bool ReferencesElement(IWorldElement element)
    {
        if (element is not Slot slot)
            return false;
        if (UndoTargets.Touches(_group, slot) || UndoTargets.Touches(_into.Slot, slot))
            return true;
        foreach (var move in _moves)
        {
            if (UndoTargets.Touches(move.Node.Slot, slot))
                return true;
        }
        return false;
    }

    private void TakeIn()
    {
        _moves.Clear();
        var into = _into.Slot;
        foreach (var node in new List<ScriptGraphNode>(_from.Nodes))
        {
            var slot = node.Slot;
            var grab = HeldGrab(node);
            _moves.Add(new Move
            {
                Node = node,
                Held = grab != null,
                Position = slot.LocalPosition.Value,
                Rotation = slot.LocalRotation.Value,
                Scale = slot.LocalScale.Value,
            });
            if (grab != null)
            {
                grab.LastParentRef.Target = into;
                _into.NoteHeld(node);
                continue;
            }
            slot.SetParent(into, preserveGlobalTransform: true);
        }
        var came = new List<ScriptGraphNode>(_moves.Count);
        foreach (var move in _moves)
            came.Add(move.Node);
        _renames = ScriptGraphEditing.EnsureUniquePropertyNames(_into, came);
    }

    private void TakeBack()
    {
        for (int i = _renames.Count - 1; i >= 0; i--)
        {
            try
            {
                _renames[i].Undo();
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        _renames.Clear();
        var from = _from.Slot;
        for (int i = _moves.Count - 1; i >= 0; i--)
        {
            var move = _moves[i];
            var node = move.Node;
            var slot = node.Slot;
            if (node.IsDestroyed || slot == null || !ReferenceEquals(node.Space, _into))
                continue;
            if (HeldGrab(node) is { } grab)
            {
                grab.LastParentRef.Target = from;
                _from.NoteHeld(node);
                continue;
            }
            // In a hand when it came over, it never had a place of its own in the old graph.
            if (move.Held)
            {
                slot.SetParent(from, preserveGlobalTransform: true);
                continue;
            }
            slot.SetParent(from, preserveGlobalTransform: false);
            slot.LocalPosition.Value = move.Position;
            slot.LocalRotation.Value = move.Rotation;
            slot.LocalScale.Value = move.Scale;
        }
    }

    // Why whoever acts may not move all of these nodes and the group they leave, or null when they may.
    private string? Refusal(IEnumerable<ScriptGraphNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (!MayMove(node))
                return $"those groups cannot join: you may not move {node.Title}";
        }
        var group = _group;
        if (!group.ParentSlotRef.IsWriteAllowed() || !group.ActiveSelf.IsWriteAllowed() || !group.LocalPosition.IsWriteAllowed()
            || !group.LocalRotation.IsWriteAllowed() || !group.LocalScale.IsWriteAllowed())
            return PutAwayRefused;
        return null;
    }

    // The slot's name is asked too: the parent and pose of a node nobody holds pass this machine's gate for
    // anyone, as a grab would, but the host takes a move from someone not holding it only when they may edit
    // the slot, and the name is where that question is asked plainly.
    private static bool MayMove(ScriptGraphNode node)
    {
        var slot = node.Slot;
        if (slot == null || slot.IsDestroyed)
            return false;
        if (HeldGrab(node) is { } grab)
            return grab.LastParentRef.IsWriteAllowed();
        return slot.Name.IsWriteAllowed() && slot.ParentSlotRef.IsWriteAllowed() && slot.LocalPosition.IsWriteAllowed()
            && slot.LocalRotation.IsWriteAllowed() && slot.LocalScale.IsWriteAllowed();
    }

    private static Grabbable? HeldGrab(ScriptGraphNode node)
        => node.Slot?.GetComponent<Grabbable>() is { IsGrabbed: true } grab ? grab : null;

    // A wire between a node that came over and one that was already there: someone wired across after the
    // join, and taking the nodes back would cut it.
    private bool Tangled()
    {
        var came = new HashSet<ScriptGraphNode>();
        foreach (var move in _moves)
        {
            if (!move.Node.IsDestroyed)
                came.Add(move.Node);
        }
        foreach (var node in _into.Nodes)
        {
            bool over = came.Contains(node);
            foreach (var wire in node.Inputs)
            {
                if (Across(wire.Node.Target, over, came))
                    return true;
            }
            foreach (var wire in node.Exits)
            {
                if (Across(wire.Node.Target, over, came))
                    return true;
            }
            foreach (var value in node.Values)
            {
                if (value.Reference.Target is ScriptGraphNode named && Across(named, over, came))
                    return true;
            }
        }
        return false;
    }

    private static bool Across(ScriptGraphNode? other, bool over, HashSet<ScriptGraphNode> came)
        => other is { IsDestroyed: false } && came.Contains(other) != over;
}
