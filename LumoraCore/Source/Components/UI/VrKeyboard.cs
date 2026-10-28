// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The in-VR typing surface. It lives in userspace beside the dash rather than inside it: a focused
// field can be on the dash, on a world panel or on an inspector, and one keyboard has to reach all
// three. Keys drive the focused TextInput through its public typing surface, so the caret, the
// selection replace and the length clamp are the same code the hardware keyboard runs.
//
// Showing is driven by focus, not by a button: a TextInput holding the static focus, or a dash
// screen reporting one of its inline fields focused. Neither has a focus-changed event, so this polls
// each common update. That poll has to keep running while the panel is HIDDEN, which is why
// CanRunUpdates drops the active-slot gate below - an inactive slot gets no updates, and a keyboard
// that only wakes up while it is already visible never appears. The update order puts that poll
// ahead of UserspacePointer (-1000), so the frame the panel appears is already the frame a laser
// wakes for it rather than a frame of keyboard nothing can press. -xlinka
[ComponentCategory("UI")]
[DefaultUpdateOrder(-1100)]
[UserspaceOnly]
public sealed class VrKeyboard : Component, Helio.UI.IKeepsTextFocus
{
    // Metres per canvas unit, where the panel docks at the user's own size.
    private const float PanelScale = VrKeyboardLayout.PanelScale;

    // Keep a useful placement between fields, but bring it back on the next show if the user has
    // walked away or turned around. Distances are relative to the focused user's scale.
    private const float ReachDistance = 0.8f;
    private const float ReachAngleDegrees = 55f;
    // Late updates after a show that keep re-docking. The dash positions itself in its own late
    // update, after ours, so the frame it opens the surface is still where it last was.
    private const int DockSettleFrames = 2;

    // Keys are poked through the userspace hand's CanvasTouchSource like any other panel; the press,
    // release, hover and punch-through depths it ships with are the ones this keyboard was tuned on.

    // One band above the dash surface (UserspaceDashboard.DashSurfaceSortingOrder) and below the
    // laser cursor bands, so the keyboard covers the dash it is typing into and the pointer still
    // shows on top of the keys. -xlinka
    private const int KeyboardSortingOrder = 20100;

    // Held caps reads brighter than the one-shot accent so the two shift states are never confused.
    private static readonly color ShiftHeldFill = new color(0.62f, 0.55f, 0.98f, 1f);
    // A key that is present but cannot do anything - Paste on a platform with no clipboard.
    private static readonly color KeyDisabledFill = new color(0.15f, 0.14f, 0.20f, 1f);

    // Shift is applying right now, whether that is a one-shot or a held caps.
    public readonly Sync<bool> ShiftActive;
    // Sticky: shift survives the next character instead of clearing after it.
    public readonly Sync<bool> ShiftHeld;
    public readonly Sync<float> Distance;
    public readonly Sync<float> DropBelowGaze;
    // Seconds a repeating key is held before it starts re-firing, and the gap between re-fires after
    // that. The defaults are the usual OS shape: long enough that a normal keypress never repeats,
    // fast enough that a held backspace clears a line at a sensible rate.
    public readonly Sync<float> RepeatDelay;
    public readonly Sync<float> RepeatInterval;
    // How long after a shift tap a second tap still counts as a DOUBLE tap and locks caps on.
    public readonly Sync<float> ShiftHoldWindow;
    // Seconds the panel stays up after the field it was typing into loses focus. A field that blurs
    // and refocuses inside the window (Enter into the next field, a tap that bounces focus) keeps the
    // keyboard where it is instead of hiding and re-showing it.
    public readonly Sync<float> HideGrace;

    private readonly SyncRef<Text> _preview;
    private readonly SyncRef<VrKey> _shiftKey;
    private readonly SyncRef<VrKey> _pasteKey;

