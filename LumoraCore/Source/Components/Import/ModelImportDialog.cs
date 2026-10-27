// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Helio.UI;
using Helio.UI.Layout;
using Lumora.Core.Assets;
using Lumora.Core.Components.UI;
using Lumora.Core.Localization;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Import;

[ComponentCategory("Assets/Import")]
public sealed class ModelImportDialog : ImportDialog
{
    public readonly Sync<float> Scale;
    public readonly Sync<bool> AutoScale;
    public readonly Sync<ModelMaterialType> Material;
    public readonly Sync<ModelColliderMode> ColliderMode;
    public readonly Sync<bool> SolidToPeople;
    public readonly Sync<bool> AllowGrabbable;
    public readonly Sync<bool> Scalable;
    public readonly Sync<bool> ImportAtOrigin;
    public readonly Sync<bool> CalculateNormals;
    public readonly Sync<bool> CalculateTangents;
    public readonly Sync<bool> ImportVertexColors;
    public readonly Sync<bool> ImportAlbedoColor;
    public readonly Sync<bool> ImportEmissive;
    public readonly Sync<bool> MakeDualSided;
    public readonly Sync<bool> MakeFlatShaded;
    public readonly Sync<int> MaxTextureSize;
    public readonly Sync<bool> ForceNoMipMaps;
    public readonly Sync<bool> ImportAnimations;
    public readonly Sync<bool> ImportLights;
    public readonly Sync<bool> ImportCameras;
    public readonly Sync<bool> AssetsOnly;
    public readonly Sync<bool> MatchTexturesByName;
    public readonly Sync<bool> AvatarSetup;
    public readonly Sync<ModelAssetPlacement> AssetPlacement;
    public readonly Sync<bool> ShareAssets;

    protected override string TitleText => "Model Import";
    protected override float2 CanvasSize => new float2(460f, 640f);

    public ModelImportDialog()
    {
        Scale = new Sync<float>(this, 1f);
        AutoScale = new Sync<bool>(this, false);
        Material = new Sync<ModelMaterialType>(this, ModelMaterialType.Lit);
        ColliderMode = new Sync<ModelColliderMode>(this, ModelColliderMode.Mesh);
        SolidToPeople = new Sync<bool>(this, false);
        AllowGrabbable = new Sync<bool>(this, true);
        Scalable = new Sync<bool>(this, true);
        ImportAtOrigin = new Sync<bool>(this, false);
        CalculateNormals = new Sync<bool>(this, true);
        CalculateTangents = new Sync<bool>(this, true);
        ImportVertexColors = new Sync<bool>(this, false);
        ImportAlbedoColor = new Sync<bool>(this, true);
        ImportEmissive = new Sync<bool>(this, true);
        MakeDualSided = new Sync<bool>(this, false);
        MakeFlatShaded = new Sync<bool>(this, false);
        MaxTextureSize = new Sync<int>(this, -1);
        ForceNoMipMaps = new Sync<bool>(this, false);
        ImportAnimations = new Sync<bool>(this, true);
        ImportLights = new Sync<bool>(this, false);
        ImportCameras = new Sync<bool>(this, false);
        AssetsOnly = new Sync<bool>(this, false);
        MatchTexturesByName = new Sync<bool>(this, false);
        AvatarSetup = new Sync<bool>(this, false);
        AssetPlacement = new Sync<ModelAssetPlacement>(this, ModelAssetPlacement.WithParts);
        ShareAssets = new Sync<bool>(this, false);
    }

    // A preset starts from the importer's own remembered answers, which are the plain import until they
    // change something; the units come after it.
    private void DefaultPreset()
    {
        LoadAnswers();
        Scale.Value = 1f;
        AutoScale.Value = false;
    }

    private bool _answersLoaded;

    protected override void OpenRoot(UIBuilder ui)
    {
        if (!_answersLoaded)
        {
            _answersLoaded = true;
            LoadAnswers();
        }
        var body = SetupSection(ui, "How should this model be imported?", backButton: false);
        AnswerNote(body);
        SetupGrid(body);
        GridButton(body, "Regular", PresetRegular);
        GridButton(body, "Vertex Color", PresetVertexColor);
        GridButton(body, "Advanced", () => OpenPage(MenuCustom));
        GridButton(body, "As Raw File", AsRawFile, BackColor);
    }

    public void PresetRegular()
    {
        DefaultPreset();
        OpenPage(MenuScale);
    }

    // The plain import showing the mesh's own colours (ModelImporter.WithVertexColors).
    public void PresetVertexColor()
    {
        DefaultPreset();
        _vertexColorsByPreset = !ImportVertexColors.Value;
        ImportVertexColors.Value = true;
        OpenPage(MenuScale);
    }

