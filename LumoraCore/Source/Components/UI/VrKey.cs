// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core.Input;

namespace Lumora.Core.Components.UI;

// What a key on the virtual keyboard does when it is pressed.
//
// Append only. Role is a Sync member, so an existing value has to keep meaning what it meant.
public enum VrKeyRole
{
    Character,
    Space,
    Backspace,
    Enter,
    Tab,
    Shift,
    Escape,
    Close,
    ArrowLeft,
    ArrowRight,
    ArrowUp,
    ArrowDown,
    Copy,
    Paste,
}

// One key on the VrKeyboard panel. Holds the two faces of the key (unshifted and shifted) and the
// job it does, and forwards its press to the keyboard that owns it. The keyboard decides what a
// press MEANS - the key never touches the focused field itself - so the shift state, the one-shot
// consume and the show/hide rules all live in one place. -xlinka
//
// The one thing the key owns is REPEAT. A Button only ever hands out edges (it acts as the press lands),
// so "still holding it" has to be read off the button's live press state every frame, and that read
// belongs next to the timer that consumes it. Updates gate on the default Slot.IsActive, which is
// exactly right here: the key slots are inactive while the panel is hidden, and a hidden keyboard
// repeating into the field you just walked away from would be a bug, not a feature. -xlinka
[ComponentCategory("UI")]
public sealed class VrKey : Component
{
    public readonly Sync<string> Glyph;
    public readonly Sync<string> ShiftGlyph;
    public readonly Sync<VrKeyRole> Role;
    public readonly SyncRef<VrKeyboard> Keyboard;
    // The key's face. The keyboard rewrites it in one pass when shift flips.
    public readonly SyncRef<Text> Label;
    // The small corner face carrying whichever glyph is NOT active, on the keys that have two
    // genuinely different ones. Null on letters and on the modifiers - see VrKeyboard.BuildKey.
    public readonly SyncRef<Text> AltLabel;

    private Button? _button;
    // Per-peer repeat timing: holding a key is local input, not replicated state.
    private KeyRepeat _repeat;
    private TextInput? _pressedField;

    public VrKey()
    {
        Glyph = new Sync<string>(this, string.Empty);
        ShiftGlyph = new Sync<string>(this, string.Empty);
        Role = new Sync<VrKeyRole>(this, VrKeyRole.Character);
        Keyboard = new SyncRef<VrKeyboard>(this);
        Label = new SyncRef<Text>(this);
        AltLabel = new SyncRef<Text>(this);
    }

    // Keys that make sense to hold down. Shift/Enter/Escape/Close/Copy/Paste deliberately do not:
    // repeating any of those is destructive rather than useful (a held Enter submits a form over and
    // over, a held Close is just Close). -xlinka
    public bool Repeats => Role.Value switch
    {
        VrKeyRole.Character => true,
        VrKeyRole.Space => true,
        VrKeyRole.Backspace => true,
        VrKeyRole.Tab => true,
        VrKeyRole.ArrowLeft => true,
        VrKeyRole.ArrowRight => true,
        VrKeyRole.ArrowUp => true,
        VrKeyRole.ArrowDown => true,
        _ => false,
    };

    public override void OnStart()
    {
        base.OnStart();
        _button = Slot?.GetComponent<Button>();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var keyboard = Keyboard.Target;
        _button ??= Slot?.GetComponent<Button>();
        bool held = Repeats && Enabled.Value && Slot is { IsActive: true }
            && keyboard is { IsDestroyed: false }
            && keyboard.Slot.IsActive && !keyboard.IsGrabbed
            && ReferenceEquals(_pressedField, TextInput.Focused)
            && _button is { IsDestroyed: false, CanInteract: true }
            && _button.IsPressedNow;
        int count = _repeat.Advance(held, Now, keyboard?.RepeatInterval.Value ?? KeyRepeat.DefaultInterval);
        for (int i = 0; i < count; i++) keyboard!.HandleKey(this);
    }

    // A key with no shifted face (space, enter, the specials) reads the same either way.
    public string ActiveGlyph(bool shift)
    {
        string shifted = ShiftGlyph.Value ?? string.Empty;
        if (shift && shifted.Length > 0)
            return shifted;
        return Glyph.Value ?? string.Empty;
    }

    // Two faces that differ by more than case - '1' and '!', ',' and '<'. A letter key is the same
    // glyph twice as far as the face is concerned, so it stays a single big letter.
    public bool HasDualFace
    {
        get
        {
            string glyph = Glyph.Value ?? string.Empty;
            string shifted = ShiftGlyph.Value ?? string.Empty;
            return shifted.Length > 0 && !string.Equals(glyph, shifted, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void ApplyShiftState(bool shift)
    {
        if (Role.Value != VrKeyRole.Character)
            return;
        // Retext, never rebuild: this runs over every key on the panel each time shift flips, and
        // tearing down a button to change a letter would re-mesh the row AND drop its color driver.
        SetContent(Label.Target, ActiveGlyph(shift));
        SetContent(AltLabel.Target, ActiveGlyph(!shift));
    }

    private static void SetContent(Text? text, string content)
    {
        if (text == null || text.IsDestroyed)
            return;
        if (text.Content.Value != content)
            text.Content.Value = content;
    }

    [SyncMethod]
    public void OnPressed(Button button, UIInteractionContext context)
    {
        _button = button;
        _pressedField = TextInput.Focused;
        _repeat.Begin(Now, Keyboard.Target?.RepeatDelay.Value ?? KeyRepeat.DefaultDelay);
        Keyboard.Target?.HandleKey(this);
    }

    private double Now => World?.Time?.TotalTime ?? 0.0;

    public override void OnDisabled()
    {
        _repeat.Reset();
        base.OnDisabled();
    }
}
