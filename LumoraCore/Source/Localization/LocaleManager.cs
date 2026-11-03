// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Lumora.Core.Logging;

namespace Lumora.Core.Localization;

public readonly struct LocaleInfo
{
    public readonly string Code;
    // Shown in the language picker, in the language itself - somebody who cannot read the current
    // interface language still has to be able to find their own.
    public readonly string NativeName;
    public readonly int MessageCount;

    public LocaleInfo(string code, string nativeName, int messageCount)
    {
        Code = code;
        NativeName = nativeName;
        MessageCount = messageCount;
    }
}

// Loads the JSON locale tables and answers lookups against the active one.
//
// Static rather than a component because the interface language is a machine preference, not world
// state: the dashboard, the context menu and a world's own UI all read the same answer, and none of
// them should have to find a component first.
//
// Resolution order for a keyed value, first hit wins:
//   active locale table -> en table -> the English fallback at the call site -> the key itself
// A label never comes back empty. The key is a deliberately ugly last resort - if "Settings.Foo.Bar"
// is on screen, somebody shipped a key with no English behind it, and that should be obvious. -xlinka
public static class LocaleManager
{
    public const string BaseLocale = "en";

    // Written straight through the shared config store rather than EngineSettings, for the same reason
    // the colour swatches are: this is a machine preference that has to survive a crash, not a preview
    // value waiting on the exit screen's Commit.
    public const string SettingsKey = "Engine.Interface.Locale";

