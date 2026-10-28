// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;
using Lumora.Core;
using Lumora.Core.Input;

namespace Helio.UI;

// editable text field. click to focus, then type; backspace/delete remove, arrows/home/end move
// the caret, shift+move selects, typing replaces a selection, enter submits (or inserts a newline
// when Multiline), escape cancels. drives a child "Text" component's content and its
// caret/selection visuals; shows Placeholder when empty and unfocused.
// The child Text draws the caret and selection; only the caret blinks. -xlinka
// Only the clicked instance edits (single static focus), reading the LOCAL keyboard;
// the value replicates through the synced Text field. Mouse-drag selection is future work.
//
// The in-VR keyboard types through the public TypeString/PressBackspace/PressEnter/PressTab
// surface below, which runs the SAME insert/delete helpers the physical keyboard does. Keep it
// that way: a second copy of the caret and selection rules is how the two input paths end up
// disagreeing about where the caret is. -xlinka
[ComponentCategory("UI/Helio/Interaction")]
public sealed class TextInput : InteractionElement, IProxySource, IProxyReceiver
{
    public readonly Sync<string> Text;
    public readonly Sync<string> Placeholder;
    public readonly Sync<bool> Multiline;
    public readonly Sync<int> MaxLength;
    // mask each character as a bullet (password field)
    public readonly Sync<bool> Mask;
    // display-only: can't be focused or edited
    public readonly Sync<bool> ReadOnly;
    // what typing leaves behind once it stops, applied as focus goes
    public readonly Sync<TextFinishHandling> FinishHandling;
    // keep what was typed when focus goes elsewhere (a click away, another field, the on-screen keyboard
    // closing) the way Enter keeps it; Escape still drops the edit
    public readonly Sync<bool> SubmitOnBlur;

    public readonly SyncDelegate<Action<TextInput, string>> ChangeAction;
    public readonly SyncDelegate<Action<TextInput, string>> SubmitAction;

    public event Action<TextInput>? EditingStarted;
    public event Action<TextInput, string>? EditingChanged;
    public event Action<TextInput, string>? EditingFinished;
    // fires on ANY focus loss (enter, escape, click-away, destroy), unlike EditingFinished which
    // is submit-only (enter, or a blur on a SubmitOnBlur field). editors use it to close a typing
    // session exactly once. it runs before EditingFinished.
    public event Action<TextInput, string>? FocusLost;

    // drives the child Text's content. declared member: the target replicates and saves.
    public readonly FieldDrive<string> ContentDrive = new();

    private static TextInput? _focused;

    private Helio.UI.Text? _text;
    private int _caret;
    private int _selStart = -1; // selection anchor, -1 = no selection
    private KeyRepeat _backspaceRepeat;
    private KeyRepeat _deleteRepeat;
    // the text as focus came in: a blur that typed nothing submits nothing
    private string _focusText = string.Empty;

    public bool IsFocused => ReferenceEquals(_focused, this);

    // the input currently owning the local keyboard, if any. game-input readers (menu key, tool
    // hotkeys) stand down while this is non-null.
    public static TextInput? Focused => _focused;

    // Caret index into the CURRENT value, for anything drawing its own view of this field (the VR
    // keyboard's preview strip). The child Text carries the same number in CaretPosition, but that one
    // is masked-string relative and only written while focused.
    public int CaretIndex => _caret;

    public TextInput()
    {
        Text = new Sync<string>(this, string.Empty);
        // Typing into a field is the field's own state; what it commits to is judged as an edit of that.
        Text.MarkUiState();
        Placeholder = new Sync<string>(this, string.Empty);
        Multiline = new Sync<bool>(this, false);
        MaxLength = new Sync<int>(this, 0);
        Mask = new Sync<bool>(this, false);
        ReadOnly = new Sync<bool>(this, false);
        FinishHandling = new Sync<TextFinishHandling>(this, TextFinishHandling.LeaveAsIs);
        SubmitOnBlur = new Sync<bool>(this, false);
        ChangeAction = new SyncDelegate<Action<TextInput, string>>(this);
        SubmitAction = new SyncDelegate<Action<TextInput, string>>(this);
    }

    public void SetChangeAction(Action<TextInput, string>? action)
    {
        if (action == null) return;
        if (action.Target is IWorldElement) ChangeAction.Target = action;
        else EditingChanged += action;
    }

