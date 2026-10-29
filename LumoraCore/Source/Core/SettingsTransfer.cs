// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Lumora.Core.Input.Actions;
using Lumora.Core.Localization;

namespace Lumora.Core;

// Settings out to a file the person picks and back in again: a backup before an account goes, or a set of
// graphics choices handed to a friend. The store stays where it is; this only copies keys out of it and
// writes chosen ones back, the same way a change made on the settings screen lands.
//
// Nothing secret and nothing tied to this machine ever leaves or comes in: the saved sign-in, tokens, which
// audio devices and monitor this computer has, the browser cookies come from, the OBS address and password,
// tracker pairings and ban lists (they carry other people's machine ids). A file that names one of those has
// it skipped, not applied. -xlinka
public static class SettingsTransfer
{
    public const string FormatName = "lumora-settings";
    public const int FormatVersion = 1;
    public const string FileExtension = ".json";

    // Bigger than any real store by a wide margin, small enough that a wrong file is refused before it is read.
    public const long MaxFileBytes = 2L * 1024 * 1024;
    public const int MaxEntries = 4096;
    public const int MaxKeyLength = 256;
    public const int MaxValueLength = 64 * 1024;

    public sealed class Group
    {
        public readonly string Id;
        public readonly LocaleText Label;
        public readonly string[] Prefixes;

        public Group(string id, LocaleText label, params string[] prefixes)
        {
            Id = id;
            Label = label;
            Prefixes = prefixes;
        }
    }

    // First match wins, so the catch-all for any other engine setting goes last: a setting added later is
    // carried without anyone having to remember this list.
    public static readonly IReadOnlyList<Group> Groups = new[]
    {
        new Group("graphics", "Settings.Backup.Group.Graphics".AsLocale("Graphics"), "Engine.Video."),
        new Group("audio", "Settings.Backup.Group.Audio".AsLocale("Audio"), "Engine.Audio."),
        new Group("controls", "Settings.Backup.Group.Controls".AsLocale("Controls and Keybinds"), "Engine.Input."),
        new Group("movement", "Settings.Backup.Group.Movement".AsLocale("Movement"), "Engine.Comfort."),
        new Group("interface", "Settings.Backup.Group.Interface".AsLocale("Interface and Dash"),
            "Engine.Interface.", "Engine.Desktop.", "Engine.Color.", "Dash."),
        new Group("avatar", "Settings.Backup.Group.Avatar".AsLocale("You and Your Avatar"), "Engine.Avatar."),
        new Group("media", "Settings.Backup.Group.Media".AsLocale("Photos, Camera and Video"),
            "Engine.Photos.", "Engine.Camera.", "Engine.Media.", "Engine.Storage."),
        new Group("network", "Settings.Backup.Group.Network".AsLocale("Network"), "Engine.Network."),
        new Group("favourites", "Settings.Backup.Group.Favourites".AsLocale("Favourites"), "Favourites."),
        new Group("webaccess", "Settings.Backup.Group.WebAccess".AsLocale("Web Access"), "WebAccess.Sites."),
        new Group("import", "Settings.Backup.Group.Import".AsLocale("Import Choices"), "Import."),
        new Group("other", "Settings.Backup.Group.Other".AsLocale("Everything Else"), "Engine."),
    };

    private static readonly string[] ExcludedKeys =
    {
        "Engine.Desktop.PortalToken",
        "Engine.Desktop.Monitor",
        "Engine.Media.VideoCookiesBrowser",
        "Engine.Audio.InputDevice",
        "Engine.Audio.OutputDevice",
        "Engine.Audio.StreamDevice",
        "Engine.Audio.StreamOutputDevice",
    };

    private static readonly string[] ExcludedPrefixes =
    {
        "Account.",
        "Engine.Audio.MicrophoneGainSeed.",
        "Engine.Stream.",
        "Input.Trackers.",
        "Security.",
        "Network.",
        "@",
    };

    // Said on the screen, so nobody has to guess whether their sign-in went into a file they shared.
    public static readonly LocaleText ExcludedSummary = "Settings.Backup.Excluded.Detail".AsLocale(
        "Your saved sign-in and tokens, audio devices and monitor, which browser video cookies come from, the OBS address and password, tracker pairings and ban lists");

