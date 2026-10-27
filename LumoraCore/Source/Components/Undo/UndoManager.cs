// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Localization;

namespace Lumora.Core.Components;

// Undo/Redo return false when the batch can no longer apply (targets destroyed) and the manager drops
// it; OnEvicted is the permanent-cleanup hook for batches that park resources (graveyard slots).
//
// LocalizedDescription has a default body so a step type written before the locale layer landed still
// compiles: its plain Description string converts straight to an untranslated LocaleText. Steps that
// care override it. -xlinka
public interface IUndoBatch
{
    string Description { get; }
    LocaleText LocalizedDescription => Description;
    bool Undo();
    bool Redo();
    void OnEvicted();
}

// Optional. A step that knows which world elements it would touch implements this so the history can
// be filtered when something is destroyed for real. A step that does not implement it is kept and
// relies on failing its own Undo/Redo instead, which is the floor every step has to hold anyway.
public interface IUndoTargetQuery
{
    bool ReferencesElement(IWorldElement element);
}

// Opened by UndoManager.BeginBatch. A struct so a gesture that opens a batch and records nothing
// costs no allocation at all.
public readonly struct UndoBatchScope : IDisposable
{
    private readonly UndoManager? _manager;

    internal UndoBatchScope(UndoManager? manager)
    {
        _manager = manager;
    }

    public void Dispose() => _manager?.EndBatch();
}

