// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Assets;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// The chrome around the tree and the component list: tabs and tool icons in the title bar, the selection
// line with its jump buttons, scroll helpers, the divider, the resize corner, component order and notes.
//
// Whoever spawns the panel builds this, but the authority keeps it current, so every piece the refresh
// touches is held through a synced ref. A plain field would only exist on the spawner's machine and a
// panel opened by anyone but the host would never update its tabs. -xlinka
public partial class SceneInspectorPanel
{
    public readonly SyncRefList<InspectorTab> Tabs = new();
    public readonly SyncRef<InspectorTab> ActiveTab = new();
    public readonly Sync<float> HierarchyWidth = new(DefaultHierarchyWidth);
    public readonly Sync<bool> ShowCompass = new();

    private readonly SyncRef<Slot> _tabStrip = new();
    private readonly SyncRefList<Button> _tools = new();
    private readonly SyncRef<Image> _colorSwatch = new();
    private readonly SyncRef<Text> _objectTitle = new();
    private readonly SyncRef<LayoutElement> _hierarchyColumn = new();
    private readonly SyncRef<ScrollRect> _treeScroll = new();
    private readonly SyncRef<ScrollRect> _componentScroll = new();

    public const float DefaultHierarchyWidth = 380f;
    private const float MinHierarchyWidth = 230f;
    private const float MinDetailWidth = 400f;
    // Shell and page padding, both column gaps and the divider: width the hierarchy can never take.
    private const float BodyChrome = 80f;
    private const int MaxTabs = 10;
    private const float TabWidth = 210f;
    private const float TabMinWidth = 52f;
    private const float TabCloseWidth = 24f;
    private const float ToolSize = 34f;
    private const float ToolIconSize = 20f;
    private const float ChromeRowHeight = 32f;
    private const float ChromeFontSize = 15f;
    private const float ScrollColumn = 24f;
    private const float HintWidth = 180f;
    private const float HintHeight = 30f;
    // Metres toward the viewer, so a torn-off tab never shares a plane with the panel it came out of.
    private const float TearOffLift = 0.03f;
    private const int BranchExpandBudget = RowBudget;
    private const float WorkspaceRefreshInterval = 0.25f;
    private const string IconRoot = "res://Assets/Textures/Icons/";

    private static readonly color ChipHover = new(1f, 1f, 1f, 0.08f);
    private static readonly color ChipPressed = new(1f, 1f, 1f, 0.14f);
    private static readonly color ActiveTabFill = new(1f, 1f, 1f, 0.10f);
    private static readonly color GroupFill = new(1f, 1f, 1f, 0.04f);
    private static readonly color SwatchIdle = new(InspectorUI.MutedColor.r, InspectorUI.MutedColor.g, InspectorUI.MutedColor.b, 0.35f);

    private static readonly (string Key, string Label, float Width)[] JumpTargets =
    {
        ("root", "Root", 50f), ("player", "Player", 62f), ("chest", "Chest", 56f),
        ("head", "Head", 50f), ("left", "L Hand", 64f), ("right", "R Hand", 64f),
    };

    private sealed class TabChip
    {
        public Button Button = null!;
        public Text Label = null!;
    }

    // Local to whichever machine built the panel: the scrollbar drags through closures, so only that
    // machine can drive it anyway.
    private sealed class QuickScroll
    {
        public RectTransform Viewport = null!;
        public RectTransform Content = null!;
        public Slot Top = null!;
        public Slot Bottom = null!;
    }

    private readonly Dictionary<InspectorTab, TabChip> _tabChips = new();
    private readonly List<DashScrollbar> _scrollbars = new();
    private readonly List<QuickScroll> _quickScrolls = new();
    private string _tabSignature = "";
    private int _builtOrder;
    private float _laidOutHierarchyWidth = DefaultHierarchyWidth;
    private Component? _dropTarget;
    private User? _actionActor;
    private Slot? _compassLine;
    private float3 _compassStart, _compassEnd;
    private float _workspaceRefresh;

    // ---- title bar ---------------------------------------------------------------------------------

    private void BuildHeaderBar()
    {
        var shell = Slot.GetComponent<PanelShell>();
        if (shell == null)
            return;
        var bar = shell.EnsureHeaderContent();
        bar.DestroyChildren();
        _tabChips.Clear();
        _tabSignature = "";
        _tools.Clear();

        var layout = bar.GetOrAttachComponent<HorizontalLayout>();
        layout.Spacing.Value = 10f;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;
        var ui = new UIBuilder(bar);
        InspectorUI.ApplyTheme(ui, Slot);

        ui.PushStyle().MinWidth(0f).FlexibleWidth(1f);
        var strip = ui.Next("Tabs");
        ui.PopStyle();
        var stripLayout = strip.AttachComponent<HorizontalLayout>();
        stripLayout.Spacing.Value = 4f;
        stripLayout.ForceExpandWidth.Value = false;
        stripLayout.ForceExpandHeight.Value = true;
        _tabStrip.Target = strip;

        const int toolCount = 5;
        const float toolGap = 2f;
        float toolsWidth = toolCount * ToolSize + (toolCount - 1) * toolGap;
        ui.PushStyle().MinWidth(toolsWidth).PreferredWidth(toolsWidth).FlexibleWidth(0f);
        var tools = ui.Next("Tools");
        ui.PopStyle();
        var toolLayout = tools.AttachComponent<HorizontalLayout>();
        toolLayout.Spacing.Value = toolGap;
        toolLayout.ForceExpandWidth.Value = false;
        toolLayout.ForceExpandHeight.Value = true;
        var toolUi = new UIBuilder(tools);
        InspectorUI.ApplyTheme(toolUi, Slot);
        AddToolButton(toolUi, "panel:pin", "pin", "Pin to me");
        AddToolButton(toolUi, "panel:camera", "camera-off", "Hide from cameras");
        AddColorButton(toolUi);
        AddToolButton(toolUi, "panel:compass", "compass", "Line to selection");
        AddToolButton(toolUi, "panel:lock", "lock", "Lock in place");
    }

