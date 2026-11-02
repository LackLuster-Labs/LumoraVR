// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Math;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;

namespace Lumora.Core.Components;

// dotted path addresses one leaf inside the field's value ("x", "r"; "" = the whole value).
// editors read/write the WHOLE boxed value through a StructMemberAccessor, so one non-generic
// component edits any value type.
public abstract class MemberEditor : Component, IProxyReceiver
{
    public readonly SyncRef<IWorldElement> TargetMember;
    public readonly Sync<string> MemberPath;

    private IChangeable? _watched;

    public MemberEditor()
    {
        TargetMember = new SyncRef<IWorldElement>(this);
        MemberPath = new Sync<string>(this, "");
    }

    protected IField? Field => TargetMember.Target as IField;

    protected StructMemberAccessor? Accessor
    {
        get
        {
            var field = Field;
            return field == null ? null : StructMemberAccessor.Get(field.ValueType, MemberPath.Value ?? "");
        }
    }

    protected Type? LeafType => Accessor?.LeafType;

    public void Setup(IField field, string path, UIBuilder ui)
    {
        TargetMember.Target = field;
        MemberPath.Value = path ?? "";
        BuildUI(ui);
        WatchField();
        RefreshDisplay();
    }

    // Copy the actual leaf value, including its precision, rather than the rounded display text. -xlinka
    public virtual IGrabbable? CopyValue(TextInput input, in float3 spawnPoint)
    {
        if (World == null || Field is not { IsDestroyed: false } || LeafType is not { } type)
            return null;
        return ValueProxy.Spawn(World, type, GetMemberValue(), spawnPoint);
    }

    protected abstract void BuildUI(UIBuilder ui);

    // also called on remote changes
    protected virtual void RefreshDisplay() { }

    public override void OnStart()
    {
        base.OnStart();
        WatchField();
        KeepDisplay();
    }

    // What the row shows follows its field on the authority, which keeps every panel's rows, the panel in
    // somebody's hand included; every other machine has it from there. Every peer used to write the same text
    // on every change, so a panel picked up mid-change had the others' writes refused at the host. -xlinka
    private void KeepDisplay()
    {
        if (World?.IsAuthority != true)
            return;
        using var keep = World.DataModelPermissions?.EnterUiUpkeep(this);
        RefreshDisplay();
    }

    public override void OnDestroy()
    {
        UnwatchField();
        base.OnDestroy();
    }

    private void WatchField()
    {
        UnwatchField();
        if (TargetMember.Target is IChangeable changeable)
        {
            _watched = changeable;
            changeable.Changed += OnFieldChanged;
        }
    }

    private void UnwatchField()
    {
        if (_watched != null)
        {
            _watched.Changed -= OnFieldChanged;
            _watched = null;
        }
    }

    private void OnFieldChanged(IChangeable element)
    {
        if (!IsDestroyed)
            KeepDisplay();
    }

    // Whether this editor must refuse writes to the EDITED MEMBER (not this editor component's own
    // drive state, which is what ComponentBase.IsDriven answers). A driven value is DERIVED: the
    // editor shows it live but must not author it, or the write lands for one frame and the driver
    // stamps over it - an edit box that silently rejects everything you type.
    // A hooked link is the passthrough escape: a hook INTERCEPTS the write and decides what to do
    // with it rather than rejecting it, so the editor stays live and lets the hook arbitrate. Same
    // rule as the datamodel's IsBlockedByDrive. -xlinka
    //
    // A member inside an ImmutableComponent subtree is read-only for the same reason it is not in the
    // tree: the person is not the one who authors it. Nothing else changes - the row still shows the
    // live value, and whatever composes it keeps writing.
    //
    // A config-locked member (the world's settings and permission config) is read-only for everybody,
    // host included: the datamodel refuses the write anyway, and a row that swallows every edit in
    // silence is worse than a row that says up front that it will not take one. The Session dashboard is
    // where those are edited. -xlinka
    //
    // Part of somebody else who has not said yes to the person writing is read-only the same way: the gate refuses
    // it, and an editor that sends the write anyway only looks broken. -xlinka
    protected bool IsReadOnly
        => TargetMember.Target is ILinkable { IsDestroyed: false, IsDriven: true, IsHooked: false }
           || ImmutableComponent.IsProtected(TargetMember.Target)
           || TargetMember.Target is SyncElement { IsConfigLocked: true }
           || Networking.Session.SessionEditConsent.Refuses(ObjectBoundary.SlotOf(TargetMember.Target), World?.LocalUser);

    protected object? GetMemberValue()
    {
        var field = Field;
        if (field == null)
            return null;
        try { return Accessor?.GetValue(field.BoxedValue); }
        catch { return null; }
    }

