// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Components.UI;
using Lumora.Core.Persistence;

namespace Lumora.Core.Components;

// A player's own inspector: one they saved to their inventory, changed however they liked and made their
// favourite. Every inspector this machine opens for its user is then that one, on the spot the built-in would
// take and showing what the built-in would have shown.
//
// Whose is what. A saved inspector is its frame: the title bar, the selection and action rows, the panes, the
// footer, every colour, size and font in them, and anything added to them (slots, components, Loom graphs)
// come back as saved, and nothing here writes over them. The rows code draws (the tree, the component sections,
// the tab chips) never go into a save and are made fresh wherever the panel opens, so whatever is put inside
// those three lists goes with the next rebuild. The tool toggles' fills follow what they toggle, and the
// shell's title bar, close button and edges follow its PanelShell fields, which is where to recolour them. -xlinka
public partial class SceneInspectorPanel
{
    // Set when this panel came out of a saved copy, until what it showed where it was saved is cleared.
    private bool _fromSave;

    public override void Load(DataTreeNode node, LoadControl control)
    {
        base.Load(node, control);
        _fromSave = true;
    }

    // The last step of every new panel: this machine's favourite when there is one and the world takes the
    // user's own things, the built-in otherwise. opened hears the panel once it stands, at once or after the
    // favourite's first fetch; the return is null while that fetch is out. A caller that cannot wait gets the
    // built-in instead.
    private static SceneInspectorPanel? Fill(Slot panelSlot, Slot rootTarget, Action<SceneInspectorPanel>? opened, bool wait = true)
    {
        var world = panelSlot.World;
        if (!FavouriteSpawn.Wanted(world, FavouriteKind.Inspector))
            return Opened(BuiltIn(panelSlot, rootTarget), opened);

        switch (FavouriteTrees.Peek(FavouriteKind.Inspector, out var tree, out var problem))
        {
            case FavouriteTrees.State.Kept:
                return Opened(FromFavourite(ref panelSlot, tree!, rootTarget) ?? BuiltIn(panelSlot, rootTarget), opened);
            case FavouriteTrees.State.Fetching when wait:
                FavouriteTrees.WhenFetched(FavouriteKind.Inspector, fetched => world.RunSynchronously(() =>
                {
                    if (panelSlot.IsDestroyed)
                        return;
                    if (rootTarget.IsDestroyed)
                    {
                        panelSlot.Destroy();
                        return;
                    }
                    var slot = panelSlot;
                    var panel = fetched?.Tree is { } kept ? FromFavourite(ref slot, kept, rootTarget) : null;
                    if (panel == null && fetched?.Problem != null)
                        Tell(slot, fetched.Problem);
                    Opened(panel ?? BuiltIn(slot, rootTarget), opened);
                }));
                return null;
            case FavouriteTrees.State.Failed:
                Tell(panelSlot, problem);
                break;
        }
        return Opened(BuiltIn(panelSlot, rootTarget), opened);
    }

    private static SceneInspectorPanel Opened(SceneInspectorPanel panel, Action<SceneInspectorPanel>? opened)
    {
        opened?.Invoke(panel);
        return panel;
    }

    private static SceneInspectorPanel BuiltIn(Slot panelSlot, Slot rootTarget)
    {
        var panel = panelSlot.AttachComponent<SceneInspectorPanel>();
        panel.Root.Target = rootTarget;
        panel.Selected.Target = rootTarget;
        return panel;
    }

    // Once per failure, not at every open.
    private static void Tell(Slot near, string? problem)
    {
        if (problem != null && FavouriteTrees.Tell(FavouriteKind.Inspector))
            FavouriteNotice.Show(near.World, near, FavouriteSpawn.BuiltIn(FavouriteKind.Inspector), problem);
    }