    private void MenuScale(UIBuilder ui)
    {
        var body = SetupSection(ui, "Units");
        SetupGrid(body);
        GridButton(body, "Auto", () => SetScaleAndOpenFinish(1f, true));
        GridButton(body, "Humanoid", () => SetScaleAndOpenFinish(1.8f, true));
        GridButton(body, "Meters", () => SetScaleAndOpenFinish(1f, false));
        GridButton(body, "Millimeters", () => SetScaleAndOpenFinish(0.001f, false));
        GridButton(body, "Centimeters", () => SetScaleAndOpenFinish(0.01f, false));
        GridButton(body, "Inches", () => SetScaleAndOpenFinish(0.0254f, false));
    }

    private void SetScaleAndOpenFinish(float scale, bool autoScale)
    {
        Scale.Value = scale;
        AutoScale.Value = autoScale;
        OpenPage(MenuFinish);
    }

    private void MenuFinish(UIBuilder ui)
    {
        var body = SetupSection(ui, "Finalize");
        AnswerNote(body);
        SetupGrid(body);
        GridButton(body, "Import Model", RunImport, primary: true);
        GridButton(body, "Advanced", () => OpenPage(MenuCustom));
    }

    // ANSWERS
    //
    // Every advanced answer once: its field, the default that is the plain import, its words, and whether it is
    // remembered for next time. The units page owns Auto Scale, so it is not remembered. Vertex colours are, when
    // ticked here, but not when the Vertex Color preset turned them on: a preset's own choice must not quietly
    // follow into the next plain import.
    //
    // Normals, tangents and flat shading stay on the component for old dialogs but are not offered here: the
    // importer always generates normals and tangents where a file lacks them and has no flat-shading path, so a
    // box for them would change nothing. -xlinka

    private abstract class Answer
    {
        public readonly string Name;
        public readonly LocaleText Label;
        public readonly LocaleText Hint;
        public readonly bool Remembered;

        protected Answer(string name, LocaleText label, LocaleText hint, bool remembered)
        {
            Name = name;
            Label = label;
            Hint = hint;
            Remembered = remembered;
        }

        public abstract bool Changed { get; }
        public abstract void Reset();
        public abstract void Load();
        public abstract void Save();
    }

    private sealed class Toggle : Answer
    {
        public readonly Sync<bool> Field;
        private readonly bool _default;

        public Toggle(Sync<bool> field, bool fallback, string name, LocaleText label, LocaleText hint, bool remembered = true)
            : base(name, label, hint, remembered)
        {
            Field = field;
            _default = fallback;
        }

        public override bool Changed => Field.Value != _default;
        public override void Reset() => Field.Value = _default;
        public override void Load() => Field.Value = Remembered ? ModelImportPreferences.Get(Name, _default) : _default;
        public override void Save()
        {
            if (Remembered)
                ModelImportPreferences.Set(Name, Field.Value, _default);
        }
    }

    private abstract class PickBase : Answer
    {
        protected PickBase(string name, LocaleText label, LocaleText hint) : base(name, label, hint, remembered: true) { }

        public abstract int Count { get; }
        public abstract LocaleText OptionName(int index);
        public abstract bool IsChosen(int index);
        public abstract void Choose(int index);
    }

    private sealed class Pick<T> : PickBase where T : struct, Enum
    {
        private readonly Sync<T> _field;
        private readonly T _default;
        private readonly (T Value, LocaleText Name)[] _options;

        public Pick(Sync<T> field, T fallback, string name, LocaleText label, LocaleText hint, (T, LocaleText)[] options)
            : base(name, label, hint)
        {
            _field = field;
            _default = fallback;
            _options = options;
        }

        public override bool Changed => !_field.Value.Equals(_default);
        public override void Reset() => _field.Value = _default;
        public override void Load() => _field.Value = ModelImportPreferences.Get(Name, _default);
        public override void Save() => ModelImportPreferences.Set(Name, _field.Value, _default);
        public override int Count => _options.Length;
        public override LocaleText OptionName(int index) => _options[index].Name;
        public override bool IsChosen(int index) => _field.Value.Equals(_options[index].Value);
        public override void Choose(int index) => _field.Value = _options[index].Value;
    }

    private sealed record Group(string Name, LocaleText Title, Answer[] Answers);

    private List<Group>? _groups;
    private List<Group> Groups => _groups ??= BuildGroups();

    private static LocaleText L(string key, string english) => ("Import.Advanced." + key).AsLocale(english);