    // Per-peer handles, refilled by TakeUpLayout from whatever layout this slot carries rather than
    // replicated: userspace is this machine's alone.
    private readonly List<VrKey> _keys = new();
    private Canvas? _canvas;
    private Grabbable? _grab;
    private bool _built;
    // The shift and paste keys' own fills, which their state tints go back to.
    private KeyLook _shiftLook;
    private KeyLook _pasteLook;
    // Whether a field has held focus since the panel came up. Without it, a keyboard summoned by
    // hand with nothing focused would hide itself on its very first update.
    private bool _typingSession;
    // World time the focus went away, NaN while it is held. The hide waits HideGrace from here.
    private double _focusLostAt = double.NaN;
    // Keep the user's chosen size when recovering a panel they moved out of reach.
    private bool _placed;
    private bool _userPlaced;
    private int _dockFrames;
    // World time of the last shift tap, for the double-tap window.
    private double _lastShiftTap = double.NegativeInfinity;

    public static VrKeyboard? LocalInstance { get; private set; }

    // What UserspacePointer reads to keep a laser alive for the keyboard the way it does for the dash.
    public static bool LocalShown => LocalInstance is { IsDestroyed: false } keyboard && keyboard.IsShown;

    public bool IsShown => Slot != null && !Slot.IsDestroyed && Slot.ActiveSelf.Value;

    // Held by a hand right now. Keys do nothing while this holds: a grab is a move, not a press.
    public bool IsGrabbed => _grab is { IsDestroyed: false, IsGrabbed: true };

    // Whether a text field wants the keyboard: the static TextInput focus, or a dash screen with one of
    // its own inline fields focused. The dash being open, or its screen being ABLE to take keys, is
    // not a field. -xlinka
    public static bool WantsKeys => TextInput.Focused is { IsDestroyed: false } || DashFieldFocused();

    // The focus poll is the whole point of this component and it has to run while the panel is off.
    protected override bool CanRunUpdates => !IsDestroyed && Slot != null && !Slot.IsDestroyed;

    public VrKeyboard()
    {
        ShiftActive = new Sync<bool>(this, false);
        ShiftHeld = new Sync<bool>(this, false);
        Distance = new Sync<float >(this, 0.55f);
        DropBelowGaze = new Sync<float>(this, 0.3f);
        RepeatDelay = new Sync<float>(this, 0.45f);
        RepeatInterval = new Sync<float>(this, 0.05f);
        ShiftHoldWindow = new Sync<float>(this, 0.5f);
        HideGrace = new Sync<float>(this, 0.3f);
        _preview = new SyncRef<Text>(this);
        _shiftKey = new SyncRef<VrKey>(this);
        _pasteKey = new SyncRef<VrKey>(this);
    }

    public override void OnStart()
    {
        base.OnStart();
        LocalInstance = this;
        // A layout that cannot be taken up is a favourite gone wrong: the built-in comes back in its place,
        // so a broken favourite never leaves anyone without a keyboard.
        try
        {
            TakeUpLayout();
        }
        catch (Exception ex)
        {
            Logging.Logger.Error($"VrKeyboard: could not take up the layout on '{Slot?.Name.Value}': {ex.Message}");
            FavouriteKeyboard.Broken(this, ex.Message);
            return;
        }
        RefreshShiftVisuals();
        RefreshPasteKey();
    }

