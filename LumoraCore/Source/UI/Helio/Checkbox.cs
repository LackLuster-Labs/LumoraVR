// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;

namespace Helio.UI;

[ComponentCategory("UI/Helio/Interaction")]
public sealed class Checkbox : InteractionElement
{
    public readonly Sync<bool> IsChecked;
    // Drives the tick's active state. A real sync member: the target replicates and saves,
    // so a loaded or remote checkbox already knows what it drives. -xlinka
    public readonly FieldDrive<bool> CheckVisual = new();

    // A field the box shows and flips instead of its own IsChecked: a setting somewhere else, ticked from here.
    public readonly SyncRef<IField<bool>> StateField = new();

    public bool Checked => StateField.Target is { } field ? field.Value : IsChecked.Value;

    // Duplicable change action - see Button.Pressed.
    public readonly SyncDelegate<Action<Checkbox, bool>> ChangeAction;

    // Flips when the press lets go instead of when it lands. See Button.ActOnRelease.
    public readonly Sync<bool> ActOnRelease;

    public event Action<Checkbox, bool>? ValueChanged;

    public Checkbox()
    {
        IsChecked = new Sync<bool>(this, false);
        ChangeAction = new SyncDelegate<Action<Checkbox, bool>>(this);
        ActOnRelease = new Sync<bool>(this, false);
    }

    protected override bool ActsOnPress => !ActOnRelease.Value && !InReleaseList();
    protected override bool IsClickControl => true;

    // A drag that starts here scrolls the list it sits in, the same as from a button.
    public override bool PassDragToParent(in float2 dragDelta)
        => System.Math.Abs(dragDelta.x) > DragPassThreshold || System.Math.Abs(dragDelta.y) > DragPassThreshold;

    public void SetAction(Action<Checkbox, bool>? action)
    {
        if (action == null)
            return;
        if (action.Target is IWorldElement)
            ChangeAction.Target = action;
        else
            ValueChanged += action;
    }

    public override void OnStart()
    {
        base.OnStart();
        // Default the tick drive from the built child structure, but ONLY when nothing set it. A link
        // that came from a save or from a peer already names its target and must not be stomped; this
        // is just the fallback for a checkbox built by hand or duplicated without a stored link.
        if (CheckVisual.ShouldApplyDefault)
        {
            var check = Slot?.FindChild("Box", recursive: false)?.FindChild("Check", recursive: false);
            if (check != null)
                CheckVisual.DriveTarget(check.ActiveSelf);
        }
        UpdateCheckVisual();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        UpdateCheckVisual();
    }

    public void SetCheckVisual(IField<bool> target)
    {
        CheckVisual.DriveTarget(target);
        UpdateCheckVisual();
    }

    protected override void OnSubmit(in UIInteractionContext context)
    {
        if (StateField.Target is { } field)
            field.Value = !field.Value;
        else
            IsChecked.Value = !IsChecked.Value;
        UpdateCheckVisual();
        ValueChanged?.Invoke(this, Checked);
        ChangeAction.Target?.Invoke(this, Checked);
    }

    // The field somewhere else can change under the box; the tick follows it.
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (StateField.Target != null)
            UpdateCheckVisual();
    }

    private void UpdateCheckVisual()
    {
        if (CheckVisual.IsLinkValid && CheckVisual.Target!.Value != Checked)
        {
            CheckVisual.SetValue(Checked);
        }
    }
}
