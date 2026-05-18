// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.UI;

/// <summary>
/// Central manager for the user's radial context menu.
///
/// Architecture:
///   ContextMenuSystem           → owns the page stack and item collection
///   ContextMenuHook (Godot)     → subscribes to events, handles rendering
///   ContextMenuItemSource       → contributes contextual items at open time
///   RootContextMenuItem         → contributes permanent items at open time
///
/// Usage:
/// <code>
///   var sys = userSlot.GetComponent&lt;ContextMenuSystem&gt;();
///
///   // Toggle open/close (bind this to a controller button)
///   sys.Toggle();
///
///   // Open a specific custom page
///   sys.Open(myPage);
///
///   // Navigate into a sub-page
///   sys.PushPage(subPage);
/// </code>
/// </summary>
[ComponentCategory("UI/Context Menu")]
public class ContextMenuSystem : ImplementableComponent<IHook>
{
    // ── State ──────────────────────────────────────────────────────────────────

    /// <summary>Whether the menu is currently open.</summary>
    public readonly Sync<bool> IsOpen;

    /// <summary>The page currently being displayed (null when closed).</summary>
    public ContextMenuPage? CurrentPage { get; private set; }

    /// <summary>True if there are pages below the current one (i.e. PopPage() would go back).</summary>
    public bool HasPageHistory => _pageStack.Count > 0;

    // ── Events ─────────────────────────────────────────────────────────────────

    /// <summary>Fired when the menu opens. Provides the initial page to display.</summary>
    public event Action<ContextMenuPage>? MenuOpened;

    /// <summary>Fired when the menu closes.</summary>
    public event Action? MenuClosed;

    /// <summary>Fired when navigation moves to a different page (sub-page or back).</summary>
    public event Action<ContextMenuPage>? PageChanged;

    // ── Internal ───────────────────────────────────────────────────────────────

    private readonly Stack<ContextMenuPage> _pageStack = new();

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public override void OnAwake()
    {
        base.OnAwake();
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Toggle the root context menu open or closed.
    /// Bind this to a controller button or middle mouse.
    /// </summary>
    public void Toggle()
    {
        if (IsOpen.Value) Close();
        else Open();
    }

    /// <summary>
    /// Open the root context menu.
    /// Collects items from all RootContextMenuItem and ContextMenuItemSource
    /// components in the slot hierarchy, then fires MenuOpened.
    /// </summary>
    public void Open()
    {
        var page = BuildRootPage();
        page.LayoutItems();
        Open(page);
    }

    /// <summary>Open the menu showing a pre-built page directly.</summary>
    public void Open(ContextMenuPage page)
    {
        if (page == null) return;

        _pageStack.Clear();
        CurrentPage = page;
        IsOpen.Value = true;

        MenuOpened?.Invoke(page);
        LumoraLogger.Log($"ContextMenuSystem: Opened '{page.Title}' ({page.Items.Count} items)");
    }

    /// <summary>
    /// Navigate into a sub-page (saves current page so PopPage() can go back).
    /// </summary>
    public void PushPage(ContextMenuPage page)
    {
        if (page == null || CurrentPage == null) return;

        _pageStack.Push(CurrentPage);
        CurrentPage = page;
        CurrentPage.LayoutItems();
        PageChanged?.Invoke(CurrentPage);
    }

    /// <summary>
    /// Go back one page. Closes the menu if already at the root page.
    /// </summary>
    public void PopPage()
    {
        if (_pageStack.Count == 0) { Close(); return; }

        CurrentPage = _pageStack.Pop();
        PageChanged?.Invoke(CurrentPage);
    }

    /// <summary>Close the menu.</summary>
    public void Close()
    {
        if (!IsOpen.Value) return;

        _pageStack.Clear();
        CurrentPage = null;
        IsOpen.Value = false;

        MenuClosed?.Invoke();
        LumoraLogger.Log("ContextMenuSystem: Closed");
    }

    /// <summary>
    /// Handle item selection from the view layer.
    /// Navigates to SubPage if set, otherwise invokes OnPressed and closes.
    /// </summary>
    public void SelectItem(ContextMenuItem item)
    {
        if (item == null || !item.IsEnabled) return;

        if (item.SubPage != null)
        {
            PushPage(item.SubPage);
        }
        else
        {
            item.OnPressed?.Invoke(item);
            Close();
        }
    }

    // ── Internal ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the root page by collecting items from the slot hierarchy.
    /// </summary>
    private ContextMenuPage BuildRootPage()
    {
        var page = new ContextMenuPage("Options");

        // 1. Fixed items from RootContextMenuItem components (sorted by Priority desc)
        var rootItems = new List<(int priority, ContextMenuItem item)>();
        foreach (var root in CollectComponents<RootContextMenuItem>())
            rootItems.Add((root.Priority.Value, root.ToMenuItem()));

        foreach (var (_, item) in rootItems.OrderByDescending(x => x.priority))
            page.AddItem(item);

        // 2. Contextual items from ContextMenuItemSource components
        foreach (var source in CollectComponents<ContextMenuItemSource>()
                                    .OrderByDescending(s => s.Priority.Value))
        {
            try
            {
                source.PopulateContextMenu(page);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"ContextMenuSystem: {source.GetType().Name}.PopulateContextMenu threw: {ex.Message}");
            }
        }

        return page;
    }

    private IEnumerable<T> CollectComponents<T>() where T : Component
    {
        var results = new List<T>();
        CollectRecursive<T>(Slot, results);
        return results;
    }

    private static void CollectRecursive<T>(Slot slot, List<T> results) where T : Component
    {
        foreach (var comp in slot.Components)
            if (comp is T t) results.Add(t);
        foreach (var child in slot.Children)
            CollectRecursive<T>(child, results);
    }
}
