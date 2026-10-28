// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The column of app plates down the dash's left edge, in the same plate language as the context menu.
// Pressing an app opens it, brings it forward, or sends it to the dock when it is already in front; the
// plate at the top clears every window off the workspace; Exit sits alone at the bottom. A bar beside a
// plate means the app is up, a dot means it is on the dock, a hollow ring means it is out in the room.
// Hovering a plate names it in a tip beside the dock. -xlinka
[ComponentCategory("Hidden")]
[UserspaceOnly]
public sealed class DashDock : UIComponent
{
    public const float Plate = 50f;
    public const float Gap = 6f;
    public const float Pad = 8f;
    public const float Width = Plate + Pad * 2f;
    private const float Glyph = 24f;
    private const float PlateCorner = 15f;
    private const float SeparatorSpace = 11f;
    private const float BadgeHeight = 18f;
    private const float BadgeDigit = 8f;
    private const int BadgeCap = 99;

    private static readonly color Ground = DashTheme.GlassChrome;
    private static readonly color PlateRest = DashTheme.GlassCard;
    private static readonly color PlateHover = color.Lerp(DashTheme.SurfaceHover, DashTheme.Accent, 0.12f).WithAlpha(DashTheme.GlassCardHover.a);
    private static readonly color Hairline = new(1f, 1f, 1f, 0.1f);

    private sealed class Entry
    {
        public DashWindow? Window;
        public Slot Slot = null!;
        public RoundedPanel Plate = null!;
        public Image Glyph = null!;
        public RoundedButton Button = null!;
        public Slot? Marker;
        public RoundedPanel? MarkerPaint;
        public string Name = string.Empty;
        public float CenterY;
        public bool Hovered;
        public color Ink;
        public Slot? Badge;
        public Text? BadgeText;
        public int BadgeShown;
    }

    private readonly List<Entry> _entries = new();
    private Dashboard? _dash;
    private DashWorkspace? _workspace;
    private RectTransform? _rect;
    private int _builtCount = -1;
    private int _seenRevision = -1;

    public float Height { get; private set; }

    // Centred on the desk rather than the whole dash: centred on the dash, its top edge all but touched the
    // top bar. This is how far the desk's middle sits below the dash's.
    private const float DeskShift = (Dashboard.IslandTop + Dashboard.IslandHeight + Dashboard.DeskTopGap - Dashboard.Outer) * 0.5f;

    // The dock's top edge, from the dash's top.
    public float Top => (_dash?.Size.Value.y ?? 0f) * 0.5f - Height * 0.5f + DeskShift;

    internal void Bind(Dashboard dash, DashWorkspace workspace)
    {
        _dash = dash;
        _workspace = workspace;
        _rect = RectTransform ?? Slot.GetComponent<RectTransform>() ?? Slot.AttachComponent<RectTransform>();
        _rect.AnchorMin.Value = new float2(0f, 0.5f);
        _rect.AnchorMax.Value = new float2(0f, 0.5f);
        Slot.AttachComponent<GraphicChunkRoot>();
        SettingsUI.Panel(Slot, Ground, DashTheme.OutlineStrong, 22f);
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (_workspace == null || _dash == null)
            return;
        int count = DockedCount();
        if (count != _builtCount)
            Rebuild();
        if (_workspace.Revision != _seenRevision)
        {
            _seenRevision = _workspace.Revision;
            Repaint();
        }
        foreach (var entry in _entries)
            ShowBadge(entry, entry.Window?.Screen?.DockBadge ?? 0);
    }