    private static readonly (ModelMaterialType, LocaleText)[] MaterialChoices =
    {
        (ModelMaterialType.Lit, L("Kind.Lit", "PBS Metallic")),
        (ModelMaterialType.Specular, L("Kind.Specular", "PBS Specular")),
        (ModelMaterialType.DualSided, L("Kind.DualSided", "PBS Dual Sided")),
        (ModelMaterialType.Triplanar, L("Kind.Triplanar", "PBS Triplanar")),
        (ModelMaterialType.Unlit, L("Kind.Unlit", "Unlit")),
        (ModelMaterialType.Toon, L("Kind.Toon", "Toon")),
        (ModelMaterialType.ToonOutline, L("Kind.ToonOutline", "Toon + Outline")),
        (ModelMaterialType.Wireframe, L("Kind.Wireframe", "Wireframe")),
    };

    private List<Group> BuildGroups() => new()
    {
        new Group("Materials", L("Group.Materials", "Materials"), new Answer[]
        {
            new Pick<ModelMaterialType>(Material, ModelMaterialType.Lit, "Material",
                L("Material", "Material"), L("Material.Hint", "The kind of material every surface gets"), MaterialChoices),
            new Toggle(ImportVertexColors, false, "VertexColors",
                L("VertexColors", "Use Vertex Colors"), L("VertexColors.Hint", "Show the colors stored on the mesh's vertices")),
            new Toggle(MatchTexturesByName, false, "MatchTexturesByName",
                L("MatchTextures", "Find Textures By Name"), L("MatchTextures.Hint", "Unlinked maps from pictures named after the material")),
            new Toggle(ImportAlbedoColor, true, "AlbedoColor",
                L("AlbedoColor", "Import Albedo Color"), L("AlbedoColor.Hint", "Keep each material's base color")),
            new Toggle(ImportEmissive, true, "Emissive",
                L("Emissive", "Import Emissive"), L("Emissive.Hint", "Keep glow colors and glow maps")),
            new Toggle(MakeDualSided, false, "DualSided",
                L("DualSided", "Dual Sided"), L("DualSided.Hint", "Draw the back of every face too")),
            new Toggle(ForceNoMipMaps, false, "NoMipMaps",
                L("NoMipMaps", "No Texture Mipmaps"), L("NoMipMaps.Hint", "Sharper far away, but it shimmers")),
        }),
        new Group("Handling", L("Group.Handling", "Handling"), new Answer[]
        {
            new Pick<ModelColliderMode>(ColliderMode, ModelColliderMode.Mesh, "Colliders",
                L("Colliders", "Colliders"), L("Colliders.Hint", "Shapes: a fitted primitive or convex hull per part"),
                new (ModelColliderMode, LocaleText)[]
                {
                    (ModelColliderMode.None, L("Colliders.None", "None")),
                    (ModelColliderMode.Mesh, L("Colliders.Mesh", "Exact Mesh")),
                    (ModelColliderMode.Shapes, L("Colliders.Shapes", "Shapes")),
                }),
            // A collider is for lasers, grabs and falling objects; whether it is also a wall is a separate
            // decision. Importing a building wants this; importing a chair does not. -xlinka
            new Toggle(SolidToPeople, false, "SolidToPeople",
                L("SolidToPeople", "Walk Into It"), L("SolidToPeople.Hint", "Colliders stop people walking through")),
            new Toggle(AllowGrabbable, true, "Grabbable",
                L("Grabbable", "Grabbable"), L("Grabbable.Hint", "Pick it up as one object")),
            new Toggle(Scalable, true, "Scalable",
                L("Scalable", "Scalable"), L("Scalable.Hint", "Resize it while holding it")),
        }),
        new Group("Contents", L("Group.Contents", "Contents"), new Answer[]
        {
            new Toggle(ImportAnimations, true, "Animations",
                L("Animations", "Import Animations"), L("Animations.Hint", "Clips, and an animator on rigged models")),
            new Toggle(ImportLights, false, "Lights",
                L("Lights", "Import Lights"), L("Lights.Hint", "The file's lamps, spots and suns, shadows off")),
            new Toggle(ImportCameras, false, "Cameras",
                L("Cameras", "Import Cameras"), L("Cameras.Hint", "The file's cameras, as framings to film from")),
            new Toggle(AssetsOnly, false, "AssetsOnly",
                L("AssetsOnly", "Assets Only"), L("AssetsOnly.Hint", "Meshes and materials on empty slots, nothing drawn")),
        }),
        new Group("Placement", L("Group.Placement", "Placement"), new Answer[]
        {
            new Toggle(AutoScale, false, "AutoScale",
                L("AutoScale", "Auto Scale"), L("AutoScale.Hint", "Resize to a standard height"), remembered: false),
            new Toggle(ImportAtOrigin, false, "AtOrigin",
                L("AtOrigin", "Position At Origin"), L("AtOrigin.Hint", "Land at the world's center, not in front of you")),
            new Pick<ModelAssetPlacement>(AssetPlacement, ModelAssetPlacement.WithParts, "AssetPlacement",
                L("AssetPlacement", "Where Assets Go"), L("AssetPlacement.Hint", "Where meshes, materials and textures are kept"),
                new (ModelAssetPlacement, LocaleText)[]
                {
                    (ModelAssetPlacement.WithParts, L("AssetPlacement.WithParts", "With Each Part")),
                    (ModelAssetPlacement.UnderModel, L("AssetPlacement.UnderModel", "In The Model")),
                    (ModelAssetPlacement.World, L("AssetPlacement.World", "World Assets")),
                }),
            new Toggle(ShareAssets, false, "ShareAssets",
                L("ShareAssets", "Share Identical Assets"), L("ShareAssets.Hint", "The same mesh, texture or material becomes one")),
        }),
        new Group("Avatar", L("Group.Avatar", "Avatar"), new Answer[]
        {
            new Toggle(AvatarSetup, false, "AvatarSetup",
                L("AvatarSetup", "Set Up As Avatar"), L("AvatarSetup.Hint", "Rig it and open Avatar Studio, markers aligned")),
        }),
    };