// plain local state, no sync: undo history is a per-client concern
[ComponentCategory("Users")]
public class UndoManager : Component
{
    public static UndoManager? ForLocalUser(World? world)
        => world?.LocalUser?.Root?.Slot?.GetComponentInChildren<UndoManager>();

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        // Everyone else's history is ruled out by where it sits before the walk that finds the local one.
        var localRoot = World.LocalUser?.Root;
        if (!World.IsFocused || localRoot == null || !ReferenceEquals(Slot.ActiveUserRoot, localRoot)
            || !ReferenceEquals(this, ForLocalUser(World)) || Helio.UI.TextInput.Focused != null)
            return;
        var input = Engine.Current?.InputInterface;
        var actions = input?.Actions?.Editing;
        if (actions == null) return;
        bool shift = input!.Keyboard?.IsKeyPressed(Input.Key.LeftShift) == true
            || input.Keyboard?.IsKeyPressed(Input.Key.RightShift) == true;
        // Inline dash fields suppress action bindings too, but Ctrl+Z still belongs to this history.
        if (input.Actions.TextFocusHeld && input.Keyboard is { } keyboard
            && (keyboard.IsKeyPressed(Input.Key.LeftControl) || keyboard.IsKeyPressed(Input.Key.RightControl))
            && !keyboard.IsKeyPressed(Input.Key.LeftAlt) && !keyboard.IsKeyPressed(Input.Key.RightAlt))
        {
            if (keyboard.IsKeyJustPressed(Input.Key.Y) || shift && keyboard.IsKeyJustPressed(Input.Key.Z)) Redo();
            else if (keyboard.IsKeyJustPressed(Input.Key.Z)) Undo();
            return;
        }
        if (actions.Redo.Pressed || actions.Undo.Pressed && shift) Redo();
        else if (actions.Undo.Pressed) Undo();
    }

    public const int DefaultMaxSteps = 100;

    // One list, not two. Steps [0.._performed) are applied, [_performed..) have been undone and are
    // waiting to be redone. Undo and Redo move the cursor over the SAME record rather than shuffling
    // objects between stacks, so undoing a step and redoing it grows the history by exactly nothing,
    // and a new action after an undo simply truncates the tail. -xlinka
    private readonly List<IUndoBatch> _steps = new();
    private int _performed;

    // Used as a stack. Records land in the innermost open scope; only closing the outermost one puts
    // anything in the history, so a gesture that spans three layers of helpers is still one step.
    private readonly List<Scope> _open = new();
    private readonly List<Scope> _scopePool = new();

    private int _maxSteps = DefaultMaxSteps;

    public int MaxSteps
    {
        get => _maxSteps;
        set
        {
            _maxSteps = global::System.Math.Max(1, value);
            TrimExcessSteps();
        }
    }

    public bool CanUndo => _performed > 0;
    public bool CanRedo => _performed < _steps.Count;
    public int StepCount => _steps.Count;
    public int OpenBatchDepth => _open.Count;

    public LocaleText NextUndoDescription => CanUndo ? _steps[_performed - 1].LocalizedDescription : LocaleText.Empty;
    public LocaleText NextRedoDescription => CanRedo ? _steps[_performed].LocalizedDescription : LocaleText.Empty;

    // so consecutive edits of the same target can merge. Inside an open batch this is the last record
    // of that batch, otherwise the last applied step.
    public IUndoBatch? CurrentBatch
    {
        get
        {
            for (int i = _open.Count - 1; i >= 0; i--)
            {
                var records = _open[i].Records;
                if (records != null && records.Count > 0)
                    return records[^1];
            }
            return _performed > 0 ? _steps[_performed - 1] : null;
        }
    }

    public void Record(IUndoBatch? batch)
    {
        if (batch == null)
            return;

        if (_open.Count > 0)
        {
            // A gesture lives inside one frame. A scope still open on a later frame is a caller that
            // never ended it, and letting the next unrelated action fall into it would glue two
            // separate things into one history entry. Costs one compare on the path that is already
            // checking the depth, which is why there is no per-frame sweep. -xlinka
            if (_open[0].OpenedAt != CurrentUpdateIndex)
                CloseLeakedBatches();
            else
            {
                _open[^1].Add(batch);
                return;
            }
        }

        // A new action invalidates the redo branch.
        TrimReversedActions();
        _steps.Add(batch);
        _performed = _steps.Count;
        TrimExcessSteps();
    }

    public UndoBatchScope BeginBatch(LocaleText description = default)
    {
        Scope scope;
        if (_scopePool.Count > 0)
        {
            scope = _scopePool[^1];
            _scopePool.RemoveAt(_scopePool.Count - 1);
        }
        else
        {
            scope = new Scope();
        }
        scope.Description = description;
        scope.OpenedAt = CurrentUpdateIndex;
        _open.Add(scope);
        return new UndoBatchScope(this);
    }

    // Closing the outermost scope commits; closing an inner one folds its records into its parent so
    // the whole nest lands as one step. Unbalanced calls log and do nothing.
    public void EndBatch()
    {
        if (_open.Count == 0)
        {
            Logging.Logger.Warn("UndoManager: EndBatch with no batch open; ignoring.");
            return;
        }

        var scope = _open[^1];
        _open.RemoveAt(_open.Count - 1);

        var records = scope.Records;
        if (records == null || records.Count == 0)
        {
            Release(scope);
            return;
        }

        if (_open.Count > 0)
        {
            var parent = _open[^1];
            for (int i = 0; i < records.Count; i++)
                parent.Add(records[i]);
            Release(scope);
            return;
        }

        // A lone record keeps its own description unless the gesture named itself, in which case the
        // gesture's name is the one the user recognises.
        IUndoBatch? combined = records.Count == 1 && scope.Description.IsEmpty
            ? records[0]
            : CompositeUndoBatch.Combine(scope.Description, records);
        Release(scope);
        Record(combined);
    }

    public bool Undo()
    {
        CloseLeakedBatches();
        // Skip over steps that can no longer apply.
        while (_performed > 0)
        {
            var batch = _steps[_performed - 1];
            if (SafeApply(batch, undo: true))
            {
                _performed--;
                return true;
            }
            _performed--;
            _steps.RemoveAt(_performed);
            Discard(batch, "undo");
        }
        return false;
    }

    public bool Redo()
    {
        CloseLeakedBatches();
        while (_performed < _steps.Count)
        {
            var batch = _steps[_performed];
            if (SafeApply(batch, undo: false))
            {
                _performed++;
                return true;
            }
            _steps.RemoveAt(_performed);
            Discard(batch, "redo");
        }
        return false;
    }

    // Drop the undone tail. Nothing there has been re-applied, so evicting frees whatever those steps
    // were holding onto (parked slots included).
    public void TrimReversedActions()
    {
        while (_steps.Count > _performed)
        {
            var stale = _steps[^1];
            _steps.RemoveAt(_steps.Count - 1);
            SafeEvict(stale);
        }
    }

    public void TrimExcessSteps()
    {
        while (_steps.Count > _maxSteps)
        {
            var oldest = _steps[0];
            _steps.RemoveAt(0);
            if (_performed > 0)
                _performed--;
            SafeEvict(oldest);
        }
    }

    // Returns how many steps went. Evicting a still-applied destroy step makes that destroy permanent,
    // which is exactly what "this history entry can never be reached again" means.
    public int ClearUndoPoints(Predicate<IUndoBatch> filter)
    {
        if (filter == null)
            return 0;

        int removed = 0;
        for (int i = _steps.Count - 1; i >= 0; i--)
        {
            var batch = _steps[i];
            bool match;
            try
            {
                match = filter(batch);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"UndoManager: undo-point filter threw on '{batch.Description}': {ex.Message}");
                continue;
            }
            if (!match)
                continue;

            _steps.RemoveAt(i);
            if (i < _performed)
                _performed--;
            SafeEvict(batch);
            removed++;
        }
        return removed;
    }

    // For when something is destroyed for real rather than parked: every step that admits to touching
    // it leaves, so no later undo can put a piece of it back into a hierarchy that is gone.
    public int ClearUndoPointsFor(IWorldElement? element)
    {
        if (element == null)
            return 0;
        return ClearUndoPoints(batch => batch is IUndoTargetQuery query && Touches(query, element));
    }

    public void Clear()
    {
        CloseLeakedBatches();
        for (int i = 0; i < _steps.Count; i++)
            SafeEvict(_steps[i]);
        _steps.Clear();
        _performed = 0;
    }

    public override void OnDestroy()
    {
        // Whatever this user still had parked is theirs alone; nobody can reach it once the manager is
        // gone, so it dies here rather than sitting in the graveyard for the rest of the session.
        var world = World;
        _open.Clear();
        _scopePool.Clear();
        for (int i = 0; i < _steps.Count; i++)
            SafeEvict(_steps[i]);
        _steps.Clear();
        _performed = 0;
        UndoGraveyard.DisposeIfEmpty(world);
        base.OnDestroy();
    }

    private static bool Touches(IUndoTargetQuery query, IWorldElement element)
    {
        try
        {
            return query.ReferencesElement(element);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"UndoManager: target query threw: {ex.Message}");
            return false;
        }
    }

    private ulong CurrentUpdateIndex => World?.Time?.UpdateIndex ?? 0UL;

    private void CloseLeakedBatches()
    {
        if (_open.Count == 0)
            return;
        Logging.Logger.Warn($"UndoManager: {_open.Count} undo batch(es) were opened and never ended; closing them.");
        while (_open.Count > 0)
            EndBatch();
    }

    private void Release(Scope scope)
    {
        scope.Reset();
        if (_scopePool.Count < 8)
            _scopePool.Add(scope);
    }

    private static void Discard(IUndoBatch batch, string direction)
    {
        Logging.Logger.Log($"UndoManager: dropping '{batch.Description}' - it can no longer {direction} (targets gone).");
        SafeEvict(batch);
    }

    // A record that throws is a record that can no longer apply, so it is dropped exactly like one
    // that reported false. Anything else takes the press handler - and whatever called it - down with
    // it, and a half-applied record is not worth a crash. -xlinka
    private static bool SafeApply(IUndoBatch batch, bool undo)
    {
        try
        {
            return undo ? batch.Undo() : batch.Redo();
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"UndoManager: '{batch.Description}' threw during {(undo ? "undo" : "redo")}: {ex.Message}");
            return false;
        }
    }

    private static void SafeEvict(IUndoBatch batch)
    {
        try
        {
            batch.OnEvicted();
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"UndoManager: batch eviction failed: {ex.Message}");
        }
    }

    private sealed class Scope
    {
        public LocaleText Description;
        public ulong OpenedAt;
        public List<IUndoBatch>? Records;

        public void Add(IUndoBatch batch)
        {
            Records ??= new List<IUndoBatch>();
            Records.Add(batch);
        }

        public void Reset()
        {
            Description = default;
            Records?.Clear();
        }
    }
}