    private void AddToolButton(UIBuilder ui, string action, string icon, string hint)
    {
        ui.PushStyle().BackgroundColor(color.Transparent);
        var button = InspectorUI.RelayButton(ui, this, action, "", ToolSize);
        ui.PopStyle();
        SetChipColors(button, color.Transparent);
        AddIcon(button.Slot, icon, ToolIconSize, InspectorUI.MutedColor);
        AddHint(button.Slot, hint);
        _tools.Add(button);
    }

    private void AddColorButton(UIBuilder ui)
    {
        ui.PushStyle().BackgroundColor(color.Transparent);
        var button = InspectorUI.RelayButton(ui, this, "panel:color", "", ToolSize);
        ui.PopStyle();
        SetChipColors(button, color.Transparent);
        var dot = button.Slot.AddSlot("Swatch");
        Center(dot.AttachComponent<RectTransform>(), 16f);
        var swatch = dot.AttachComponent<Image>();
        swatch.CornerRadius.Value = 8f;
        swatch.Tint.Value = SwatchIdle;
        _colorSwatch.Target = swatch;
        AddHint(button.Slot, "Frame colour");
        _tools.Add(button);
    }

    private static Image AddIcon(Slot host, string icon, float size, in color tint)
    {
        var glyph = host.AddSlot("Icon");
        Center(glyph.AttachComponent<RectTransform>(), size);
        var picture = glyph.AttachComponent<ImageProvider>();
        // The UI shader samples without mips, so these icons are authored at 64 px instead: close to the
        // size they draw at, which is what keeps thin strokes from shimmering.
        picture.GenerateMipmaps.Value = false;
        picture.AllowCompression.Value = false;
        picture.MaxSizeOverride.Value = -1;
        picture.URL.Value = new Uri(IconRoot + icon + ".svg");
        var image = glyph.AttachComponent<Image>();
        image.Texture.Target = picture;
        image.Tint.Value = tint;
        return image;
    }

    // A label above the control while it is hovered. Above, not below: the content under the title bar
    // draws later and would cover it.
    private void AddHint(Slot host, string text)
    {
        var tip = host.AddSlot("Tip");
        var rect = tip.AttachComponent<RectTransform>();
        tip.AttachComponent<IgnoreLayout>();
        rect.AnchorMin.Value = rect.AnchorMax.Value = new float2(0.5f, 1f);
        rect.OffsetMin.Value = new float2(-HintWidth * 0.5f, 8f);
        rect.OffsetMax.Value = new float2(HintWidth * 0.5f, 8f + HintHeight);
        var background = tip.AttachComponent<Image>();
        background.Tint.Value = DashTheme.Surface;
        background.CornerRadius.Value = DashTheme.RadiusChip;
        var ui = new UIBuilder(tip);
        InspectorUI.ApplyTheme(ui, Slot);
        var label = ui.Text(text, ChromeFontSize, InspectorUI.TextColor);
        InspectorUI.FillParent(label.RectTransform!);
        label.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        label.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        tip.ActiveSelf.Value = false;
        host.AttachComponent<HoverTip>();
    }

    // ---- selection line ----------------------------------------------------------------------------

    // The selected slot on the left, the object it belongs to on the right, then the jump buttons.
    private void BuildSelectionRow(Slot page)
    {
        InspectorUI.FixedRow(page, "Selection", ChromeRowHeight, out var ui, Slot);
        _selectedTitle.Target = ClippedLabel(ui, "Selected", 1.4f, InspectorUI.FontSize + 1f, InspectorUI.TextColor, TextHorizontalAlignment.Left);
        _objectTitle.Target = ClippedLabel(ui, "Object", 1f, ChromeFontSize, InspectorUI.MutedColor, TextHorizontalAlignment.Right);

        float width = 4f;
        foreach (var target in JumpTargets)
            width += target.Width;
        ui.PushStyle().MinWidth(width).PreferredWidth(width).FlexibleWidth(0f);
        var group = ui.Next("Jump");
        ui.PopStyle();
        var fill = group.AttachComponent<Image>();
        fill.Tint.Value = GroupFill;
        fill.CornerRadius.Value = DashTheme.RadiusControl;
        var layout = group.AttachComponent<HorizontalLayout>();
        layout.PaddingLeft.Value = layout.PaddingRight.Value = 2f;
        layout.PaddingTop.Value = layout.PaddingBottom.Value = 2f;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;
        var jumpUi = new UIBuilder(group);
        InspectorUI.ApplyTheme(jumpUi, Slot);
        jumpUi.FontSize(ChromeFontSize).TextColor(InspectorUI.MutedColor).BackgroundColor(color.Transparent);
        foreach (var target in JumpTargets)
            SetChipColors(InspectorUI.RelayButton(jumpUi, this, "nav:" + target.Key, target.Label, target.Width), color.Transparent);
    }

    // Text clipped to its own cell, so a long slot name stops at the edge instead of running under
    // whatever sits beside it.
    private Text ClippedLabel(UIBuilder ui, string name, float flexible, float size, in color tint, TextHorizontalAlignment align)
    {
        ui.PushStyle().MinWidth(0f).FlexibleWidth(flexible);
        var host = ui.Next(name);
        ui.PopStyle();
        host.AttachComponent<Mask>();
        var labelUi = new UIBuilder(host);
        InspectorUI.ApplyTheme(labelUi, Slot);
        var text = labelUi.Text("", size, tint);
        InspectorUI.FillParent(text.RectTransform!);
        text.HorizontalAlignment.Value = align;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        // Slot names are user content with inline style tags.
        text.RichText.Value = true;
        return text;
    }