    // Vertex colours turned on by the preset rather than ticked by hand: used, shown on the advanced page, but not
    // saved and not listed as "your settings". A tick on the page makes them an answer again.
    private bool _vertexColorsByPreset;

    private bool ByPreset(Answer answer) => _vertexColorsByPreset && answer is Toggle toggle && ReferenceEquals(toggle.Field, ImportVertexColors);

    private void LoadAnswers()
    {
        foreach (var group in Groups)
            foreach (var answer in group.Answers)
                answer.Load();
        _vertexColorsByPreset = false;
    }

    private void SaveAnswers()
    {
        foreach (var group in Groups)
        {
            foreach (var answer in group.Answers)
            {
                if (!ByPreset(answer))
                    answer.Save();
            }
        }
    }

    private void ResetAnswers()
    {
        foreach (var group in Groups)
            foreach (var answer in group.Answers)
                answer.Reset();
        _vertexColorsByPreset = false;
        _perMaterial.Clear();
        Wizard?.Refresh();
    }

    private int ChangedIn(Group group)
    {
        int changed = 0;
        foreach (var answer in group.Answers)
        {
            if (answer.Changed)
                changed++;
        }
        if (group.Name == "Materials" && _perMaterial.Count > 0)
            changed++;
        return changed;
    }

    // What this import will do differently from the plain one, said where the import is pressed, so a remembered
    // answer never acts unseen.
    private void AnswerNote(UIBuilder body)
    {
        var changed = new List<string>();
        foreach (var group in Groups)
        {
            foreach (var answer in group.Answers)
            {
                if (answer.Changed && answer.Remembered && !ByPreset(answer))
                    changed.Add(answer.Label.Resolve());
            }
        }
        if (_perMaterial.Count > 0)
            changed.Add(L("PerMaterial", "Per Material").Resolve());
        if (changed.Count == 0)
            return;

        body.PushStyle().MinHeight(40f).PreferredHeight(40f).FlexibleHeight(0f);
        var note = body.Text(L("Note", "Using your settings: {0}").WithArgs(string.Join(", ", changed)), DashTheme.FontSmall, DashTheme.TextDim);
        body.PopStyle();
        note.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        note.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        note.WordWrap.Value = true;
    }

    // THE ADVANCED PAGE: a folded group per topic. A folded group still says how many of its answers differ from
    // the plain import, so nothing changed hides behind a closed header.

    private readonly HashSet<string> _openGroups = new(StringComparer.Ordinal);

    private const float GroupHeaderHeight = 46f;
    private const float OptionRowHeight = 54f;
    private const float PickTileHeight = 44f;

    private void MenuCustom(UIBuilder ui)
    {
        var body = SetupSection(ui, L("Title", "Advanced Settings"));
        var list = ScrollContent(body);
        foreach (var group in Groups)
            BuildGroup(list, group);

        SetupGrid(body);
        GridButton(body, L("Reset", "Defaults"), ResetAnswers, BackColor);
        GridButton(body, L("Import", "Import Model"), RunImport, primary: true);
    }