    public override void OnDestroy()
    {
        if (ReferenceEquals(LocalInstance, this))
            LocalInstance = null;
        if (_grab != null)
        {
            _grab.OnLocalGrabbed -= OnGrabbed;
            _grab.OnLocalReleased -= OnReleased;
        }
        _keys.Clear();
        base.OnDestroy();
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();

        var focused = TextInput.Focused;

        // Up while a field has focus, down once it has gone; the same rule for a TextInput anywhere
        // and for a dash screen's inline field. Dash screens do not focus a TextInput (their fields
        // take raw keystrokes through the dashboard's feed), so they report their own field focus
        // through IDashboardKeyInput instead. A screen that merely COULD take keys is not a reason to
        // be up: that rule put the keyboard over every dash screen you opened. -xlinka
        if (WantsKeys)
        {
            _typingSession = true;
            _focusLostAt = double.NaN;
            // Auto-show is VR-only: on desktop the hardware keyboard is right there and a panel
            // covering the view would be in the way. Show() itself stays open to anyone.
            if (!IsShown && Engine.Current?.InputInterface?.IsVRActive == true)
                Show();
        }
        else if (_typingSession && IsShown)
        {
            // Not straight away: a field that blurs and refocuses within the grace (Enter moving to
            // the next field, a tap that bounces focus) keeps the panel up rather than blinking it.
            double now = World?.Time?.TotalTime ?? 0.0;
            if (double.IsNaN(_focusLostAt))
                _focusLostAt = now;
            else if (now - _focusLostAt >= HideGrace.Value)
                Hide();
        }
        else if (_typingSession)
        {
            _typingSession = false;
            _focusLostAt = double.NaN;
        }

        if (IsShown)
            UpdatePreview(focused);
    }

    private static bool DashFieldFocused()
    {
        // An app out in the room with a field waiting counts, dash open or not.
        if (DashWindowPanel.TakesKeys)
            return true;
        var input = Engine.Current?.InputInterface;
        if (input == null || !input.IsDashboardOpen)
            return false;
        var dash = UserspaceDashboard.LocalInstance;
        if (dash == null || dash.IsDestroyed || !dash.IsOpen.Value)
            return false;
        return dash.Dashboard?.CurrentScreen is IDashboardKeyInput keys && keys.HasFieldFocus;
    }

    // SHOW / HIDE

    public void Show()
    {
        if (Slot == null || Slot.IsDestroyed)
            return;

        // Coming up for a field that is already focused counts as the start of a typing session, so
        // the poll knows to put the panel away again when that focus goes.
        if (WantsKeys)
        {
            _typingSession = true;
            _focusLostAt = double.NaN;
        }

        Slot.ActiveSelf.Value = true;
        // Preserve a reachable placement. A keyboard left behind must still reopen near the user,
        // even when its previous position was chosen by hand.
        if (!IsGrabbed && (!_placed || !IsWithinReach()))
        {
            Dock();
            _dockFrames = DockSettleFrames;
        }
        // The clipboard service is wired at startup, before userspace exists, but a test (or a
        // platform that hands one over late) can arrive after the panel was built.
        RefreshPasteKey();
        // Why the favourite keyboard is not the one up, or came up without some of its parts.
        FavouriteKeyboard.Shown();
    }

    // Putting the keyboard away ends the typing session too. Leaving the field focused would keep
    // the whole hardware keyboard gated off (InputInterface.TextFocusHeld) with nothing on screen
    // saying why you cannot walk. -xlinka
    public void Hide()
    {
        PokeCanvas()?.ReleaseAllPointers();
        if (Slot != null && !Slot.IsDestroyed)
            Slot.ActiveSelf.Value = false;
        _typingSession = false;
        _focusLostAt = double.NaN;
        _dockFrames = 0;
        BlurFocused();
        UserspaceDashboard.LocalInstance?.ReleaseFieldFocus();
    }

    // GRAB
    // The panel is an ordinary grabbable: the userspace pointer's laser and a hand both pick it up
    // through the Grabber on the pointer rig, two hands stretch it, and it turns with the hand that
    // holds it. What the keyboard adds is that a grab is never a keypress. -xlinka

    private void OnGrabbed(IGrabbable item)
    {
        _userPlaced = true;
        _dockFrames = 0;
        // Whatever was down on a key when the hand closed lets go there: it stops repeating, nothing more
        // is typed, and the hands stay disarmed for poking until each tip has come back out of the panel.
        Touch.CanvasTouchSource.DisarmAll(World);
        PokeCanvas()?.ReleaseAllPointers();
    }

    private void OnReleased(IGrabbable item)
    {
        Touch.CanvasTouchSource.DisarmAll(World);
        PokeCanvas()?.ReleaseAllPointers();
    }

