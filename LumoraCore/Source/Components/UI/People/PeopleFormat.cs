// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Lumora.Core.Math;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Components.UI;

// How the People window turns the service's words into what it shows: colours, status, times, languages.
internal static class PeopleFormat
{
    public static readonly color BotTint = Lumora.Core.Components.Avatar.NameplateBadgeColors.Bot;

    // Links lifted off the accent so they read on a card, code in a warm ink on a faint inset.
    public static readonly Lumora.Core.Social.BioInks BioInks = new(
        Lift(DashTheme.Accent, 0.3f), Lift(DashTheme.Warning, 0.25f), new color(1f, 1f, 1f, 0.07f));

    private static color Lift(in color c, float share)
        => new color(c.r + (1f - c.r) * share, c.g + (1f - c.g) * share, c.b + (1f - c.b) * share, c.a);

    // The header's name with the pronouns after it, smaller and dimmer. Rich text, so both are escaped:
    // pronouns are letters only by the time they get here, a name is whatever the service sent.
    public static string NameWithPronouns(string name, string? pronouns)
    {
        string shown = EscapeRich(name);
        if (string.IsNullOrEmpty(pronouns))
            return shown;
        return shown + "   <size=62%><alpha=#B4>" + EscapeRich(pronouns) + "</alpha></size>";
    }

    public static string EscapeRich(string text) => text.Replace("<", "<noparse><</noparse>");

    public static color ParseColor(string? hex, in color fallback)
    {
        if (string.IsNullOrEmpty(hex))
            return fallback;
        var span = hex.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
            span = span.Slice(1);
        if (span.Length < 6)
            return fallback;
        if (!int.TryParse(span.Slice(0, 2), NumberStyles.HexNumber, null, out int r)
            || !int.TryParse(span.Slice(2, 2), NumberStyles.HexNumber, null, out int g)
            || !int.TryParse(span.Slice(4, 2), NumberStyles.HexNumber, null, out int b))
        {
            return fallback;
        }
        return new color(r / 255f, g / 255f, b / 255f, 1f).ToLinear();
    }

    // A name in its own colour, lifted when the colour is too dark to read on the window's ground. The
    // account picked the hue, so the hue stays; only the lightness moves. -xlinka
    public static color NameColor(string? hex)
    {
        var c = ParseColor(hex, DashTheme.Text);
        float luminance = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        const float Floor = 0.18f;
        if (luminance >= Floor)
            return c;
        float lift = luminance <= 0.0001f ? 1f : (Floor - luminance) / (1f - luminance);
        return new color(c.r + (1f - c.r) * lift, c.g + (1f - c.g) * lift, c.b + (1f - c.b) * lift, 1f);
    }

    public static color StatusColor(string? mode) => mode switch
    {
        StatusModes.Online => DashTheme.Positive,
        StatusModes.Away => DashTheme.Warning,
        StatusModes.Busy => DashTheme.Negative,
        _ => DashTheme.TextMuted,
    };

    public static string StatusLabel(string? mode) => (mode switch
    {
        StatusModes.Online => PeopleLocale.ModeOnline,
        StatusModes.Away => PeopleLocale.ModeAway,
        StatusModes.Busy => PeopleLocale.ModeBusy,
        StatusModes.Invisible => PeopleLocale.ModeInvisible,
        _ => PeopleLocale.ModeOffline,
    }).Resolve();

    public static bool IsOnline(string? mode) => mode is StatusModes.Online or StatusModes.Away or StatusModes.Busy;

    public static bool InWorld(PresenceInfo? presence)
        => presence != null && presence.IsOnline && (presence.IsPrivate || !string.IsNullOrEmpty(presence.WorldName));

    // One line saying where someone is, as much as they let a friend see.
    public static string WhereLine(PresenceInfo? presence)
    {
        if (presence == null || !presence.IsOnline)
        {
            return presence?.LastSeen is DateTime seen
                ? PeopleLocale.LastSeen(Ago(seen)).Resolve()
                : PeopleLocale.OfflineNow.Resolve();
        }
        if (presence.IsPrivate)
            return PeopleLocale.PrivateWorld.Resolve();
        if (!string.IsNullOrEmpty(presence.WorldName))
            return PeopleLocale.InWorld(presence.WorldName!).Resolve();
        return StatusLabel(presence.Mode);
    }