    private Slot? SelectionObjectRoot()
    {
        var selected = Selected.Target;
        if (selected == null || selected.IsDestroyed)
            return null;
        var root = selected.ObjectRoot;
        if (!root.IsRootSlot)
            return root;
        root = selected;
        while (root.Parent != null && !root.Parent.IsRootSlot && root.Parent.ActiveUserRoot == selected.ActiveUserRoot)
            root = root.Parent;
        return root;
    }

    private Slot? BoneShortcut(BodyNode bone)
    {
        var selected = Selected.Target;
        var rig = selected?.GetComponentInParents<HumanoidRig>()
            ?? selected?.GetComponentInChildren<HumanoidRig>()
            ?? SelectionObjectRoot()?.GetComponentInChildren<HumanoidRig>();
        if (rig == null)
            return null;
        return rig.TryGetBone(bone) ?? (bone == BodyNode.Chest ? rig.TryGetBone(BodyNode.Spine) : null);
    }

    // ---- body helpers ------------------------------------------------------------------------------

    private void BuildDivider(Slot body)
    {
        var divider = body.AddSlot("Divider");
        divider.AttachComponent<RectTransform>();
        var layout = divider.AttachComponent<LayoutElement>();
        layout.MinWidth.Value = layout.PreferredWidth.Value = 8f;
        layout.FlexibleWidth.Value = 0f;
        layout.FlexibleHeight.Value = 1f;
        var image = divider.AttachComponent<Image>();
        image.Tint.Value = InspectorUI.HeaderColor;
        image.CornerRadius.Value = 4f;
        var grip = divider.AttachComponent<InspectorDivider>();
        grip.Panel.Target = this;
        grip.AddColorDriver(image.Tint);
    }

    public void SetHierarchyWidth(float width)
    {
        float total = Slot.GetComponent<PanelShell>()?.Size.Value.x ?? 1000f;
        float widest = MathF.Max(MinHierarchyWidth, total - MinDetailWidth - BodyChrome);
        float clamped = System.Math.Clamp(width, MinHierarchyWidth, widest);
        if (HierarchyWidth.Value != clamped)
            HierarchyWidth.Value = clamped;
        ApplyHierarchyWidth();
    }

    private void ApplyHierarchyWidth()
    {
        if (_hierarchyColumn.Target is { IsDestroyed: false } column && column.PreferredWidth.Value != HierarchyWidth.Value)
            column.PreferredWidth.Value = HierarchyWidth.Value;
        RelayoutHierarchyWidth();
    }

    // The column is its own graphics chunk, and a width change on a chunk root only relays that chunk:
    // the body's layout, which is what actually sizes the column, never runs, so the divider drags and
    // nothing moves. Dirty the body instead. Every peer runs this from its tick, because a width that
    // arrives over the wire takes the same dead-end path. -xlinka
    private void RelayoutHierarchyWidth()
    {
        if (_hierarchyColumn.Target is not { IsDestroyed: false } column)
            return;
        float width = column.PreferredWidth.Value;
        if (width == _laidOutHierarchyWidth)
            return;
        _laidOutHierarchyWidth = width;
        column.Slot.Parent?.GetComponent<RectTransform>()?.MarkChangeDirty();
    }

    private void BuildResizeHandle()
    {
        var shell = Slot.GetComponent<PanelShell>();
        var content = shell?.ContentSlot;
        if (shell == null || content == null)
            return;
        // RectTransform before IgnoreLayout: every UI component attaches a rect of its own when there is
        // none yet, and the anchors below would then land on a second, dead one.
        var corner = content.AddSlot("Resize");
        var rect = corner.AttachComponent<RectTransform>();
        corner.AttachComponent<IgnoreLayout>();
        rect.AnchorMin.Value = rect.AnchorMax.Value = new float2(1f, 0f);
        rect.OffsetMin.Value = new float2(-30f, 0f);
        rect.OffsetMax.Value = new float2(0f, 30f);
        var handle = corner.AttachComponent<PanelResizeHandle>();
        handle.Panel.Target = shell;
        handle.Standard.Value = shell.Size.Value;
        AddIcon(corner, "resize", 22f, InspectorUI.MutedColor);
    }

    // A scrollbar down the right edge of a list with jump-to-top and jump-to-bottom caps, which only show
    // while there is something to scroll.
    private void BuildScrollControls(Slot host, ScrollRect scroll, RectTransform content, bool tree)
    {
        (tree ? _treeScroll : _componentScroll).Target = scroll;
        var bar = DashScrollbar.OnRightEdge(host, (ScrollColumn - DashScrollbar.Width) * 0.5f, ScrollColumn, ScrollColumn);
        bar.Bind(scroll, scroll.RectTransform, content);
        _scrollbars.Add(bar);
        string list = tree ? "tree" : "components";
        _quickScrolls.Add(new QuickScroll
        {
            Viewport = scroll.RectTransform!,
            Content = content,
            Top = BuildQuickScroll(host, $"scroll:{list}:top", "↑", top: true),
            Bottom = BuildQuickScroll(host, $"scroll:{list}:bottom", "↓", top: false),
        });
    }

    private Slot BuildQuickScroll(Slot host, string action, string glyph, bool top)
    {
        var cap = host.AddSlot(top ? "Top" : "Bottom");
        var rect = cap.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = rect.AnchorMax.Value = new float2(1f, top ? 1f : 0f);
        rect.OffsetMin.Value = new float2(-ScrollColumn, top ? -ScrollColumn : 0f);
        rect.OffsetMax.Value = new float2(0f, top ? 0f : ScrollColumn);
        var ui = new UIBuilder(cap);
        InspectorUI.ApplyTheme(ui, Slot);
        ui.BackgroundColor(color.Transparent).TextColor(InspectorUI.MutedColor);
        var button = InspectorUI.RelayButton(ui, this, action, glyph, 0f);
        InspectorUI.FillParent(button.RectTransform!);
        SetChipColors(button, color.Transparent);
        cap.ActiveSelf.Value = false;
        return cap;
    }