    public static bool IsExcluded(string key)
    {
        if (string.IsNullOrEmpty(key))
            return true;
        foreach (var excluded in ExcludedKeys)
        {
            if (string.Equals(key, excluded, StringComparison.Ordinal))
                return true;
        }
        foreach (var prefix in ExcludedPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // The group a key travels with, or null when it never travels.
    public static Group? GroupOf(string key)
    {
        if (IsExcluded(key))
            return null;
        foreach (var group in Groups)
        {
            foreach (var prefix in group.Prefixes)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    return group;
            }
        }
        return null;
    }

    public static Group? FindGroup(string id)
    {
        foreach (var group in Groups)
        {
            if (string.Equals(group.Id, id, StringComparison.Ordinal))
                return group;
        }
        return null;
    }

    // EXPORT

    // What the store holds for the picked groups, as the file's text. A live change still waiting for its
    // save goes in first, so the file is what the screen shows rather than what was saved a second ago.
    public static string Export(IReadOnlyCollection<string> groupIds, out int count)
    {
        EngineSettings.Commit();
        var picked = new HashSet<string>(groupIds, StringComparer.Ordinal);
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Settings.KeysUnder(string.Empty))
        {
            if (GroupOf(key) is not { } group || !picked.Contains(group.Id))
                continue;
            values[key] = Settings.ReadValue<string>(key, string.Empty) ?? string.Empty;
        }
        count = values.Count;

        using var stream = new MemoryStream();
        // Relaxed escaping: the file is for people to read and share, and nothing ever embeds it in a page.
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("version", FormatVersion);
            writer.WriteString("exported", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteStartArray("groups");
            foreach (var group in Groups)
            {
                if (picked.Contains(group.Id))
                    writer.WriteStringValue(group.Id);
            }
            writer.WriteEndArray();
            writer.WriteStartObject("settings");
            foreach (var (key, value) in values)
                writer.WriteString(key, value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryExportToFile(string path, IReadOnlyCollection<string> groupIds, out int count, out LocaleText error)
    {
        count = 0;
        error = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Settings.Backup.Error.NoPath".AsLocale("Pick a file first.");
            return false;
        }
        try
        {
            string text = Export(groupIds, out count);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            error = "Settings.Backup.Error.Write".AsLocale("Could not write the file: {0}", ex.Message);
            return false;
        }
    }

    // IMPORT

    public sealed class Snapshot
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        // Keys the file had that never come in: secret, tied to the machine that wrote it, or unknown here.
        public int Skipped;
        public string? Exported;
    }

    public static bool TryReadFile(string path, out Snapshot? snapshot, out LocaleText error)
    {
        snapshot = null;
        error = default;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "Settings.Backup.Error.Missing".AsLocale("That file is not there.");
                return false;
            }
            if (info.Length > MaxFileBytes)
            {
                error = "Settings.Backup.Error.TooBig".AsLocale("That file is far too big to be a settings file.");
                return false;
            }
            return TryParse(File.ReadAllText(path, Encoding.UTF8), out snapshot, out error);
        }
        catch (Exception ex)
        {
            error = "Settings.Backup.Error.Read".AsLocale("Could not read the file: {0}", ex.Message);
            return false;
        }
    }

    // Everything about the file is checked before anything is planned from it; one wrong value refuses the whole
    // file rather than applying the half that parsed.
    public static bool TryParse(string? text, out Snapshot? snapshot, out LocaleText error)
    {
        snapshot = null;
        error = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxFileBytes)
        {
            error = NotSettings;
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            error = NotSettings;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String
                || !string.Equals(format.GetString(), FormatName, StringComparison.Ordinal))
            {
                error = NotSettings;
                return false;
            }
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int number) || number < 1)
            {
                error = Malformed;
                return false;
            }
            if (number > FormatVersion)
            {
                error = "Settings.Backup.Error.Newer".AsLocale("This file comes from a newer version of Lumora.");
                return false;
            }
            if (!root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            {
                error = Malformed;
                return false;
            }

            var result = new Snapshot();
            if (root.TryGetProperty("exported", out var exported) && exported.ValueKind == JsonValueKind.String)
                result.Exported = exported.GetString();

            int entries = 0;
            foreach (var property in settings.EnumerateObject())
            {
                if (++entries > MaxEntries)
                {
                    error = Malformed;
                    return false;
                }
                string key = property.Name;
                if (key.Length == 0 || key.Length > MaxKeyLength || HasControl(key)
                    || property.Value.ValueKind != JsonValueKind.String)
                {
                    error = Malformed;
                    return false;
                }
                string value = property.Value.GetString() ?? string.Empty;
                if (value.Length > MaxValueLength)
                {
                    error = Malformed;
                    return false;
                }
                if (GroupOf(key) == null || result.Values.ContainsKey(key))
                {
                    result.Skipped++;
                    continue;
                }
                result.Values[key] = value;
            }
            snapshot = result;
            return true;
        }
    }

    private static LocaleText NotSettings => "Settings.Backup.Error.NotSettings".AsLocale("That is not a Lumora settings file.");
    private static LocaleText Malformed => "Settings.Backup.Error.Malformed".AsLocale("That settings file is damaged and was not used.");

    private static bool HasControl(string text)
    {
        foreach (char c in text)
        {
            if (char.IsControl(c))
                return true;
        }
        return false;
    }

    // One key the import would change. Current or Incoming null means absent: a key the file leaves out goes
    // back to its default, so a picked group ends up exactly as the file has it.
    public readonly record struct Change(string Key, string GroupId, string? Current, string? Incoming);

    public static List<Change> Plan(Snapshot snapshot, IReadOnlyCollection<string> groupIds)
    {
        var picked = new HashSet<string>(groupIds, StringComparer.Ordinal);
        var changes = new List<Change>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        EngineSettings.Commit();

        foreach (var (key, incoming) in snapshot.Values)
        {
            if (GroupOf(key) is not { } group || !picked.Contains(group.Id))
                continue;
            seen.Add(key);
            string? current = Settings.HasValue(key) ? Settings.ReadValue<string>(key, string.Empty) : null;
            if (!string.Equals(current, incoming, StringComparison.Ordinal))
                changes.Add(new Change(key, group.Id, current, incoming));
        }
        foreach (var key in Settings.KeysUnder(string.Empty))
        {
            if (seen.Contains(key) || GroupOf(key) is not { } group || !picked.Contains(group.Id))
                continue;
            changes.Add(new Change(key, group.Id, Settings.ReadValue<string>(key, string.Empty), null));
        }
        changes.Sort((a, b) =>
        {
            int byGroup = GroupIndex(a.GroupId).CompareTo(GroupIndex(b.GroupId));
            return byGroup != 0 ? byGroup : string.CompareOrdinal(a.Key, b.Key);
        });
        return changes;
    }

    private static int GroupIndex(string id)
    {
        for (int i = 0; i < Groups.Count; i++)
        {
            if (string.Equals(Groups[i].Id, id, StringComparison.Ordinal))
                return i;
        }
        return Groups.Count;
    }

    // Writes the planned keys and brings everything that read them up to date. Main thread: what listens to
    // the engine settings reaches into the window and the renderer.
    //
    // An engine setting going back to its default is written AS its default first and taken out after the reload:
    // the reload keeps the live value for a key the store does not hold, so only taking it out would leave the
    // old choice in force until the next start. -xlinka
    public static int Apply(IReadOnlyList<Change> changes)
    {
        EngineSettings.Commit();
        int applied = 0;
        bool engine = false, bindings = false, locale = false;
        var resets = new List<string>();
        foreach (var change in changes)
        {
            if (GroupOf(change.Key) == null)
                continue;
            if (change.Incoming != null)
                Settings.WriteValue(change.Key, change.Incoming);
            else if (EngineSettings.TryGetDefault(change.Key, out var fallback))
            {
                Settings.WriteValue(change.Key, fallback);
                resets.Add(change.Key);
            }
            else
                Settings.DeleteValue(change.Key);
            applied++;
            engine |= change.Key.StartsWith("Engine.", StringComparison.Ordinal);
            bindings |= string.Equals(change.Key, InputBindingStore.SettingsKey, StringComparison.Ordinal);
            locale |= string.Equals(change.Key, LocaleManager.SettingsKey, StringComparison.Ordinal);
        }
        if (engine)
            EngineSettings.Reload();
        foreach (var key in resets)
            Settings.DeleteValue(key);
        if (bindings)
            Engine.Current?.InputInterface?.ReloadBindingOverrides();
        if (locale)
            LocaleManager.SetLocale(Settings.ReadValue<string>(LocaleManager.SettingsKey, LocaleManager.BaseLocale));
        return applied;
    }

    // FILES

    public static string DefaultFolder
    {
        get
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
                documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(documents, "Lumora", "Settings");
        }
    }

    public static string DefaultFileName => $"lumora-settings-{DateTime.Now:yyyy-MM-dd}{FileExtension}";

    public static string DefaultPath => Path.Combine(DefaultFolder, DefaultFileName);
}
