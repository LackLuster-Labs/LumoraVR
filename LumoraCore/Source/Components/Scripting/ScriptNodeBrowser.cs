// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Core.Components.Scripting;

// The nodes a graph can use: folders by category, and a search across all of them. It is a
// shared list, in the same shell as the inspector. Each peer builds the controls from the shared browse
// state, while a pick readies that person's own tool. Script and shader nodes switch at the top.
//
// A pick readies that node on the user's equipped node tool, to be placed with a double press, and
// the panel stays open for the next one. The readied node's row stays lit. -xlinka
[ComponentCategory("Hidden")]
public sealed class ScriptNodeBrowser : Component
{
    private const float Scale = InspectorUI.PanelScale;
    private const float RowHeight = InspectorUI.RowHeight;
    private const int MaxSearchResults = 120;
    private static readonly float2 PanelSize = new(560f, 880f);

    private static readonly Dictionary<GraphKind, Folder> _libraries = new();

    public readonly Sync<GraphKind> BrowseKind = new();
    public readonly Sync<string> BrowseQuery = new(string.Empty);
    public readonly Sync<string> BrowsePath = new(string.Empty);

    private ScriptNodeTool? _tool;
    private GraphKind _kind;
    private string _query = string.Empty;
    private string _path = string.Empty;
    private string? _shownReady;
    private PanelShell? _shell;
    private Slot? _view;
    private Slot? _list;
    private Slot? _breadcrumbs;
    private Text? _hint;
    private TextInput? _search;
    private bool _dirty = true;

    public int RowCount { get; private set; }

    public string FolderPath => _path;

    public GraphKind Kind => _kind;