    // ---- tabs --------------------------------------------------------------------------------------

    // The panel's own Root/Selected/expansion fields ARE the live tab, so the active tab's component only
    // gets written when it is parked behind another one.
    private void EnsureTab()
    {
        if (ActiveTab.Target is { IsDestroyed: false } || Root.Target == null)
            return;
        var tab = Slot.AttachComponent<InspectorTab>();
        // A tab is where this panel is looking, which a saved copy has no use for.
        tab.Persistent = false;
        tab.Root.Target = Root.Target;
        tab.Selected.Target = Selected.Target;
        Tabs.Add(tab);
        ActiveTab.Target = tab;
        ParkActiveTab();
    }

    private Slot? TabRoot(InspectorTab tab)
        => ReferenceEquals(tab, ActiveTab.Target) ? Root.Target : tab.Root.Target;

    private void ParkActiveTab()
    {
        if (ActiveTab.Target is not { IsDestroyed: false } tab)
            return;
        SetRef(tab.Root, Root.Target);
        SetRef(tab.Selected, Selected.Target);
        CopyExpansion(ExpandedSlots, tab.ExpandedSlots);
        CopyExpansion(ExpandedComponents, tab.ExpandedComponents);
        if (_treeScroll.Target is { IsDestroyed: false } tree)
            SetValue(tab.TreeScroll, tree.NormalizedPosition);
        if (_componentScroll.Target is { IsDestroyed: false } components)
            SetValue(tab.ComponentScroll, components.NormalizedPosition);
        SetValue(tab.HierarchyWidth, HierarchyWidth.Value);
    }

    private static void CopyExpansion(SyncFieldList<ulong> from, SyncFieldList<ulong> to)
    {
        if (from.SequenceEqual(to))
            return;
        var values = from.ToArray();
        to.Clear();
        to.AddRange(values);
    }

    // A tab already rooted at the target comes to the front instead of opening twice, unless the caller
    // asked for a fresh one (the "+" button).
    public void OpenTab(Slot target, bool reuseExisting = true)
    {
        if (target == null || target.IsDestroyed || RefuseProtected(target, "open tab"))
            return;
        EnsureTab();
        if (reuseExisting)
        {
            foreach (var existing in Tabs)
            {
                if (existing is not { IsDestroyed: false } || !ReferenceEquals(TabRoot(existing), target))
                    continue;
                SwitchTab(existing);
                return;
            }
        }
        while (Tabs.Count >= MaxTabs)
        {
            var oldest = Tabs.FirstOrDefault(t => t != null && !ReferenceEquals(t, ActiveTab.Target));
            if (oldest == null)
                break;
            Tabs.Remove(oldest);
            oldest.Destroy();
        }
        var tab = Slot.AttachComponent<InspectorTab>();
        tab.Persistent = false;
        tab.Root.Target = target;
        tab.Selected.Target = target;
        tab.HierarchyWidth.Value = HierarchyWidth.Value;
        Tabs.Add(tab);
        SwitchTab(tab);
    }

    public void SwitchTab(InspectorTab tab)
    {
        if (tab == null || tab.IsDestroyed || !Tabs.Contains(tab) || ReferenceEquals(tab, ActiveTab.Target))
            return;
        ParkActiveTab();
        ActiveTab.Target = tab;
        RestoreTab(tab);
    }

    private void RestoreTab(InspectorTab tab)
    {
        Root.Target = tab.Root.Target is { IsDestroyed: false } root ? root : World.RootSlot;
        Selected.Target = tab.Selected.Target is { IsDestroyed: false } selected ? selected : Root.Target;
        CopyExpansion(tab.ExpandedSlots, ExpandedSlots);
        CopyExpansion(tab.ExpandedComponents, ExpandedComponents);
        SetHierarchyWidth(tab.HierarchyWidth.Value);

        // ScrollRect stores normalized positions independently of layout. Restore them immediately:
        // the next layout uses the new range, and a quick switch cannot park another tab's position.
        // Setting the position also stops any wheel animation left over from the previous tab.
        if (_treeScroll.Target is { IsDestroyed: false } tree)
            tree.NormalizedPosition = tab.TreeScroll.Value;
        if (_componentScroll.Target is { IsDestroyed: false } components)
            components.NormalizedPosition = tab.ComponentScroll.Value;
        _hierarchyDirty = _componentsDirty = true;
        RunApplyChangesSafe();
    }

    // Closing the last tab closes the panel, the neighbour to the right takes over otherwise.
    public void CloseTab(InspectorTab tab)
    {
        int index = Tabs.IndexOf(tab);
        if (index < 0)
            return;
        if (Tabs.Count == 1)
        {
            Slot.GetComponent<PanelShell>()?.Close();
            return;
        }
        if (ReferenceEquals(tab, ActiveTab.Target))
        {
            var next = Tabs[index + 1 < Tabs.Count ? index + 1 : index - 1];
            if (next != null)
                SwitchTab(next);
        }
        Tabs.Remove(tab);
        tab.Destroy();
        RunApplyChangesSafe();
    }

    // The displayed root died: drop just that tab when there are others to fall back on.
    private void CloseActiveTabOrPanel()
    {
        if (IsDestroyed)
            return;
        using var keep = Upkeep();
        if (Tabs.Count > 1 && ActiveTab.Target is { IsDestroyed: false } active)
            CloseTab(active);
        else
            Slot.Destroy();
    }