    protected void SetMemberValue(object? leaf)
    {
        var field = Field;
        var accessor = Accessor;
        if (field == null || accessor == null || IsReadOnly)
            return;

        object? before = field.BoxedValue;
        // A fresh box to write into: SetValue changes a leaf IN the box it is handed, and changing `before`
        // made the undo step's before and after one object, so undoing a vector lane edit did nothing. -xlinka
        object? after = accessor.SetValue(field.BoxedValue, leaf);
        // BoxedValue is typed non-null, but a reference-type field's new value can legitimately be null. -xlinka
        field.BoxedValue = after!;
        InspectorUndo.RecordEdit(this, field, before, after);
    }

    // no undo record: live per-keystroke edits route here, editor records ONE edit for the
    // whole typing session on focus loss instead
    protected void SetMemberValueSilent(object? leaf)
    {
        var field = Field;
        var accessor = Accessor;
        if (field == null || accessor == null || IsReadOnly)
            return;
        object? after = accessor.SetValue(field.BoxedValue, leaf);
        field.BoxedValue = after!;
    }

    // shared tint rule: a member that has gone away reads broken, a driven one magenta,
    // a merely linked one cyan, anything else normal
    protected color FieldStateColor(color? normal = null)
        => InspectorUI.FieldStateColor(TargetMember.Target, normal);

    // Put a widget's color field on the row's live state tint so it re-colors as drives attach and
    // detach, and paint it once now. The tint component is attached to the ROW by the member-row
    // builder BEFORE the editor is built, so an editor always finds it in its parents; no tint
    // component (a standalone editor outside a member row) just means the build-time color stands.
    // Takes the color field rather than the graphic so any widget kind can be signalled. -xlinka
    protected void BindStateTint(IField<color>? widgetTint, color baseColor)
    {
        if (widgetTint == null || widgetTint.IsDestroyed)
            return;
        InspectorUI.ApplyStateTint(widgetTint, FieldStateColor(baseColor));
        var tint = Slot?.GetComponentInParent<MemberStateTint>();
        if (tint == null)
            return;
        tint.StateTint.Target = widgetTint;
        tint.TintBaseColor.Value = baseColor;
    }

    // A value card released or pressed onto this editor: the field takes the card's value through the same
    // write and undo a hand edit uses, one undo step per card. A driven, locked or read-only field refuses the
    // way typing into it does, and says why in the field for a moment. Anything that is not a value card goes
    // on outward, so a tree row still takes a slot card dropped on its Active box. Reference editors answer
    // this themselves. -xlinka
    bool IProxyReceiver.TryReceiveProxy(IReadOnlyList<IGrabbable> held, Grabber grabber)
        => ReceiveValue(held, grabber, null);

    public bool ReceiveValue(IReadOnlyList<IGrabbable> held, Grabber grabber, TextInput? input)
    {
        LocaleText refusal = LocaleText.Empty;
        bool sawCard = false;
        for (int i = 0; i < held.Count; i++)
        {
            if (held[i] is not Component { Slot: { IsDestroyed: false } slot } || slot.GetComponent<ValueProxy>() is not { } card)
                continue;
            sawCard = true;
            if (TryTakeValue(card, input, out var why))
            {
                card.Consume(held[i], grabber);
                return true;
            }
            if (refusal.IsEmpty)
                refusal = why;
        }
        if (!sawCard)
            return PassOutward(held, grabber);
        if (!refusal.IsEmpty)
            ShowDropRefusal(refusal.Resolve());
        return false;
    }

    // The next receiver further out on this panel, the one the hand would have found without this editor.
    private bool PassOutward(IReadOnlyList<IGrabbable> held, Grabber grabber)
    {
        for (var current = Slot?.Parent; current != null; current = current.Parent)
        {
            foreach (var receiver in current.GetComponentsImplementing<IProxyReceiver>())
            {
                if (receiver is Component { IsDestroyed: false } component && component.Enabled.Value)
                    return receiver.TryReceiveProxy(held, grabber);
            }
            if (current.GetComponent<Canvas>() != null)
                break;
        }
        return false;
    }

    // Writes the card's value into the field. The card goes where it fits best, outermost first: the whole
    // field, then each part down to the one this editor shows, so a float3 card on Position's x box sets all
    // of Position while a float card there sets only x. A part of the card's own type wins over one it would
    // have to be converted for.
    public bool TryTakeValue(ValueProxy card, out LocaleText refusal)
        => TryTakeValue(card, null, out refusal);

    private bool TryTakeValue(ValueProxy card, TextInput? input, out LocaleText refusal)
    {
        refusal = LocaleText.Empty;
        var field = Field;
        if (field == null || field.IsDestroyed)
        {
            refusal = ValueCardConversion.Gone;
            return false;
        }
        if (!card.TryGetValue(out var value, out var type) || type == null)
        {
            refusal = ValueCardConversion.Empty;
            return false;
        }
        if (WriteRefusal(field) is { } locked)
        {
            refusal = locked;
            return false;
        }
        if (!TryFit(field, value, type, input, out var path, out var fitted, out refusal))
            return false;

        // An accepted drop ends the pending edit before writing, so a later blur cannot put stale text back. -xlinka
        if (TextInput.Focused is { } focused && (ReferenceEquals(focused.Slot, Slot) || focused.Slot.IsDescendantOf(Slot)))
            focused.Cancel();
        var accessor = StructMemberAccessor.Get(field.ValueType, path);
        object? before = field.BoxedValue;
        object? after = accessor.SetValue(field.BoxedValue, fitted);
        if (Equals(before, after))
        {
            RefreshDisplay();
            return true;
        }
        try
        {
            field.BoxedValue = after!;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            refusal = ValueCardConversion.Refused;
            return false;
        }
        object? landed = field.BoxedValue;
        // A hook decides for itself what a write means; anything else that did not move refused the write.
        if (Equals(landed, before) && field is not ILinkable { IsHooked: true })
        {
            refusal = ValueCardConversion.Refused;
            return false;
        }
        if (!Equals(landed, before))
            InspectorUndo.RecordEditStep(this, field, before, landed);
        RefreshDisplay();
        return true;
    }