    private void BuildGroup(UIBuilder list, Group group)
    {
        bool open = _openGroups.Contains(group.Name);

        var header = list.Root.AddSlot(group.Name);
        header.AttachComponent<RectTransform>();
        var headerElement = header.AttachComponent<LayoutElement>();
        headerElement.MinHeight.Value = GroupHeaderHeight;
        headerElement.PreferredHeight.Value = GroupHeaderHeight;
        var panel = header.AttachComponent<RoundedPanel>();
        panel.Color.Value = DashTheme.Surface;
        panel.OutlineColor.Value = DashTheme.OutlineStrong;
        panel.OutlineThickness.Value = DashTheme.OutlineWidth;
        panel.CornerRadius.Value = DashTheme.RadiusControl;
        var row = header.AttachComponent<HorizontalLayout>();
        row.Spacing.Value = 10f;
        row.PaddingLeft.Value = 10f;
        row.PaddingRight.Value = 14f;
        row.ForceExpandWidth.Value = false;
        row.ForceExpandHeight.Value = true;

        var hb = new UIBuilder(header).SetStyle(list.Style);

        // A slim bar in the accent once anything inside is changed.
        var markSlot = header.AddSlot("Mark");
        markSlot.AttachComponent<RectTransform>();
        var markElement = markSlot.AttachComponent<LayoutElement>();
        markElement.MinWidth.Value = 4f;
        markElement.PreferredWidth.Value = 4f;
        markElement.FlexibleWidth.Value = 0f;
        var mark = markSlot.AttachComponent<RoundedPanel>();
        mark.CornerRadius.Value = 2f;

        hb.PushStyle().FlexibleWidth(1f);
        var title = hb.Text(group.Title, DashTheme.FontHeading, DashTheme.Text);
        hb.PopStyle();
        title.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        title.VerticalAlignment.Value = TextVerticalAlignment.Middle;

        hb.PushStyle().MinWidth(110f).PreferredWidth(110f).FlexibleWidth(0f);
        var summary = hb.Text(string.Empty, DashTheme.FontSmall, DashTheme.TextDim);
        hb.PopStyle();
        summary.HorizontalAlignment.Value = TextHorizontalAlignment.Right;
        summary.VerticalAlignment.Value = TextVerticalAlignment.Middle;

        hb.PushStyle().MinWidth(16f).PreferredWidth(16f).FlexibleWidth(0f);
        var sign = hb.Text(open ? "-" : "+", DashTheme.FontHeading, DashTheme.TextDim);
        hb.PopStyle();
        sign.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        sign.VerticalAlignment.Value = TextVerticalAlignment.Middle;

        var content = list.Root.AddSlot(group.Name + " Options");
        content.AttachComponent<RectTransform>();
        var contentLayout = content.AttachComponent<VerticalLayout>();
        contentLayout.Spacing.Value = 4f;
        contentLayout.PaddingLeft.Value = 6f;
        contentLayout.PaddingRight.Value = 4f;
        contentLayout.PaddingBottom.Value = 6f;
        contentLayout.ForceExpandWidth.Value = true;
        contentLayout.ForceExpandHeight.Value = false;
        content.ActiveSelf.Value = open;

        void Refresh()
        {
            int changed = ChangedIn(group);
            LocaleTextRegistry.Bind(summary, changed > 0 ? L("Changed", "{0} changed").WithArgs(changed) : LocaleText.Plain(string.Empty));
            mark.Color.Value = changed > 0 ? DashTheme.Accent : DashTheme.Outline;
        }

        var cb = new UIBuilder(content).SetStyle(list.Style);
        foreach (var answer in group.Answers)
        {
            if (answer is Toggle toggle)
                ToggleRow(cb, toggle, Refresh);
            else if (answer is PickBase pick)
                PickRow(cb, pick, Refresh);
            if (answer.Name == "Material")
                PerMaterialRow(cb);
        }
        Refresh();

        var button = header.AttachComponent<Button>();
        button.Clicked += (_, _) =>
        {
            if (!CanInteract)
                return;
            bool nowOpen = !content.ActiveSelf.Value;
            content.ActiveSelf.Value = nowOpen;
            sign.Content.Value = nowOpen ? "-" : "+";
            if (nowOpen) _openGroups.Add(group.Name);
            else _openGroups.Remove(group.Name);
        };
        var driver = button.AddColorDriver(panel.Color, DashTheme.Surface, InteractionColorMode.Direct);
        driver.HighlightColor.Value = DashTheme.SurfaceHover;
        driver.PressedColor.Value = DashTheme.SurfacePressed;
    }