    // The favourite into the slot the built-in would have had: it keeps the slot's spot, turn and size, its
    // name and its place out of world saves. Null when it is not an inspector after all or would not load,
    // with slot swapped for a clean one standing where it stood, for the built-in.
    private static SceneInspectorPanel? FromFavourite(ref Slot slot, DataTreeDictionary tree, Slot rootTarget)
    {
        var world = slot.World;
        var position = slot.LocalPosition.Value;
        var rotation = slot.LocalRotation.Value;
        var scale = slot.LocalScale.Value;
        string? problem = null;
        // A dependency comes in beside the object, at the world root. It belongs to this panel and goes with it.
        bool dependencies = tree.TryGetList("Dependencies") is { Count: > 0 };
        var before = dependencies ? new HashSet<Slot>(world.RootSlot.Children) : null;
        try
        {
            slot.LoadObject(tree, assetsRoot: slot);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }
        if (before != null)
        {
            foreach (var child in new List<Slot>(world.RootSlot.Children))
            {
                if (!before.Contains(child) && !ReferenceEquals(child, slot))
                    child.SetParent(slot, preserveGlobalTransform: false);
            }
        }
        slot.LocalPosition.Value = position;
        slot.LocalRotation.Value = rotation;
        slot.LocalScale.Value = scale;
        slot.Name.Value = "Inspector";
        slot.Persistent.Value = false;
        slot.Tag.Value = "Developer";

        var panel = problem == null ? slot.GetComponent<SceneInspectorPanel>() : null;
        if (panel != null && (panel._hierarchyContent.Target is { IsDestroyed: false } || panel._componentsContent.Target is { IsDestroyed: false }))
        {
            panel.TakeLoaded(rootTarget);
            return panel;
        }

        string name = Favourites.Get(FavouriteKind.Inspector)?.Name ?? "";
        string why = (problem != null ? FavouriteLocale.InspectorDidNotLoad(name, problem) : FavouriteLocale.InspectorNoLists(name)).Resolve();
        Logging.Logger.Warn($"SceneInspectorPanel: {why}");
        FavouriteTrees.Refuse(FavouriteKind.Inspector, why);
        Tell(slot, why);
        var fresh = slot.Parent.AddSlot("Inspector");
        fresh.LocalPosition.Value = position;
        fresh.LocalRotation.Value = rotation;
        fresh.LocalScale.Value = scale;
        fresh.Persistent.Value = false;
        fresh.Tag.Value = "Developer";
        slot.Destroy();
        slot = fresh;
        return null;
    }

    // A panel out of a saved copy: the tabs, the opened branches and the pin it had where it was saved mean
    // nothing here (the branches are ids out of that world), so it starts clean on root.
    private void TakeLoaded(Slot root)
    {
        _fromSave = false;
        foreach (var tab in new List<InspectorTab>(Slot.GetComponents<InspectorTab>()))
            tab.Destroy();
        Tabs.Clear();
        ActiveTab.Target = null!;
        ExpandedSlots.Clear();
        ExpandedComponents.Clear();
        if (Slot.GetComponent<PanelWorkspace>() is { } workspace)
        {
            workspace.PinnedUser.Target = null!;
            workspace.OriginalParent.Target = null!;
        }
        foreach (var list in new[] { _hierarchyContent.Target, _componentsContent.Target, _tabStrip.Target })
        {
            if (list is { IsDestroyed: false })
                list.DestroyChildren();
        }
        Root.Target = root;
        Selected.Target = root;
        _hierarchyDirty = _componentsDirty = true;
        MarkChangeDirty();
    }

    // The scrollbars drag through closures, which no save and no replica carries. A panel built here has them
    // from its build; one this machine loaded takes them up from the parts it came with, once the load has
    // put its references back (asked again from the refresh until then). Everyone else's copy goes without,
    // as it does for a built-in panel.
    private bool _localPartsTaken;

    private void AdoptLocalParts()
    {
        if (_localPartsTaken || World == null || Slot == null)
            return;
        if (_scrollbars.Count > 0 || !MintedHere(World, Slot))
        {
            _localPartsTaken = true;
            return;
        }
        if (_treeScroll.Target == null && _componentScroll.Target == null)
            return;
        _localPartsTaken = true;
        AdoptScroll(_treeScroll.Target, _hierarchyContent.Target);
        AdoptScroll(_componentScroll.Target, _componentsContent.Target);
    }

    private void AdoptScroll(ScrollRect? scroll, Slot? content)
    {
        var host = scroll is { IsDestroyed: false } ? scroll.Slot?.Parent : null;
        var contentRect = content is { IsDestroyed: false } ? content.GetComponent<RectTransform>() : null;
        // From the slot: a loaded ScrollRect only learns its own rect once it starts.
        var viewport = scroll is { IsDestroyed: false } ? scroll.Slot?.GetComponent<RectTransform>() : null;
        if (host == null || contentRect == null || viewport == null)
            return;
        if (DashScrollbar.Adopt(host.FindChild("Scrollbar", recursive: false)) is { } bar)
        {
            bar.Bind(scroll, viewport, contentRect);
            _scrollbars.Add(bar);
        }
        if (host.FindChild("Top", recursive: false) is { } top && host.FindChild("Bottom", recursive: false) is { } bottom)
            _quickScrolls.Add(new QuickScroll { Viewport = viewport, Content = contentRect, Top = top, Bottom = bottom });
    }

    // What code draws and draws again stays out of a saved copy, so a saved inspector is its frame and nothing
    // else. Gated: after the first pass over a list this only reads.
    private void LeaveRowsUnsaved()
    {
        LeaveUnsaved(_hierarchyContent.Target);
        LeaveUnsaved(_componentsContent.Target);
        LeaveUnsaved(_tabStrip.Target);
    }

    private static void LeaveUnsaved(Slot? list)
    {
        if (list == null || list.IsDestroyed)
            return;
        foreach (var row in list.Children)
        {
            if (row.Persistent.Value)
                row.Persistent.Value = false;
        }
    }
}