    // In front of the user a little below eye level, facing them.
    public static ScriptNodeBrowser Open(World world, ScriptNodeTool tool, GraphKind kind = GraphKind.Script)
    {
        var slot = world.AddSpawnSlot(world.RootSlot, "Node Browser");
        slot.Persistent.Value = false;
        var head = world.LocalUser?.Root?.HeadSlot;
        if (head != null && !head.IsDestroyed)
        {
            var forward = head.GlobalRotation * float3.Backward;
            forward.y = 0f;
            forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
            slot.GlobalPosition = head.GlobalPosition + forward * 0.8f + float3.Down * 0.1f;
            var toViewer = head.GlobalPosition - slot.GlobalPosition;
            slot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toViewer.x, toViewer.z));
        }
        slot.LocalScale.Value = float3.One * Scale;
        var browser = slot.AttachComponent<ScriptNodeBrowser>();
        browser._tool = tool;
        browser.ShowKind(kind);
        return browser;
    }

    // Switches between the script nodes and the shader nodes, back at the top of an unfiltered list.
    public void ShowKind(GraphKind kind)
    {
        BrowseKind.Value = kind;
        BrowsePath.Value = string.Empty;
        BrowseQuery.Value = string.Empty;
        bool changed = kind != _kind;
        _kind = kind;
        _path = string.Empty;
        _dirty = true;
        if (changed && _view is { IsDestroyed: false })
        {
            _query = string.Empty;
            BuildView();
        }
    }

    // Where the shared list is: anyone at the panel browses it, whoever has it in hand.
    public override void OnAwake()
    {
        base.OnAwake();
        BrowseKind.MarkUiState();
        BrowseQuery.MarkUiState();
        BrowsePath.MarkUiState();
    }

    public override void OnAttach()
    {
        base.OnAttach();
        var theme = Slot.GetOrAttachComponent<UITheme>();
        _shell = Slot.GetOrAttachComponent<PanelShell>();
        _shell.Title.Value = "Nodes";
        _shell.Size.Value = PanelSize;
        theme.ApplyTo(_shell);
    }

    public override void OnStart()
    {
        base.OnStart();
        EnsureView();
    }

    private void EnsureView()
    {
        if (_view is { IsDestroyed: false }) return;
        _shell = Slot.GetComponent<PanelShell>();
        if (_shell?.ContentSlot is not { } content) return;
        _view = content.AddLocalSlot("Node Browser View");
        InspectorUI.FillParent(_view.AttachComponent<RectTransform>());
        _kind = BrowseKind.Value;
        _query = BrowseQuery.Value;
        _path = BrowsePath.Value;
        BuildView();
    }

    private void BuildView()
    {
        if (_view == null) return;
        _view.DestroyChildren();
        var page = _view.AddSlot("Page");
        InspectorUI.FillParent(page.AttachComponent<RectTransform>());
        BuildLayout(new UIBuilder(page));
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        EnsureView();
        _tool = ScriptNodeTool.ForLocalUser(World);
        if (_kind != BrowseKind.Value)
        {
            _kind = BrowseKind.Value;
            BuildView();
        }
        if (_query != BrowseQuery.Value || _path != BrowsePath.Value)
            _dirty = true;
        _query = BrowseQuery.Value;
        _path = BrowsePath.Value;
        if (_search is { IsDestroyed: false } && _search.Text.Value != _query)
            _search.Text.Value = _query;
        if (_tool?.ReadyType != _shownReady)
            _dirty = true;
        if (_dirty)
            Rebuild();
    }

    // Readies the node on the tool that opened the panel.
    public void Pick(NodeDefinition definition)
    {
        _tool = ScriptNodeTool.ForLocalUser(World);
        if (_tool is { IsDestroyed: false } tool)
            tool.Ready(definition.Name, ScriptGraphEditing.DefaultSignature(definition, GraphLanguage.For(_kind)), _kind);
        _dirty = true;
    }

    public void Search(string query)
    {
        _query = query ?? string.Empty;
        BrowseQuery.Value = _query;
        _dirty = true;
    }

    // Opens a folder by its path, "" being the top. A path that names no folder goes to the top.
    public void ShowFolder(string path)
    {
        _path = Library.Find(path) != null ? path : string.Empty;
        BrowsePath.Value = _path;
        Search(string.Empty);
        _dirty = true;
    }

    [SyncMethod]
    public void OnSearchChanged(TextInput input, string text) => Search(text);

    private void BuildLayout(UIBuilder ui)
    {
        var page = ui.Current;
        InspectorUI.ApplyTheme(ui, Slot);

        var column = page.AttachComponent<VerticalLayout>();
        column.Spacing.Value = 6f;
        column.PaddingLeft.Value = 6f;
        column.PaddingRight.Value = 6f;
        column.PaddingTop.Value = 6f;
        column.PaddingBottom.Value = 6f;
        column.ForceExpandWidth.Value = true;
        column.ForceExpandHeight.Value = false;

        InspectorUI.FixedRow(page, "Lists", 38f, out var tabsUi, Slot);
        Tab(tabsUi, "Script nodes", GraphKind.Script);
        Tab(tabsUi, "Shader nodes", GraphKind.Shader);

        InspectorUI.FixedRow(page, "Search", 38f, out var searchUi, Slot);
        searchUi.PushStyle();
        searchUi.FlexibleWidth(1f);
        var input = InspectorUI.CreateTextInput(searchUi, "Search");
        _search = input;
        input.Text.Value = BrowseQuery.Value;
        input.Placeholder.Value = "Search nodes...";
        input.SetChangeAction(OnSearchChanged);
        input.SetSubmitAction(OnSearchChanged);
        searchUi.PopStyle();

        _breadcrumbs = InspectorUI.FixedRow(page, "Categories", 34f, out _, Slot);

        var host = page.AddSlot("List");
        host.AttachComponent<RectTransform>();
        var hostLayout = host.AttachComponent<LayoutElement>();
        hostLayout.FlexibleHeight.Value = 1f;
        hostLayout.MinHeight.Value = 200f;
        var scrollUi = new UIBuilder(host);
        InspectorUI.ApplyTheme(scrollUi, Slot);
        var scroll = scrollUi.ScrollRect(out var content, null, InspectorUI.PaneColor);
        scroll.RowsActOnRelease.Value = true;
        InspectorUI.FillParent(scroll.Slot.GetComponent<RectTransform>()!);
        var rows = content.Slot.AttachComponent<VerticalLayout>();
        rows.Spacing.Value = 2f;
        rows.ForceExpandWidth.Value = true;
        rows.ForceExpandHeight.Value = false;
        _list = content.Slot;

        InspectorUI.FixedRow(page, "Hint", 30f, out var hintUi, Slot);
        hintUi.PushStyle();
        hintUi.FlexibleWidth(1f);
        _hint = hintUi.Text(string.Empty, InspectorUI.FontSize - 2f, InspectorUI.MutedColor);
        InspectorUI.FillParent(_hint.RectTransform!);
        _hint.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        _hint.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        _hint.WordWrap.Value = false;
        _hint.ClipToRect.Value = true;
        _hint.Ellipsis.Value = true;
        hintUi.PopStyle();
        _dirty = true;
    }

    // The list shown wears the accent, the other stays quiet, so the one picked reads as picked.
    private void Tab(UIBuilder ui, string label, GraphKind kind)
    {
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        ui.TextColor(kind == _kind ? InspectorUI.TextColor : InspectorUI.MutedColor);
        if (kind == _kind)
            ui.BackgroundColor(InspectorUI.AccentColor);
        ui.Button(label, (_, _) => ShowKind(kind));
        ui.PopStyle();
    }

    private void Rebuild()
    {
        var list = _list;
        if (list == null || list.IsDestroyed)
            return;
        _dirty = false;
        _shownReady = _tool?.ReadyType;
        list.DestroyChildren();
        RowCount = 0;
        BuildBreadcrumbs();

        string query = _query.Trim();
        if (query.Length > 0)
            BuildSearch(list, query);
        else
            BuildFolder(list, Library.Find(_path) ?? Library);

        // The title is the shared shell's, kept by the authority like every panel's rows; the list itself is
        // this machine's own.
        if (_shell != null && !_shell.IsDestroyed && World?.IsAuthority == true)
        {
            string noun = _kind == GraphKind.Shader ? "Shader nodes" : "Nodes";
            string title = query.Length > 0 ? noun + ": search all categories" : _path.Length == 0 ? noun : noun + " / " + _path.Replace("/", " / ");
            if (_shell.Title.Value != title)
            {
                using var keep = World.DataModelPermissions?.EnterUiUpkeep(this);
                _shell.Title.Value = title;
            }
        }
        if (_hint != null && !_hint.IsDestroyed)
        {
            bool readyHere = _shownReady != null && _tool?.ReadyKind == _kind;
            _hint.Content.Value = _tool == null ? "Equip a node tool to place a node from this browser."
                : readyHere && GraphLanguage.For(_kind).Registry.TryGetDefinition(_shownReady!, out var ready)
                ? $"Placing {TitleOf(ready)}: double press empty space."
                : _kind == GraphKind.Shader
                    ? "Pick a node, place it, then wire it into a material's graph."
                    : "Pick a node, then double press empty space to place it.";
        }
    }

    // Keep every parent within reach, including while a search is showing. -xlinka
    private void BuildBreadcrumbs()
    {
        if (_breadcrumbs is not { IsDestroyed: false } row)
            return;
        row.DestroyChildren();
        var ui = new UIBuilder(row);
        InspectorUI.ApplyTheme(ui, Slot);
        Breadcrumb(ui, "All categories", string.Empty);
        string path = string.Empty;
        foreach (var part in _path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? part : path + "/" + part;
            Breadcrumb(ui, "> " + part, path);
        }
    }

    private void Breadcrumb(UIBuilder ui, string label, string path)
    {
        ui.PushStyle();
        ui.MinWidth(28f);
        ui.FlexibleWidth(label.Length);
        ui.TextColor(path == _path ? InspectorUI.TextColor : InspectorUI.AccentColor);
        var button = ui.Button(label, (_, _) => ShowFolder(path));
        if (button.Slot.GetComponentInChildren<Text>() is { } text)
        {
            text.WordWrap.Value = false;
            text.ClipToRect.Value = true;
            text.Ellipsis.Value = true;
        }
        ui.PopStyle();
    }

    private void BuildFolder(Slot list, Folder folder)
    {
        foreach (var sub in folder.Folders)
        {
            var path = sub.Path;
            AddRow(list, sub.Name + " >", InspectorUI.AccentColor, false, () => ShowFolder(path),
                $"{sub.NodeCount} nodes");
        }
        foreach (var definition in folder.Nodes)
        {
            var chosen = definition;
            AddRow(list, TitleOf(definition), InspectorUI.TextColor, IsReady(definition), () => Pick(chosen), help: definition);
        }
    }

    // Every node the search finds across all folders, best first (NodeSearch), with its folder on the right.
    private void BuildSearch(Slot list, string query)
    {
        int total = 0;
        foreach (var definition in NodeSearch.Find(Library.All(), query))
        {
            total++;
            if (RowCount >= MaxSearchResults)
                continue;
            var chosen = definition;
            AddRow(list, TitleOf(definition), InspectorUI.TextColor, IsReady(definition), () => Pick(chosen), ParentOf(definition.Path), definition);
        }
        if (total == 0)
            AddNote(list, "No matching nodes");
        else if (total > RowCount)
            AddNote(list, $"{total - RowCount} more, refine the search");
    }

    private bool IsReady(NodeDefinition definition) => _shownReady == definition.Name && _tool?.ReadyKind == _kind;

    private void AddRow(Slot list, string label, color tint, bool lit, Action pressed, string? folder = null, NodeDefinition? help = null)
    {
        InspectorUI.FixedRow(list, label, RowHeight, out var ui, Slot);
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        ui.TextColor(tint);
        if (lit)
            ui.BackgroundColor(InspectorUI.SelectionColor);
        var button = ui.Button(label, (_, _) => pressed());
        button.Tip.Value = ScriptNodeHelp.For(help);
        var text = button.Slot.GetComponentInChildren<Text>();
        if (text?.RectTransform is { } rect)
        {
            InspectorUI.FillParent(rect);
            rect.OffsetMin.Value = new float2(10f, 0f);
            rect.OffsetMax.Value = new float2(-10f, 0f);
            text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
            text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            text.WordWrap.Value = false;
            text.ClipToRect.Value = true;
            text.Ellipsis.Value = true;
        }
        ui.PopStyle();

        if (folder != null)
        {
            ui.PushStyle();
            ui.MinWidth(180f);
            ui.PreferredWidth(200f);
            ui.FlexibleWidth(0f);
            var path = ui.Text(folder.Replace("/", " / "), InspectorUI.FontSize - 2f, InspectorUI.MutedColor);
            InspectorUI.FillParent(path.RectTransform!);
            path.HorizontalAlignment.Value = TextHorizontalAlignment.Right;
            path.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            path.WordWrap.Value = false;
            path.ClipToRect.Value = true;
            path.Ellipsis.Value = true;
            ui.PopStyle();
        }
        RowCount++;
    }

    private void AddNote(Slot list, string label)
    {
        InspectorUI.FixedRow(list, "Note", RowHeight, out var ui, Slot);
        ui.PushStyle();
        ui.FlexibleWidth(1f);
        var text = ui.Text(label, InspectorUI.FontSize, InspectorUI.MutedColor);
        InspectorUI.FillParent(text.RectTransform!);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        ui.PopStyle();
    }

    private static string TitleOf(NodeDefinition definition)
    {
        int slash = definition.Path.LastIndexOf('/');
        return slash >= 0 ? definition.Path[(slash + 1)..] : definition.Path;
    }

    private static string ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : string.Empty;
    }

    // The folder tree for the list shown, from the node paths, built once per list: a registry does not
    // change after startup.
    private Folder Library
    {
        get
        {
            lock (_libraries)
            {
                if (!_libraries.TryGetValue(_kind, out var library))
                    _libraries[_kind] = library = Folder.Build(GraphLanguage.For(_kind).Registry.Definitions);
                return library;
            }
        }
    }

    private sealed class Folder
    {
        public string Name = string.Empty;
        public string Path = string.Empty;
        public int NodeCount;
        public readonly List<Folder> Folders = new();
        public readonly List<NodeDefinition> Nodes = new();

        public static Folder Build(IEnumerable<NodeDefinition> definitions)
        {
            var root = new Folder();
            foreach (var definition in definitions)
            {
                var folder = root;
                var parts = definition.Path.Split('/');
                for (int i = 0; i < parts.Length - 1; i++)
                    folder = folder.Child(parts[i]);
                folder.Nodes.Add(definition);
            }
            root.Sort();
            return root;
        }

        public Folder? Find(string path)
        {
            if (string.IsNullOrEmpty(path))
                return this;
            var folder = this;
            foreach (var part in path.Split('/'))
            {
                folder = folder.Folders.Find(f => f.Name == part);
                if (folder == null)
                    return null;
            }
            return folder;
        }

        public IEnumerable<NodeDefinition> All()
        {
            foreach (var definition in Nodes)
                yield return definition;
            foreach (var folder in Folders)
            {
                foreach (var definition in folder.All())
                    yield return definition;
            }
        }

        private Folder Child(string name)
        {
            var existing = Folders.Find(f => f.Name == name);
            if (existing != null)
                return existing;
            var made = new Folder { Name = name, Path = Path.Length == 0 ? name : Path + "/" + name };
            Folders.Add(made);
            return made;
        }

        private void Sort()
        {
            Folders.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            Nodes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            NodeCount = Nodes.Count;
            foreach (var folder in Folders)
            {
                folder.Sort();
                NodeCount += folder.NodeCount;
            }
        }
    }
}