    public void SetSubmitAction(Action<TextInput, string>? action)
    {
        if (action == null) return;
        if (action.Target is IWorldElement) SubmitAction.Target = action;
        else EditingFinished += action;
    }

    public override void OnStart()
    {
        base.OnStart();
        RebindVisuals();
        if (Slot != null)
        {
            Slot.ActiveChanged -= OnSlotActiveChanged;
            Slot.ActiveChanged += OnSlotActiveChanged;
        }
    }

    // A hidden field stops updating, so the check in OnUpdate never runs and the focus would outlive it:
    // the keyboard stays taken and every hotkey stays down with nothing on screen to type into. A dialog
    // closed or a screen switched mid-edit lets go here instead, the way a click away does. -xlinka
    private void OnSlotActiveChanged(Slot slot)
    {
        if (IsFocused && (slot == null || !slot.IsActive))
            Unfocus();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        UpdateDisplay();
    }

    public override void OnDestroy()
    {
        if (Slot != null)
            Slot.ActiveChanged -= OnSlotActiveChanged;
        if (ReferenceEquals(_focused, this))
        {
            _focused = null;
            // Destroyed mid-edit (pane rebuild): close the session and let go of the keyboard.
            FocusLost?.Invoke(this, Text.Value ?? string.Empty);
        }
        _text = null;
        base.OnDestroy();
    }

    private void RebindVisuals()
    {
        // _text is a per-peer handle for the caret/selection fields; the CONTENT link is a real member,
        // so only default it when nothing named a target, on the machine that made the field (the others get
        // the link with it). The authority builds the rows of a panel somebody holds, and this is that field
        // wiring itself up. -xlinka
        _text = Slot?.FindChild("Text", recursive: false)?.GetComponent<Helio.UI.Text>();
        if (_text != null && ContentDrive.ShouldApplyDefault && IsMintedHere)
        {
            using var keep = World?.DataModelPermissions?.EnterUiUpkeep(this);
            ContentDrive.DriveTarget(_text.Content);
        }
        UpdateDisplay();
    }

    protected override void OnPress(in UIInteractionContext context)
    {
        if (!ReadOnly.Value)
            Focus();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!IsFocused)
            return;
        if (!CanInteract || ReadOnly.Value)
        {
            Unfocus();
            return;
        }

        // A focused field OWNS the keyboard: it reads keys raw, and the action map's keyboard source
        // is gated off for as long as this holds focus, so nothing else can see the same keystrokes.
        // That ownership is why the reads below are not action reads - routing them through the map
        // would mean gating the map on the very thing the map would have to drive. Modifiers here
        // (shift for selection) are text-editing state, not rebindable controls. -xlinka
        var kb = Engine.Current?.InputInterface?.Keyboard;
        if (kb == null)
            return;

        string value = Text.Value ?? string.Empty;
        ClampCaret(value);

        bool shift = kb.IsKeyPressed(Key.LeftShift) || kb.IsKeyPressed(Key.RightShift);
        bool changed = false;
        bool caretMoved = false;

        // Ctrl+C/X/V run the SAME three methods the VR keyboard's Copy/Paste keys call. One path, so the
        // two keyboards can never end up with different ideas of what copy means on a field. Handled
        // before the typed-text insert and returning out of the frame: the shortcut has consumed the
        // keystroke, and letting it fall through would type a stray character on top of the paste. -xlinka
        bool shortcut = (kb.IsKeyPressed(Key.LeftControl) || kb.IsKeyPressed(Key.RightControl))
            && !kb.IsKeyPressed(Key.LeftAlt) && !kb.IsKeyPressed(Key.RightAlt);
        if (shortcut)
        {
            if (kb.IsKeyJustPressed(Key.A)) { SelectRange(0, value.Length); return; }
            if (kb.IsKeyJustPressed(Key.C)) { CopySelection(); return; }
            if (kb.IsKeyJustPressed(Key.X)) { CutSelection(); return; }
            if (kb.IsKeyJustPressed(Key.V)) { PasteClipboard(); return; }
            if (kb.IsKeyJustPressed(Key.Z) || kb.IsKeyJustPressed(Key.Y))
            {
                var undo = Lumora.Core.Components.UndoManager.ForLocalUser(World.IsUserspace
                    ? Engine.Current?.WorldManager?.FocusedWorld : World);
                if (kb.IsKeyJustPressed(Key.Y) || shift) undo?.Redo(); else undo?.Undo();
                return;
            }
        }

