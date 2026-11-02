// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Localization;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components;

public sealed class FieldEditUndoBatch : IUndoBatch
{
    private readonly IField _field;
    private readonly object? _before;
    private object? _after;
    private readonly bool _mergeable;

    public LocaleText LocalizedDescription { get; }

    public string Description => LocalizedDescription.Resolve();

    public FieldEditUndoBatch(IField field, object? before, object? after, LocaleText description, bool mergeable = true)
    {
        _field = field;
        _before = before;
        _after = after;
        LocalizedDescription = description;
        _mergeable = mergeable;
    }

    // consecutive edits of the same field merge into one step (slider drags, typing); a step recorded as
    // whole (a dropped value card) neither joins the one before it nor takes the ones after
    public bool TryMerge(IField field, object? after)
    {
        if (!_mergeable || !ReferenceEquals(field, _field))
            return false;
        _after = after;
        return true;
    }

    public bool Undo()
    {
        if (!IsReplayable())
            return false;
        // BoxedValue is typed non-null, but a reference-type field legitimately restores to null. -xlinka
        _field.BoxedValue = _before!;
        return true;
    }

    public bool Redo()
    {
        if (!IsReplayable())
            return false;
        _field.BoxedValue = _after!;
        return true;
    }

    // A config-locked member would swallow the write and report success, which would walk the cursor past
    // a step that did nothing. Refusing here keeps the history honest rather than silently lossy. -xlinka
    private bool IsReplayable()
        => _field is SyncElement { IsDestroyed: false, IsConfigLocked: false };

    public void OnEvicted() { }
}

public static class InspectorUndo
{
    // no-op without a manager or batch
    public static void Record(Worker context, IUndoBatch? batch)
        => Record(context?.World, batch);

    // overload for a caller that has a world but no worker of its own (static import paths)
    public static void Record(World? world, IUndoBatch? batch)
    {
        if (batch == null)
            return;
        world?.LocalUser?.Root?.Slot?.GetComponentInChildren<UndoManager>()?.Record(batch);
    }

    public static void RecordEdit(Worker context, IField field, object? before, object? after)
    {
        var world = context?.World;
        var manager = world?.LocalUser?.Root?.Slot?.GetComponentInChildren<UndoManager>();
        if (manager == null)
            return;

        if (manager.CurrentBatch is FieldEditUndoBatch merge && merge.TryMerge(field, after))
            return;
        string name = (field as ISyncMember)?.Name ?? "field";
        manager.Record(new FieldEditUndoBatch(field, before, after, UndoLocale.EditField(name)));
    }

    // One edit as one step of its own, never merged with the field's typing or slider steps around it.
    public static void RecordEditStep(Worker context, IField field, object? before, object? after)
    {
        string name = (field as ISyncMember)?.Name ?? "field";
        Record(context, new FieldEditUndoBatch(field, before, after, UndoLocale.EditField(name), mergeable: false));
    }
}
