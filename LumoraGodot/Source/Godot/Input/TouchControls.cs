// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core.Components;
using Lumora.Core.Input;
using Lumora.Core.Input.Actions;
using Lumora.Core.Math;
using Lumora.Source.Godot.Input.Drivers;
using Lumora.Source.Godot.UI;
using Lumora.Source.UI;

namespace Lumora.Source.Godot.Input;

// Phone controls feed the existing action map, so movement permissions, dashboard gates and tool
// behavior stay shared with desktop/VR. Each finger keeps its role until released or canceled.
public partial class TouchControls : Control, IInputDriver
{
    [Flags]
    private enum ActionButton { None = 0, Use = 1, Grab = 2, Jump = 4, Crouch = 8, Run = 16, Tools = 32, Menu = 64, Camera = 128, Pointer = 256 }
    private enum FingerRole { Move, Look, Pointer, Scroll, Button }
    private readonly record struct Finger(FingerRole Role, ActionButton Button = ActionButton.None);
    private readonly record struct HitButton(ActionButton Action, Rect2 Rect);
    private readonly Dictionary<int, Finger> _fingers = new();
    private readonly List<HitButton> _buttons = new();
    private InputInterface? _input;
    private GodotMouseDriver? _mouse;
    private DesktopCameraController? _camera;
    private Font _font = null!;
    private bool _alive;
    private bool _focused = true;
    private bool _pointerMode;
    private bool _dashboard;
    private bool _oldQuitOnBack;
    private ActionButton _held;
    private ActionButton _pending;
    private ActionButton _toggles;
    private Vector2 _move;
    private Vector2 _stickCenter;
    private Vector2 _stickOffset;
    private float _unit = 1f;
    private float _stickRadius;
    private int _moveFinger = -1;
    private int _lookFinger = -1;
    private int _pointerFinger = -1;
    private object? _focusedWorld;
    private Vector2 _layoutSize;

    public static TouchControls? Instance { get; private set; }
    public bool PointerActive => _alive && _focused && _pointerMode;
    public Vector2 PointerPosition { get; private set; }
    public int UpdateOrder => 100;

    public void Initialize(InputInterface input, GodotMouseDriver mouse, DesktopCameraController camera)
    {
        _input = input;
        _mouse = mouse;
        _camera = camera;
    }

    public void RegisterInputs(InputInterface inputInterface) => _input = inputInterface;

    public override void _Ready()
    {
        Instance = this;
        _alive = true;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _font = GD.Load<Font>("res://Assets/Fonts/Lato/Lato-Semibold.ttf");
        _oldQuitOnBack = GetTree().QuitOnGoBack;
        GetTree().QuitOnGoBack = false;
        if (_mouse != null) _mouse.TouchEnabled = true;
        Resized += Reflow;
        Reflow();
    }