        // Typed characters: replace the selection (if any), then insert at the caret.
        string typed = shortcut ? string.Empty : kb.GetTypedText();
        if (!string.IsNullOrEmpty(typed))
            changed |= InsertText(ref value, typed);

        double now = World?.Time?.TotalTime ?? 0.0;
        int backs = DeletionCount(kb, Key.Backspace, ref _backspaceRepeat, now);
        int deletes = DeletionCount(kb, Key.Delete, ref _deleteRepeat, now);
        for (int i = 0; i < backs; i++) changed |= DeleteBack(ref value);
        for (int i = 0; i < deletes; i++) changed |= DeleteForward(ref value);

        if (kb.IsKeyJustPressed(Key.LeftArrow)) { MoveCaret(TextElements.Previous(value, _caret), shift); caretMoved = true; }
        if (kb.IsKeyJustPressed(Key.RightArrow)) { MoveCaret(TextElements.Next(value, _caret), shift, value.Length); caretMoved = true; }
        // Home/End are line-relative (single-line has no newlines, so this is unchanged for it). Up/Down
        // keep the column and hop to the same spot on the neighbor line - the caret moves users expect
        // in a multi-line editor. -xlinka
        if (kb.IsKeyJustPressed(Key.Home)) { MoveCaret(LineStartOf(value, _caret), shift, value.Length); caretMoved = true; }
        if (kb.IsKeyJustPressed(Key.End)) { MoveCaret(LineEndOf(value, _caret), shift, value.Length); caretMoved = true; }
        if (kb.IsKeyJustPressed(Key.UpArrow)) { MoveCaret(CaretLineStep(value, _caret, up: true), shift, value.Length); caretMoved = true; }
        if (kb.IsKeyJustPressed(Key.DownArrow)) { MoveCaret(CaretLineStep(value, _caret, up: false), shift, value.Length); caretMoved = true; }

        // Tab in a multi-line field indents by spaces (Tab is stripped from typed text, so handle it here).
        if (Multiline.Value && kb.IsKeyJustPressed(Key.Tab))
            changed |= InsertLiteral(ref value, IndentText);

        bool enter = kb.IsKeyJustPressed(Key.Return) || kb.IsKeyJustPressed(Key.KeypadEnter);
        bool escape = kb.IsKeyJustPressed(Key.Escape);

        if (enter && Multiline.Value)
        {
            changed |= InsertLiteral(ref value, "\n");
            enter = false;
        }

        if (changed)
            ApplyValue(value);
        if (changed || caretMoved)
            UpdateDisplay();