    // A count over the plate's top right corner: messages, requests, whatever the app has waiting.
    private void ShowBadge(Entry entry, int count)
    {
        if (count == entry.BadgeShown || entry.Slot.IsDestroyed)
            return;
        entry.BadgeShown = count;
        if (count <= 0)
        {
            if (entry.Badge != null)
                ListingStyle.SetActive(entry.Badge, false);
            return;
        }
        if (entry.Badge == null || entry.Badge.IsDestroyed)
        {
            entry.Badge = SettingsUI.Child(entry.Slot, "Badge", float2.One, float2.One, float2.Zero, float2.Zero);
            // under the plate's own button, so a press on the count is a press on the app
            var pill = SettingsUI.Panel(entry.Badge, DashTheme.Negative, Ground, BadgeHeight * 0.5f);
            pill.OutlineThickness.Value = 2f;
            entry.BadgeText = SettingsUI.FillLabel(entry.Badge, "Count", _dash?.FontSemibold.Target, 12f, DashTheme.OnAccent);
        }
        string text = count > BadgeCap ? BadgeCap + "+" : count.ToString();
        float width = System.MathF.Max(BadgeHeight, BadgeDigit * text.Length + 8f);
        SettingsUI.SetOffsets(SettingsUI.Rect(entry.Badge), new float2(-width + 5f, -BadgeHeight + 5f), new float2(5f, 5f));
        ListingStyle.SetText(entry.BadgeText!, text);
        ListingStyle.SetActive(entry.Badge, true);
    }

    private int DockedCount()
    {
        int count = 0;
        foreach (var window in _workspace!.Windows)
        {
            if (window.InDock)
                count++;
        }
        return count;
    }

    private void Rebuild()
    {
        _builtCount = DockedCount();
        foreach (var entry in _entries)
        {
            if (!entry.Slot.IsDestroyed)
                entry.Slot.Destroy();
            if (entry.Marker != null && !entry.Marker.IsDestroyed)
                entry.Marker.Destroy();
        }
        _entries.Clear();
        foreach (var child in new List<Slot>(Slot.Children))
        {
            if (child.Name.Value == "Separator")
                child.Destroy();
        }

        var docked = new List<DashWindow>();
        foreach (var window in _workspace!.Windows)
        {
            if (window.InDock)
                docked.Add(window);
        }
        // Stable by group: registration order inside a group.
        docked.Sort((a, b) => a.DockGroup != b.DockGroup ? a.DockGroup.CompareTo(b.DockGroup) : IndexOf(a).CompareTo(IndexOf(b)));

        // Top-down from the dock's top edge, sized afterwards.
        float y = Pad;
        var layout = new List<(Entry Entry, float Top)>();
        var separators = new List<float>();
        var workspacePlate = NewEntry(null, "Workspace", "workspace", DashTheme.TextDim);
        layout.Add((workspacePlate, y));
        y += Plate;
        int group = int.MinValue;
        foreach (var window in docked)
        {
            if (window.DockGroup != group)
            {
                separators.Add(y + SeparatorSpace * 0.5f);
                y += SeparatorSpace;
                group = window.DockGroup;
            }
            else
            {
                y += Gap;
            }
            var ink = window.Screen?.NavLabelColor ?? DashTheme.Text;
            if (window.Screen is not ExitScreen)
                ink = DashTheme.Text;
            var entry = NewEntry(window, window.Label, window.Icon, window.Placeholder ? DashTheme.TextMuted : ink);
            layout.Add((entry, y));
            y += Plate;
        }
        y += Pad;
        Height = y;
        SettingsUI.SetOffsets(_rect!, new float2(Dashboard.Outer, -y * 0.5f - DeskShift), new float2(Dashboard.Outer + Width, y * 0.5f - DeskShift));

        foreach (var (entry, top) in layout)
        {
            Place(entry.Slot, Pad, top, Plate, Plate, y);
            entry.CenterY = top + Plate * 0.5f;
            if (entry.Marker != null)
                Place(entry.Marker, 1f, top + Plate * 0.5f - 9f, 4f, 18f, y);
        }
        foreach (float line in separators)
        {
            var separator = SettingsUI.Child(Slot, "Separator", new float2(0f, 1f), new float2(0f, 1f),
                new float2(Pad + 6f, -(line + 0.5f)), new float2(Pad + Plate - 6f, -(line - 0.5f)));
            var image = separator.AttachComponent<Image>();
            image.Tint.Value = DashTheme.OutlineStrong;
            image.InteractionTarget.Value = false;
        }
        _seenRevision = -1;
    }

