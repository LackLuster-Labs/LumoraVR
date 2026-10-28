// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Localization;

namespace Helio.UI.Listing;

public enum ListingChange
{
    Added,
    Updated,
    Removed,
    // The whole path changed shape and nothing about the old set can be trusted. Cheaper for a source
    // than tracking every add/remove when a category is re-derived from scratch.
    Reset,
}

public readonly struct ListingChangeEvent
{
    public readonly ListingChange Change;
    public readonly ListingItem? Item;
    public readonly IReadOnlyList<string>? Path;

    public ListingChangeEvent(ListingChange change, ListingItem? item = null, IReadOnlyList<string>? path = null)
    {
        Change = change;
        Item = item;
        Path = path;
    }
}

// One row's worth of data. Items carry the accessors, never the widgets: a row instance is recycled
// across many items as the list scrolls, so anything that pins itself to a slot would leak into the
// next item that lands on that row. -xlinka
public abstract class ListingItem
{
    // Stable within its path. Selection, template caching and the reconcile all key off this, so two
    // items in the same path must never share one.
    public string Key { get; }

    // Label and Detail resolve through the locale tables on every read. A template reads them inside
    // Bind, which the view only calls when a row is (re)bound, and every template write is
    // equality-gated - so a language switch shows up on the next rebind without anything re-tessellating
    // for the rows whose text did not move. A plain string costs a field read and no lookup. -xlinka
    public LocaleText LabelText { get; set; }

    public string Label
    {
        get => LabelText.Resolve();
        set => LabelText = value;
    }

    // Secondary text a template may show next to the label (units, current binding, status).
    public LocaleText DetailText { get; set; }

    public string Detail
    {
        get => DetailText.Resolve();
        set => DetailText = value;
    }

    // Optional template override. Empty means "map me by my type"; set it to route two items of the
    // same type at different templates (a plain action vs a destructive one, say).
    public string Kind { get; set; } = string.Empty;

    public bool Interactable { get; set; } = true;

    // Sort key inside the path. Equal values keep source order.
    public long Order { get; set; }

    // Anything the owning screen wants to carry through to its own template.
    public object? Tag { get; set; }

    protected ListingItem(string key)
    {
        Key = string.IsNullOrEmpty(key) ? Guid.NewGuid().ToString("N") : key;
    }

    public override string ToString() => $"{GetType().Name}:{Key}";
}

// Read-only row. Value is the right-hand text.
public class ListingLabel : ListingItem
{
    public Func<string>? Read;

    public ListingLabel(string key, LocaleText label, Func<string>? read = null) : base(key)
    {
        LabelText = label;
        Read = read;
    }

    public string Value => Read?.Invoke() ?? Detail;
}

// Navigates the view one level deeper. Key doubles as the path segment.
public class ListingCategory : ListingItem
{
    public Func<int>? EntryCount;

    public ListingCategory(string key, LocaleText label) : base(key)
    {
        LabelText = label;
    }
}

public class ListingToggle : ListingItem
{
    public required Func<bool> Read;
    public required Action<bool> Write;

    public ListingToggle(string key, LocaleText label) : base(key)
    {
        LabelText = label;
    }
}

public class ListingSlider : ListingItem
{
    public float Min;
    public float Max = 1f;
    // 0 leaves the raw drag value alone. Anything else snaps the write to a multiple of it, which is
    // what keeps a texture-cap or fps drag from landing between the values that actually exist.
    public float Step;
    public required Func<float> Read;
    public required Action<float> Write;
    public Func<float, string>? Format;

    public ListingSlider(string key, LocaleText label) : base(key)
    {
        LabelText = label;
    }

    public float Snap(float value)
    {
        if (Step > 0f)
            value = MathF.Round(value / Step) * Step;
        if (value < Min) value = Min;
        if (value > Max) value = Max;
        return value;
    }

    public string Describe(float value) => Format?.Invoke(value) ?? value.ToString("0.##");
}

// Fixed set of options rendered as a segmented strip. Covers enums without needing a keyboard, which
// is the whole point in VR. -xlinka
public class ListingChoice : ListingItem
{
    public required IReadOnlyList<string> Options;
    public required Func<int> Read;
    public required Action<int> Write;

    public ListingChoice(string key, LocaleText label) : base(key)
    {
        LabelText = label;
    }

    public static ListingChoice FromEnum<T>(string key, LocaleText label, Func<T> read, Action<T> write)
        where T : struct, Enum
    {
        var values = (T[])Enum.GetValues(typeof(T));
        var names = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
            names[i] = values[i].ToString();
        return new ListingChoice(key, label)
        {
            Options = names,
            Read = () => Array.IndexOf(values, read()),
            Write = index =>
            {
                if (index >= 0 && index < values.Length)
                    write(values[index]);
            },
        };
    }
}

// Long choices open a searchable list instead of allocating a button for every option.
public sealed class ListingDropdown : ListingChoice
{
    public IReadOnlyList<string> OptionDetails = Array.Empty<string>();
    public LocaleText ListHint;

    public ListingDropdown(string key, LocaleText label) : base(key, label) { }
}

public class ListingAction : ListingItem
{
    public required Action Invoke;
    public LocaleText ButtonLabel = "Apply";
    // Painted in the warning fill instead of the neutral one.
    public bool Destructive;

    public ListingAction(string key, LocaleText label) : base(key)
    {
        LabelText = label;
    }
}

// A line of text typed in place. Write gets what was typed when the edit is kept (Enter, or a click away);
// Read is what the row shows the rest of the time, so a refused or trimmed write shows what really stuck.
public class ListingTextField : ListingItem
{
    public required Func<string> Read;
    public required Action<string> Write;
    public string Placeholder = string.Empty;
    public int MaxLength;

    public ListingTextField(string key, LocaleText label) : base(key)
    {
        LabelText = label;
    }
}

// Escape hatch for a row the built-in templates cannot express (a rebind grid, a segmented module
// strip). Route it with Kind and read Tag inside your own template.
public class ListingCustom : ListingItem
{
    public ListingCustom(string key, string kind, LocaleText label = default) : base(key)
    {
        Kind = kind;
        LabelText = label;
    }
}
