// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Core.Components.UI;

/// <summary>
/// Data for a single radial context menu item.
///
/// Polar coordinate layout:
///   AngleStart + ArcLength (degrees) = arc position around the ring
///   RadiusStart + Thickness (pixels) = inner and outer radius of the arc
/// </summary>
public class ContextMenuItem
{
    // ── Display ────────────────────────────────────────────────────────────────

    /// <summary>Text label shown on the arc segment.</summary>
    public string Label { get; set; } = "";

    /// <summary>res:// path to an icon Texture2D. Null = no icon.</summary>
    public string? IconPath { get; set; }

    /// <summary>Arc fill color (RGBA 0-1).</summary>
    public float[] FillColor { get; set; } = { 0.12f, 0.12f, 0.12f, 0.9f };

    /// <summary>Arc border/outline color.</summary>
    public float[] OutlineColor { get; set; } = { 0.45f, 0.45f, 0.45f, 1f };

    /// <summary>Label text color.</summary>
    public float[] LabelColor { get; set; } = { 1f, 1f, 1f, 1f };

    // ── State ──────────────────────────────────────────────────────────────────

    /// <summary>Whether this item responds to interaction.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Whether this is a stateful toggle (IsToggled reflects current state).</summary>
    public bool IsToggle { get; set; } = false;

    /// <summary>Current toggled state. Only meaningful when IsToggle is true.</summary>
    public bool IsToggled { get; set; } = false;

    // ── Actions ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Callback fired when the item is pressed.
    /// Null for label-only (non-interactive) items.
    /// </summary>
    public Action<ContextMenuItem>? OnPressed { get; set; }

    /// <summary>
    /// If set, pressing this item navigates into this sub-page instead of firing OnPressed.
    /// </summary>
    public ContextMenuPage? SubPage { get; set; }

    // ── Polar coordinate layout (set by ContextMenuPage.LayoutItems) ───────────

    /// <summary>Start angle in degrees. 0° = right, increases clockwise.</summary>
    public float AngleStart { get; internal set; }

    /// <summary>Angular size of this segment in degrees.</summary>
    public float ArcLength { get; internal set; }

    /// <summary>Inner radius in pixels (distance from center to the inner arc edge).</summary>
    public float RadiusStart { get; set; } = 55f;

    /// <summary>Radial depth in pixels (inner-to-outer arc width).</summary>
    public float Thickness { get; set; } = 80f;

    // ── Computed helpers ───────────────────────────────────────────────────────

    public float AngleEnd => AngleStart + ArcLength;
    public float AngleMiddle => AngleStart + ArcLength * 0.5f;
    public float RadiusEnd => RadiusStart + Thickness;
    public float RadiusMiddle => RadiusStart + Thickness * 0.5f;
}

/// <summary>
/// A page of context menu items displayed as a radial ring.
/// Pages can be stacked to create sub-menus.
/// </summary>
public class ContextMenuPage
{
    /// <summary>Short title shown in the center circle.</summary>
    public string Title { get; set; } = "";

    /// <summary>All items on this page. Laid out by LayoutItems().</summary>
    public List<ContextMenuItem> Items { get; } = new();

    /// <summary>Gap in degrees between adjacent items (default 5°).</summary>
    public float SeparationAngle { get; set; } = 5f;

    public ContextMenuPage(string title = "") => Title = title;

    // ── Fluent builder ─────────────────────────────────────────────────────────

    public ContextMenuPage AddItem(ContextMenuItem item)
    {
        Items.Add(item);
        return this;
    }

    public ContextMenuPage AddItem(string label, Action<ContextMenuItem> onPressed,
                                   string? iconPath = null)
    {
        Items.Add(new ContextMenuItem
        {
            Label = label,
            OnPressed = onPressed,
            IconPath = iconPath,
        });
        return this;
    }

    // ── Layout ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Distribute all items evenly around 360°, respecting SeparationAngle gaps.
    /// Call this before passing a page to ContextMenuView.
    /// </summary>
    public void LayoutItems()
    {
        if (Items.Count == 0) return;

        float totalGap = SeparationAngle * Items.Count;
        float available = 360f - totalGap;
        float slice = available / Items.Count;

        // Start at top (-90° = 12 o'clock)
        float angle = -90f;
        foreach (var item in Items)
        {
            item.AngleStart = angle + SeparationAngle * 0.5f;
            item.ArcLength = slice;
            angle += slice + SeparationAngle;
        }
    }
}