    private const string LocaleFolder = "Locale";
    private const string BuiltInPrefix = "Lumora.Locale.";

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Dictionary<string, string>> _tables =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _nativeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English",
        [PseudoLocale.Code] = "Pseudo (test)",
    };
    private static readonly List<string> _searchPaths = new();
    private static readonly List<LocaleInfo> _available = new();

    private static bool _loaded;
    private static string _current = BaseLocale;

    // Raised after the active locale changes and the tables behind it are in place. Anything holding
    // resolved text re-resolves here; nothing polls.
    public static event Action? Changed;

    public static string CurrentLocale
    {
        get
        {
            EnsureLoaded();
            return _current;
        }
    }

    public static IReadOnlyList<LocaleInfo> Available
    {
        get
        {
            EnsureLoaded();
            return _available;
        }
    }

    // Extra directory to scan for <code>.json; a test points it wherever it wrote its fixtures.
    public static void AddSearchPath(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;
        lock (_lock)
        {
            foreach (var existing in _searchPaths)
            {
                if (string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            _searchPaths.Add(directory!);
            _loaded = false;
        }
    }

    public static void Reload()
    {
        lock (_lock)
            _loaded = false;
        EnsureLoaded();
        Changed?.Invoke();
    }

    // Load a table from JSON text rather than a path. The platform layer uses this for the tables that
    // ship inside the export's resource pack: those have no on-disk file to scan, so the only thing that
    // can read them is the platform's own resource API. Additive and first-wins, exactly like a file. -xlinka
    public static void LoadJson(string? json, string? sourceName = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        EnsureLoaded();
        lock (_lock)
        {
            if (!Ingest(json!, sourceName ?? "memory"))
                return;
            RebuildAvailable();
            if (!_tables.ContainsKey(_current) && !string.Equals(_current, PseudoLocale.Code, StringComparison.OrdinalIgnoreCase))
                _current = BaseLocale;
        }
    }

    public static bool SetLocale(string? code)
    {
        EnsureLoaded();
        string wanted = string.IsNullOrWhiteSpace(code) ? BaseLocale : code!.Trim();
        lock (_lock)
        {
            if (!_tables.ContainsKey(wanted) && !string.Equals(wanted, PseudoLocale.Code, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(_current, wanted, StringComparison.OrdinalIgnoreCase))
                return true;
            _current = wanted;
        }
        try
        {
            Settings.WriteValue(SettingsKey, wanted);
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocaleManager: could not persist the language choice: {ex.Message}");
        }
        Changed?.Invoke();
        return true;
    }

    public static int IndexOf(string? code)
    {
        EnsureLoaded();
        for (int i = 0; i < _available.Count; i++)
        {
            if (string.Equals(_available[i].Code, code, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public static bool HasKey(string code, string key)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (string.Equals(code, PseudoLocale.Code, StringComparison.OrdinalIgnoreCase))
                code = BaseLocale;
            return _tables.TryGetValue(code, out var table) && table.ContainsKey(key);
        }
    }

    public static int MessageCount(string code)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (string.Equals(code, PseudoLocale.Code, StringComparison.OrdinalIgnoreCase))
                code = BaseLocale;
            return _tables.TryGetValue(code, out var table) ? table.Count : 0;
        }
    }

    public static string Resolve(in LocaleText text)
    {
        if (!text.IsKey)
            return Format(text.Fallback ?? string.Empty, text.Args);

        EnsureLoaded();
        string key = text.Key!;
        string? pattern;
        bool pseudo;
        lock (_lock)
        {
            pseudo = string.Equals(_current, PseudoLocale.Code, StringComparison.OrdinalIgnoreCase);
            string lookup = pseudo ? BaseLocale : _current;
            pattern = Lookup(lookup, key) ?? Lookup(BaseLocale, key);
        }

        pattern ??= string.IsNullOrEmpty(text.Fallback) ? key : text.Fallback;
        // Pseudo transforms the PATTERN, before the arguments land, so a user name or a number pasted
        // into the string stays readable and only the translatable wording is mangled.
        if (pseudo)
            pattern = PseudoLocale.Transform(pattern);
        return Format(pattern, text.Args);
    }

    private static string? Lookup(string code, string key)
        => _tables.TryGetValue(code, out var table) && table.TryGetValue(key, out var value) ? value : null;

    private static string Format(string pattern, object[]? args)
    {
        if (args == null || args.Length == 0 || pattern.Length == 0)
            return pattern;
        try
        {
            return string.Format(pattern, args);
        }
        catch (FormatException)
        {
            // A translator wrote {1} into a one-argument string. Show the raw pattern rather than
            // taking the screen down with them.
            return pattern;
        }
    }

    private static void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded)
                return;
            _loaded = true;
            _tables.Clear();

            foreach (var directory in ResolveDirectories())
                LoadDirectory(directory);
            LoadBuiltIn();

            if (!_tables.ContainsKey(BaseLocale))
                _tables[BaseLocale] = new Dictionary<string, string>(StringComparer.Ordinal);

            RebuildAvailable();

            string stored = ReadStoredLocale();
            _current = _tables.ContainsKey(stored) || string.Equals(stored, PseudoLocale.Code, StringComparison.OrdinalIgnoreCase)
                ? stored
                : BaseLocale;
        }
    }

    private static string ReadStoredLocale()
    {
        try
        {
            var stored = Settings.ReadValue(SettingsKey, string.Empty);
            return string.IsNullOrWhiteSpace(stored) ? BaseLocale : stored.Trim();
        }
        catch
        {
            return BaseLocale;
        }
    }

    // User overrides first so a dropped-in file beats the shipped table for the same key; the loader is
    // first-wins per key, additively.
    private static IEnumerable<string> ResolveDirectories()
    {
        string? user = null;
        try
        {
            user = Persistence.PathResolver.Resolve(LocaleFolder);
        }
        catch
        {
            user = null;
        }
        if (!string.IsNullOrEmpty(user))
            yield return user!;

        for (int i = 0; i < _searchPaths.Count; i++)
            yield return _searchPaths[i];

        // The source tree's Locale folder beside the game project, so an edited table shows on the next reload
        // without a rebuild. An export has no such folder and gets the built-in copies instead.
        var root = Engine.Current?.ResourceRoot;
        if (!string.IsNullOrWhiteSpace(root))
            yield return Path.Combine(root!, "..", LocaleFolder);
    }

    // The repo's Locale/*.json are built into this assembly, so every platform and every export carries them
    // with no file to find. Loaded after the folders: first-wins per key, so a dropped-in file still beats them.
    private static void LoadBuiltIn()
    {
        var assembly = typeof(LocaleManager).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(BuiltInPrefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null)
                    continue;
                using var reader = new StreamReader(stream);
                Ingest(reader.ReadToEnd(), name.Substring(BuiltInPrefix.Length, name.Length - BuiltInPrefix.Length - 5));
            }
            catch (Exception ex)
            {
                Logger.Warn($"LocaleManager: built-in table '{name}' did not load: {ex.Message}");
            }
        }
    }

    private static void LoadDirectory(string directory)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(directory))
                return;
            files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocaleManager: could not scan '{directory}': {ex.Message}");
            return;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Length; i++)
            LoadFile(files[i]);
    }

    private static void LoadFile(string path)
    {
        try
        {
            Ingest(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocaleManager: '{Path.GetFileName(path)}' did not load: {ex.Message}");
        }
    }

    private static bool Ingest(string json, string fallbackCode)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string code = root.TryGetProperty("localeCode", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString() ?? string.Empty
                : fallbackCode;
            if (string.IsNullOrWhiteSpace(code))
                return false;

            if (root.TryGetProperty("nativeName", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                var native = nameElement.GetString();
                if (!string.IsNullOrWhiteSpace(native))
                    _nativeNames[code] = native!;
            }

            if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Object)
                return false;

            if (!_tables.TryGetValue(code, out var table))
            {
                table = new Dictionary<string, string>(StringComparer.Ordinal);
                _tables[code] = table;
            }

            foreach (var message in messages.EnumerateObject())
            {
                if (message.Value.ValueKind != JsonValueKind.String)
                    continue;
                // First table wins so a user override loaded earlier is not clobbered by the shipped one.
                if (!table.ContainsKey(message.Name))
                    table[message.Name] = message.Value.GetString() ?? string.Empty;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocaleManager: locale '{fallbackCode}' did not parse: {ex.Message}");
            return false;
        }
    }

    private static void RebuildAvailable()
    {
        _available.Clear();
        int baseCount = _tables.TryGetValue(BaseLocale, out var baseTable) ? baseTable.Count : 0;
        _available.Add(new LocaleInfo(BaseLocale, NativeNameOf(BaseLocale), baseCount));

        var codes = new List<string>(_tables.Keys);
        codes.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            if (string.Equals(code, BaseLocale, StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, PseudoLocale.Code, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _available.Add(new LocaleInfo(code, NativeNameOf(code), _tables[code].Count));
        }

        // The pseudo locale is generated from the en table, never shipped as a file, and always last.
        _available.Add(new LocaleInfo(PseudoLocale.Code, NativeNameOf(PseudoLocale.Code), baseCount));
    }

    private static string NativeNameOf(string code)
        => _nativeNames.TryGetValue(code, out var name) ? name : code;
}