    // Gripped out of the strip: a new inspector on this one's spot, a little toward the viewer, carrying
    // the tab's view. The hand grabs it on the same frame.
    public IGrabbable? TearOffTab(InspectorTab tab)
    {
        if (Tabs.Count < 2 || !Tabs.Contains(tab) || RefuseLocked(World))
            return null;
        if (ReferenceEquals(tab, ActiveTab.Target))
            ParkActiveTab();
        var root = tab.Root.Target is { IsDestroyed: false } r ? r : World.RootSlot;
        var pane = SpawnBeside(this, root, new float3(0f, 0f, TearOffLift));
        if (pane == null)
            return null;
        pane.EnsureTab();
        pane.RestoreTab(tab);
        pane.ParkActiveTab();
        CloseTab(tab);
        return pane.Slot.GetComponent<Grabbable>();
    }

    private void RefreshTabs()
    {
        var strip = _tabStrip.Target;
        if (strip == null || strip.IsDestroyed)
            return;

        // A background tab whose root is gone has nothing left to show.
        for (int i = Tabs.Count - 1; i >= 0; i--)
        {
            var tab = Tabs[i];
            if (tab is { IsDestroyed: false } && !ReferenceEquals(tab, ActiveTab.Target) && tab.Root.Target == null)
            {
                Tabs.RemoveAt(i);
                tab.Destroy();
            }
        }

        var signature = new System.Text.StringBuilder();
        foreach (var tab in Tabs)
        {
            if (tab is { IsDestroyed: false })
                signature.Append(tab.ReferenceID.RawValue).Append(',');
        }
        if (signature.ToString() != _tabSignature)
        {
            _tabSignature = signature.ToString();
            BuildTabStrip(strip);
        }
        foreach (var pair in _tabChips)
            StyleTab(pair.Key, pair.Value);
    }

    private void BuildTabStrip(Slot strip)
    {
        strip.DestroyChildren();
        _tabChips.Clear();
        var ui = new UIBuilder(strip);
        InspectorUI.ApplyTheme(ui, Slot);
        foreach (var tab in Tabs)
        {
            if (tab is { IsDestroyed: false })
                _tabChips[tab] = BuildTab(ui, tab);
        }
        ui.PushStyle().BackgroundColor(color.Transparent).TextColor(InspectorUI.MutedColor).FontSize(24f);
        var add = InspectorUI.RelayButton(ui, this, "tab:new", "+", ToolSize);
        ui.PopStyle();
        SetChipColors(add, color.Transparent);
        LeaveUnsaved(strip);
    }

    private TabChip BuildTab(UIBuilder ui, InspectorTab tab)
    {
        ulong id = tab.ReferenceID.RawValue;
        ui.PushStyle().MinWidth(TabMinWidth).PreferredWidth(TabWidth).FlexibleWidth(0f);
        var chip = ui.Next("Tab");
        ui.PopStyle();
        var background = chip.AttachComponent<Image>();
        background.Tint.Value = color.Transparent;
        background.CornerRadius.Value = DashTheme.RadiusControl;
        var button = chip.AttachComponent<Button>();
        BindRelay(button, $"tab:pick:{id}");
        var grip = chip.AttachComponent<InspectorTabGrip>();
        grip.Panel.Target = this;
        grip.Tab.Target = tab;

        var labelHost = chip.AddSlot("Label");
        var labelRect = labelHost.AttachComponent<RectTransform>();
        labelRect.AnchorMin.Value = float2.Zero;
        labelRect.AnchorMax.Value = float2.One;
        labelRect.OffsetMin.Value = new float2(12f, 0f);
        labelRect.OffsetMax.Value = new float2(-(TabCloseWidth + 6f), 0f);
        labelHost.AttachComponent<Mask>();
        var labelUi = new UIBuilder(labelHost);
        InspectorUI.ApplyTheme(labelUi, Slot);
        var label = labelUi.Text("", ChromeFontSize, InspectorUI.MutedColor);
        InspectorUI.FillParent(label.RectTransform!);
        label.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        label.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        label.RichText.Value = true;

        var close = chip.AddSlot("Close");
        var closeRect = close.AttachComponent<RectTransform>();
        closeRect.AnchorMin.Value = new float2(1f, 0f);
        closeRect.AnchorMax.Value = new float2(1f, 1f);
        closeRect.OffsetMin.Value = new float2(-(TabCloseWidth + 4f), 5f);
        closeRect.OffsetMax.Value = new float2(-4f, -5f);
        var closeImage = close.AttachComponent<Image>();
        closeImage.Tint.Value = color.Transparent;
        closeImage.CornerRadius.Value = DashTheme.RadiusChip;
        var closeButton = close.AttachComponent<Button>();
        BindRelay(closeButton, $"tab:close:{id}");
        SetChipColors(closeButton, color.Transparent);
        var closeUi = new UIBuilder(close);
        InspectorUI.ApplyTheme(closeUi, Slot);
        var cross = closeUi.Text("×", ChromeFontSize + 3f, InspectorUI.MutedColor);
        InspectorUI.FillParent(cross.RectTransform!);
        cross.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        cross.VerticalAlignment.Value = TextVerticalAlignment.Middle;

        return new TabChip { Button = button, Label = label };
    }

    private void StyleTab(InspectorTab tab, TabChip chip)
    {
        if (tab.IsDestroyed || chip.Button.IsDestroyed || chip.Label.IsDestroyed)
            return;
        bool active = ReferenceEquals(tab, ActiveTab.Target);
        SetChipColors(chip.Button, active ? ActiveTabFill : color.Transparent);
        var root = TabRoot(tab);
        SetText(chip.Label, root == null || root.IsDestroyed ? "<i>gone</i>" : root.SlotName.Value ?? "Slot");
        SetColor(chip.Label.Color, active ? InspectorUI.TextColor : InspectorUI.MutedColor);
    }

    private void BindRelay(Button button, string action)
    {
        var relay = button.Slot.AttachComponent<InspectorButtonRelay>();
        relay.Argument.Value = action;
        relay.Handler.Target = this;
        button.SetAction(relay.OnPressed);
    }

