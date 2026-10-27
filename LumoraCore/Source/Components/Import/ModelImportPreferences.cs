// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;

namespace Lumora.Core.Components.Import;

// What someone picked in the model import dialog's advanced settings, kept in the settings store and following
// their account, so the next import starts from their own answers. A key exists only while its answer differs
// from the default: nothing stored is the plain import, and putting an answer back removes it. -xlinka
public static class ModelImportPreferences
{
    public const string Prefix = "Import.Model.";

    public static bool Follows(string key) => key.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool Get(string name, bool fallback)
    {
        var raw = Settings.ReadValue(Prefix + name, string.Empty);
        return bool.TryParse(raw, out var value) ? value : fallback;
    }

    public static T Get<T>(string name, T fallback) where T : struct, Enum
    {
        var raw = Settings.ReadValue(Prefix + name, string.Empty);
        return Enum.TryParse<T>(raw, ignoreCase: false, out var value) && Enum.IsDefined(value) ? value : fallback;
    }

    public static void Set(string name, bool value, bool fallback)
        => Store(name, value == fallback ? null : value.ToString(CultureInfo.InvariantCulture));

    public static void Set<T>(string name, T value, T fallback) where T : struct, Enum
        => Store(name, value.Equals(fallback) ? null : value.ToString());

    // Only an answer that moved is written: every write saves the whole store.
    private static void Store(string name, string? value)
    {
        string key = Prefix + name;
        if (value == null)
        {
            if (Settings.HasValue(key))
                Settings.DeleteValue(key);
            return;
        }
        if (Settings.ReadValue(key, string.Empty) != value)
            Settings.WriteValue(key, value);
    }
}