    // A box, the answer's name, and one line under it saying what it does.
    private void ToggleRow(UIBuilder ui, Toggle toggle, Action changed)
    {
        var row = ui.Root.AddSlot(toggle.Name);
        row.AttachComponent<RectTransform>();
        var rowElement = row.AttachComponent<LayoutElement>();
        rowElement.MinHeight.Value = OptionRowHeight;
        rowElement.PreferredHeight.Value = OptionRowHeight;
        var layout = row.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = 10f;
        layout.PaddingLeft.Value = 6f;
        layout.PaddingRight.Value = 4f;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;

        var rb = new UIBuilder(row).SetStyle(ui.Style);
        rb.PushStyle().MinWidth(28f).PreferredWidth(28f).FlexibleWidth(0f);
        rb.Checkbox(toggle.Field.Value, (_, v) =>
        {
            if (!CanInteract)
                return;
            toggle.Field.Value = v;
            if (ReferenceEquals(toggle.Field, ImportVertexColors))
                _vertexColorsByPreset = false;
            changed();
        });
        rb.PopStyle();

        Words(row, ui.Style, toggle.Label, toggle.Hint);
    }

    private void Words(Slot row, UIStyle style, LocaleText label, LocaleText hint)
    {
        var column = row.AddSlot("Words");
        column.AttachComponent<RectTransform>();
        var columnLayout = column.AttachComponent<VerticalLayout>();
        columnLayout.Spacing.Value = 0f;
        columnLayout.ForceExpandWidth.Value = true;
        columnLayout.ForceExpandHeight.Value = true;
        var columnElement = column.AttachComponent<LayoutElement>();
        columnElement.PriorityValue.Value = 0;
        columnElement.FlexibleWidth.Value = 1f;

        var wb = new UIBuilder(column).SetStyle(style);
        var title = wb.Text(label, DashTheme.FontBody, DashTheme.Text);
        title.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        title.VerticalAlignment.Value = TextVerticalAlignment.Bottom;
        var line = wb.Text(hint, DashTheme.FontSmall, DashTheme.TextDim);
        line.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        line.VerticalAlignment.Value = TextVerticalAlignment.Top;
    }

    // One choice of several: the words, then a row of tiles, the chosen one in the accent.
    private void PickRow(UIBuilder ui, PickBase pick, Action changed)
    {
        var words = ui.Root.AddSlot(pick.Name);
        words.AttachComponent<RectTransform>();
        var wordsElement = words.AttachComponent<LayoutElement>();
        wordsElement.MinHeight.Value = OptionRowHeight - 8f;
        wordsElement.PreferredHeight.Value = OptionRowHeight - 8f;
        var wordsLayout = words.AttachComponent<HorizontalLayout>();
        wordsLayout.PaddingLeft.Value = 6f;
        wordsLayout.ForceExpandWidth.Value = false;
        wordsLayout.ForceExpandHeight.Value = true;
        Words(words, ui.Style, pick.Label, pick.Hint);

        int columns = pick.Count <= 3 ? pick.Count : 2;
        int rows = (pick.Count + columns - 1) / columns;
        var tiles = ui.Root.AddSlot(pick.Name + " Options");
        tiles.AttachComponent<RectTransform>();
        float height = rows * PickTileHeight + System.Math.Max(0, rows - 1) * 6f;
        var tilesElement = tiles.AttachComponent<LayoutElement>();
        tilesElement.UseZeroMetrics.Value = true;
        tilesElement.MinHeight.Value = height;
        tilesElement.PreferredHeight.Value = height;
        var grid = tiles.AttachComponent<GridLayout>();
        grid.Columns.Value = columns;
        grid.Spacing.Value = 6f;

        var tb = new UIBuilder(tiles).SetStyle(ui.Style);
        var drivers = new List<ColorDriver>(pick.Count);
        void Show()
        {
            for (int i = 0; i < drivers.Count; i++)
            {
                var driver = drivers[i];
                if (driver.IsDestroyed)
                    continue;
                bool chosen = pick.IsChosen(i);
                driver.NormalColor.Value = chosen ? DashTheme.Accent : ButtonFill;
                driver.HighlightColor.Value = chosen ? DashTheme.AccentHover : DashTheme.SurfaceHover;
                driver.PressedColor.Value = chosen ? DashTheme.AccentPressed : DashTheme.SurfacePressed;
            }
        }
        for (int i = 0; i < pick.Count; i++)
        {
            int index = i;
            var button = ActionButton(tb, pick.OptionName(i), () =>
            {
                if (!CanInteract)
                    return;
                pick.Choose(index);
                Show();
                changed();
            }, out var driver);
            var element = button.Slot.GetComponent<LayoutElement>();
            if (element != null)
            {
                element.MinHeight.Value = PickTileHeight;
                element.PreferredHeight.Value = PickTileHeight;
            }
            drivers.Add(driver);
        }
        Show();
    }