    private static void BlurFocused()
    {
        var focused = TextInput.Focused;
        if (focused is { IsDestroyed: false })
        {
            using (HoldWorldOf(focused))
                focused.Unfocus();
        }
    }

    // A field out in a world is that world's to change, and a session's sync takes the world from a thread of
    // its own: a keystroke written in from this space's update while it does was refused, and the throw left
    // the key's press hanging, so the key went on repeating by itself. The world is held for the edit the way
    // its own update holds it, which waits out a sync pass already under way. A field in this space (the
    // dash's) is this update's own and the hold is a no-op re-entry. -xlinka
    private static WorldHold HoldWorldOf(TextInput field) => new(field.World?.HookManager);

    private readonly struct WorldHold : IDisposable
    {
        private readonly HookManager? _hooks;

        public WorldHold(HookManager? hooks)
        {
            _hooks = hooks;
            hooks?.DataModelLock(System.Threading.Thread.CurrentThread);
        }

        public void Dispose() => _hooks?.DataModelUnlock();
    }

    // Dock below the field's panel only when that position is comfortable to reach. Large or distant
    // windows must not send the keyboard to the floor or across the room.
    private const float DockGap = 0.06f;
    private const float DockTowardViewer = 0.18f;
    private const float DockTiltDegrees = -45f;

    public override void OnLateUpdate(float delta)
    {
        base.OnLateUpdate(delta);
        if (_dockFrames <= 0 || !IsShown || IsGrabbed)
            return;
        _dockFrames--;
        Dock();
    }

    private void Dock()
    {
        if (Slot == null || Slot.IsDestroyed)
            return;
        _placed = true;

        // Under the app being typed into when that is a panel out in the room.
        var panel = DashWindowPanel.TakesKeys ? DashWindowPanel.KeyFocus?.Surface : null;
        if (panel?.Slot is { IsDestroyed: false } panelSurface && Engine.Current?.InputInterface?.IsVRActive == true)
        {
            var bottom = panelSurface.LocalPointToGlobal(new float3(0f, -panel.Size.y * PanelSurface.UnitMeters * 0.5f, 0f));
            var turn = panelSurface.GlobalRotation;
            PlaceDockOrHead(bottom + turn * (float3.Down * DockGap + float3.Forward * DockTowardViewer), turn);
            return;
        }

        var dash = UserspaceDashboard.LocalInstance;
        var surface = dash != null && !dash.IsDestroyed && dash.IsOpen.Value ? dash.SurfaceSlot : null;
        if (surface == null || Engine.Current?.InputInterface?.IsVRActive != true)
        {
            PlaceInFrontOfHead();
            return;
        }

        float halfHeight = dash!.DisplayHeight.Value * 0.5f;
        var local = new float3(0f, -halfHeight - DockGap, DockTowardViewer);
        PlaceDockOrHead(surface.LocalPointToGlobal(local), surface.GlobalRotation);
    }

    private void PlaceDockOrHead(float3 position, floatQ rotation)
    {
        var head = HeadSlot();
        if (head == null || !IsWithinReach(position, head)
            || float3.Dot(head.GlobalPosition - position, rotation * float3.Forward) <= 0f)
        {
            PlaceInFrontOfHead();
            return;
        }
        Slot.GlobalPosition = position;
        Slot.GlobalRotation = rotation * floatQ.AxisAngleRad(float3.Right, DockTiltDegrees * (MathF.PI / 180f));
        ApplyDefaultScale(head);
    }