        if (enter) { Submit(); return; }
        if (escape) { Cancel(); return; }
    }

    private static int DeletionCount(Keyboard keyboard, Key key, ref KeyRepeat repeat, double now)
    {
        if (keyboard.IsKeyJustPressed(key)) { repeat.Begin(now); return 1; }
        return repeat.Advance(keyboard.IsKeyPressed(key), now);
    }

    // Index of the first char on the caret's line (just after the previous '\n', or 0).
    private static int LineStartOf(string s, int caret)
    {
        int i = System.Math.Clamp(caret, 0, s.Length) - 1;
        while (i >= 0 && s[i] != '\n') i--;
        return i + 1;
    }

    // Index of the '\n' ending the caret's line, or the string length for the last line.
    private static int LineEndOf(string s, int caret)
    {
        int i = System.Math.Clamp(caret, 0, s.Length);
        while (i < s.Length && s[i] != '\n') i++;
        return i;
    }

    // Same-column move to the previous/next line; clamps to that line's length. Returns the caret index
    // for the neighbor line, or the document edge when there is no neighbor. -xlinka
    private static int CaretLineStep(string s, int caret, bool up)
    {
        int lineStart = LineStartOf(s, caret);
        int column = caret - lineStart;
        if (up)
        {
            if (lineStart == 0)
                return 0; // already on the first line
            int prevEnd = lineStart - 1; // the '\n' before this line
            int prevStart = LineStartOf(s, prevEnd);
            return System.Math.Min(prevStart + column, prevEnd);
        }
        int lineEnd = LineEndOf(s, caret);
        if (lineEnd >= s.Length)
            return s.Length; // already on the last line
        int nextStart = lineEnd + 1;
        int nextEnd = LineEndOf(s, nextStart);
        return System.Math.Min(nextStart + column, nextEnd);
    }

    // EDITING PRIMITIVES
    // Every insert and delete in this component goes through these three, whether the keystroke came
    // off the hardware keyboard in OnUpdate or off a VR key through the public surface further down. -xlinka

    private const string IndentText = "    ";

    private void ClampCaret(string value)
    {
        _caret = TextElements.Floor(value, System.Math.Clamp(_caret, 0, value.Length));
        if (_selStart >= 0) _selStart = TextElements.Floor(value, _selStart);
    }

    // Printable characters only: control codes and DEL are dropped, which is what keeps a stray
    // newline out of a single-line field. Newlines and indents come in through InsertLiteral.
    private bool InsertText(ref string value, string typed)
    {
        int max = MaxLength.Value;
        bool changed = false;
        var characters = StringInfo.GetTextElementEnumerator(typed);
        while (characters.MoveNext())
        {
            string character = characters.GetTextElement();
            if (character[0] < ' ' || character[0] == (char)127)
                continue;
            if (HasSelection())
            {
                DeleteSelection(ref value);
                changed = true;
            }
            if (max > 0 && value.Length + character.Length > max)
                break;
            value = value.Insert(_caret, character);
            _caret += character.Length;
            changed = true;
        }
        return changed;
    }

    // Verbatim insert for the things the printable filter would eat (newline, tab indent).
    private bool InsertLiteral(ref string value, string literal)
    {
        int max = MaxLength.Value;
        if (max > 0 && value.Length >= max)
            return false;
        if (HasSelection())
            DeleteSelection(ref value);
        value = value.Insert(_caret, literal);
        _caret += literal.Length;
        return true;
    }

    private bool DeleteBack(ref string value)
    {
        if (HasSelection())
        {
            DeleteSelection(ref value);
            return true;
        }
        if (_caret <= 0)
            return false;
        int start = TextElements.Previous(value, _caret);
        value = value.Remove(start, _caret - start);
        _caret = start;
        return true;
    }

    private bool DeleteForward(ref string value)
    {
        if (HasSelection()) { DeleteSelection(ref value); return true; }
        if (_caret >= value.Length) return false;
        int end = TextElements.Next(value, _caret);
        value = value.Remove(_caret, end - _caret);
        return true;
    }

    private bool HasSelection() => _selStart >= 0 && _selStart != _caret;

    private void DeleteSelection(ref string value)
    {
        int s = _selStart < _caret ? _selStart : _caret;
        int e = _selStart < _caret ? _caret : _selStart;
        s = System.Math.Clamp(s, 0, value.Length);
        e = System.Math.Clamp(e, 0, value.Length);
        if (e > s)
            value = value.Remove(s, e - s);
        _caret = s;
        _selStart = -1;
    }

    private void MoveCaret(int target, bool extendSelection, int limit = 0)
    {
        if (extendSelection)
        {
            if (_selStart < 0) _selStart = _caret; // start a selection from the old caret
        }
        else
        {
            _selStart = -1;
        }

        if (target < 0) target = 0;
        if (limit > 0 && target > limit) target = limit;
        _caret = TextElements.Floor(Text.Value ?? string.Empty, target);
    }

    private void ApplyValue(string value)
    {
        if (value == (Text.Value ?? string.Empty))
            return;
        Text.Value = value; // OnChanges -> UpdateDisplay
        EditingChanged?.Invoke(this, value);
        ChangeAction.Target?.Invoke(this, value);
    }

    // TYPING SURFACE
    // What a virtual key press calls. Each one clamps, runs the same primitive the hardware path runs,
    // then commits and redraws exactly as OnUpdate does - so change events, MaxLength and selection
    // replacement behave identically no matter which keyboard you typed on. All of them stand down
    // unless this field currently holds focus. -xlinka

    private bool CanType => CanInteract && IsFocused && !ReadOnly.Value;

    public void TypeString(string text)
    {
        if (!CanType || string.IsNullOrEmpty(text))
            return;
        string value = Text.Value ?? string.Empty;
        ClampCaret(value);
        if (InsertText(ref value, text))
            ApplyValue(value);
        UpdateDisplay();
    }

    public void PressBackspace()
    {
        if (!CanType)
            return;
        string value = Text.Value ?? string.Empty;
        ClampCaret(value);
        if (DeleteBack(ref value))
            ApplyValue(value);
        UpdateDisplay();
    }

    public void PressDelete()
    {
        if (!CanType) return;
        string value = Text.Value ?? string.Empty;
        ClampCaret(value);
        if (DeleteForward(ref value)) ApplyValue(value);
        UpdateDisplay();
    }

    // Newline in a multi-line field, submit in a single-line one - the Return key's own rule.
    public void PressEnter()
    {
        if (!CanType)
            return;
        if (!Multiline.Value)
        {
            Submit();
            return;
        }
        string value = Text.Value ?? string.Empty;
        ClampCaret(value);
        if (InsertLiteral(ref value, "\n"))
            ApplyValue(value);
        UpdateDisplay();
    }

    // Indent, and only in a multi-line field. Single-line Tab does nothing here because it does
    // nothing on the hardware keyboard either; inventing a focus-hop for the virtual key would make
    // the two keyboards behave differently on the same field. -xlinka
    public void PressTab()
    {
        if (!CanType || !Multiline.Value)
            return;
        string value = Text.Value ?? string.Empty;
        ClampCaret(value);
        if (InsertLiteral(ref value, IndentText))
            ApplyValue(value);
        UpdateDisplay();
    }

    // Relative caret step, selection dropped. Named apart from the private absolute MoveCaret so
    // nobody reads MoveCaret(3) as "move to index 3".
    public void MoveCaretBy(int delta)
    {
        if (IsDestroyed || !IsFocused || delta == 0)
            return;
        string value = Text.Value ?? string.Empty;
        ClampCaret(value);
        int target = _caret;
        for (int i = 0; i < System.Math.Min(System.Math.Abs((long)delta), value.Length); i++)
            target = delta < 0 ? TextElements.Previous(value, target) : TextElements.Next(value, target);
        MoveCaret(target, false, value.Length);
        UpdateDisplay();
    }

    // Same-column hop to the neighbouring line, straight off the helper the Up/Down keys run in
    // OnUpdate. A single-line field has no neighbour, so this lands on the document edge.
    public void MoveCaretLine(bool up)
    {
        if (IsDestroyed || !IsFocused)
            return;
        string value = Text.Value ?? string.Empty;
        ClampCaret(value);
        MoveCaret(CaretLineStep(value, _caret, up), false, value.Length);
        UpdateDisplay();
    }

    // Home/End, line-relative, off the same two helpers the hardware keys use.
    public void MoveCaretToLineEdge(bool start)
    {
        if (IsDestroyed || !IsFocused)
            return;
        string value = Text.Value ?? string.Empty;
        ClampCaret(value);
        MoveCaret(start ? LineStartOf(value, _caret) : LineEndOf(value, _caret), false, value.Length);
        UpdateDisplay();
    }

    // Set the selection from code. Shift+arrow off the hardware keyboard is otherwise the ONLY thing
    // in this component that can move the anchor, which leaves the copy/cut surface below with a main
    // branch nothing outside OnUpdate can reach. Anchor and caret both clamp into the value; equal
    // means no selection. -xlinka
    public void SelectRange(int anchor, int caret)
    {
        if (IsDestroyed || !IsFocused)
            return;
        string value = Text.Value ?? string.Empty;
        _caret = System.Math.Clamp(caret, 0, value.Length);
        int start = System.Math.Clamp(anchor, 0, value.Length);
        _selStart = start == _caret ? -1 : start;
        ClampCaret(value);
        UpdateDisplay();
    }

    // CLIPBOARD
    // The platform clipboard is a runner-injected service, so there is nothing to reach on a headless
    // build and every method here degrades to a no-op instead of throwing. -xlinka

    // Test seam. A headless test has no Engine, so there is no InputInterface to hang a clipboard
    // off and no way to exercise copy/paste at all without this. Null in every real session; the
    // platform's clipboard goes on InputInterface where the rest of the platform services live.
    public static IClipboardText? ClipboardOverride { get; set; }

    private static IClipboardText? Clipboard => ClipboardOverride ?? Engine.Current?.InputInterface?.ClipboardText;

    // Whether copy/paste can do anything at all right now. The VR keyboard greys its Paste key on this.
    public static bool ClipboardAvailable => Clipboard != null;

    private string SelectedText(string value)
    {
        if (!HasSelection())
            return string.Empty;
        int s = System.Math.Clamp(_selStart < _caret ? _selStart : _caret, 0, value.Length);
        int e = System.Math.Clamp(_selStart < _caret ? _caret : _selStart, 0, value.Length);
        return e > s ? value.Substring(s, e - s) : string.Empty;
    }

    // The selection if there is one, the whole value if there is not - what every editor does when you
    // hit copy without having selected anything. Returns what it put on the clipboard so a caller can
    // see the result without reading the OS clipboard back, which is also what makes it testable.
    public string CopySelection()
    {
        if (IsDestroyed || !IsFocused)
            return string.Empty;
        string value = Text.Value ?? string.Empty;
        ClampCaret(value);
        string copied = HasSelection() ? SelectedText(value) : value;
        if (copied.Length == 0)
            return string.Empty;
        Clipboard?.SetText(copied);
        return copied;
    }

    // Copy then delete, and ONLY with a selection. A bare cut that empties the whole field is one
    // mis-hit away from losing everything you typed, and there is no undo on a text field. -xlinka
    public string CutSelection()
    {
        if (!CanType || !HasSelection())
            return string.Empty;
        string value = Text.Value ?? string.Empty;
        ClampCaret(value);
        string copied = SelectedText(value);
        if (copied.Length == 0)
            return string.Empty;
        Clipboard?.SetText(copied);
        DeleteSelection(ref value);
        ApplyValue(value);
        UpdateDisplay();
        return copied;
    }

    // Paste rides TypeString, so MaxLength, the selection replace and the change event behave exactly as
    // if the text had been typed in. InsertText's printable filter drops control codes, which is what
    // strips newlines out of a single-line field for free; a multi-line field has to put the breaks back
    // with the same literal insert the Enter key uses, since the filter would eat those too. -xlinka
    public bool PasteClipboard()
    {
        if (!CanType)
            return false;
        string text = Clipboard?.GetText() ?? string.Empty;
        if (text.Length == 0)
            return false;

        PasteText(text);
        return true;
    }

    private void PasteText(string text)
    {
        if (!Multiline.Value)
        {
            TypeString(text);
            return;
        }

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                string value = Text.Value ?? string.Empty;
                ClampCaret(value);
                if (InsertLiteral(ref value, "\n"))
                    ApplyValue(value);
                UpdateDisplay();
            }
            TypeString(lines[i]);
        }
    }

    // Take focus without a click. The pointer path goes through OnPress; anything else that needs to
    // start a typing session (a test, a panel handing off to a field) calls this.
    //
    // Typing takes the keyboard and nothing else. While a field holds focus the action map's keyboard
    // source reads as released (InputInterface raises TextFocusHeld off Focused), so walking, crouch and the
    // hotkeys stand down; the mouse, the camera and the headset stay with the user. Holding the whole desktop
    // channel would freeze mouse look, the third-person orbit and the free camera, and park the sticks in VR,
    // for as long as anyone is typing. -xlinka
    public void Focus()
    {
        if (ReferenceEquals(_focused, this))
            return;
        _focused?.Unfocus();
        _focused = this;
        _backspaceRepeat.Reset();
        _deleteRepeat.Reset();
        _focusText = Text.Value ?? string.Empty;
        _caret = _focusText.Length;
        _selStart = -1;
        UpdateDisplay();
        EditingStarted?.Invoke(this);
    }

    // A press that goes down anywhere but on the field ends its edit, the way a click away does. The field,
    // anything inside it, and a surface that types into it (the on-screen keyboard) keep it. Canvases report
    // the element a press lands on, the laser what it hit out in the world, or nothing. -xlinka
    public static void PressedAt(Slot? at, User? by)
    {
        var field = _focused;
        if (field == null || field.IsDestroyed || field.Slot == null)
            return;
        if (by != null && !by.IsLocal)
            return;
        for (var slot = at; slot != null; slot = slot.Parent)
        {
            if (ReferenceEquals(slot, field.Slot) || KeepsFocus(slot))
                return;
        }
        field.Unfocus();
    }

    private static bool KeepsFocus(Slot slot)
    {
        foreach (var component in slot.Components)
        {
            if (component is IKeepsTextFocus)
                return true;
        }
        return false;
    }

    // The blur path: drop focus, release the keyboard and fire FocusLost. Click-away, another field and
    // the VR keyboard's Close land here. A SubmitOnBlur field submits what was typed, when anything was;
    // any other field commits nothing.
    public void Unfocus()
    {
        if (EndFocus(out string text) && SubmitOnBlur.Value && !string.Equals(text, _focusText, StringComparison.Ordinal))
            RaiseSubmitted(text);
    }

    // Escape, from either keyboard: the edit is dropped on every field, SubmitOnBlur or not.
    public void Cancel() => EndFocus(out _);

    private bool EndFocus(out string text)
    {
        _backspaceRepeat.Reset();
        _deleteRepeat.Reset();
        bool was = ReferenceEquals(_focused, this);
        if (was)
        {
            _focused = null;
            ApplyFinishHandling();
        }
        _selStart = -1;
        UpdateDisplay();
        text = Text.Value ?? string.Empty;
        if (was)
            FocusLost?.Invoke(this, text);
        return was;
    }

    // Typing stopped: an empty or blank value becomes no value at all, when the field is set to.
    private void ApplyFinishHandling()
    {
        string? value = Text.Value;
        if (value == null || ReadOnly.Value)
            return;
        bool clear = FinishHandling.Value switch
        {
            TextFinishHandling.NullOnEmpty => value.Length == 0,
            TextFinishHandling.NullOnWhitespace => string.IsNullOrWhiteSpace(value),
            _ => false,
        };
        if (clear)
            Text.Value = null!;
    }

    private void Submit()
    {
        EndFocus(out string value);
        RaiseSubmitted(value);
    }

    private void RaiseSubmitted(string value)
    {
        EditingFinished?.Invoke(this, value);
        SubmitAction.Target?.Invoke(this, value);
    }

    public IGrabbable? TryCreateProxy(Grabber grabber, in float3 spawnPoint)
    {
        if (Mask.Value || World == null || !CanInteract)
            return null;
        var editor = Slot?.GetComponentInParents<MemberEditor>();
        return editor != null
            ? editor.CopyValue(this, spawnPoint)
            : ValueProxy.Spawn(World, Text, spawnPoint);
    }

    public bool TryReceiveProxy(IReadOnlyList<IGrabbable> held, Grabber grabber)
    {
        if (!CanInteract || ReadOnly.Value)
            return false;
        if (Slot?.GetComponentInParents<MemberEditor>() is { } editor)
            return editor.ReceiveValue(held, grabber, this);
        foreach (var item in held)
        {
            if (item is not Component { Slot: { } slot } || slot.GetComponent<ValueProxy>() is not { } card
                || !card.TryGetValue(out var value, out _))
                continue;
            Focus();
            _selStart = 0;
            _caret = (Text.Value ?? string.Empty).Length;
            string copied = Text.Value ?? string.Empty;
            DeleteSelection(ref copied);
            ApplyValue(copied);
            PasteText(ValueCardConversion.Display(value));
            Submit();
            card.Consume(item, grabber);
            return true;
        }
        return false;
    }

    private void UpdateDisplay()
    {
        string? raw = Text.Value;
        string value = raw ?? string.Empty;
        string masked = Mask.Value && value.Length > 0 ? new string('•', value.Length) : value;

        // No text at all and no hint of the field's own leaves the shown text empty, so it shows its own
        // null hint instead.
        string? content;
        if (IsFocused)
            content = masked;
        else if (value.Length == 0)
            content = string.IsNullOrEmpty(Placeholder.Value) ? raw : Placeholder.Value;
        else
            content = masked;

        ContentDrive.SetValue(content!);

        // The caret goes on whatever text the content drive lands on, which need not be a child named Text.
        if (ContentDrive.Target is Lumora.Core.Networking.Sync.SyncElement { Parent: Helio.UI.Text driven }
            && !driven.IsDestroyed && !ReferenceEquals(driven, _text))
            _text = driven;

        if (_text != null)
        {
            int length = content?.Length ?? 0;
            if (IsFocused)
            {
                _text.ResetCaretBlink();
                _text.CaretPosition.Value = System.Math.Clamp(_caret, 0, length);
                _text.SelectionStart.Value = HasSelection() ? System.Math.Clamp(_selStart, 0, length) : -1;
            }
            else
            {
                if (_text.CaretPosition.Value != -1) _text.CaretPosition.Value = -1;
                if (_text.SelectionStart.Value != -1) _text.SelectionStart.Value = -1;
            }
        }
    }
}