    // PER MATERIAL
    //
    // The file's own materials, each with a kind of its own or "As Above". The names come from the file without
    // importing it (ModelMaterialNames), read off the world thread the first time the page opens. Not
    // remembered: the names belong to this file. -xlinka

    private readonly Dictionary<string, ModelMaterialType> _perMaterial = new(StringComparer.Ordinal);
    private List<string>? _materialNames;
    private bool _materialScanStarted;

    private void PerMaterialRow(UIBuilder ui)
    {
        var label = _perMaterial.Count > 0
            ? L("PerMaterialCount", "Per Material ({0} set)").WithArgs(_perMaterial.Count)
            : L("PerMaterial", "Per Material");
        var button = ActionButton(ui, label, () => { if (CanInteract) OpenPage(MenuPerMaterial); });
        var element = button.Slot.GetComponent<LayoutElement>();
        if (element != null)
        {
            element.MinHeight.Value = PickTileHeight;
            element.PreferredHeight.Value = PickTileHeight;
        }
    }

    private void MenuPerMaterial(UIBuilder ui)
    {
        var body = SetupSection(ui, L("PerMaterial", "Per Material"));
        StartMaterialScan();

        if (_materialNames == null || _materialNames.Count == 0)
        {
            body.PushStyle().FlexibleHeight(1f);
            var message = body.Text(_materialNames == null
                ? L("Reading", "Reading the file's materials")
                : L("NoMaterialList", "This file names no materials before import. The Material choice covers them all."),
                DashTheme.FontBody, DashTheme.TextDim);
            body.PopStyle();
            message.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            message.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            message.WordWrap.Value = true;
            return;
        }

        var list = ScrollContent(body);
        foreach (var name in _materialNames)
            MaterialRow(list, name);

        SetupGrid(body);
        GridButton(body, L("OneForAll", "One For All"), () =>
        {
            _perMaterial.Clear();
            Wizard?.Refresh();
        }, BackColor);
    }

    private void MaterialRow(UIBuilder ui, string name)
    {
        var row = ui.Root.AddSlot(name);
        row.AttachComponent<RectTransform>();
        var rowElement = row.AttachComponent<LayoutElement>();
        rowElement.MinHeight.Value = PickTileHeight;
        rowElement.PreferredHeight.Value = PickTileHeight;
        var layout = row.AttachComponent<HorizontalLayout>();
        layout.Spacing.Value = 8f;
        layout.PaddingLeft.Value = 6f;
        layout.ForceExpandWidth.Value = false;
        layout.ForceExpandHeight.Value = true;

        var rb = new UIBuilder(row).SetStyle(ui.Style);
        rb.PushStyle().FlexibleWidth(1f);
        var title = rb.Text(name, DashTheme.FontBody, DashTheme.Text);
        rb.PopStyle();
        title.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        title.VerticalAlignment.Value = TextVerticalAlignment.Middle;

        // Each press moves on to the next kind, and past the last one back to the dialog's choice.
        rb.PushStyle().MinWidth(170f).PreferredWidth(170f).FlexibleWidth(0f);
        Text? shown = null;
        var button = ActionButton(rb, KindName(name), () =>
        {
            if (!CanInteract)
                return;
            int at = -1;
            if (_perMaterial.TryGetValue(name, out var current))
                at = Array.FindIndex(MaterialChoices, c => c.Item1 == current);
            at++;
            if (at >= MaterialChoices.Length)
                _perMaterial.Remove(name);
            else
                _perMaterial[name] = MaterialChoices[at].Item1;
            if (shown != null)
                LocaleTextRegistry.Bind(shown, KindName(name));
        }, primary: false);
        rb.PopStyle();
        shown = button.Slot.GetComponentInChildren<Text>();
    }

    private LocaleText KindName(string material)
    {
        if (!_perMaterial.TryGetValue(material, out var kind))
            return L("AsAbove", "As Above");
        foreach (var (value, name) in MaterialChoices)
        {
            if (value == kind)
                return name;
        }
        return kind.ToString();
    }

