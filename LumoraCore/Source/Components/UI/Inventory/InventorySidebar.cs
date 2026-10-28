// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Assets;
using Lumora.Core.Components.UI.Worlds;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// The inventory's left rail. On top, every inventory the account can save into or browse: its own, then one
// root per group it is in, each group in its own colour with a stripe down its edge and its tag or icon, so a
// group folder never passes for one of yours. Under the root being looked at hangs the way down to the open
// folder, one indented step per folder, any step a press away; a folder someone shared in shows in the share
// colour with whose it is. At the bottom, the kind filters and the two ways to add a folder or a link, pinned
// by a spacer the way the world browser's rail does it. -xlinka
internal sealed class InventorySidebar
{
    public const float Width = 200f;
    private const float RootHeight = 34f;
    private const float RowHeight = 27f;
    private const float RowSpacing = 3f;
    private const float RowInset = 12f;
    private const float Indent = 13f;
    private const float StepInset = 22f;
    private const float CountGutter = 40f;
    private const float MarkSize = 22f;
    private const float Stripe = 3f;
    private static readonly Uri OwnIcon = new("res://Assets/Textures/Icons/inventory.svg");

    public readonly Slot Root;
    // A root of the tree by its index, pressed while another one is open.
    public Action<int>? RootPicked;
    // A step of the open root's chain by its index, 0 being the root itself.
    public Action<int>? StepPicked;
    // The delete requests waiting under a root, by the root's index.
    public Action<int>? RequestsPicked;
    public Action<int>? FilterPicked;

    private readonly BrowserParts _parts;
    private readonly PeoplePictureCache _pictures;
    private readonly Slot _tree;
    private readonly LayoutElement _treeElement;
    private readonly List<Slot> _stepRows = new();
    private readonly List<(GroupMark mark, InventoryRoot root)> _marks = new();
    private readonly Slot _filters;
    private readonly List<Row> _filterRows = new();
    private readonly List<(RectTransform rect, InventoryPlace place)> _treeRects = new();
    private readonly PillButton _newFolder;
    private readonly PillButton _addLink;
    private int _rootCount;
    private int _fixedRows;
    private int _selectedFilter = -1;

    public IReadOnlyList<(RectTransform rect, InventoryPlace place)> TreeRects => _treeRects;

