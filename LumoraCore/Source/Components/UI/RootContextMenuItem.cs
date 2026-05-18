// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Components.UI;

/// <summary>
/// Registers a permanent item in the user's root context menu.
///
/// Attach to any slot under the UserRoot. The ContextMenuSystem scans the
/// hierarchy for all RootContextMenuItem components each time the menu opens
/// and includes them in the root page, sorted by Priority.
///
/// Provides a data-driven way to add always-visible items without writing
/// a full ContextMenuItemSource subclass.
///
/// Example: adding a "Laser" toggle to every user session:
/// <code>
///   var item = slot.AttachComponent&lt;RootContextMenuItem&gt;();
///   item.Label.Value    = "Laser";
///   item.IconPath.Value = "res://Icons/laser.png";
///   item.IsToggle.Value = true;
///   item.Pressed        += _ => ToggleLaser();
/// </code>
/// </summary>
[ComponentCategory("UI/Context Menu")]
public class RootContextMenuItem : Component
{
    // ── Synced settings ────────────────────────────────────────────────────────

    public readonly Sync<string> Label = new();
    public readonly Sync<string> IconPath = new();
    public readonly Sync<bool> IsEnabled = new();
    public readonly Sync<bool> IsToggle = new();
    public readonly Sync<bool> IsToggled = new();

    /// <summary>Sort priority. Items with higher Priority appear earlier in the menu.</summary>
    public readonly Sync<int> Priority = new();

    // ── Runtime-only ──────────────────────────────────────────────────────────

    /// <summary>
    /// Optional sub-page. If set, pressing this item navigates into SubPage
    /// instead of firing Pressed.
    /// </summary>
    public ContextMenuPage? SubPage { get; set; }

    /// <summary>Fired when the item is pressed (and SubPage is null).</summary>
    public event Action<ContextMenuItem>? Pressed;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void OnInit()
    {
        base.OnInit();
        // Label = "" (string null/empty treated same way, but match original)
        Label.Value = "";
        IconPath.Value = "";
        IsEnabled.Value = true;
        // IsToggle  = false (C# default, skip)
        // IsToggled = false (C# default, skip)
        // Priority  = 0 (C# default, skip)
    }

    // ── Conversion ────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a ContextMenuItem from this component's current state.
    /// Called by ContextMenuSystem when constructing the root page.
    /// </summary>
    public ContextMenuItem ToMenuItem()
    {
        var item = new ContextMenuItem
        {
            Label = Label.Value,
            IconPath = string.IsNullOrEmpty(IconPath.Value) ? null : IconPath.Value,
            IsEnabled = IsEnabled.Value,
            IsToggle = IsToggle.Value,
            IsToggled = IsToggled.Value,
            SubPage = SubPage,
        };

        if (SubPage == null)
        {
            item.OnPressed = pressedItem =>
            {
                if (IsToggle.Value)
                    IsToggled.Value = !IsToggled.Value;
                Pressed?.Invoke(pressedItem);
            };
        }

        return item;
    }

    /// <summary>
    /// Invoke from code to fire Pressed and update toggle state.
    /// Useful for testing or scripted activation.
    /// </summary>
    public void Invoke()
    {
        if (!IsEnabled.Value) return;
        if (IsToggle.Value) IsToggled.Value = !IsToggled.Value;
        Pressed?.Invoke(ToMenuItem());
    }
}
