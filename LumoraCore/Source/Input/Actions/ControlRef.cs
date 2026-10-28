// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Core.Input.Actions;

// Which physical device family a control lives on.
public enum InputDeviceKind
{
    None = 0,
    Keyboard,
    Mouse,
    Gamepad,
    VRController
}

// Identity of one physical control, stable across saves.
//
// Control ids are STRINGS, not enum ordinals: a saved binding file has to survive us inserting a
// value in the middle of the Key enum, and it has to be legible when someone opens the config to
// see what they bound. The cost is a dictionary lookup per read, which is nothing next to a frame.
// Side is only meaningful for VR, where the same control id exists twice. -xlinka
public readonly struct ControlRef : IEquatable<ControlRef>
{
    public readonly InputDeviceKind Device;
    public readonly string Control;
    public readonly Chirality Side;

    public ControlRef(InputDeviceKind device, string control, Chirality side = Chirality.None)
    {
        Device = device;
        Control = control ?? string.Empty;
        Side = side;
    }

    public bool IsValid => Device != InputDeviceKind.None && !string.IsNullOrEmpty(Control);

    public static ControlRef Key(Key key) => new ControlRef(InputDeviceKind.Keyboard, InputControlCatalog.KeyId(key));
    public static ControlRef Mouse(string control) => new ControlRef(InputDeviceKind.Mouse, control);
    public static ControlRef Pad(string control) => new ControlRef(InputDeviceKind.Gamepad, control);
    public static ControlRef VR(Chirality side, string control) => new ControlRef(InputDeviceKind.VRController, control, side);

    // Wire form: "Device/Control" or "Device/Side/Control" for chiral devices.
    public string Serialize()
        => Side == Chirality.None ? $"{Device}/{Control}" : $"{Device}/{Side}/{Control}";

    public static bool TryParse(string? text, out ControlRef control)
    {
        control = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('/');
        if (parts.Length < 2 || parts.Length > 3)
            return false;
        if (!Enum.TryParse<InputDeviceKind>(parts[0], ignoreCase: true, out var device) || device == InputDeviceKind.None)
            return false;

        if (parts.Length == 2)
        {
            control = new ControlRef(device, parts[1]);
            return control.IsValid;
        }

        if (!Enum.TryParse<Chirality>(parts[1], ignoreCase: true, out var side))
            return false;
        control = new ControlRef(device, parts[2], side);
        return control.IsValid;
    }

    public string Describe() => InputControlCatalog.Describe(this);

    public bool Equals(ControlRef other)
        => Device == other.Device
           && Side == other.Side
           && string.Equals(Control, other.Control, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ControlRef other && Equals(other);
    public override int GetHashCode() => HashCode.Combine((int)Device, (int)Side, Control ?? string.Empty);
    public override string ToString() => Serialize();

    public static bool operator ==(ControlRef a, ControlRef b) => a.Equals(b);
    public static bool operator !=(ControlRef a, ControlRef b) => !a.Equals(b);
}

// The controls each device family exposes, their display names, and the order the rebind listener
// scans them in.
//
// The keyboard list is CURATED rather than reflected off the Key enum: that enum has several
// colliding values (Keypad4 shares PageUp's number, Keypad9 shares UpArrow's, and so on), so
// Enum.GetName picks a name at random for those and a round-tripped binding could come back as a
// different key. Everything in this list has a unique value, which makes the wire form honest.
// -xlinka
public static class InputControlCatalog
{
    // MOUSE
    public const string MouseLeft = "Left";
    public const string MouseRight = "Right";
    public const string MouseMiddle = "Middle";
    public const string MouseButton4 = "Button4";
    public const string MouseButton5 = "Button5";
    public const string MouseWheel = "Wheel";
    public const string MouseDelta = "Delta";

    // GAMEPAD - named by physical position, not by one vendor's letters, so a DualShock or a Switch
    // pad reads the same way. The display name carries both labels.
    public const string PadFaceDown = "FaceDown";
    public const string PadFaceRight = "FaceRight";
    public const string PadFaceLeft = "FaceLeft";
    public const string PadFaceUp = "FaceUp";
    public const string PadLeftShoulder = "LeftShoulder";
    public const string PadRightShoulder = "RightShoulder";
    public const string PadLeftTrigger = "LeftTrigger";
    public const string PadRightTrigger = "RightTrigger";
    public const string PadLeftStickPress = "LeftStickPress";
    public const string PadRightStickPress = "RightStickPress";
    public const string PadStart = "Start";
    public const string PadBack = "Back";
    public const string PadGuide = "Guide";
    public const string PadDPadUp = "DPadUp";
    public const string PadDPadDown = "DPadDown";
    public const string PadDPadLeft = "DPadLeft";
    public const string PadDPadRight = "DPadRight";
    public const string PadLeftStick = "LeftStick";
    public const string PadRightStick = "RightStick";

    // VR CONTROLLER
    public const string VRTrigger = "Trigger";
    public const string VRGrip = "Grip";
    public const string VRPrimary = "Primary";
    public const string VRSecondary = "Secondary";
    public const string VRFaceLeft = "FaceLeft";
    public const string VRFaceUp = "FaceUp";
    public const string VRShoulder = "Shoulder";
    public const string VRSelectPress = "SelectPress";
    public const string VRMenu = "Menu";
    public const string VRStick = "Stick";
    public const string VRStickPress = "StickPress";
    public const string VRStickTouch = "StickTouch";
    public const string VRPad = "Pad";
    public const string VRPadPress = "PadPress";

    // The menu button's two jobs on a controller with no dedicated dash button: MenuHold fires once
    // at full charge, MenuTap fires once on a release before the tap limit. Both are one-frame
    // pulses, never held, and neither fires on a profile that has face buttons. -xlinka
    public const string VRMenuHold = "MenuHold";
    public const string VRMenuTap = "MenuTap";

    private static readonly Key[] _keys =
    {
        Key.A, Key.B, Key.C, Key.D, Key.E, Key.F, Key.G, Key.H, Key.I, Key.J, Key.K, Key.L, Key.M,
        Key.N, Key.O, Key.P, Key.Q, Key.R, Key.S, Key.T, Key.U, Key.V, Key.W, Key.X, Key.Y, Key.Z,
        Key.Alpha0, Key.Alpha1, Key.Alpha2, Key.Alpha3, Key.Alpha4,
        Key.Alpha5, Key.Alpha6, Key.Alpha7, Key.Alpha8, Key.Alpha9,
        Key.Space, Key.Return, Key.Tab, Key.Backspace, Key.Delete, Key.Escape,
        Key.LeftShift, Key.RightShift, Key.LeftControl, Key.RightControl, Key.LeftAlt, Key.RightAlt,
        Key.UpArrow, Key.DownArrow, Key.LeftArrow, Key.RightArrow,
        Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.Insert, Key.CapsLock,
        Key.Minus, Key.Equals, Key.LeftBracket, Key.RightBracket, Key.Backslash,
        Key.Semicolon, Key.Quote, Key.Comma, Key.Period, Key.Slash, Key.BackQuote,
        Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6,
        Key.F7, Key.F8, Key.F9, Key.F10, Key.F11, Key.F12
    };

    private static readonly string[] _mouseControls =
    {
        MouseLeft, MouseRight, MouseMiddle, MouseButton4, MouseButton5, MouseWheel, MouseDelta
    };

    private static readonly string[] _padControls =
    {
        PadFaceDown, PadFaceRight, PadFaceLeft, PadFaceUp,
        PadLeftShoulder, PadRightShoulder, PadLeftTrigger, PadRightTrigger,
        PadLeftStickPress, PadRightStickPress, PadStart, PadBack, PadGuide,
        PadDPadUp, PadDPadDown, PadDPadLeft, PadDPadRight,
        PadLeftStick, PadRightStick
    };

    private static readonly string[] _vrControls =
    {
        VRTrigger, VRGrip, VRPrimary, VRSecondary, VRMenu, VRStick, VRStickPress, VRStickTouch,
        VRPad, VRPadPress, VRMenuHold, VRMenuTap, VRSelectPress, VRFaceLeft, VRFaceUp, VRShoulder
    };

    // Both directions are built in one pass from one source of truth. Two separate initializers that
    // called each other bit once already: field initializers run top to bottom, so the first one to
    // run read the second one's dictionary while it was still null. -xlinka
    private static readonly Dictionary<Key, string> _keyIds = new Dictionary<Key, string>();
    private static readonly Dictionary<string, Key> _keysById = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);

    static InputControlCatalog()
    {
        foreach (var key in _keys)
        {
            var id = Enum.GetName(typeof(Key), key) ?? ((int)key).ToString();
            _keyIds[key] = id;
            _keysById[id] = key;
        }
    }

    public static IReadOnlyList<Key> Keys => _keys;

    public static string KeyId(Key key)
        => _keyIds.TryGetValue(key, out var id) ? id : (Enum.GetName(typeof(Key), key) ?? ((int)key).ToString());

    public static bool TryParseKey(string? id, out Key key)
    {
        key = Key.None;
        return !string.IsNullOrEmpty(id) && _keysById.TryGetValue(id, out key);
    }

    public static IReadOnlyList<string> ControlsFor(InputDeviceKind device) => device switch
    {
        InputDeviceKind.Mouse => _mouseControls,
        InputDeviceKind.Gamepad => _padControls,
        InputDeviceKind.VRController => _vrControls,
        _ => Array.Empty<string>()
    };

    // True for controls that only ever produce a two-axis value.
    public static bool IsVectorControl(in ControlRef control) => control.Device switch
    {
        InputDeviceKind.Mouse => control.Control == MouseDelta,
        InputDeviceKind.Gamepad => control.Control is PadLeftStick or PadRightStick,
        InputDeviceKind.VRController => control.Control is VRStick or VRPad,
        _ => false
    };

    // Controls whose press is a latch with its own thresholds. A digital action bound to one reads
    // the latch only and never falls back to cutting the analog at its press point: the grip's
    // analog is finger curl and sits above any sane cut from a relaxed hold, and a bare cut beside
    // the trigger's latch would bring back the chatter its release band is there to stop. -xlinka
    public static bool PressIsLatched(in ControlRef control)
        => control.Device == InputDeviceKind.VRController && control.Control is VRGrip or VRTrigger;

    public static string Describe(in ControlRef control)
    {
        if (!control.IsValid)
            return "-";

        switch (control.Device)
        {
            case InputDeviceKind.Keyboard:
                return DescribeKey(control.Control);
            case InputDeviceKind.Mouse:
                return "Mouse " + control.Control switch
                {
                    MouseLeft => "Left",
                    MouseRight => "Right",
                    MouseMiddle => "Middle",
                    MouseButton4 => "4",
                    MouseButton5 => "5",
                    MouseWheel => "Wheel",
                    MouseDelta => "Move",
                    _ => control.Control
                };
            case InputDeviceKind.Gamepad:
                // A Deck's buttons are printed on the Deck; its controls screen reads them, not two pads' letterings.
                if (SteamDeckPlatform.IsSteamDeck)
                    return InputHints.PadName(control.Control, PadFamily.SteamDeck);
                return control.Control switch
                {
                    PadFaceDown => "A / Cross",
                    PadFaceRight => "B / Circle",
                    PadFaceLeft => "X / Square",
                    PadFaceUp => "Y / Triangle",
                    PadLeftShoulder => "LB",
                    PadRightShoulder => "RB",
                    PadLeftTrigger => "LT",
                    PadRightTrigger => "RT",
                    PadLeftStickPress => "L3",
                    PadRightStickPress => "R3",
                    PadStart => "Start",
                    PadBack => "Back",
                    PadGuide => "Guide",
                    PadDPadUp => "D-Pad Up",
                    PadDPadDown => "D-Pad Down",
                    PadDPadLeft => "D-Pad Left",
                    PadDPadRight => "D-Pad Right",
                    PadLeftStick => "Left Stick",
                    PadRightStick => "Right Stick",
                    _ => control.Control
                };
            case InputDeviceKind.VRController:
                string hand = control.Side == Chirality.Left ? "L" : control.Side == Chirality.Right ? "R" : "";
                string name = control.Control switch
                {
                    VRTrigger => "Trigger",
                    VRGrip => "Grip",
                    VRPrimary => "A / X",
                    VRSecondary => "B / Y",
                    VRFaceLeft => "X / D-Pad Left",
                    VRFaceUp => "Y / D-Pad Up",
                    VRShoulder => "Shoulder",
                    VRMenu => "Menu",
                    VRStick => "Stick",
                    VRStickPress => "Stick Press",
                    VRStickTouch => "Stick Touch",
                    VRPad => "Touchpad",
                    VRPadPress => "Touchpad Press",
                    VRSelectPress => "Stick / Index Touchpad Press",
                    VRMenuHold => "Menu (hold)",
                    VRMenuTap => "Menu (tap)",
                    _ => control.Control
                };
                return string.IsNullOrEmpty(hand) ? name : $"{hand} {name}";
            default:
                return control.Control;
        }
    }

    private static string DescribeKey(string id)
    {
        if (!TryParseKey(id, out var key))
            return id;
        return key switch
        {
            Key.Alpha0 => "0",
            Key.Alpha1 => "1",
            Key.Alpha2 => "2",
            Key.Alpha3 => "3",
            Key.Alpha4 => "4",
            Key.Alpha5 => "5",
            Key.Alpha6 => "6",
            Key.Alpha7 => "7",
            Key.Alpha8 => "8",
            Key.Alpha9 => "9",
            Key.LeftShift => "L Shift",
            Key.RightShift => "R Shift",
            Key.LeftControl => "L Ctrl",
            Key.RightControl => "R Ctrl",
            Key.LeftAlt => "L Alt",
            Key.RightAlt => "R Alt",
            Key.UpArrow => "Up",
            Key.DownArrow => "Down",
            Key.LeftArrow => "Left",
            Key.RightArrow => "Right",
            Key.Return => "Enter",
            Key.BackQuote => "`",
            Key.LeftBracket => "[",
            Key.RightBracket => "]",
            Key.Semicolon => ";",
            Key.Quote => "'",
            Key.Comma => ",",
            Key.Period => ".",
            Key.Slash => "/",
            Key.Backslash => "\\",
            Key.Minus => "-",
            Key.Equals => "=",
            _ => id
        };
    }
}