    public InventorySidebar(BrowserParts parts, Slot parent, Action newFolder, Action addLink)
    {
        _parts = parts;
        Root = parent.AddSlot("Sidebar");
        Root.AttachComponent<RectTransform>();
        var element = Root.AttachComponent<LayoutElement>();
        element.MinWidth.Value = Width;
        element.PreferredWidth.Value = Width;
        element.FlexibleWidth.Value = 0f;
        element.FlexibleHeight.Value = 1f;
        _pictures = new PeoplePictureCache(Root.AddSlot("Pictures"));

        var column = Root.AttachComponent<VerticalLayout>();
        column.Spacing.Value = RowSpacing;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;

        var heading = Root.AddSlot("Heading");
        heading.AttachComponent<RectTransform>();
        BrowserParts.Height(heading, 22f);
        _parts.Label(heading, "INVENTORIES", DashTheme.FontSmall, DashTheme.TextMuted,
            BrowserParts.Weight.Semibold, TextHorizontalAlignment.Left, RowInset, 0f);

        _tree = Root.AddSlot("Tree");
        _tree.AttachComponent<RectTransform>();
        var treeColumn = _tree.AttachComponent<VerticalLayout>();
        treeColumn.Spacing.Value = RowSpacing;
        treeColumn.ForceExpandWidth.Value = true;
        treeColumn.ForceExpandHeight.Value = false;
        // The tree asks for all its rows but gives way: in a short window FitTree folds the steps nearest the
        // root away, and the roots and the folder you are in stay.
        _treeElement = _tree.AttachComponent<LayoutElement>();
        _treeElement.MinHeight.Value = RootHeight;
        _treeElement.PreferredHeight.Value = RootHeight;
        _treeElement.FlexibleHeight.Value = 0f;

        var spacer = Root.AddSlot("Spacer");
        spacer.AttachComponent<RectTransform>();
        var spacerElement = spacer.AttachComponent<LayoutElement>();
        spacerElement.MinHeight.Value = RowSpacing;
        spacerElement.PreferredHeight.Value = RowSpacing;
        spacerElement.FlexibleHeight.Value = 1f;
        spacerElement.FlexibleWidth.Value = 1f;

        var showHeading = Root.AddSlot("ShowHeading");
        showHeading.AttachComponent<RectTransform>();
        BrowserParts.Height(showHeading, 22f);
        _parts.Label(showHeading, "SHOW", DashTheme.FontSmall, DashTheme.TextMuted,
            BrowserParts.Weight.Semibold, TextHorizontalAlignment.Left, RowInset, 0f);

        _filters = Root.AddSlot("Filters");
        _filters.AttachComponent<RectTransform>();
        var filterColumn = _filters.AttachComponent<VerticalLayout>();
        filterColumn.Spacing.Value = RowSpacing;
        filterColumn.ForceExpandWidth.Value = true;
        filterColumn.ForceExpandHeight.Value = false;

        var divider = Root.AddSlot("Divider");
        divider.AttachComponent<RectTransform>();
        var dividerElement = BrowserParts.Height(divider, 1f);
        dividerElement.Margin.Value = new float4(0f, 6f, 0f, 6f);
        divider.AttachComponent<Image>().Tint.Value = DashTheme.Divider;

        var actions = Root.AddSlot("Actions");
        actions.AttachComponent<RectTransform>();
        BrowserParts.Height(actions, DashTheme.ControlHeight);
        var actionRow = actions.AttachComponent<HorizontalLayout>();
        actionRow.Spacing.Value = 6f;
        actionRow.ForceExpandWidth.Value = true;
        actionRow.ForceExpandHeight.Value = true;
        _newFolder = _parts.AddButton(actions, "+ Folder", 0f, DashTheme.ControlHeight, DashTheme.Accent,
            DashTheme.AccentHover, DashTheme.AccentPressed, DashTheme.OnAccent, BrowserParts.Weight.Semibold, newFolder);
        _addLink = _parts.AddButton(actions, "+ Link", 0f, DashTheme.ControlHeight, DashTheme.Surface,
            DashTheme.SurfaceHover, DashTheme.SurfacePressed, DashTheme.Text, BrowserParts.Weight.Semibold, addLink);
        _newFolder.Text.Size.Value = DashTheme.FontBody;
        _addLink.Text.Size.Value = DashTheme.FontBody;
    }

    // A folder can be made where the account may change things; a link only ever lands in its own inventory.
    public void SetActions(bool canMakeFolder, bool canAddLink)
    {
        _newFolder.SetEnabled(canMakeFolder);
        _addLink.SetEnabled(canAddLink);
    }

    // Every root, the open one with its chain under it. The last step of the chain is the folder you are in
    // and is lit; with the chain at its root, the root row is lit instead.
    public void SetTree(IReadOnlyList<InventoryRoot> roots, int active, IReadOnlyList<InventoryPlace> chain)
    {
        _tree.DestroyChildren();
        _treeRects.Clear();
        _stepRows.Clear();
        _marks.Clear();
        _rootCount = roots.Count;
        _fixedRows = 0;
        int rows = 0;
        float full = 0f;
        for (int r = 0; r < roots.Count; r++)
        {
            var root = roots[r];
            bool open = r == active;
            var rootRect = AddRoot(root, r, open, open && chain.Count <= 1);
            full += RootHeight;
            rows++;
            if (root.Requests > 0)
            {
                AddRequests(root, r);
                full += RowHeight;
                rows++;
            }
            if (!open)
                continue;
            if (chain.Count > 0)
                _treeRects.Add((rootRect, chain[0]));
            for (int i = 1; i < chain.Count; i++)
            {
                AddStep(root, chain[i], i, i == chain.Count - 1);
                full += RowHeight;
                rows++;
            }
        }
        full += System.Math.Max(0, rows - 1) * RowSpacing;
        if (_treeElement.PreferredHeight.Value != full)
            _treeElement.PreferredHeight.Value = full;
    }