    public static string Ago(DateTime when)
    {
        var utc = when.Kind == DateTimeKind.Local ? when.ToUniversalTime() : when;
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 0)
            span = TimeSpan.Zero;
        if (span.TotalMinutes < 1)
            return PeopleLocale.JustNow.Resolve();
        if (span.TotalMinutes < 60)
            return PeopleLocale.MinutesAgo((int)span.TotalMinutes).Resolve();
        if (span.TotalHours < 24)
            return PeopleLocale.HoursAgo((int)span.TotalHours).Resolve();
        if (span.TotalDays < 2)
            return PeopleLocale.YesterdayAgo.Resolve();
        if (span.TotalDays < 30)
            return PeopleLocale.DaysAgo((int)span.TotalDays).Resolve();
        return utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string ClockTime(DateTime when)
        => (when.Kind == DateTimeKind.Utc ? when.ToLocalTime() : when).ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string DayLabel(DateTime when)
    {
        var local = (when.Kind == DateTimeKind.Utc ? when.ToLocalTime() : when).Date;
        var today = DateTime.Now.Date;
        if (local == today)
            return PeopleLocale.Today.Resolve();
        if (local == today.AddDays(-1))
            return PeopleLocale.Yesterday.Resolve();
        return local.ToString(local.Year == today.Year ? "ddd d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
    }

    public static string MonthYear(DateTime when)
        => when <= DateTime.MinValue.AddDays(1) ? PeopleLocale.FactNone.Resolve()
            : when.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    // Their wall clock right now, or null when the zone is unset or this machine does not know it.
    public static string? TheirTime(string? zoneId)
    {
        var zone = FindZone(zoneId);
        if (zone == null)
            return null;
        try
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            return now.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    public static TimeZoneInfo? FindZone(string? zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId))
            return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId!);
        }
        catch
        {
            return null;
        }
    }

    // The zones as the service stores them (IANA ids), each with its offset now, sorted west to east.
    // Windows hands its own ids out of GetSystemTimeZones, so those are converted; anything with no IANA
    // twin is left out rather than sent and refused. -xlinka
    public static List<(string Id, string Label)> TimeZones()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var zones = new List<(string Id, TimeSpan Offset, string Label)>();
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            string? iana = zone.HasIanaId ? zone.Id : null;
            if (iana == null && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var converted))
                iana = converted;
            if (string.IsNullOrEmpty(iana) || !seen.Add(iana!))
                continue;
            var offset = zone.GetUtcOffset(DateTime.UtcNow);
            zones.Add((iana!, offset, $"{OffsetLabel(offset)}  {iana}"));
        }
        zones.Sort((a, b) =>
        {
            int byOffset = a.Offset.CompareTo(b.Offset);
            return byOffset != 0 ? byOffset : string.CompareOrdinal(a.Id, b.Id);
        });
        var list = new List<(string, string)>(zones.Count);
        foreach (var zone in zones)
            list.Add((zone.Id, zone.Label));
        return list;
    }

    public static string? LocalZoneId()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId)
            return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : null;
    }

    private static string OffsetLabel(TimeSpan offset)
    {
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }

    // Every language this machine can name, as the code the service takes and the name it reads as.
    public static List<(string Code, string Label)> Languages()
    {
        var list = new List<(string Code, string Label)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                string code = culture.Name;
                if (code.Length < 2 || code.Length > 3 || !IsLower(code) || !seen.Add(code))
                    continue;
                list.Add((code, $"{culture.EnglishName} ({code})"));
            }
        }
        catch
        {
        }
        if (list.Count < 10)
        {
            foreach (var code in FallbackLanguages)
            {
                if (seen.Add(code))
                    list.Add((code, LanguageName(code) + " (" + code + ")"));
            }
        }
        list.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static readonly string[] FallbackLanguages =
    {
        "en", "de", "fr", "es", "it", "pt", "nl", "sv", "no", "da", "fi", "pl", "cs", "hu", "ro", "el", "tr",
        "ru", "uk", "ar", "he", "hi", "ja", "ko", "zh", "th", "vi", "id",
    };

    public static string LanguageName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            if (!string.IsNullOrEmpty(culture.EnglishName) && !culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal))
                return culture.EnglishName;
        }
        catch
        {
        }
        return code;
    }

    public static string LanguageCodes(IReadOnlyList<string> languages)
    {
        if (languages.Count == 0)
            return string.Empty;
        var parts = new string[languages.Count];
        for (int i = 0; i < languages.Count; i++)
            parts[i] = languages[i].ToUpperInvariant();
        return string.Join("  ", parts);
    }

    public static string LanguageNames(IReadOnlyList<string> languages)
    {
        if (languages.Count == 0)
            return PeopleLocale.FactNone.Resolve();
        var parts = new string[languages.Count];
        for (int i = 0; i < languages.Count; i++)
            parts[i] = LanguageName(languages[i]);
        return string.Join(", ", parts);
    }

    public static string Initials(string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsLetterOrDigit(name[i]))
                return char.ToUpperInvariant(name[i]).ToString();
        }
        return "?";
    }

    public static bool IsHash(string? value)
    {
        if (value == null || value.Length != 64)
            return false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f'))
                return false;
        }
        return true;
    }

    public static string Bytes(long bytes) => Inventory.FormatBytes(bytes);

    private static bool IsLower(string code)
    {
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] < 'a' || code[i] > 'z')
                return false;
        }
        return true;
    }
}
