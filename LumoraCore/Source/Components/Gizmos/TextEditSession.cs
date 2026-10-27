// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// Typing into a text in the world, at the text. A small card docks under it with a field holding what it says,
// and every keystroke lands in the text straight away, so the sign changes as you type and everyone in the
// session sees it change. Done, a press anywhere else, the pencil again or the headset keyboard's Close keep it
// as one undo step; Escape or Cancel put back what was there.
//
// The card is this machine's own (a local slot nobody else receives) and only one text is edited at a time,
// the way only one field holds the keyboard. Whether the person may edit it at all is asked of the permission
// gate before anything opens: a text somebody else built in a world that keeps it theirs, one a drive owns
// (a nameplate) or one that is locked says why and stays shut. -xlinka
[ComponentCategory("Hidden")]
public sealed class TextEditSession : Component, IKeepsTextFocus
{
    private const float CardWidth = 520f;
    private const float CardHeight = 178f;
    private const float FieldHeight = 104f;
    private const float Pad = 12f;
    // World panels' size (0.65 mm a unit), so the card reads like every other panel at the same distance.
    private const float PixelSize = 0.00065f;
    private const float GapBelowText = 0.015f;
    private const float ButtonWidth = 96f;

    public static readonly LocaleText RefusedTitle = "Gizmo.Text.Refused.Title".AsLocale("Can't edit this text");

    private static TextEditSession? _current;

    private TextRenderer? _target;
    private string? _original;
    private TextInput? _field;
    private Slot? _card;
    private bool _submitted;
    private bool _closing;
    private bool _writing;

    public static TextEditSession? Current => _current is { IsDestroyed: false } session ? session : null;

    public TextRenderer? Target => _target;

    public TextInput? Field => _field;

    // Wound down already: the field let go and the close is queued for this world's next update.
    public bool IsEnding => _closing;

    public static bool IsEditing(TextRenderer? text)
        => text != null && Current is { } session && ReferenceEquals(session._target, text);

    // PERMISSION

    public static bool CanEdit(TextRenderer? text, User? editor, out LocaleText why)
    {
        why = default;
        var world = text?.World;
        if (text == null || text.IsDestroyed || world == null || text.Slot is not { IsDestroyed: false } slot)
        {
            why = "Gizmo.Text.Refused.Gone".AsLocale("That text is gone.");
            return false;
        }
        if (text.IsLocalElement)
        {
            why = "Gizmo.Text.Refused.Local".AsLocale("That text only exists on this computer and is drawn by something else.");
            return false;
        }
        if (text.Text.IsDriven)
        {
            why = "Gizmo.Text.Refused.Driven".AsLocale("Something else writes this text, so typing into it would not stay.");
            return false;
        }
        if (text.Text.IsConfigLocked || ImmutableComponent.IsProtected(slot))
        {
            why = "Gizmo.Text.Refused.Locked".AsLocale("This text is locked.");
            return false;
        }
        bool allowed;
        var permissions = world.DataModelPermissions;
        using (permissions?.EnterActor(editor ?? world.LocalUser))
            allowed = text.Text.IsWriteAllowedQuietly();
        if (!allowed)
        {
            why = "Gizmo.Text.Refused.Permission".AsLocale("Your role here does not let you change what other people made.");
            return false;
        }
        return true;
    }

    // OPEN AND CLOSE

    // Null when the person may not edit it, after saying why where they are looking.
    public static TextEditSession? Begin(TextRenderer? text, User? editor = null, bool explain = true)
    {
        var world = text?.World;
        if (!CanEdit(text, editor, out var why))
        {
            if (explain && world != null)
                ToolNotice.Say(world, RefusedTitle.Resolve(), why.Resolve());
            return null;
        }
        if (IsEditing(text))
            return Current;
        Current?.Done();

        var host = world!.RootSlot.AddLocalSlot("Text Editor");
        host.Persistent.Value = false;
        var session = host.AttachComponent<TextEditSession>();
        session.Open(text!);
        return session;
    }

    // Keep what was typed.
    public void Done()
    {
        if (_closing)
            return;
        if (_field is { IsDestroyed: false, IsFocused: true } field)
        {
            field.Unfocus();
            return;
        }
        _submitted = true;
        QueueClose();
    }

    // Put the text back as it was.
    public void Cancel()
    {
        if (_closing)
            return;
        if (_field is { IsDestroyed: false, IsFocused: true } field)
        {
            field.Cancel();
            return;
        }
        _submitted = false;
        QueueClose();
    }