    private RectTransform AddRoot(in InventoryRoot root, int index, bool open, bool lit)
    {
        var slot = _tree.AddSlot(root.Place.Name);
        var rect = slot.AttachComponent<RectTransform>();
        BrowserParts.Height(slot, RootHeight);
        var fill = lit ? root.Tint.WithAlpha(0.9f) : open ? root.Tint.WithAlpha(0.16f) : color.Transparent;
        var panel = BrowserParts.Panel(slot, fill, DashTheme.RadiusControl, open && !lit ? root.Tint.WithAlpha(0.35f) : null, 1f);

        if (root.IsGroup)
        {
            var stripe = slot.AddSlot("Stripe");
            var stripeRect = stripe.AttachComponent<RectTransform>();
            stripeRect.AnchorMin.Value = new float2(0f, 0f);
            stripeRect.AnchorMax.Value = new float2(0f, 1f);
            stripeRect.OffsetMin.Value = new float2(1f, 6f);
            stripeRect.OffsetMax.Value = new float2(1f + Stripe, -6f);
            BrowserParts.Panel(stripe, lit ? DashTheme.OnAccent : root.Tint, Stripe * 0.5f);
        }

        var markHost = slot.AddSlot("Mark");
        var markRect = markHost.AttachComponent<RectTransform>();
        markRect.AnchorMin.Value = new float2(0f, 0.5f);
        markRect.AnchorMax.Value = new float2(0f, 0.5f);
        markRect.OffsetMin.Value = new float2(RowInset, -MarkSize * 0.5f);
        markRect.OffsetMax.Value = new float2(RowInset + MarkSize, MarkSize * 0.5f);
        if (root.IsGroup)
        {
            var mark = GroupMark.Build(markHost, _parts.FontFor(BrowserParts.Weight.Bold), 6f, 10f);
            mark.Bind(_pictures, root.IconHash, MarkLetters(root), root.Place.Name, root.Tint);
            _marks.Add((mark, root));
        }
        else
        {
            var picture = markHost.AttachComponent<ImageProvider>();
            picture.GenerateMipmaps.Value = false;
            picture.URL.Value = OwnIcon;
            var image = markHost.AttachComponent<Image>();
            image.Texture.Target = picture;
            image.Tint.Value = lit ? DashTheme.OnAccent : DashTheme.TextDim;
        }

        // The note keeps its room on the right, as wide as its words; a long group name gives way and ends in an
        // ellipsis rather than running under it.
        float textLeft = RowInset + MarkSize + 9f;
        float noteRoom = root.Note.Length > 0 ? 12f + root.Note.Length * 5.6f : 8f;
        var name = _parts.Label(slot, BrowserParts.Truncate(root.Place.Name, 18), DashTheme.FontBody,
            lit ? DashTheme.OnAccent : root.IsGroup ? root.Ink : DashTheme.Text, BrowserParts.Weight.Semibold,
            TextHorizontalAlignment.Left, textLeft, noteRoom);
        name.Ellipsis.Value = true;
        if (root.Note.Length > 0)
        {
            _parts.Label(slot, root.Note, DashTheme.FontLabel,
                lit ? DashTheme.OnAccent.WithAlpha(0.8f) : root.Warns ? DashTheme.Warning : DashTheme.TextMuted,
                root.Warns ? BrowserParts.Weight.Semibold : BrowserParts.Weight.Regular, TextHorizontalAlignment.Right, 0f, 9f);
        }

        var row = new Row(index, panel, null, null, fill, lit);
        var button = slot.AttachComponent<Button>();
        button.HoverEntered += _ => row.SetHovered(true);
        button.HoverExited += _ => row.SetHovered(false);
        int target = index;
        button.Clicked += (_, _) =>
        {
            if (open)
                StepPicked?.Invoke(0);
            else
                RootPicked?.Invoke(target);
        };
        return rect;
    }