    // Same rules as IsReadOnly, worded, plus the permission gate a typed value would meet in the setter.
    private static LocaleText? WriteRefusal(IField field)
    {
        if (field is ILinkable { IsDriven: true, IsHooked: false })
            return ValueCardConversion.Driven;
        if (field is SyncElement { IsConfigLocked: true })
            return ValueCardConversion.Locked;
        if (ImmutableComponent.IsProtected(field))
            return ValueCardConversion.ReadOnly;
        if (field is SyncElement element && !element.IsWriteAllowed())
            return ValueCardConversion.NoPermission;
        return null;
    }

    protected virtual bool TryFit(IField field, object? value, Type type, TextInput? input, out string path, out object? fitted, out LocaleText refusal)
    {
        fitted = null;
        refusal = LocaleText.Empty;
        var paths = PathsOutward(MemberPath.Value ?? "");
        foreach (var candidate in paths)
        {
            if (StructMemberAccessor.Get(field.ValueType, candidate).LeafType == type)
            {
                path = candidate;
                fitted = value;
                return true;
            }
        }
        foreach (var candidate in paths)
        {
            var leafType = StructMemberAccessor.Get(field.ValueType, candidate).LeafType;
            if (ValueCardConversion.TryConvert(value, type, leafType, out fitted, out var why))
            {
                path = candidate;
                return true;
            }
            if (refusal.IsEmpty)
                refusal = why;
        }
        path = "";
        if (refusal.IsEmpty)
            refusal = ValueCardConversion.NoFit.WithArgs(ValueCardConversion.TypeName(type), ValueCardConversion.TypeName(field.ValueType));
        return false;
    }

    // "a.x" -> "", "a", "a.x"
    private static List<string> PathsOutward(string path)
    {
        var paths = new List<string> { "" };
        if (path.Length == 0)
            return paths;
        int dot = path.IndexOf('.');
        while (dot >= 0)
        {
            paths.Add(path[..dot]);
            dot = path.IndexOf('.', dot + 1);
        }
        paths.Add(path);
        return paths;
    }

    // The reason goes where the drop landed: in a text box of this editor's own, the same red note a mistyped
    // value gets, else in place of the row's label.
    private void ShowDropRefusal(string reason)
    {
        var input = Slot?.GetComponentInChildren<TextInput>();
        if (input != null && !input.IsDestroyed && ReferenceEquals(input.Slot.GetComponentInParent<MemberEditor>(), this))
        {
            FieldHint.Show(input, input.Text.Value ?? "", reason, RefreshDisplay);
            return;
        }
        var tint = Slot?.GetComponentInParent<MemberStateTint>();
        if (tint?.Label.Target is { IsDestroyed: false } label)
            ShowLabelNote(label, reason, TargetMember.Target, tint.BaseColor.Value);
    }

    private sealed class LabelNote
    {
        public bool Up;
        public int Serial;
        public string Content = "";
        public bool AutoSized;
        public float AutoSizeMin;
    }

    private static readonly ConditionalWeakTable<Text, LabelNote> LabelNotes = new();
    private const float LabelNoteSeconds = 2.5f;

    private static void ShowLabelNote(Text label, string message, IWorldElement? member, color baseColor)
    {
        var note = LabelNotes.GetValue(label, _ => new LabelNote());
        if (!note.Up)
        {
            note.Content = label.Content.Value ?? "";
            note.AutoSized = label.AutoSizeHorizontal.Value;
            note.AutoSizeMin = label.AutoSizeMin.Value;
        }
        note.Up = true;
        int serial = ++note.Serial;
        label.Content.Value = message;
        label.Color.Value = DashTheme.Negative;
        label.AutoSizeHorizontal.Value = true;
        label.AutoSizeMin.Value = 8f;
        label.RunInSeconds(LabelNoteSeconds, () =>
        {
            if (note.Serial != serial || !note.Up || label.IsDestroyed)
                return;
            note.Up = false;
            label.Content.Value = note.Content;
            label.AutoSizeHorizontal.Value = note.AutoSized;
            label.AutoSizeMin.Value = note.AutoSizeMin;
            label.Color.Value = InspectorUI.FieldStateColor(member, baseColor);
        });
    }
}