    private void StartMaterialScan()
    {
        if (_materialScanStarted)
            return;
        _materialScanStarted = true;
        var paths = new List<string>(Paths);
        var world = World;
        Task.Run(() =>
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                if (!ModelMaterialNames.CanRead(path))
                    continue;
                foreach (var name in ModelMaterialNames.Read(path) ?? new List<string>())
                {
                    if (seen.Add(name))
                        names.Add(name);
                }
            }
            world?.RunSynchronously(() =>
            {
                if (IsDestroyed)
                    return;
                _materialNames = names;
                Wizard?.Refresh();
            });
        });
    }

    public void RunImport()
    {
        if (!CanInteract || RefuseSpawn()) return;
        Logger.Debug($"ModelImportDialog: scale={Scale.Value} auto={AutoScale.Value} material={Material.Value} colliders={ColliderMode.Value} "
            + $"lights={ImportLights.Value} cameras={ImportCameras.Value} assetsOnly={AssetsOnly.Value} placement={AssetPlacement.Value} share={ShareAssets.Value} "
            + $"byName={MatchTexturesByName.Value} avatar={AvatarSetup.Value} perMaterial={_perMaterial.Count} vertexColors={ImportVertexColors.Value}");
        SaveAnswers();

        int rowSize = (int)MathF.Max(1f, MathF.Ceiling(MathF.Sqrt(Paths.Count)));
        int index = 0;
        var basePos = Slot.GlobalPosition;
        var baseRot = Slot.GlobalRotation;
        var handler = ImportHandlers.Model;
        var request = BuildRequest();
        var target = ResolveTargetWorld();

        foreach (var file in Paths)
        {
            var s = target.AddSpawnSlot(target.RootSlot, Path.GetFileName(file) ?? file);
            if (!ImportAtOrigin.Value)
            {
                var offset = UniversalImporter.GridOffset(ref index, rowSize);
                s.GlobalPosition = basePos + baseRot * offset;
                s.GlobalRotation = baseRot;
            }
            s.GlobalScale = float3.One * request.TargetScaleFor(file);

            // Grabbable on the slot the model lands in, whoever builds the model: its colliders are children,
            // so a grab anywhere on it walks up to this. The handler path used to skip the option entirely and
            // an imported model came in that nothing could pick up. Assets alone have nothing to take hold of.
            // -xlinka
            if (AllowGrabbable.Value && !AssetsOnly.Value)
            {
                var g = s.AttachComponent<Grabbable>();
                g.AllowGrab.Value = true;
                g.Scalable.Value = Scalable.Value;
            }

            if (handler != null)
            {
                var pathCaptured = file;
                // Owned by the slot the model imports into, not dropped on the floor. The dialog destroys
                // itself on the next line, so it cannot be the owner; the target can, and that is the
                // thing whose disappearance should stop the import. A discarded Task also swallowed every
                // exception the import threw - this one is watched and logged. -xlinka
                s.StartTask(() => handler.ImportAsync(s, pathCaptured, request));
            }
            else
            {
                UniversalImporter.SpawnFileLabel(s, Path.GetFileName(file) ?? file);
            }
        }

        Slot.Destroy();
    }

    private ModelImportRequest BuildRequest()
    {
        return new ModelImportRequest
        {
            Scale = Scale.Value,
            AutoScale = AutoScale.Value,
            Material = Material.Value,
            Colliders = ColliderMode.Value != ModelColliderMode.None,
            FitColliders = ColliderMode.Value == ModelColliderMode.Shapes,
            SolidToPeople = SolidToPeople.Value,
            Grabbable = AllowGrabbable.Value,
            Scalable = Scalable.Value,
            ImportAtOrigin = ImportAtOrigin.Value,
            CalculateNormals = CalculateNormals.Value,
            CalculateTangents = CalculateTangents.Value,
            ImportVertexColors = ImportVertexColors.Value,
            ImportAlbedoColor = ImportAlbedoColor.Value,
            ImportEmissive = ImportEmissive.Value,
            MakeDualSided = MakeDualSided.Value,
            MakeFlatShaded = MakeFlatShaded.Value,
            MaxTextureSize = MaxTextureSize.Value,
            ForceNoMipMaps = ForceNoMipMaps.Value,
            ImportAnimations = ImportAnimations.Value,
            ImportLights = ImportLights.Value,
            ImportCameras = ImportCameras.Value,
            AssetsOnly = AssetsOnly.Value,
            MatchTexturesByName = MatchTexturesByName.Value,
            AvatarSetup = AvatarSetup.Value,
            AssetPlacement = AssetPlacement.Value,
            ShareAssets = ShareAssets.Value,
            MaterialTypes = _perMaterial.Count > 0 ? new Dictionary<string, ModelMaterialType>(_perMaterial, StringComparer.Ordinal) : null,
        };
    }
}