    private void PlaceInFrontOfHead()
    {
        if (Slot == null || Slot.IsDestroyed)
            return;

        var head = HeadSlot();
        if (head == null)
        {
            Slot.LocalPosition.Value = new float3(0f, 1.4f, -Distance.Value);
            Slot.LocalRotation.Value = floatQ.Identity;
            Slot.LocalScale.Value = float3.One * PanelScale;
            return;
        }

        var up = ViewUp(head);
        float scale = ViewScale(head);
        var forward = FlatViewForward(head);
        Slot.GlobalPosition = head.GlobalPosition
            + forward * System.Math.Clamp(Distance.Value, 0.35f, 0.65f) * scale
            - up * System.Math.Clamp(DropBelowGaze.Value, 0.15f, 0.45f) * scale;

        // Yaw so the panel's readable +Z face turns back at the head. Never floatQ.LookRotation here:
        // it hands back the inverse rotation and the panel goes edge-on at oblique angles. -xlinka
        Slot.GlobalRotation = LaserCarry.FacingRotation(-forward, up)
            * floatQ.AxisAngleRad(float3.Right, -30f * (MathF.PI / 180f));
        ApplyDefaultScale(head);
    }

    private void ApplyDefaultScale(Slot head)
    {
        if (!_userPlaced)
            Slot.GlobalScale = float3.One * (PanelScale * ViewScale(head));
    }

    private bool IsWithinReach()
    {
        var head = HeadSlot();
        // No head to measure against is not a reason to yank the panel somewhere else.
        if (head == null || Slot == null || Slot.IsDestroyed)
            return true;

        return IsWithinReach(Slot.GlobalPosition, head);
    }

    private static bool IsWithinReach(float3 position, Slot head)
    {
        var offset = (position - head.GlobalPosition) / ViewScale(head);
        var up = ViewUp(head);
        float height = float3.Dot(offset, up);
        if (offset.Length > ReachDistance || height < -0.65f || height > -0.08f)
            return false;

        var flat = LaserCarry.FlattenInRoot(up, offset);
        if (flat.Length < 0.2f)
            return false;

        float alignment = float3.Dot(flat.Normalized, FlatViewForward(head));
        return alignment >= MathF.Cos(ReachAngleDegrees * (MathF.PI / 180f));
    }

    private Slot? HeadSlot()
    {
        // The focused view is authoritative. The userspace proxy can lag a teleport or a change of
        // avatar scale; keep it as the fallback while worlds are switching.
        var head = Engine.Current?.WorldManager?.FocusedWorld?.LocalUser?.Root?.HeadSlot;
        if (head == null || head.IsDestroyed)
            head = World?.LocalUser?.Root?.HeadSlot;
        return head != null && !head.IsDestroyed ? head : null;
    }

    private static float3 ViewUp(Slot head) => head.ActiveUserRoot?.Slot.Up ?? float3.Up;

    private static float ViewScale(Slot head)
    {
        var scale = head.ActiveUserRoot?.Slot.GlobalScale ?? float3.One;
        return MathF.Max(0.001f, (MathF.Abs(scale.x) + MathF.Abs(scale.y) + MathF.Abs(scale.z)) / 3f);
    }

    private static float3 FlatViewForward(Slot head)
    {
        float3 forward = head.GlobalRotation * float3.Backward; // view forward is -Z
        forward = LaserCarry.FlattenInRoot(ViewUp(head), forward);
        return forward.LengthSquared > 1e-6f ? forward.Normalized
            : (head.ActiveUserRoot?.Slot.GlobalRotation ?? floatQ.Identity) * float3.Backward;
    }

    // POKE
    // Pushing a fingertip into the panel presses whatever key it lands on. That is the general
    // CanvasTouchSource on each userspace hand driving this canvas like any other; the one thing the
    // keyboard adds is that a grab is never a keypress (see OnGrabbed). The holding hand's tip sits inside
    // the panel for as long as the hold lasts, and the source skips a panel held by its own hand. -xlinka

    private Canvas? PokeCanvas()
    {
        if (_canvas != null && !_canvas.IsDestroyed)
            return _canvas;
        _canvas = Slot != null && !Slot.IsDestroyed ? Slot.GetComponent<Canvas>() : null;
        return _canvas;
    }

    // KEY DISPATCH

