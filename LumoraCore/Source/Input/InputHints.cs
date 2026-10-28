// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input.Actions;

namespace Lumora.Core.Input;

public enum PadFamily
{
    Xbox,
    PlayStation,
    Nintendo,
    SteamDeck,
}

// What to call a control in a hint on screen ("Enter sends", "B closes"), in the words of the hardware the
// person last touched.
//
// The pad's own lettering, not the controls screen's double names: a hint has room for one word, and the word
// has to be the one printed on the button under the thumb. A Deck reads L1 and View where an Xbox pad reads LB
// and View and a DualSense reads L1 and Create. Steam hands every pad to the game as an Xbox pad, so the
// family comes from the Deck check first and the pad's name after. -xlinka
public static class InputHints
{
    public static InputBindingMap? Map => Engine.Current?.InputInterface?.Actions;

    // True while the last thing pressed was on the pad.
    public static bool PadNames => Map?.HintDevice == InputDeviceKind.Gamepad;

    public static PadFamily Family => FamilyOf(Engine.Current?.InputInterface?.Gamepad?.DeviceName);

    public static PadFamily FamilyOf(string? padName)
    {
        if (SteamDeckPlatform.IsSteamDeck)
            return PadFamily.SteamDeck;
        string name = padName ?? string.Empty;
        if (Has(name, "PS3") || Has(name, "PS4") || Has(name, "PS5") || Has(name, "DualSense") || Has(name, "DualShock")
            || Has(name, "PlayStation") || Has(name, "Sony"))
            return PadFamily.PlayStation;
        if (Has(name, "Nintendo") || Has(name, "Switch") || Has(name, "Joy-Con") || Has(name, "Pro Controller"))
            return PadFamily.Nintendo;
        return PadFamily.Xbox;
    }

    private static bool Has(string text, string part) => text.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;

    public static string PadName(string control, PadFamily family)
    {
        bool ps = family == PadFamily.PlayStation;
        bool nintendo = family == PadFamily.Nintendo;
        bool xbox = family == PadFamily.Xbox;
        return control switch
        {
            InputControlCatalog.PadFaceDown => ps ? "Cross" : nintendo ? "B" : "A",
            InputControlCatalog.PadFaceRight => ps ? "Circle" : nintendo ? "A" : "B",
            InputControlCatalog.PadFaceLeft => ps ? "Square" : nintendo ? "Y" : "X",
            InputControlCatalog.PadFaceUp => ps ? "Triangle" : nintendo ? "X" : "Y",
            InputControlCatalog.PadLeftShoulder => xbox ? "LB" : nintendo ? "L" : "L1",
            InputControlCatalog.PadRightShoulder => xbox ? "RB" : nintendo ? "R" : "R1",
            InputControlCatalog.PadLeftTrigger => xbox ? "LT" : nintendo ? "ZL" : "L2",
            InputControlCatalog.PadRightTrigger => xbox ? "RT" : nintendo ? "ZR" : "R2",
            InputControlCatalog.PadLeftStickPress => "L3",
            InputControlCatalog.PadRightStickPress => "R3",
            InputControlCatalog.PadStart => ps ? "Options" : nintendo ? "+" : "Menu",
            InputControlCatalog.PadBack => ps ? "Create" : nintendo ? "-" : "View",
            InputControlCatalog.PadGuide => family == PadFamily.SteamDeck ? "Steam" : ps ? "PS" : nintendo ? "Home" : "Guide",
            InputControlCatalog.PadDPadUp => "D-pad Up",
            InputControlCatalog.PadDPadDown => "D-pad Down",
            InputControlCatalog.PadDPadLeft => "D-pad Left",
            InputControlCatalog.PadDPadRight => "D-pad Right",
            InputControlCatalog.PadLeftStick => "Left Stick",
            InputControlCatalog.PadRightStick => "Right Stick",
            _ => control
        };
    }

    // The first control an action answers to on the hardware hints follow; the other desk's control when
    // that one has none, and the action's own label when nothing is bound at all.
    public static string Name(InputAction? action)
    {
        if (action == null)
            return string.Empty;
        bool pad = PadNames;
        string? found = pad ? FirstPad(action) : FirstDesk(action);
        found ??= pad ? FirstDesk(action) : FirstPad(action);
        return found ?? action.Label;
    }

    private static string? FirstPad(InputAction action)
    {
        var family = Family;
        foreach (var binding in action.Bindings)
        {
            if (binding.Control.Device == InputDeviceKind.Gamepad && binding.Modifiers.Count == 0)
                return PadName(binding.Control.Control, family);
        }
        return null;
    }

    private static string? FirstDesk(InputAction action)
    {
        foreach (var binding in action.Bindings)
        {
            if (binding.Control.Device is InputDeviceKind.Keyboard or InputDeviceKind.Mouse)
                return binding.Describe();
        }
        return null;
    }
}