    private int IndexOf(DashWindow window)
    {
        var windows = _workspace!.Windows;
        for (int i = 0; i < windows.Count; i++)
        {
            if (ReferenceEquals(windows[i], window))
                return i;
        }
        return 0;
    }

    private static void Place(Slot slot, float left, float top, float width, float height, float total)
    {
        var rect = SettingsUI.Rect(slot);
        rect.AnchorMin.Value = new float2(0f, 1f);
        rect.AnchorMax.Value = new float2(0f, 1f);
        SettingsUI.SetOffsets(rect, new float2(left, -(top + height)), new float2(left + width, -top));
    }

    private Entry NewEntry(DashWindow? window, string name, string icon, in color ink)
    {
        var slot = Slot.AddSlot(name);
        slot.AttachComponent<RectTransform>();
        slot.AttachComponent<GraphicChunkRoot>();
        var plate = slot.AttachComponent<RoundedPanel>();
        plate.CornerRadius.Value = PlateCorner;
        plate.Color.Value = PlateRest;
        plate.OutlineColor.Value = Hairline;
        plate.OutlineThickness.Value = 1f;
        // Each plate answers out to halfway to the next one, square; the plate is drawn rounded inside that.
        var button = slot.AttachComponent<RoundedButton>();
        button.HitOutset.Value = Gap * 0.5f;

        var glyphSlot = SettingsUI.Child(slot, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(-Glyph * 0.5f, -Glyph * 0.5f), new float2(Glyph * 0.5f, Glyph * 0.5f));
        var glyph = glyphSlot.AttachComponent<Image>();
        glyph.Texture.Target = _dash!.IconProvider(icon);
        glyph.PreserveAspect.Value = true;
        glyph.InteractionTarget.Value = false;
        glyph.Tint.Value = ink;

        var entry = new Entry { Window = window, Slot = slot, Plate = plate, Glyph = glyph, Button = button, Name = name, Ink = ink };
        if (window != null)
        {
            entry.Marker = Slot.AddSlot(name + " Marker");
            entry.Marker.AttachComponent<RectTransform>();
            entry.MarkerPaint = SettingsUI.Panel(entry.Marker, color.Transparent, color.Transparent, 2f);
            entry.Marker.ActiveSelf.Value = false;
        }

        button.Clicked += (_, _) => Press(entry);
        button.HoverEntered += _ => SetHover(entry, true);
        button.HoverExited += _ => SetHover(entry, false);
        _entries.Add(entry);
        return entry;
    }

    private void Press(Entry entry)
    {
        if (_workspace == null)
            return;
        if (entry.Window == null)
        {
            _workspace.MinimizeAll();
            CopyVersion(entry);
        }
        else
            _workspace.Toggle(entry.Window);
    }

    private void CopyVersion(Entry entry)
    {
        if (_dash == null)
            return;
        var clipboard = Engine.Current?.InputInterface?.ClipboardText;
        string version = _dash.Version.Value;
        string message = "Clipboard unavailable: " + version;
        if (clipboard != null)
        {
            try
            {
                clipboard.SetText(version);
                message = "Version copied: " + version;
            }
            catch (System.Exception ex)
            {
                Logging.Logger.Warn($"Workspace: could not copy version: {ex.Message}");
                message = "Could not copy: " + version;
            }
        }
        _dash.ShowTip(message, Top + entry.CenterY, 520f);
    }

    private void SetHover(Entry entry, bool hovered)
    {
        entry.Hovered = hovered;
        Paint(entry);
        if (_dash == null)
            return;
        if (hovered)
        {
            _dash.ShowTip(TipFor(entry, _dash), Top + entry.CenterY, entry.Window == null ? 520f : 260f);
        }
        else
        {
            _dash.ShowTip(null, 0f);
        }
    }