    private void Open(TextRenderer text)
    {
        _current = this;
        _target = text;
        _original = text.Text.Value;
        BuildCard(text);
        Follow();
        _field?.Focus();
    }

    // FocusLost runs first on every way out and EditingFinished only after a blur that typed something (the
    // field submits on blur), so by the time the queued close runs both have spoken.
    private void OnFocusLost(TextInput field, string value) => QueueClose();

    private void OnSubmitted(TextInput field, string value) => _submitted = true;

    private void QueueClose()
    {
        if (_closing)
            return;
        _closing = true;
        World?.RunSynchronously(Close);
    }

    private void Close()
    {
        var text = _target;
        if (text is { IsDestroyed: false } && text.Slot is { IsDestroyed: false })
        {
            string? now = text.Text.Value;
            if (!_submitted)
            {
                if (!string.Equals(now, _original, StringComparison.Ordinal))
                    Write(_original);
            }
            else if (!string.Equals(now, _original, StringComparison.Ordinal))
            {
                InspectorUndo.RecordEditStep(this, text.Text, _original, now);
            }
        }
        if (ReferenceEquals(_current, this))
            _current = null;
        if (Slot is { IsDestroyed: false } host)
            host.Destroy();
    }

    public override void OnDestroy()
    {
        if (_field != null)
        {
            _field.FocusLost -= OnFocusLost;
            _field.EditingFinished -= OnSubmitted;
            _field.EditingChanged -= OnTyped;
        }
        if (ReferenceEquals(_current, this))
            _current = null;
        base.OnDestroy();
    }

    // TYPING

    private void OnTyped(TextInput field, string value)
    {
        if (_closing || _writing)
            return;
        if (!Write(value))
        {
            // The role changed under the edit, or something took the text over.
            ToolNotice.Say(World, RefusedTitle.Resolve(),
                "Gizmo.Text.Refused.Midway".AsLocale("You can no longer change this text.").Resolve());
            _submitted = false;
            field.Cancel();
        }
    }

    private bool Write(string? value)
    {
        var text = _target;
        if (text == null || text.IsDestroyed)
            return false;
        _writing = true;
        try
        {
            // Asked quietly first: a write the gate turns down is logged and counted against the writer.
            using (World?.DataModelPermissions?.EnterActor(World.LocalUser))
            {
                if (text.Text.IsDriven || !text.Text.IsWriteAllowedQuietly())
                    return false;
                text.Text.Value = value!;
            }
            return string.Equals(text.Text.Value, value, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"TextEditSession: write refused: {ex.Message}");
            return false;
        }
        finally
        {
            _writing = false;
        }
    }

