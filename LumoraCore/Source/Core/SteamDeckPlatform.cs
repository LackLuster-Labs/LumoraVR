// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;

namespace Lumora.Core;

// Whether this is a Steam Deck, and whether the window is the only thing on the screen the way it is in the
// Deck's game mode.
//
// Steam's own answer is the best one, but it only exists once Steam is up, in a build that carries it, and
// never with --no-steam. So the platform layer reports Steam's answer when it has one, and until then (or
// without it) the machine answers: Steam puts SteamDeck=1 in the game's environment on a Deck, gamescope
// marks the session it runs, and the board names itself Jupiter (LCD) or Galileo (OLED). Any one of them is
// enough. --simulate-steam-deck pretends, so the Deck branches can be walked from a desk; everything that
// prints the answer says SIMULATED. -xlinka
public static class SteamDeckPlatform
{
    private const string SimulateFlag = "--simulate-steam-deck";

    public static bool IsSimulated { get; }

    // From the environment and the board, read once.
    public static bool DetectedLocally { get; }

    // Running inside gamescope: one fullscreen window, nothing else to click on, no title bar to drag.
    public static bool IsGamescope { get; }

    private static bool _steamSaysDeck;
    private static bool _bigPicture;
    private static bool _standaloneHeadset;

    // Steam says Deck on a Frame too, and its environment can look the same, so a headset the platform layer
    // recognised is never a Deck: no handheld controls, hints or texture cap on a face. -xlinka
    public static bool IsSteamDeck => !_standaloneHeadset && (IsSimulated || DetectedLocally || _steamSaysDeck);

    public static void ReportStandaloneHeadset() => _standaloneHeadset = true;

    // Steam's big screen mode, on a Deck or on a TV. Steam's on-screen keyboard is there in both.
    public static bool InBigPicture => _bigPicture || IsSteamDeck;

    public static bool SteamAnswered { get; private set; }

    static SteamDeckPlatform()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (string.Equals(arg?.Trim(), SimulateFlag, StringComparison.OrdinalIgnoreCase))
                IsSimulated = true;
        }

        try
        {
            DetectedLocally = Environment.GetEnvironmentVariable("SteamDeck") == "1" || BoardIsDeck();
            IsGamescope = IsSimulated
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY"))
                || string.Equals(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), "gamescope", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // A sandbox that will not show the environment or /sys leaves Steam's answer to decide.
        }
    }

    private static bool BoardIsDeck()
    {
        if (!OperatingSystem.IsLinux())
            return false;
        const string product = "/sys/devices/virtual/dmi/id/product_name";
        const string vendor = "/sys/devices/virtual/dmi/id/board_vendor";
        if (!File.Exists(product))
            return false;
        string name = File.ReadAllText(product).Trim();
        string maker = File.Exists(vendor) ? File.ReadAllText(vendor).Trim() : string.Empty;
        return (name == "Jupiter" || name == "Galileo") && (maker.Length == 0 || maker.StartsWith("Valve", StringComparison.OrdinalIgnoreCase));
    }

    // From the platform layer once Steam is up, and again whenever big screen mode may have changed.
    public static void ReportSteam(bool steamDeck, bool bigPicture)
    {
        _steamSaysDeck = steamDeck;
        _bigPicture = bigPicture;
        SteamAnswered = true;
    }

    public static string Describe()
    {
        if (_standaloneHeadset)
            return $"standalone headset, not a Steam Deck{(_steamSaysDeck ? " (Steam says Deck)" : string.Empty)}";
        if (!IsSteamDeck && !IsGamescope)
            return "not a Steam Deck";
        string what = IsSteamDeck ? "Steam Deck" : "gamescope session";
        string how = IsSimulated ? "SIMULATED" : _steamSaysDeck ? "Steam says so" : DetectedLocally ? "environment/board" : "gamescope";
        return $"{what} ({how}{(IsGamescope ? ", gamescope" : string.Empty)}{(_bigPicture ? ", big picture" : string.Empty)})";
    }
}