    public override void _ExitTree()
    {
        _alive = false;
        ResetTouches();
        if (_mouse != null) _mouse.TouchEnabled = false;
        if (Instance == this) Instance = null;
        Resized -= Reflow;
        GetTree().QuitOnGoBack = _oldQuitOnBack;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut || what == NotificationApplicationPaused)
        {
            _focused = false;
            ResetTouches();
        }
        else if (what == NotificationApplicationFocusIn || what == NotificationApplicationResumed)
            _focused = true;
        else if (what == NotificationWMGoBackRequest && _alive)
        {
            ResetTouches();
            if (_pointerMode && !_dashboard) _pending |= ActionButton.Tools;
            else _pending |= ActionButton.Menu;
        }
    }

    public override void _Process(double delta)
    {
        RefreshMode();
        var world = Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld;
        if (!ReferenceEquals(world, _focusedWorld))
        {
            _focusedWorld = world;
            ResetTouches();
        }
        if (Size != _layoutSize) Reflow();
    }

    private void RefreshMode()
    {
        bool enabled = _alive && _focused && _input?.IsVRActive != true;
        Visible = enabled;
        if (_mouse != null) _mouse.TouchEnabled = enabled;
        if (!enabled)
        {
            ResetTouches();
            return;
        }
        bool dashboard = _input?.IsDashboardOpen == true || DashboardToggle.IsDashboardVisible;
        var state = UserInputState.ForFocusedLocalUser;
        // A focused field keeps a phone's screen a pointer: touches go to the panel being typed on, not
        // to the view.
        bool pointer = dashboard || state?.DesktopInputSuppressed == true || state?.ViewHeld == true
            || Helio.UI.TextInput.Focused is { IsDestroyed: false }
            || (state?.MouseLookSuppressed == true && !state.ExternalCameraActive);
        if (pointer != _pointerMode || dashboard != _dashboard)
        {
            ResetTouches();
            _pointerMode = pointer;
            _dashboard = dashboard;
            PointerPosition = Size * 0.5f;
            Reflow();
        }
    }

    private void Reflow()
    {
        ResetTouches();
        _layoutSize = Size;
        // Use logical viewport units; density and render resolution must not shrink the touch targets.
        _unit = Mathf.Clamp(Mathf.Min(Size.X / 800f, Size.Y / 450f), 0.65f, 2.5f);
        Rect2 safe = new(Vector2.Zero, Size);
        if (OS.HasFeature("android"))
        {
            var area = DisplayServer.GetDisplaySafeArea();
            var window = DisplayServer.WindowGetSize();
            if (window.X > 0 && window.Y > 0 && area.Size.X > 0 && area.Size.Y > 0)
            {
                Vector2 ratio = Size / new Vector2(window.X, window.Y);
                safe = safe.Intersection(new Rect2((Vector2)area.Position * ratio, (Vector2)area.Size * ratio));
            }
        }
        float pad = 20f * _unit;
        _stickRadius = 64f * _unit;
        _stickCenter = new Vector2(safe.Position.X + pad + _stickRadius, safe.End.Y - pad - _stickRadius);
        _buttons.Clear();
        var buttonSize = new Vector2(72f, 48f) * _unit;
        _buttons.Add(new HitButton(ActionButton.Menu, new Rect2(safe.End.X - pad - buttonSize.X, safe.Position.Y + pad, buttonSize.X, buttonSize.Y)));
        if (!_pointerMode)
        {
            _buttons.Add(new HitButton(ActionButton.Camera, new Rect2(safe.End.X - pad - buttonSize.X * 2f - 8f * _unit, safe.Position.Y + pad, buttonSize.X, buttonSize.Y)));
            ActionButton[] actions = { ActionButton.Use, ActionButton.Grab, ActionButton.Jump, ActionButton.Crouch, ActionButton.Run, ActionButton.Tools };
            for (int i = 0; i < actions.Length; i++)
            {
                var at = new Vector2(safe.End.X - pad - buttonSize.X * (2 - i % 2) - (i % 2 == 0 ? 8f * _unit : 0f),
                    safe.End.Y - pad - buttonSize.Y * (3 - i / 2) - (2 - i / 2) * 8f * _unit);
                _buttons.Add(new HitButton(actions[i], new Rect2(at, buttonSize)));
            }
        }
        QueueRedraw();
    }

    public override void _Input(InputEvent @event)
    {
        if (!_alive || _input == null) return;
        RefreshMode();
        if (!Visible) return;
        if (@event is InputEventScreenTouch touch)
        {
            if (touch.Canceled)
            {
                if (_fingers.TryGetValue(touch.Index, out var canceled))
                {
                    _pending &= ~(canceled.Button | (canceled.Role == FingerRole.Pointer ? ActionButton.Pointer : ActionButton.None));
                    _toggles &= ~canceled.Button;
                }
                ReleaseFinger(touch.Index);
                _mouse?.ClearTouchInput();
            }
            else if (!touch.Pressed) ReleaseFinger(touch.Index);
            else BeginFinger(touch.Index, touch.Position);
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventScreenDrag drag && _fingers.TryGetValue(drag.Index, out var finger))
        {
            switch (finger.Role)
            {
                case FingerRole.Move: MoveStick(drag.Position); break;
                case FingerRole.Look: AddLook(drag.Relative); break;
                case FingerRole.Pointer: PointerPosition = drag.Position; break;
                case FingerRole.Scroll:
                    Lumora.Core.Components.UI.UserspaceDashboard.LocalInstance?.FeedAxis(new float2(0f, drag.Relative.Y / (40f * _unit)));
                    break;
            }
            GetViewport().SetInputAsHandled();
        }
    }

    private void BeginFinger(int id, Vector2 position)
    {
        ReleaseFinger(id);
        foreach (var button in _buttons)
        {
            if (!button.Rect.HasPoint(position)) continue;
            _fingers[id] = new Finger(FingerRole.Button, button.Action);
            if (button.Action is ActionButton.Grab or ActionButton.Crouch or ActionButton.Run)
                _toggles ^= button.Action;
            else
            {
                _held |= button.Action;
                _pending |= button.Action;
            }
            QueueRedraw();
            return;
        }
        if (_pointerMode)
        {
            if (_pointerFinger < 0)
            {
                _pointerFinger = id;
                PointerPosition = position;
                _held |= ActionButton.Pointer;
                _pending |= ActionButton.Pointer;
                _fingers[id] = new Finger(FingerRole.Pointer);
            }
            else
                _fingers[id] = new Finger(FingerRole.Scroll);
        }
        else if (_moveFinger < 0 && position.DistanceTo(_stickCenter) <= _stickRadius * 1.5f)
        {
            _moveFinger = id;
            _fingers[id] = new Finger(FingerRole.Move);
            MoveStick(position);
        }
        else if (_lookFinger < 0)
        {
            _lookFinger = id;
            _fingers[id] = new Finger(FingerRole.Look);
        }
    }

    private void ReleaseFinger(int id)
    {
        if (!_fingers.Remove(id, out var finger)) return;
        switch (finger.Role)
        {
            case FingerRole.Move: _moveFinger = -1; _move = _stickOffset = Vector2.Zero; break;
            case FingerRole.Look: _lookFinger = -1; break;
            case FingerRole.Pointer: _pointerFinger = -1; _held &= ~ActionButton.Pointer; break;
            case FingerRole.Button:
                bool another = false;
                foreach (var active in _fingers.Values) another |= active.Button == finger.Button;
                if (!another) _held &= ~finger.Button;
                break;
        }
        QueueRedraw();
    }

    private void MoveStick(Vector2 position)
    {
        _stickOffset = (position - _stickCenter).LimitLength(_stickRadius);
        _move = new Vector2(_stickOffset.X, -_stickOffset.Y) / _stickRadius;
        QueueRedraw();
    }

    private void AddLook(Vector2 relative)
    {
        // Match the existing camera's reference resolution, independent of phone density/stretch.
        var motion = relative * (1080f / Mathf.Max(Size.Y, 1f));
        _mouse?.AddTouchMotion(motion);
        _camera?.AddTouchMotion(motion);
    }

    public void ResetTouches()
    {
        _fingers.Clear();
        _moveFinger = _lookFinger = _pointerFinger = -1;
        _move = _stickOffset = Vector2.Zero;
        _held = _pending = _toggles = ActionButton.None;
        _mouse?.ClearTouchInput();
        if (IsInsideTree()) QueueRedraw();
    }

    public void UpdateInputs(float deltaTime)
    {
        if (!_alive || !_focused || _input == null || _input.IsVRActive) return;
        var actions = _input.Actions;
        if (actions == null) return;
        var buttons = _held | _pending | _toggles;
        _pending = ActionButton.None;
        bool Has(ActionButton button) => (buttons & button) != 0;
        static void Press(DigitalAction action, bool held) { if (held) action.ExternalInput = true; }
        if (!_pointerMode)
        {
            actions.Locomotion.Move.ExternalInput = (actions.Locomotion.Move.ExternalInput ?? float2.Zero) + new float2(_move.X, _move.Y);
            actions.Camera.FlyMove.ExternalInput = new float2(_move.X, _move.Y);
            Press(actions.Locomotion.Jump, Has(ActionButton.Jump));
            Press(actions.Locomotion.Crouch, Has(ActionButton.Crouch));
            Press(actions.Locomotion.Sprint, Has(ActionButton.Run));
            actions.Locomotion.Fly.ExternalInput = (Has(ActionButton.Jump) ? 1f : 0f) - (Has(ActionButton.Crouch) ? 1f : 0f);
            Press(actions.Right.Grab, Has(ActionButton.Grab));
            Press(actions.Camera.ThirdPerson, Has(ActionButton.Camera));
        }
        Press(actions.Right.Primary, Has(ActionButton.Use) || Has(ActionButton.Pointer));
        Press(actions.Right.ContextMenu, Has(ActionButton.Tools));
        Press(actions.Menu.ToggleDashboard, Has(ActionButton.Menu));
        _mouse?.SetTouchPointer(PointerPosition, _pointerMode && Has(ActionButton.Pointer));
    }

    public override void _Draw()
    {
        if (_font == null) return;
        var accent = LoadingScreen.ThemeColor(Lumora.Core.Components.UI.DashTheme.Accent);
        var ink = new Color(1f, 1f, 1f, 0.82f);
        var fill = new Color(0.075f, 0.071f, 0.11f, 0.48f);
        if (!_pointerMode)
        {
            DrawCircle(_stickCenter, _stickRadius, fill);
            DrawArc(_stickCenter, _stickRadius, 0f, Mathf.Tau, 64, new Color(ink, 0.3f), 1.5f * _unit, true);
            DrawCircle(_stickCenter + _stickOffset * 0.65f, 23f * _unit, new Color(accent, 0.6f));
            DrawCircle(Size * 0.5f, 2f * _unit, ink);
        }
        int fontSize = Mathf.RoundToInt(14f * _unit);
        foreach (var button in _buttons)
        {
            bool pressed = ((_held | _toggles) & button.Action) != 0;
            DrawRect(button.Rect, pressed ? new Color(accent, 0.65f) : fill);
            DrawRect(button.Rect, new Color(ink, pressed ? 0.7f : 0.22f), false, _unit);
            string label = button.Action == ActionButton.Menu ? (_dashboard ? "Close" : "Menu") : button.Action.ToString();
            var textSize = _font.GetStringSize(label, fontSize: fontSize);
            DrawString(_font, button.Rect.GetCenter() + new Vector2(-textSize.X * 0.5f, _font.GetAscent(fontSize) * 0.35f), label, fontSize: fontSize, modulate: ink);
        }
    }
}