    // The top plate also says which build this is.
    private static string TipFor(Entry entry, Dashboard dash)
    {
        var window = entry.Window;
        if (window == null)
            return "Workspace  ·  " + dash.Version.Value;
        return window.State switch
        {
            DashWindow.WindowState.Out => window.Label + " (in the room)",
            DashWindow.WindowState.Minimized => window.Label + " (on the dock)",
            _ => window.Label,
        };
    }

    private void Repaint()
    {
        foreach (var entry in _entries)
            Paint(entry);
    }

    private void Paint(Entry entry)
    {
        if (entry.Slot.IsDestroyed)
            return;
        var window = entry.Window;
        bool focused = window != null && ReferenceEquals(_workspace?.Focused, window);
        var fill = entry.Hovered ? PlateHover : PlateRest;
        var edge = entry.Hovered || focused ? DashTheme.Accent.WithAlpha(entry.Hovered ? 0.9f : 0.6f) : Hairline;
        SettingsUI.SetPaint(entry.Plate, fill, edge, entry.Hovered || focused ? 1.5f : 1f);

        if (entry.Marker == null || entry.MarkerPaint == null || window == null)
            return;
        var state = window.State;
        bool show = state != DashWindow.WindowState.Closed;
        ListingStyle.SetActive(entry.Marker, show);
        if (!show)
            return;
        var rect = SettingsUI.Rect(entry.Marker);
        float top = entry.CenterY;
        switch (state)
        {
            case DashWindow.WindowState.Open:
                SettingsUI.SetOffsets(rect, new float2(1f, -(top + 9f)), new float2(5f, -(top - 9f)));
                SettingsUI.SetPaint(entry.MarkerPaint, DashTheme.Accent, color.Transparent, 0f);
                entry.MarkerPaint.CornerRadius.Value = 2f;
                break;
            case DashWindow.WindowState.Minimized:
                SettingsUI.SetOffsets(rect, new float2(1f, -(top + 3f)), new float2(5f, -(top - 3f)));
                SettingsUI.SetPaint(entry.MarkerPaint, DashTheme.TextDim, color.Transparent, 0f);
                entry.MarkerPaint.CornerRadius.Value = 2f;
                break;
            default:
                SettingsUI.SetOffsets(rect, new float2(0f, -(top + 3.5f)), new float2(7f, -(top - 3.5f)));
                SettingsUI.SetPaint(entry.MarkerPaint, color.Transparent, DashTheme.Accent, 1.5f);
                entry.MarkerPaint.CornerRadius.Value = 3.5f;
                break;
        }
    }

    // Where a window's plate sits, in canvas units from the canvas centre, y up: the spot it goes down to.
    public float2? PlateCenterOf(DashWindow window)
    {
        foreach (var entry in _entries)
        {
            if (!ReferenceEquals(entry.Window, window))
                continue;
            var rect = entry.Slot.GetComponent<RectTransform>();
            if (rect == null || rect.LocalComputeRect.width <= 0f)
                return null;
            return rect.LocalComputeRect.Center;
        }
        return null;
    }

    // What the dock shows for an app, for checks and pictures.
    public string MarkerOf(DashWindow window)
    {
        foreach (var entry in _entries)
        {
            if (!ReferenceEquals(entry.Window, window) || entry.Marker == null)
                continue;
            if (!entry.Marker.ActiveSelf.Value)
                return "none";
            return window.State switch
            {
                DashWindow.WindowState.Open => "bar",
                DashWindow.WindowState.Minimized => "dot",
                DashWindow.WindowState.Out => "ring",
                _ => "none",
            };
        }
        return "absent";
    }

    public IEnumerable<(string Name, RoundedButton Button)> Plates
    {
        get
        {
            foreach (var entry in _entries)
                yield return (entry.Name, entry.Button);
        }
    }
}
