// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
// Desktop only, like the rest of the Steam layer: see SteamManager for why it is compiled out on mobile.
#if GODOT_PC

using System;
using Godot;
using Steamworks;
using SteamDeckPlatform = Lumora.Core.SteamDeckPlatform;
using Lumora.Core.Input;
using Lumora.Core.Input.Actions;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

// Steam's half of a Deck: what Steam says this machine is, and Steam's on-screen keyboard for a text field
// taken with a pad.
//
// The keyboard that floats over the game types ordinary key presses into the window, so a field takes them
// down the same path a real keyboard's go, caret, selection, Enter and all, and nothing here touches the
// text. It comes up when a field takes focus while the pad was the last thing pressed, on a Deck or in
// Steam's big screen mode; the pad's click brings it back after it was put away; it goes when the field does.
// Placed clear of the field: the chat box sits low on the left, so the keyboard goes high; anything else is
// where the pointer is. Where the floating one is refused, Steam's full-screen keyboard is the fallback, and
// its text replaces the field's when it is submitted, which counts as Enter. A headset has its own keyboard.
// -xlinka
public static class SteamDeckBridge
{
    private static bool _ready;
    private static Callback<FloatingGamepadTextInputDismissed_t>? _floatingDismissed;
    private static Callback<GamepadTextInputDismissed_t>? _modalDismissed;
    private static bool _wasFocused;
    private static bool _shown;
    private static bool _modal;
    private static Helio.UI.TextInput? _modalTarget;
    private static double _nextModeCheck;

    public static void Initialize()
    {
        // A headset is never a Deck and has its own keyboard, so none of this applies. It also has to stay
        // out: the arm64 Steam library the Frame build carries comes from a newer SDK that dropped the
        // "running on a Deck" call, and asking it would fail. -xlinka
        if (HeadsetDeviceMode.IsSteamFrame)
        {
            SteamDeckPlatform.ReportStandaloneHeadset();
            LumoraLogger.Log($"Device: {SteamDeckPlatform.Describe()}");
            return;
        }
        if (!SteamManager.Initialized)
        {
            LumoraLogger.Log($"Device: {SteamDeckPlatform.Describe()} (Steam not up)");
            return;
        }
        try
        {
            ReportMode();
            _floatingDismissed = Callback<FloatingGamepadTextInputDismissed_t>.Create(_ => OnFloatingDismissed());
            _modalDismissed = Callback<GamepadTextInputDismissed_t>.Create(OnModalDismissed);
            _ready = true;
            LumoraLogger.Log($"Device: {SteamDeckPlatform.Describe()}");
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SteamDeckBridge: Steam's text input is unavailable: {ex.Message}");
        }
    }

    private static void ReportMode()
        => SteamDeckPlatform.ReportSteam(SteamUtils.IsSteamRunningOnSteamDeck(), SteamUtils.IsSteamInBigPictureMode());

    // Once a frame after the engine update, so the focus it reads is this frame's.
    public static void Tick(InputInterface? input, Viewport? viewport)
    {
        if (!_ready || input?.Actions == null)
            return;

        double now = Time.GetTicksMsec() / 1000.0;
        if (now >= _nextModeCheck)
        {
            _nextModeCheck = now + 2.0;
            try
            {
                ReportMode();
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"SteamDeckBridge: could not ask Steam for its mode: {ex.Message}");
                _ready = false;
                return;
            }
        }

        var map = input.Actions;
        bool focused = map.TextFocusHeld && !input.IsVRActive;
        if (!focused)
        {
            if (_shown)
                Dismiss(map);
            _wasFocused = false;
            return;
        }