    // ---- actions -----------------------------------------------------------------------------------

    private bool HandleWorkspaceAction(string action, User? actor)
    {
        var workspace = Slot.GetComponent<PanelWorkspace>();
        switch (action)
        {
            case "panel:pin": workspace?.TogglePin(actor ?? World.LocalUser); break;
            case "panel:lock": workspace?.TogglePositionLock(); break;
            case "panel:camera": if (workspace != null) workspace.HideFromCamera.Value = !workspace.HideFromCamera.Value; break;
            case "panel:color": workspace?.CycleColor(); break;
            case "panel:compass": ShowCompass.Value = !ShowCompass.Value; break;
            case "tab:new": OpenTab(Selected.Target ?? Root.Target ?? World.RootSlot, reuseExisting: false); return true;
            default: return HandleWorkspaceArgument(action, actor);
        }
        RunApplyChangesSafe();
        return true;
    }

    private bool HandleWorkspaceArgument(string action, User? actor)
    {
        var parts = action.Split(':');
        if (parts.Length == 3 && parts[0] == "tab" && ulong.TryParse(parts[2], out var tabId))
        {
            var tab = Tabs.FirstOrDefault(t => t?.ReferenceID.RawValue == tabId);
            if (tab != null)
            {
                if (parts[1] == "pick")
                    SwitchTab(tab);
                else
                    CloseTab(tab);
            }
            return true;
        }
        if (parts.Length == 3 && parts[0] == "scroll")
        {
            var scroll = (parts[1] == "tree" ? _treeScroll : _componentScroll).Target;
            if (scroll != null && !scroll.IsDestroyed)
                scroll.NormalizedPosition = new float2(0f, parts[2] == "bottom" ? 1f : 0f);
            return true;
        }
        if (parts.Length == 2 && parts[0] == "nav")
        {
            var target = parts[1] switch
            {
                "root" => World.RootSlot,
                "player" => (actor ?? World.LocalUser)?.Root?.Slot,
                "chest" => BoneShortcut(BodyNode.Chest),
                "head" => BoneShortcut(BodyNode.Head),
                "left" => BoneShortcut(BodyNode.LeftHand),
                "right" => BoneShortcut(BodyNode.RightHand),
                _ => null,
            };
            if (target != null && !target.IsDestroyed && !IsHiddenFromTree(target))
            {
                if (parts[1] is "root" or "player")
                    Root.Target = target;
                Selected.Target = target;
                RunApplyChangesSafe();
            }
            return true;
        }
        if (parts.Length == 2 && parts[0] == "branch" && ulong.TryParse(parts[1], out var id))
        {
            if (World.ReferenceController.GetObjectOrNull(new RefID(id)) is Slot branch)
                ExpandBranch(branch);
            return true;
        }
        return false;
    }

    // Unfolds everything under the slot. Capped, because every entry is a synced list element and a
    // twenty-thousand-bone import would otherwise flood the list for rows the row budget never builds.
    private void ExpandBranch(Slot branch)
    {
        var pending = new Stack<Slot>();
        pending.Push(branch);
        int added = 0;
        while (pending.Count > 0 && added < BranchExpandBudget)
        {
            var node = pending.Pop();
            if (IsHiddenFromTree(node) || !HasVisibleChildren(node))
                continue;
            if (!IsExpanded(ExpandedSlots, node.ReferenceID.RawValue))
            {
                ExpandedSlots.Add(node.ReferenceID.RawValue);
                added++;
            }
            foreach (var child in node.Children)
                pending.Push(child);
        }
        RunApplyChangesSafe();
    }