    public void HandleKey(VrKey key)
    {
        if (key == null || key.IsDestroyed || IsDestroyed || IsGrabbed)
            return;

        switch (key.Role.Value)
        {
            case VrKeyRole.Shift:
                CycleShift();
                return;
            case VrKeyRole.Close:
                Hide();
                return;
            case VrKeyRole.Escape:
                // Cancel the edit without committing it; the focus poll puts the panel away next
                // update. With no field focused the dashboard is the one editing, so it gets the
                // cancel instead - otherwise Escape is a dead key on every dash screen.
                if (TextInput.Focused is { IsDestroyed: false } focused)
                {
                    using (HoldWorldOf(focused))
                        focused.Cancel();
                }
                else
                    UserspaceDashboard.LocalInstance?.FeedEscape();
                return;
        }

        var input = TextInput.Focused;
        if (input == null || input.IsDestroyed)
        {
            // Nothing owns a text caret, so the dashboard is the consumer. Its feed already routes at
            // the current screen and falls back to the file-browser search well, which is exactly what
            // the desktop key handler does with the same keystrokes.
            FeedDashboard(key);
            return;
        }

        using var hold = HoldWorldOf(input);
        switch (key.Role.Value)
        {
            case VrKeyRole.Character:
                input.TypeString(key.ActiveGlyph(ShiftActive.Value));
                ConsumeOneShotShift();
                break;
            case VrKeyRole.Space:
                input.TypeString(" ");
                break;
            case VrKeyRole.Backspace:
                input.PressBackspace();
                break;
            case VrKeyRole.Enter:
                input.PressEnter();
                break;
            case VrKeyRole.Tab:
                input.PressTab();
                break;
            case VrKeyRole.ArrowLeft:
                input.MoveCaretBy(-1);
                break;
            case VrKeyRole.ArrowRight:
                input.MoveCaretBy(1);
                break;
            case VrKeyRole.ArrowUp:
                input.MoveCaretLine(up: true);
                break;
            case VrKeyRole.ArrowDown:
                input.MoveCaretLine(up: false);
                break;
            case VrKeyRole.Copy:
                input.CopySelection();
                break;
            case VrKeyRole.Paste:
                input.PasteClipboard();
                break;
        }
    }

    private void FeedDashboard(VrKey key)
    {
        var dash = UserspaceDashboard.LocalInstance;
        if (dash == null || dash.IsDestroyed)
            return;

        switch (key.Role.Value)
        {
            case VrKeyRole.Character:
                string glyph = key.ActiveGlyph(ShiftActive.Value);
                if (!string.IsNullOrEmpty(glyph))
                    dash.FeedSearchChar(glyph[0]);
                ConsumeOneShotShift();
                break;
            case VrKeyRole.Space:
                dash.FeedSearchChar(' ');
                break;
            case VrKeyRole.Backspace:
                dash.FeedSearchBackspace();
                break;
            case VrKeyRole.Enter:
                dash.FeedEnter();
                break;
            case VrKeyRole.Tab:
                dash.FeedKey(DashboardKey.Tab);
                break;
            case VrKeyRole.ArrowLeft:
                dash.FeedKey(DashboardKey.Left);
                break;
            case VrKeyRole.ArrowRight:
                dash.FeedKey(DashboardKey.Right);
                break;
            case VrKeyRole.ArrowUp:
                dash.FeedKey(DashboardKey.Up);
                break;
            case VrKeyRole.ArrowDown:
                dash.FeedKey(DashboardKey.Down);
                break;
            case VrKeyRole.Copy:
                dash.FeedKey(DashboardKey.Copy);
                break;
            case VrKeyRole.Paste:
                dash.FeedKey(DashboardKey.Paste);
                break;
        }
    }

    // SHIFT STATE
    // Off -> one-shot -> held -> off. A one-shot clears itself after the next character key; held
    // rides through as many as you like. Two QUICK taps lock caps, a third drops it. -xlinka