    // Under a group whose moderators have something to decide: how many delete requests wait, a press away.
    private void AddRequests(in InventoryRoot root, int index)
    {
        var slot = _tree.AddSlot("Requests");
        _fixedRows++;
        BrowserParts.Height(slot, RowHeight);
        var fill = DashTheme.Warning.WithAlpha(0.12f);
        var panel = BrowserParts.Panel(slot, fill, DashTheme.RadiusControl, DashTheme.Warning.WithAlpha(0.45f), 1f);
        _parts.Label(slot, root.Requests == 1 ? "1 delete request" : $"{root.Requests} delete requests", DashTheme.FontSmall, DashTheme.Warning,
            BrowserParts.Weight.Semibold, TextHorizontalAlignment.Left, StepInset, 6f);
        _parts.Label(slot, "Review", DashTheme.FontLabel, DashTheme.Warning.WithAlpha(0.8f), BrowserParts.Weight.Regular,
            TextHorizontalAlignment.Right, 0f, 10f);
        var row = new Row(index, panel, null, null, fill, false);
        var button = slot.AttachComponent<Button>();
        button.HoverEntered += _ => row.SetHovered(true);
        button.HoverExited += _ => row.SetHovered(false);
        int target = index;
        button.Clicked += (_, _) => RequestsPicked?.Invoke(target);
    }

    private void AddStep(in InventoryRoot root, in InventoryPlace step, int depth, bool last)
    {
        bool shared = !string.Equals(step.OwnerId ?? string.Empty, root.Place.OwnerId ?? string.Empty, StringComparison.Ordinal);
        bool library = step.IsLibrary;
        var tint = library ? InventoryCard.LibraryInk : shared ? InventoryCard.ShareInk : root.Tint;
        var slot = _tree.AddSlot(step.Name);
        _stepRows.Add(slot);
        var rect = slot.AttachComponent<RectTransform>();
        BrowserParts.Height(slot, RowHeight);
        var fill = last ? tint.WithAlpha(0.9f) : color.Transparent;
        var panel = BrowserParts.Panel(slot, fill, DashTheme.RadiusControl);
        float inset = StepInset + (depth - 1) * Indent;
        string label = "› " + BrowserParts.Truncate(step.Name, System.Math.Max(8, 19 - depth));
        if (shared && !library && step.OwnerName.Length > 0)
            label += " · " + BrowserParts.Truncate(step.OwnerName, 8);
        var ink = last ? (shared ? DashTheme.TextOnLight : DashTheme.OnAccent) : library ? InventoryCard.LibraryInk : shared ? InventoryCard.ShareInk : DashTheme.TextDim;
        _parts.Label(slot, label, DashTheme.FontBody, ink, last ? BrowserParts.Weight.Semibold : BrowserParts.Weight.Regular,
            TextHorizontalAlignment.Left, inset, 6f);
        _treeRects.Add((rect, step));
        if (last)
            return;
        var row = new Row(depth, panel, null, null, fill, false);
        var button = slot.AttachComponent<Button>();
        button.HoverEntered += _ => row.SetHovered(true);
        button.HoverExited += _ => row.SetHovered(false);
        int target = depth;
        button.Clicked += (_, _) => StepPicked?.Invoke(target);
    }

    // Group icons come down after the tree is drawn; this puts them on the marks as they land.
    public void Tick()
    {
        if (!_pictures.Settle())
            return;
        foreach (var (mark, root) in _marks)
            mark.Bind(_pictures, root.IconHash, MarkLetters(root), root.Place.Name, root.Tint);
    }

    // A 22 unit mark holds three letters at most; a longer tag gives way to the name's initials.
    private static string MarkLetters(in InventoryRoot root)
        => root.Tag.Length is > 0 and <= 3 ? root.Tag : PeopleFormat.Initials(root.Place.Name);