    // ---- refresh -----------------------------------------------------------------------------------

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if ((_workspaceRefresh += delta) >= WorkspaceRefreshInterval)
        {
            _workspaceRefresh = 0f;
            AdoptLocalParts();
            RefreshWorkspace();
            RefreshScrollControls();
            RelayoutHierarchyWidth();
        }
        UpdateCompass();
    }

    // Polled, so every write below is equality-gated: a sync write fires change events whether or not
    // the value moved, and one ungated width write here relaid the whole panel several times a second.
    private void RefreshWorkspace()
    {
        if (World?.IsAuthority != true || IsDestroyed)
            return;
        using var keep = Upkeep();
        EnsureTab();
        RefreshTabs();
        ApplyHierarchyWidth();

        var selected = Selected.Target;
        // A reorder from another peer or another panel lands on the object, not on this panel, so
        // nothing else would tell the list it is out of date.
        if (selected is { IsDestroyed: false } && !_componentsDirty && OrderSignature(selected) != _builtOrder)
        {
            _componentsDirty = true;
            MarkChangeDirty();
        }
        var objectRoot = SelectionObjectRoot();
        SetText(_objectTitle.Target, objectRoot != null && !ReferenceEquals(objectRoot, selected)
            ? "in " + objectRoot.SlotName.Value : "");

        var workspace = Slot.GetComponent<PanelWorkspace>();
        foreach (var button in _tools)
        {
            if (button == null || button.IsDestroyed)
                continue;
            bool? on = button.Slot.GetComponent<InspectorButtonRelay>()?.Argument.Value switch
            {
                "panel:pin" => workspace?.PinnedUser.Target != null,
                "panel:camera" => workspace?.HideFromCamera.Value == true,
                "panel:compass" => ShowCompass.Value,
                "panel:lock" => workspace?.PositionLocked.Value == true,
                _ => null,
            };
            if (on == null)
                continue;
            SetChipColors(button, on.Value ? DashTheme.AccentSoft : color.Transparent);
            if (button.Slot.FindChild("Icon", recursive: false)?.GetComponent<Image>() is { } icon)
                SetColor(icon.Tint, on.Value ? DashTheme.AccentHover : InspectorUI.MutedColor);
        }
        if (_colorSwatch.Target is { IsDestroyed: false } swatch)
            SetColor(swatch.Tint, workspace?.FrameColor ?? SwatchIdle);
    }

    // Runs wherever the scrollbars were built; their writes replicate like any other.
    private void RefreshScrollControls()
    {
        foreach (var bar in _scrollbars)
            bar.Refresh();
        foreach (var quick in _quickScrolls)
        {
            if (quick.Viewport.IsDestroyed || quick.Content.IsDestroyed)
                continue;
            bool overflow = quick.Content.LocalComputeRect.height > quick.Viewport.LocalComputeRect.height + 1f;
            SetActive(quick.Top, overflow);
            SetActive(quick.Bottom, overflow);
        }
    }

    // A local line from the panel's top-left corner to the selection, so a panel says which of thirty
    // identical trees it is looking at. Every peer draws its own.
    private void UpdateCompass()
    {
        var target = Selected.Target;
        bool visible = ShowCompass.Value && target is { IsDestroyed: false } && !target.IsRootSlot;
        if (!visible)
        {
            if (_compassLine is { IsDestroyed: false } && _compassLine.ActiveSelf.Value)
                _compassLine.ActiveSelf.Value = false;
            return;
        }
        if (_compassLine == null || _compassLine.IsDestroyed)
        {
            _compassLine = Slot.AddLocalSlot("Selection compass");
            _compassLine.Persistent.Value = false;
            var mesh = _compassLine.AttachComponent<CylinderMesh>();
            mesh.Radius.Value = 0.5f;
            mesh.Height.Value = 1f;
            mesh.Segments.Value = 8;
            var material = _compassLine.AttachComponent<OverlayUnlitMaterial>();
            material.BlendMode.Value = BlendMode.Alpha;
            material.FrontTintColor.Value = new colorHDR(0.3f, 0.8f, 1f, 1f);
            material.BehindTintColor.Value = new colorHDR(0.3f, 0.8f, 1f, 0.4f);
            var renderer = _compassLine.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = mesh;
            renderer.Material.Target = material;
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            _compassStart = _compassEnd = float3.Zero;
        }
        if (!_compassLine.ActiveSelf.Value)
            _compassLine.ActiveSelf.Value = true;

        var size = Slot.GetComponent<PanelShell>()?.Size.Value ?? float2.Zero;
        var start = Slot.LocalPointToGlobal(new float3(-size.x * 0.5f, size.y * 0.5f, 0f));
        var end = target!.GlobalPosition;
        if (start == _compassStart && end == _compassEnd)
            return;
        _compassStart = start;
        _compassEnd = end;
        var delta = end - start;
        _compassLine.GlobalPosition = (start + end) * 0.5f;
        _compassLine.GlobalRotation = HandPoseHelper.FromToRotation(float3.Up, delta);
        float width = 0.002f * (World.LocalUser?.Root?.GlobalScale ?? 1f);
        _compassLine.GlobalScale = new float3(width, MathF.Max(0.001f, delta.Length), width);
    }

    // ---- component order and notes -----------------------------------------------------------------

    private IEnumerable<Component> OrderedComponents(Slot selected)
    {
        var order = selected.GetComponent<InspectorComponentOrder>();
        var all = selected.GetAllComponents().Where(c => c != null && !c.IsDestroyed).ToArray();
        if (order == null)
            return all;
        return all.OrderBy(c =>
        {
            int index = order.Items.IndexOf(c);
            return index < 0 ? int.MaxValue : index;
        });
    }

    private static int OrderSignature(Slot slot)
    {
        var order = slot.GetComponent<InspectorComponentOrder>();
        if (order == null)
            return 0;
        var hash = new HashCode();
        foreach (var item in order.Items)
            hash.Add(item?.ReferenceID.RawValue ?? 0UL);
        return hash.ToHashCode();
    }

    // One handle does both jobs: press and drag reorders, the grab action pulls the component's card.
    private void BuildComponentHandle(UIBuilder ui, Component component, ReferenceProxySource source)
    {
        ui.PushStyle().MinWidth(22f).PreferredWidth(22f).FlexibleWidth(0f);
        var handle = ui.Next("Handle");
        ui.PopStyle();
        var backing = handle.AttachComponent<Image>();
        backing.Tint.Value = color.Transparent;
        backing.CornerRadius.Value = DashTheme.RadiusChip;
        var drag = handle.AttachComponent<InspectorComponentDrag>();
        drag.Panel.Target = this;
        drag.Target.Target = component;
        var driver = drag.AddColorDriver(backing.Tint, color.Transparent);
        driver.HighlightColor.Value = ChipHover;
        driver.PressedColor.Value = ChipPressed;
        source.GrabArea.Target = handle;
        var glyphUi = new UIBuilder(handle);
        InspectorUI.ApplyTheme(glyphUi, Slot);
        var glyph = glyphUi.Text("≡", InspectorUI.FontSize + 2f, InspectorUI.MutedColor);
        InspectorUI.FillParent(glyph.RectTransform!);
        glyph.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        glyph.VerticalAlignment.Value = TextVerticalAlignment.Middle;
    }

    private void BuildComponentNote(Slot container, Component target)
    {
        var row = InspectorUI.FixedRow(container, "Note", 28f, out var ui, Slot);
        var editor = row.AttachComponent<InspectorNoteEditor>();
        editor.Target.Target = target;
        ui.FlexibleWidth(1f).FontSize(ChromeFontSize);
        editor.Input.Target = ui.TextInput(InspectorComponentNote.Find(target)?.Note.Value ?? "", "Note", editor.OnTyped);
    }

    // Header rows are found from the list itself rather than a map filled at build time, so a drag works
    // on any peer, not just the one that built the rows.
    private IEnumerable<(Component Component, Slot Header)> ComponentHeaders()
    {
        var container = _componentsContent.Target;
        if (container == null || container.IsDestroyed)
            yield break;
        foreach (var row in container.Children)
        {
            if (row.GetComponent<ReferenceProxySource>()?.Target.Target is Component component && !component.IsDestroyed
                && row.GetComponentInChildren<InspectorComponentDrag>() != null)
                yield return (component, row);
        }
    }

    public void PreviewComponentDrop(Component? dragged, UIInteractionContext context)
    {
        Component? hovered = null;
        foreach (var (component, header) in ComponentHeaders())
        {
            if (ReferenceEquals(component, dragged))
                continue;
            var rect = header.GetComponent<RectTransform>();
            if (rect != null && rect.LocalComputeRect.Contains(context.PointIn(header)))
            {
                hovered = component;
                break;
            }
        }
        if (ReferenceEquals(hovered, _dropTarget))
            return;
        TintHeader(_dropTarget, InspectorUI.HeaderColor);
        _dropTarget = hovered;
        TintHeader(_dropTarget, InspectorUI.AccentColor);
    }

    public void ClearComponentDropPreview()
    {
        TintHeader(_dropTarget, InspectorUI.HeaderColor);
        _dropTarget = null;
    }

    private void TintHeader(Component? component, in color tint)
    {
        if (component == null)
            return;
        foreach (var (candidate, header) in ComponentHeaders())
        {
            if (!ReferenceEquals(candidate, component))
                continue;
            if (header.GetComponent<Image>() is { } image)
                SetColor(image.Tint, tint);
            return;
        }
    }

    public void DropComponent(Component? dragged, UIInteractionContext context)
    {
        PreviewComponentDrop(dragged, context);
        var target = _dropTarget;
        if (dragged != null && target != null)
            MoveComponentTo(dragged, target);
    }

    // Takes the place of the one it was dropped on: dragging down lands after it, dragging up before.
    public void MoveComponentTo(Component dragged, Component target)
    {
        if (ReferenceEquals(dragged, target) || dragged.IsDestroyed || target.IsDestroyed || dragged.Slot != target.Slot)
            return;
        var items = OrderedComponents(dragged.Slot)
            .Where(c => c.GetType().GetCustomAttribute<HideInInspectorAttribute>() == null).ToList();
        int from = items.IndexOf(dragged);
        int to = items.IndexOf(target);
        if (from < 0 || to < 0)
            return;
        items.RemoveAt(from);
        items.Insert(to, dragged);
        var order = dragged.Slot.GetOrAttachComponent<InspectorComponentOrder>();
        order.Items.Clear();
        foreach (var item in items)
            order.Items.Add(item);
        _componentsDirty = true;
        RunApplyChangesSafe();
    }

    // ---- small helpers -----------------------------------------------------------------------------

    // Never waits on a favourite's first fetch: a torn-off tab is in the hand the frame it comes out, so a
    // favourite not fetched yet gives way to the built-in this once.
    private static SceneInspectorPanel? SpawnBeside(SceneInspectorPanel origin, Slot rootTarget, float3 offset)
    {
        if (origin == null || RefuseLocked(origin.World) || RefuseProtected(rootTarget, "open beside"))
            return null;
        // World root even when the origin is pinned under a user: the new panel is not pinned.
        var panelSlot = origin.World.RootSlot.AddSlot("Inspector");
        panelSlot.Persistent.Value = false;
        panelSlot.Tag.Value = "Developer";
        panelSlot.GlobalPosition = origin.Slot.GlobalPosition + origin.Slot.GlobalRotation * offset;
        panelSlot.GlobalRotation = origin.Slot.GlobalRotation;
        panelSlot.GlobalScale = origin.Slot.GlobalScale;
        return Fill(panelSlot, rootTarget, null, wait: false);
    }

    // Explicit hover and press shades: a driver derives them from the base colour's own alpha, so a
    // transparent button would never visibly hover.
    private static void SetChipColors(Button button, in color normal)
    {
        var driver = button.Slot.GetComponent<ColorDriver>();
        if (driver == null || driver.IsDestroyed)
            return;
        bool clear = normal.a < 0.01f;
        var hover = clear ? ChipHover : WithAlpha(normal, MathF.Min(1f, normal.a + 0.10f));
        var pressed = clear ? ChipPressed : WithAlpha(normal, MathF.Min(1f, normal.a + 0.20f));
        bool changed = SetColor(driver.NormalColor, normal);
        changed |= SetColor(driver.HighlightColor, hover);
        changed |= SetColor(driver.PressedColor, pressed);
        changed |= SetColor(driver.DisabledColor, normal);
        if (changed)
            driver.Apply(button);
    }

    private static color WithAlpha(in color value, float alpha) => new(value.r, value.g, value.b, alpha);

    private static void Center(RectTransform rect, float size)
    {
        rect.AnchorMin.Value = rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(-size * 0.5f, -size * 0.5f);
        rect.OffsetMax.Value = new float2(size * 0.5f, size * 0.5f);
    }

    private static bool SetColor(Sync<color> field, in color value)
    {
        if (EqualityComparer<color>.Default.Equals(field.Value, value))
            return false;
        field.Value = value;
        return true;
    }

    private static void SetText(Text? text, string value)
    {
        if (text != null && !text.IsDestroyed && text.Content.Value != value)
            text.Content.Value = value;
    }

    private static void SetActive(Slot? slot, bool active)
    {
        if (slot != null && !slot.IsDestroyed && slot.ActiveSelf.Value != active)
            slot.ActiveSelf.Value = active;
    }

    private static void SetValue<T>(Sync<T> field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field.Value, value))
            field.Value = value;
    }

    private static void SetRef<T>(SyncRef<T> field, T? value) where T : class, IWorldElement
    {
        if (!ReferenceEquals(field.Target, value))
            field.Target = value!;
    }
}