    public void CycleShift()
    {
        double now = World?.Time?.TotalTime ?? 0.0;
        double sinceLast = now - _lastShiftTap;

        if (ShiftHeld.Value)
        {
            ShiftHeld.Value = false;
            ShiftActive.Value = false;
        }
        else if (ShiftActive.Value)
        {
            // Caps locks on a genuine double tap only. A second tap a minute later is somebody
            // reaching for shift again and finding it already armed - locking caps on there is a
            // surprise you then have to notice and undo, so it cancels the one-shot instead. -xlinka
            if (sinceLast <= ShiftHoldWindow.Value)
                ShiftHeld.Value = true;
            else
                ShiftActive.Value = false;
        }
        else
        {
            ShiftActive.Value = true;
        }

        _lastShiftTap = now;
        RefreshShiftVisuals();
    }

    private void ConsumeOneShotShift()
    {
        if (!ShiftActive.Value || ShiftHeld.Value)
            return;
        ShiftActive.Value = false;
        RefreshShiftVisuals();
    }

    // One pass over the registered keys instead of a driver component per key: fifty drivers on a
    // panel that already re-meshes its whole row on a text change buys nothing. -xlinka
    private void RefreshShiftVisuals()
    {
        bool shift = ShiftActive.Value;
        for (int i = 0; i < _keys.Count; i++)
        {
            var key = _keys[i];
            if (key != null && !key.IsDestroyed)
                key.ApplyShiftState(shift);
        }

        var shiftKey = _shiftKey.Target;
        if (shiftKey == null || shiftKey.IsDestroyed || shiftKey.Slot == null)
            return;

        // Off is the key's own fill; the two states paint over it through its colour driver, since a Helio
        // button drives its own backing and a direct write would be reverted on the driver's next pass.
        _shiftLook.Show(ShiftHeld.Value ? ShiftHeldFill : ShiftActive.Value ? InspectorUI.AccentColor : null);
    }

    // A platform with no clipboard service gets a Paste key that says so: muted fill, muted label, and
    // Interactable off so it cannot even be pressed. The alternative is a key that looks live and
    // silently does nothing, which is the worst of both. -xlinka
    private void RefreshPasteKey()
    {
        var key = _pasteKey.Target;
        if (key == null || key.IsDestroyed || key.Slot == null)
            return;

        bool available = TextInput.ClipboardAvailable;

        var button = key.Slot.GetComponent<Button>();
        if (button != null && !button.IsDestroyed)
            button.Interactable.Value = available;

        _pasteLook.Show(available ? null : KeyDisabledFill);

        var label = key.Label.Target;
        if (label != null && !label.IsDestroyed)
            label.Color.Value = available ? _pasteLook.Label : InspectorUI.MutedColor;
    }

    // PREVIEW

    private void UpdatePreview(TextInput? input)
    {
        var preview = _preview.Target;
        if (preview == null || preview.IsDestroyed)
            return;

        string next = string.Empty;
        if (input != null && !input.IsDestroyed)
        {
            string value = input.Text.Value ?? string.Empty;
            // A password field stays a password field on the big readable panel in front of your face.
            if (input.Mask.Value && value.Length > 0)
                value = new string('•', value.Length);
            int caret = System.Math.Clamp(input.CaretIndex, 0, value.Length);
            next = value.Insert(caret, "|");
        }

        // Only on change: writing the content every frame re-tessellates the strip's chunk every frame.
        if (preview.Content.Value != next)
            preview.Content.Value = next;
    }

    // LAYOUT
    // The keys and the panel come from the layout on this slot: the user's favourite keyboard when one was
    // loaded here (FavouriteKeyboard), else the built-in one, built here now. Either is taken up the same way:
    // every key types through this keyboard, and the panel gets what this keyboard needs of it, its own
    // overlay band above the dash and a grab that never types. Nothing else of the layout is touched, so a
    // favourite keeps its look; only the key faces (they follow the glyphs and shift) and the shift and paste
    // keys' fills (they show their state) are this keyboard's to write. -xlinka