    // As many of the last steps as the height the column gave the tree holds; the roots always stay.
    public void FitTree()
    {
        float height = _tree.GetComponent<RectTransform>()?.LocalComputeRect.height ?? 0f;
        if (height <= 0f || _stepRows.Count == 0)
            return;
        float roots = _rootCount * (RootHeight + RowSpacing) + _fixedRows * (RowHeight + RowSpacing);
        int room = System.Math.Max(1, (int)MathF.Floor((height - roots + RowSpacing + 0.5f) / (RowHeight + RowSpacing)));
        int first = System.Math.Max(0, _stepRows.Count - room);
        for (int i = 0; i < _stepRows.Count; i++)
        {
            bool show = i >= first;
            if (_stepRows[i].ActiveSelf.Value != show)
                _stepRows[i].ActiveSelf.Value = show;
        }
    }

    public void AddFilter(int id, string label)
    {
        var slot = _filters.AddSlot(label);
        slot.AttachComponent<RectTransform>();
        BrowserParts.Height(slot, RowHeight);
        var panel = BrowserParts.Panel(slot, color.Transparent, DashTheme.RadiusControl);
        var name = _parts.Label(slot, label, DashTheme.FontBody, DashTheme.TextDim,
            BrowserParts.Weight.Semibold, TextHorizontalAlignment.Left, RowInset, CountGutter);
        var count = _parts.Label(slot, string.Empty, DashTheme.FontSmall, DashTheme.TextMuted,
            BrowserParts.Weight.Regular, TextHorizontalAlignment.Right, 0f, RowInset);
        var row = new Row(id, panel, name, count, color.Transparent, false);
        _filterRows.Add(row);
        var button = slot.AttachComponent<Button>();
        button.HoverEntered += _ => row.SetHovered(true);
        button.HoverExited += _ => row.SetHovered(false);
        button.Clicked += (_, _) => FilterPicked?.Invoke(id);
        row.Repaint();
    }

    public void SetCount(int id, int count)
    {
        for (int i = 0; i < _filterRows.Count; i++)
        {
            if (_filterRows[i].Id != id)
                continue;
            BrowserParts.SetText(_filterRows[i].Count, count > 0 ? count.ToString() : string.Empty);
            return;
        }
    }

    public void SelectFilter(int id)
    {
        _selectedFilter = id;
        for (int i = 0; i < _filterRows.Count; i++)
            _filterRows[i].SetSelected(_filterRows[i].Id == id);
    }

    public int SelectedFilter => _selectedFilter;

    // Painted by hand rather than through a ColorDriver, which would own the panel colour and refuse
    // the selection write. Same trap the world browser's rail already stepped around. -xlinka
    private sealed class Row
    {
        public readonly int Id;
        public readonly Text? Count;
        private readonly RoundedPanel _panel;
        private readonly Text? _name;
        private readonly color _rest;
        private readonly bool _lit;
        private bool _hovered;
        private bool _selected;

        public Row(int id, RoundedPanel panel, Text? name, Text? count, in color rest, bool lit)
        {
            Id = id;
            _panel = panel;
            _name = name;
            Count = count;
            _rest = rest;
            _lit = lit;
        }

        public void SetHovered(bool value)
        {
            _hovered = value;
            Repaint();
        }

        public void SetSelected(bool value)
        {
            _selected = value;
            Repaint();
        }

        public void Repaint()
        {
            if (_lit)
                return;
            var fill = _selected ? DashTheme.Accent : _hovered ? DashTheme.SurfaceHover : _rest;
            BrowserParts.SetColor(_panel.Color, fill);
            if (_name != null)
                BrowserParts.SetColor(_name.Color, _selected ? DashTheme.OnAccent : DashTheme.TextDim);
            if (Count != null)
                BrowserParts.SetColor(Count.Color, _selected ? DashTheme.OnAccent.WithAlpha(0.85f) : DashTheme.TextMuted);
        }
    }
}
