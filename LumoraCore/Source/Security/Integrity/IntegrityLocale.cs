// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Localization;

namespace Lumora.Core.Security.Integrity;

// What a person reads when game files keep two games apart.
public static class IntegrityLocale
{
    public static readonly LocaleText Modified = "Integrity.Join.Modified".AsLocale(
        "Your game files are modified, so you can only join sessions running the same changes. Verify the game files in Steam to play with everyone.");

    public static readonly LocaleText HostModified = "Integrity.Join.HostModified".AsLocale(
        "The host's game files are modified, so this session is closed to unmodified games.");

    public static readonly LocaleText OtherVersion = "Integrity.Join.OtherVersion".AsLocale(
        "This session runs a different version of Lumora.");

    public static readonly LocaleText ChangedTitle = "Integrity.Changed.Title".AsLocale("Your game was modified while running");

    public static LocaleText ChangedLeft(string what) => "Integrity.Changed.Left".AsLocale(
        "{0} was loaded into the game, so you left the session. Restart the game to play with everyone again.", what);

    public static LocaleText ChangedHosting(string what) => "Integrity.Changed.Hosting".AsLocale(
        "{0} was loaded into the game, so the people in your session were sent home. Restart the game to play with everyone again.", what);

    public static readonly LocaleText ListNote = "Integrity.List.Note".AsLocale(
        "Your game files are modified: only sessions running the same changes are listed. Verify the game files in Steam to see everyone's.");
}