    private void TakeUpLayout()
    {
        if (_built || Slot == null || Slot.IsDestroyed)
            return;
        _built = true;

        var design = Slot.GetComponent<KeyboardDesign>() ?? VrKeyboardLayout.Build(Slot);
        if (Slot.GetComponent<RectTransform>() == null)
            VrKeyboardLayout.Size(Slot.AttachComponent<RectTransform>());

        // Overlay + a reserved band: a keyboard that world geometry can cut into is unusable, and the
        // depth test alone would clip it against whatever you happen to be standing in front of.
        var canvas = Slot.GetOrAttachComponent<Canvas>();
        canvas.Overlay.Value = true;
        canvas.SortingOrder.Value = KeyboardSortingOrder;
        _canvas = canvas;

        // Grabbable and scalable so a user can put it where they like and size it to their reach
        // rather than ours; the userspace pointer rig's hands carry the Grabber that takes it.
        _grab = Slot.GetOrAttachComponent<Grabbable>();
        _grab.AllowGrab.Value = true;
        _grab.Scalable.Value = true;
        _grab.FollowRotation.Value = true;
        _grab.Receivable.Value = false;
        _grab.KeepMomentum.Value = false;
        _grab.OnLocalGrabbed += OnGrabbed;
        _grab.OnLocalReleased += OnReleased;

        _keys.Clear();
        foreach (var key in Slot.GetComponentsInChildren<VrKey>())
        {
            if (key.IsDestroyed)
                continue;
            key.Keyboard.Target = this;
            if (key.Slot.GetComponent<Button>() is { } button)
            {
                button.KeepPressUntilRelease.Value = key.Repeats;
                button.SetAction(key.OnPressed);
            }
            _keys.Add(key);
            if (key.Role.Value == VrKeyRole.Shift && _shiftKey.Target == null)
                _shiftKey.Target = key;
            else if (key.Role.Value == VrKeyRole.Paste && _pasteKey.Target == null)
                _pasteKey.Target = key;
        }
        _preview.Target = design.Preview.Target!;
        _shiftLook = KeyLook.Of(_shiftKey.Target);
        _pasteLook = KeyLook.Of(_pasteKey.Target);
    }

    // A key's fill and shades as its layout gave them, for a key whose fill shows a state to come back to.
    private readonly struct KeyLook
    {
        private readonly ColorDriver? _driver;
        private readonly color _normal, _highlight, _pressed, _disabled;
        public readonly color Label;

        private KeyLook(ColorDriver? driver, in color label)
        {
            _driver = driver;
            Label = label;
            _normal = driver?.NormalColor.Value ?? default;
            _highlight = driver?.HighlightColor.Value ?? default;
            _pressed = driver?.PressedColor.Value ?? default;
            _disabled = driver?.DisabledColor.Value ?? default;
        }

        // The driver behind the key's backing, whatever kind of graphic that is.
        public static KeyLook Of(VrKey? key)
        {
            if (key?.Slot == null || key.IsDestroyed)
                return new KeyLook(null, InspectorUI.TextColor);
            ColorDriver? found = null;
            foreach (var driver in key.Slot.GetComponents<ColorDriver>())
            {
                if (driver.Target.Target != null)
                {
                    found = driver;
                    break;
                }
            }
            return new KeyLook(found, key.Label.Target is { IsDestroyed: false } label ? label.Color.Value : InspectorUI.TextColor);
        }

        // A state's fill, with its shades made from it; null puts the key's own back.
        public void Show(color? state)
        {
            if (_driver == null || _driver.IsDestroyed)
                return;
            if (state is { } fill)
            {
                _driver.SetColors(fill);
                return;
            }
            Set(_driver.NormalColor, _normal);
            Set(_driver.HighlightColor, _highlight);
            Set(_driver.PressedColor, _pressed);
            Set(_driver.DisabledColor, _disabled);
        }

        private static void Set(Sync<color> field, in color value)
        {
            if (field.Value != value)
                field.Value = value;
        }
    }
}
