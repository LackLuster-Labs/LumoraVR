// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Text;
using Lumora.Core.Input;
using Godot;
using EngineKey = Lumora.Core.Input.Key;

namespace Lumora.Source.Godot.Input.Drivers;

public class GodotKeyboardDriver : IKeyboardDriver, IInputDriver
{
    public int UpdateOrder => 0;
    private StringBuilder _typedCharacters = new StringBuilder();

    private static readonly Dictionary<EngineKey, global::Godot.Key> _keyMap = new Dictionary<EngineKey, global::Godot.Key>
    {
        { EngineKey.Backspace, global::Godot.Key.Backspace },
        { EngineKey.Tab, global::Godot.Key.Tab },
        { EngineKey.Return, global::Godot.Key.Enter },
        { EngineKey.KeypadEnter, global::Godot.Key.KpEnter },
        { EngineKey.Escape, global::Godot.Key.Escape },
        { EngineKey.Space, global::Godot.Key.Space },
        { EngineKey.Alpha0, global::Godot.Key.Key0 },
        { EngineKey.Alpha1, global::Godot.Key.Key1 },
        { EngineKey.Alpha2, global::Godot.Key.Key2 },
        { EngineKey.Alpha3, global::Godot.Key.Key3 },
        { EngineKey.Alpha4, global::Godot.Key.Key4 },
        { EngineKey.Alpha5, global::Godot.Key.Key5 },
        { EngineKey.Alpha6, global::Godot.Key.Key6 },
        { EngineKey.Alpha7, global::Godot.Key.Key7 },
        { EngineKey.Alpha8, global::Godot.Key.Key8 },
        { EngineKey.Alpha9, global::Godot.Key.Key9 },
        { EngineKey.A, global::Godot.Key.A },
        { EngineKey.B, global::Godot.Key.B },
        { EngineKey.C, global::Godot.Key.C },
        { EngineKey.D, global::Godot.Key.D },
        { EngineKey.E, global::Godot.Key.E },
        { EngineKey.F, global::Godot.Key.F },
        { EngineKey.G, global::Godot.Key.G },
        { EngineKey.H, global::Godot.Key.H },
        { EngineKey.I, global::Godot.Key.I },
        { EngineKey.J, global::Godot.Key.J },
        { EngineKey.K, global::Godot.Key.K },
        { EngineKey.L, global::Godot.Key.L },
        { EngineKey.M, global::Godot.Key.M },
        { EngineKey.N, global::Godot.Key.N },
        { EngineKey.O, global::Godot.Key.O },
        { EngineKey.P, global::Godot.Key.P },
        { EngineKey.Q, global::Godot.Key.Q },
        { EngineKey.R, global::Godot.Key.R },
        { EngineKey.S, global::Godot.Key.S },
        { EngineKey.T, global::Godot.Key.T },
        { EngineKey.U, global::Godot.Key.U },
        { EngineKey.V, global::Godot.Key.V },
        { EngineKey.W, global::Godot.Key.W },
        { EngineKey.X, global::Godot.Key.X },
        { EngineKey.Y, global::Godot.Key.Y },
        { EngineKey.Z, global::Godot.Key.Z },
        { EngineKey.Delete, global::Godot.Key.Delete },
        { EngineKey.CapsLock, global::Godot.Key.Capslock },
        { EngineKey.LeftShift, global::Godot.Key.Shift },
        { EngineKey.RightShift, global::Godot.Key.Shift },
        { EngineKey.LeftControl, global::Godot.Key.Ctrl },
        { EngineKey.RightControl, global::Godot.Key.Ctrl },
        { EngineKey.LeftAlt, global::Godot.Key.Alt },
        { EngineKey.RightAlt, global::Godot.Key.Alt },
        { EngineKey.Insert, global::Godot.Key.Insert },
        { EngineKey.Home, global::Godot.Key.Home },
        { EngineKey.End, global::Godot.Key.End },
        { EngineKey.PageUp, global::Godot.Key.Pageup },
        { EngineKey.PageDown, global::Godot.Key.Pagedown },
        { EngineKey.UpArrow, global::Godot.Key.Up },
        { EngineKey.DownArrow, global::Godot.Key.Down },
        { EngineKey.RightArrow, global::Godot.Key.Right },
        { EngineKey.LeftArrow, global::Godot.Key.Left },
        { EngineKey.F1, global::Godot.Key.F1 },
        { EngineKey.F2, global::Godot.Key.F2 },
        { EngineKey.F3, global::Godot.Key.F3 },
        { EngineKey.F4, global::Godot.Key.F4 },
        { EngineKey.F5, global::Godot.Key.F5 },
        { EngineKey.F6, global::Godot.Key.F6 },
        { EngineKey.F7, global::Godot.Key.F7 },
        { EngineKey.F8, global::Godot.Key.F8 },
        { EngineKey.F9, global::Godot.Key.F9 },
        { EngineKey.F10, global::Godot.Key.F10 },
        { EngineKey.F11, global::Godot.Key.F11 },
        { EngineKey.F12, global::Godot.Key.F12 },
    };

    public bool GetKeyState(EngineKey key)
    {
        if (_keyMap.TryGetValue(key, out global::Godot.Key godotKey))
        {
            bool isPressed = global::Godot.Input.IsKeyPressed(godotKey);
            return isPressed;
        }
        return false;
    }

    public string GetTypeDelta()
    {
        string result = _typedCharacters.ToString();
        _typedCharacters.Clear();
        return result;
    }

    public void HandleInputEvent(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed)
        {
            // Shortcut letters are not text. AltGr (Ctrl+Alt) still produces layout characters.
            // Printable OS repeats are text too; deletion repeat is handled by TextInput itself.
            if ((keyEvent.CtrlPressed || keyEvent.MetaPressed) && !keyEvent.AltPressed)
                return;
            if (keyEvent.Unicode >= 32 && keyEvent.Unicode != 127
                && System.Text.Rune.IsValid((int)keyEvent.Unicode))
            {
                _typedCharacters.Append(char.ConvertFromUtf32((int)keyEvent.Unicode));
            }
        }
    }

    public void RegisterInputs(InputInterface inputInterface)
    {
        // Keyboard driver doesn't need to register additional inputs
    }

    public void UpdateInputs(float deltaTime)
    {
        // Keyboard state is polled directly via GetKeyState()
    }

    public void UpdateKeyboard(Keyboard keyboard)
    {
        if (keyboard == null)
            return;

        var pressedKeys = new HashSet<EngineKey>();
        foreach (var kvp in _keyMap)
        {
            if (global::Godot.Input.IsKeyPressed(kvp.Value))
            {
                pressedKeys.Add(kvp.Key);
            }
        }

        string typedText = GetTypeDelta();

        keyboard.UpdateFromDriver(pressedKeys, typedText);
    }
}