    // PLACEMENT

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (_closing)
            return;
        var text = _target;
        if (text == null || text.IsDestroyed || text.Slot is not { IsDestroyed: false } slot
            || !slot.IsActive || UndoGraveyard.Holds(slot))
        {
            // Nothing left to keep it on: let go of the keys and close without writing anything.
            _target = null;
            _field?.Cancel();
            QueueClose();
            return;
        }
        Follow();
    }

    // Under the text, the way it reads, turned round when the person is on its back side. Sized for the
    // distance it is read from, so a sign across the room gets a card that can still be read.
    private void Follow()
    {
        var text = _target;
        var host = Slot;
        if (text?.Slot is not { } slot || host == null || host.IsDestroyed)
            return;

        var bounds = text.GetBoundingBox();
        bool laidOut = bounds.Min.x <= bounds.Max.x && bounds.Min.y <= bounds.Max.y;
        float3 under = laidOut ? new float3((bounds.Min.x + bounds.Max.x) * 0.5f, bounds.Min.y, 0f) : float3.Zero;
        float3 anchor = slot.LocalPointToGlobal(under);
        floatQ rotation = slot.GlobalRotation;
        if (text.FaceBackward.Value)
            rotation = rotation * floatQ.AxisAngleRad(float3.Up, MathF.PI);

        var head = World?.LocalUser?.Root?.HeadSlot;
        float distance = 1f;
        if (head != null)
        {
            float3 toHead = head.GlobalPosition - anchor;
            distance = MathF.Max(0.2f, toHead.Length);
            if (float3.Dot(toHead, rotation * float3.Forward) < 0f)
                rotation = rotation * floatQ.AxisAngleRad(float3.Up, MathF.PI);
        }

        float scale = PixelSize * System.Math.Clamp(distance / 0.8f, 1f, 4f);
        host.GlobalRotation = rotation;
        host.GlobalPosition = anchor + rotation * float3.Down * (GapBelowText * MathF.Max(1f, scale / PixelSize));
        if (_card != null && !_card.IsDestroyed)
        {
            float3 local = float3.One * scale;
            if (!Approximately(_card.LocalScale.Value, local))
                _card.LocalScale.Value = local;
        }
    }

    private static bool Approximately(in float3 a, in float3 b)
        => MathF.Abs(a.x - b.x) < 1e-7f && MathF.Abs(a.y - b.y) < 1e-7f && MathF.Abs(a.z - b.z) < 1e-7f;

    // CARD

    private void BuildCard(TextRenderer text)
    {
        var parts = new BrowserParts
        {
            Regular = Font("Regular", DashTheme.FontRegular),
            Semibold = Font("Semibold", DashTheme.FontSemibold),
        };

        _card = Slot.AddSlot("Card");
        _card.LocalScale.Value = float3.One * PixelSize;
        var rect = _card.AttachComponent<RectTransform>();
        rect.OffsetMin.Value = new float2(-CardWidth * 0.5f, -CardHeight);
        rect.OffsetMax.Value = new float2(CardWidth * 0.5f, 0f);
        _card.AttachComponent<Canvas>();

        var back = BrowserParts.Child(_card, "Back");
        BrowserParts.Panel(back, DashTheme.Panel, DashTheme.RadiusPanel, TextGizmo.EditTint.WithAlpha(0.55f), 2f);

        var fieldHost = back.AddSlot("FieldHost");
        var fieldRect = fieldHost.AttachComponent<RectTransform>();
        BrowserParts.PinTop(fieldRect, Pad, FieldHeight, Pad);
        var ui = new UIBuilder(fieldHost);
        ui.Font(parts.Regular).FontSize(DashTheme.FontBody).TextColor(DashTheme.Text)
            .ForegroundColor(DashTheme.Accent).BackgroundColor(DashTheme.Field).CornerRadius(DashTheme.RadiusControl);
        var field = ui.TextInput(text.Text.Value ?? string.Empty,
            "Gizmo.Text.Placeholder".AsLocale("Type the text").Resolve(), multiline: true);
        BrowserParts.Fill(field.Slot.GetComponent<RectTransform>()!);
        field.SubmitOnBlur.Value = true;
        field.FocusLost += OnFocusLost;
        field.EditingFinished += OnSubmitted;
        field.EditingChanged += OnTyped;
        _field = field;

        var bar = back.AddSlot("Bar");
        BrowserParts.PinBottom(bar.AttachComponent<RectTransform>(), Pad, DashTheme.ControlHeight, Pad);
        var hint = bar.AddSlot("Hint");
        var hintRect = hint.AttachComponent<RectTransform>();
        BrowserParts.Fill(hintRect);
        hintRect.OffsetMax.Value = new float2(-(ButtonWidth * 2f + DashTheme.Gap * 2f), 0f);
        parts.Label(hint, "Gizmo.Text.Hint".AsLocale("Enter makes a new line. Esc puts it back.").Resolve(),
            DashTheme.FontLabel, DashTheme.TextDim, BrowserParts.Weight.Regular);

        var cancel = parts.AddButton(bar, "Gizmo.Text.Cancel".AsLocale("Cancel").Resolve(), 0f, DashTheme.ControlHeight,
            DashTheme.Surface, DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text, BrowserParts.Weight.Semibold, Cancel);
        PinRight(cancel.Root, ButtonWidth * 2f + DashTheme.Gap, ButtonWidth);
        var done = parts.AddButton(bar, "Gizmo.Text.Done".AsLocale("Done").Resolve(), 0f, DashTheme.ControlHeight,
            DashTheme.Accent, DashTheme.AccentHover, DashTheme.AccentPressed, DashTheme.OnAccent, BrowserParts.Weight.Semibold, Done);
        PinRight(done.Root, ButtonWidth, ButtonWidth);
    }

    private static void PinRight(Slot button, float right, float width)
    {
        var rect = button.GetComponent<RectTransform>()!;
        rect.AnchorMin.Value = new float2(1f, 0f);
        rect.AnchorMax.Value = new float2(1f, 1f);
        rect.OffsetMin.Value = new float2(-right, 0f);
        rect.OffsetMax.Value = new float2(-right + width, 0f);
    }

    private IAssetProvider<FontSet> Font(string name, Uri url)
    {
        var slot = Slot.AddSlot("Font " + name);
        var provider = slot.AttachComponent<FontProvider>();
        provider.URL.Value = url;
        provider.FallbackURLs.Add(url);
        return provider;
    }
}