        bool fresh = !_wasFocused;
        _wasFocused = true;
        if (_shown || !SteamDeckPlatform.InBigPicture || map.HintDevice != InputDeviceKind.Gamepad)
            return;
        if (fresh || map.Menu.Click.Pressed)
            Show(input, viewport);
    }

    private static void Show(InputInterface input, Viewport? viewport)
    {
        var map = input.Actions;
        var field = Helio.UI.TextInput.Focused is { IsDestroyed: false } focused ? focused : null;
        bool multiline = field?.Multiline.Value == true;
        FieldRect(input, viewport, out int x, out int y, out int width, out int height);

        try
        {
            var floating = multiline
                ? EFloatingGamepadTextInputMode.k_EFloatingGamepadTextInputModeModeMultipleLines
                : EFloatingGamepadTextInputMode.k_EFloatingGamepadTextInputModeModeSingleLine;
            if (SteamUtils.ShowFloatingGamepadTextInput(floating, x, y, width, height))
            {
                _shown = true;
                _modal = false;
                map.SteamKeyboardShown = true;
                return;
            }

            string existing = field?.Text.Value ?? string.Empty;
            uint max = field != null && field.MaxLength.Value > 0 ? (uint)field.MaxLength.Value : 1024u;
            var mode = field?.Mask.Value == true ? EGamepadTextInputMode.k_EGamepadTextInputModePassword : EGamepadTextInputMode.k_EGamepadTextInputModeNormal;
            var lines = multiline ? EGamepadTextInputLineMode.k_EGamepadTextInputLineModeMultipleLines : EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine;
            if (SteamUtils.ShowGamepadTextInput(mode, lines, string.Empty, max, existing))
            {
                _shown = true;
                _modal = true;
                _modalTarget = field;
                map.SteamKeyboardShown = true;
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SteamDeckBridge: could not show Steam's keyboard: {ex.Message}");
        }
    }

    private static void Dismiss(InputBindingMap map)
    {
        try
        {
            if (_modal)
                SteamUtils.DismissGamepadTextInput();
            else
                SteamUtils.DismissFloatingGamepadTextInput();
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SteamDeckBridge: could not put Steam's keyboard away: {ex.Message}");
        }
        _shown = false;
        _modal = false;
        _modalTarget = null;
        map.SteamKeyboardShown = false;
    }

    private static void OnFloatingDismissed()
    {
        _shown = false;
        if (Lumora.Core.Engine.Current?.InputInterface?.Actions is { } map)
            map.SteamKeyboardShown = false;
    }

    // Steam calls back on the main thread from its callback pump, outside any world's update, so the text goes
    // into the field from inside that field's own world.
    private static void OnModalDismissed(GamepadTextInputDismissed_t result)
    {
        var target = _modalTarget;
        _shown = false;
        _modal = false;
        _modalTarget = null;
        if (Lumora.Core.Engine.Current?.InputInterface?.Actions is { } map)
            map.SteamKeyboardShown = false;
        if (!result.m_bSubmitted)
            return;

        string text;
        try
        {
            uint length = SteamUtils.GetEnteredGamepadTextLength();
            if (!SteamUtils.GetEnteredGamepadTextInput(out text, length + 1))
                return;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SteamDeckBridge: could not read what was typed: {ex.Message}");
            return;
        }

        if (target != null && !target.IsDestroyed && target.World != null)
        {
            target.World.RunSynchronously(() =>
            {
                if (target.IsDestroyed || !target.IsFocused)
                    return;
                target.SelectRange(0, target.Text.Value?.Length ?? 0);
                target.TypeString(text);
                if (!target.Multiline.Value)
                    target.PressEnter();
            });
            return;
        }

        // A dash screen's own field, which takes typing a character at a time.
        var dash = Lumora.Core.Components.UI.UserspaceDashboard.LocalInstance;
        if (dash == null || dash.IsDestroyed || dash.World == null)
            return;
        dash.World.RunSynchronously(() =>
        {
            if (dash.IsDestroyed)
                return;
            foreach (char c in text)
            {
                if (!char.IsControl(c))
                    dash.FeedSearchChar(c);
            }
        });
    }

    // The field's patch of the window in window pixels, so the keyboard comes up clear of it.
    private static void FieldRect(InputInterface input, Viewport? viewport, out int x, out int y, out int width, out int height)
    {
        var window = DisplayServer.WindowGetSize();
        float w = Math.Max(window.X, 1);
        float h = Math.Max(window.Y, 1);

        if (Lumora.Core.Components.Chat.DesktopChatBox.LocalInstance is { IsOpen: true, Input.IsFocused: true })
        {
            x = 0;
            y = (int)(h * 0.55f);
            width = (int)(w * 0.45f);
            height = (int)(h * 0.45f);
            return;
        }

        float fx = 0.5f, fy = 0.5f;
        if (input.PadCursor.Active)
        {
            fx = input.PadCursor.Position.x;
            fy = input.PadCursor.Position.y;
        }
        else if (input.IsDashboardOpen && viewport != null)
        {
            var size = viewport.GetVisibleRect().Size;
            var mouse = viewport.GetMousePosition();
            if (size.X > 0f && size.Y > 0f)
            {
                fx = mouse.X / size.X;
                fy = mouse.Y / size.Y;
            }
        }
        width = (int)(w * 0.4f);
        height = (int)(h * 0.08f);
        x = (int)Math.Clamp(fx * w - width * 0.5f, 0f, w - width);
        y = (int)Math.Clamp(fy * h - height * 0.5f, 0f, h - height);
    }
}
#endif
