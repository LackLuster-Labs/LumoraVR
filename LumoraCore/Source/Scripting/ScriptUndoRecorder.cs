// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Localization;
using Lumora.Core.Networking.Sync;
using Lumora.Script;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Scripting;

// What one script's undo nodes record in the call running now, and where each batch goes as it closes.
//
// A batch opens with Begin Undo Batch and closes with End Undo Batch; one opened inside an open batch joins
// it. A step recorded outside any batch is a batch of its own. Whatever is still open when the call ends
// closes then, so a call that faults part way still closes what it began, and a step whose value never
// changed is left out. A closed batch belongs to whoever set the call off, else to the script's author, and
// goes to that person's own history: here when they are at this machine, else by way of the host's relay
// to theirs. A world script's batch that nobody set off belongs to no one, and nothing is kept.
//
// Every step and batch is kept. A batch past WarnStepsPerBatch steps, or a script closing more than
// WarnBatchesPerSecond batches in a second, is warned about. -xlinka
internal sealed class ScriptUndoRecorder
{
    public const int WarnStepsPerBatch = 64;
    public const int WarnBatchesPerSecond = 10;

    public static readonly LocaleText ScriptChange = LocaleText.Keyed("Loom.Undo.ScriptChange", "Script Change");

    private readonly ScriptContext _ctx;
    private readonly List<ScriptUndoBatch> _lone = new();
    private ScriptUndoBatch? _open;
    private int _depth;
    private double _window = double.NegativeInfinity;
    private int _inWindow;

    public ScriptUndoRecorder(ScriptContext ctx)
    {
        _ctx = ctx;
    }

    public void Begin(string? description)
    {
        if (_depth++ == 0)
            _open = new ScriptUndoBatch(_ctx, Describe(description));
    }

    public void End()
    {
        if (_depth == 0)
        {
            _ctx.Fail("no undo batch is open");
            return;
        }
        if (--_depth > 0)
            return;
        var batch = _open;
        _open = null;
        if (batch != null)
            Close(batch);
    }

    public void Field<T>(IField<T>? field)
    {
        if (field == null || field.IsDestroyed)
        {
            _ctx.Fail(field == null ? "no field" : "the field is gone");
            return;
        }
        if (ScriptWrites.Member(field) is not { } element)
            throw new UnauthorizedAccessException("scripts record only fields they could change");
        ScriptWrites.Check(_ctx, element);
        Record(new ScriptUndoFieldStep<T>(field));
    }

    // A reference with rules of its own for who may set it is one no script sets, so none records it: a
    // slot's parent is the exception, and goes back through Set Parent's rules.
    public void Reference(ISyncRef? reference)
    {
        if (reference is not SyncElement element || element.IsDestroyed)
        {
            _ctx.Fail(reference == null ? "no reference" : reference is SyncElement ? "the reference is gone" : "scripts cannot record that reference");
            return;
        }
        ScriptWrites.Check(_ctx, element);
        if (ScriptUndoReferenceStep.ChildOf(element) == null && (element.HasInboundRules || reference is ISyncDelegate))
            throw new UnauthorizedAccessException("scripts do not record a drive, a hook, an action or a reference with rules of its own");
        Record(new ScriptUndoReferenceStep(reference, element));
    }

    public void Transform(Slot? slot)
    {
        if (!ScriptReads.Reached(_ctx, slot))
            return;
        ScriptWrites.Check(_ctx, slot.LocalPosition);
        ScriptWrites.Check(_ctx, slot.LocalRotation);
        ScriptWrites.Check(_ctx, slot.LocalScale);
        Record(new ScriptUndoTransformStep(slot));
    }

    public void Spawn(Slot? slot)
    {
        if (!ScriptReads.Reached(_ctx, slot) || IsParked(slot!))
            return;
        ScriptWrites.CheckPark(_ctx, slot);
        Record(ScriptUndoSlotStep.Spawned(slot));
    }

    public void Destroy(Slot? slot)
    {
        if (!ScriptReads.Reached(_ctx, slot) || IsParked(slot!))
            return;
        ScriptWrites.CheckPark(_ctx, slot);
        Record(ScriptUndoSlotStep.Destroyed(_ctx, slot));
    }

    // Already out of the world, waiting on someone's undo: there is nothing more to put out.
    private bool IsParked(Slot slot)
        => slot.Parent is { } parent && (ReferenceEquals(parent, UndoGraveyard.Find(_ctx.World)) || (parent.IsLocalElement && !parent.ActiveSelf.Value
            && parent.Name.Value == ScriptUndoSlotStep.LocalPenName && ReferenceEquals(parent.Parent, _ctx.Self)));

    // Anything the last call left open, before this one begins, and everything this one opened, as it ends.
    public void CloseAll()
    {
        if (_open != null)
        {
            var batch = _open;
            _open = null;
            _depth = 0;
            SafeClose(batch);
        }
        if (_lone.Count == 0)
            return;
        var lone = _lone.ToArray();
        _lone.Clear();
        foreach (var batch in lone)
            SafeClose(batch);
    }

    private void Record(ScriptUndoStep step)
    {
        var batch = _open;
        if (batch == null)
        {
            foreach (var alone in _lone)
            {
                if (alone.Holds(step))
                {
                    step.Evict();
                    return;
                }
            }
            batch = new ScriptUndoBatch(_ctx, step.Describe());
            _lone.Add(batch);
        }
        else if (batch.Holds(step))
        {
            step.Evict();
            return;
        }
        batch.Add(step);
        if (batch.Count == WarnStepsPerBatch + 1)
            _ctx.Warn(ScriptWarning.Undo, $"more than {WarnStepsPerBatch} steps in one undo batch");
    }

    private void SafeClose(ScriptUndoBatch batch)
    {
        try
        {
            Close(batch);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Loom [{_ctx.Principal}]: an undo batch could not be closed: {ex.Message}");
            batch.Evict();
        }
    }

    private void Close(ScriptUndoBatch batch)
    {
        if (!batch.Close())
        {
            batch.Evict();
            return;
        }
        var world = _ctx.World;
        var owner = _ctx.Cause ?? _ctx.Principal.Author;
        if (owner == null || owner.IsDestroyed || !ReferenceEquals(owner.World, world))
        {
            batch.Evict();
            return;
        }
        bool here = ReferenceEquals(owner, world.LocalUser);
        if (!here && (_ctx.IsLocal || !world.IsAuthority))
        {
            batch.Evict();
            return;
        }
        CountBatch();
        if (here)
        {
            if (ScriptUndoHub.LocalHistory(world) is not { } history)
            {
                batch.Evict();
                return;
            }
            history.Record(new ScriptUndoLocalBatch(batch));
        }
        else
        {
            world.Scripts.Undo.Offer(owner, batch);
        }
    }

    private void CountBatch()
    {
        double now = _ctx.Now;
        if (now - _window >= 1.0)
        {
            _window = now;
            _inWindow = 0;
        }
        if (++_inWindow == WarnBatchesPerSecond + 1)
            _ctx.Warn(ScriptWarning.Undo, $"more than {WarnBatchesPerSecond} undo batches in a second");
    }

    private static string Describe(string? description)
        => string.IsNullOrWhiteSpace(description) ? ScriptChange.Resolve() : description;
}
