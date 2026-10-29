// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using Helio.UI;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;
using Lumora.Shading;
using Mask = Helio.UI.Mask;

namespace Lumora.Core.Components.Scripting;

// One node's card: a small canvas on a local slot under the node, built on each machine from the
// node's data and thrown away and rebuilt when that data or its problems change. Nothing on it syncs;
// what a hand does on it (typing a constant, dropping a reference, cycling the overload) writes the
// node, and the node is what everyone shares.
//
// A node that does not compile wears a red outline and header and says why along its bottom edge. One the
// running script lately faulted in wears an orange outline and says what went wrong there. -xlinka
public sealed class ScriptNodeCard
{
    private float EditorLeft => Layout.EditorLeft;
    private float EditorWidth => Layout.EditorWidth;
    private const float BackingDepth = 4f;
    private const float BackingMargin = 6f;

    private int _editing;
    private Slot? _options;
    private Slot? _content;
    private FontProvider? _font;
    private ScriptGraphNode? _node;
    private readonly List<ScriptCardHandle> _handles = new();
    private Text? _readout;
    private ScriptGraphNode? _shows;
    private Text? _previewMode;
    private Text? _previewUpper;
    private Text? _previewLower;
    private Slot? _previewAxes;
    private Slot? _previewRange;
    private bool? _previewShownCurve;
    private float _previewShownRange = float.NaN;

    public Slot Root { get; }
    public ScriptCardLayout Layout { get; }
    public int Stamp { get; }

    // A card being typed into is left alone until the typing ends, or the rebuild would eat the field.
    public bool IsEditing => _editing > 0;

    // The handle on this card for one of its ports, or its header.
    public ScriptCardHandle? FindHandle(ScriptCardHandleKind kind, string port)
    {
        foreach (var handle in _handles)
        {
            if (!handle.IsDestroyed && handle.Kind == kind && (kind == ScriptCardHandleKind.Header || handle.Port == port))
                return handle;
        }
        return null;
    }

    private ScriptNodeCard(Slot root, ScriptCardLayout layout, int stamp)
    {
        Root = root;
        Layout = layout;
        Stamp = stamp;
    }

    public void Destroy()
    {
        if (!Root.IsDestroyed)
            Root.Destroy();
    }

    public static ScriptNodeCard Build(ScriptGraphNode node, IReadOnlyList<string> problems, int stamp, string? fault = null, bool optionsOpen = false,
        IReadOnlyList<string>? notes = null)
    {
        var world = node.World!;
        var overload = node.Resolve(out var resolveError);
        var shown = new List<string>(problems);
        if (resolveError != null)
            shown.Insert(0, resolveError);
        bool bad = shown.Count > 0;
        bool faulted = !string.IsNullOrEmpty(fault);
        var noted = notes ?? Array.Empty<string>();
        var layout = ScriptCardLayout.For(node, shown.Count + noted.Count + (faulted ? 1 : 0));
        if (layout.Compact)
            return BuildCompact(node, layout, stamp);
        layout.OptionsOpen = optionsOpen && layout.OptionsRows > 0;
        float width = layout.Width;
        float height = layout.Height;

        var root = node.Slot.AddLocalSlot("Card");
        root.LocalPosition.Value = float3.Zero;
        root.LocalRotation.Value = floatQ.Identity;
        root.LocalScale.Value = new float3(ScriptEditorStyle.CardScale, ScriptEditorStyle.CardScale, ScriptEditorStyle.CardScale);
        var rect = root.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(0f, -height);
        rect.OffsetMax.Value = new float2(width, 0f);
        root.AttachComponent<Canvas>().SharesMaterials = true;
        // The dash's heavier faces throughout, bold for the title: the regular weight read as hairlines.
        var look = LookOf(world);
        var bold = look.Bold;
        var font = look.Semibold;

        var content = root.AddSlot("Content");
        Fill(content);
        content.AttachComponent<GraphicChunkRoot>();
        var background = content.AttachComponent<RoundedPanel>();
        background.Color.Value = ScriptEditorStyle.CardBackground;
        background.CornerRadius.Value = ScriptEditorStyle.CornerRadius;
        background.OutlineColor.Value = bad ? ScriptEditorStyle.Problem : faulted ? ScriptEditorStyle.Fault : ScriptEditorStyle.CardOutline;
        background.OutlineThickness.Value = bad || faulted ? 4f : 2f;

        var card = new ScriptNodeCard(root, layout, stamp);
        card.BuildHeader(content, bold, node, bad);
        card.BuildPorts(content, font, node);
        card.BuildSettings(content, font, node);
        card.BuildExtra(content, font, node);
        card.BuildPreviewWell(content, font, node);
        card.BuildOutputWell(content, font, node);
        card.BuildProblems(content, font, shown, noted, faulted ? "at run time: " + fault : null);
        // The options drawer starts closed on nearly every card and is a sixth of a card's slots, so it is made
        // the first time it opens. -xlinka
        card._content = content;
        card._font = font;
        card._node = node;
        if (layout.OptionsOpen)
            card.BuildOptions(content, font, node, overload);
        return card;
    }

    // A relay's bar: a port at each end, the line of what it carries running between them, and its middle to
    // move it by, the way a card moves by its header. It has no words on it, so it makes no faces.
    private static ScriptNodeCard BuildCompact(ScriptGraphNode node, ScriptCardLayout layout, int stamp)
    {
        float width = layout.Width;
        float height = layout.Height;
        var root = node.Slot.AddLocalSlot("Card");
        root.LocalPosition.Value = float3.Zero;
        root.LocalRotation.Value = floatQ.Identity;
        root.LocalScale.Value = new float3(ScriptEditorStyle.CardScale, ScriptEditorStyle.CardScale, ScriptEditorStyle.CardScale);
        var rect = root.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(0f, -height);
        rect.OffsetMax.Value = new float2(width, 0f);
        root.AttachComponent<Canvas>().SharesMaterials = true;

        var carried = layout.Left.Count > 0 ? layout.Left[0].Type : layout.Right.Count > 0 ? layout.Right[0].Type : null;
        var tint = ScriptEditorStyle.Type(carried);
        var content = root.AddSlot("Content");
        Fill(content);
        content.AttachComponent<GraphicChunkRoot>();
        var background = content.AttachComponent<RoundedPanel>();
        background.Color.Value = ScriptEditorStyle.CardBackground;
        background.CornerRadius.Value = ScriptEditorStyle.CornerRadius;
        background.OutlineColor.Value = tint;
        background.OutlineThickness.Value = 2f;

        var card = new ScriptNodeCard(root, layout, stamp);
        card._node = node;

        var line = content.AddSlot("Line");
        Place(line, 0f, height * 0.5f - 3f, width, 6f);
        line.AttachComponent<Image>().Tint.Value = tint;

        float input = 48f;
        float output = 48f;
        var middle = content.AddSlot("Header");
        Place(middle, input, 0f, width - input - output, height);
        var grip = middle.AttachComponent<Image>();
        grip.Tint.Value = new color(tint.r, tint.g, tint.b, 0.16f);
        grip.CornerRadius.Value = 8f;
        middle.AttachComponent<Button>().Tip.Value = ScriptNodeHelp.For(node.Overload?.Definition);
        var handle = middle.AttachComponent<ScriptCardHandle>();
        handle.Node = node;
        handle.Kind = ScriptCardHandleKind.Header;
        card._handles.Add(handle);

        float top = layout.RowTop(0);
        if (layout.Left.Count > 0)
            card.Handle(content, node, layout.Left[0], -ScriptEditorStyle.HandleSize * 0.5f, top, -10f, input + 10f);
        if (layout.Right.Count > 0)
            card.Handle(content, node, layout.Right[0], width - ScriptEditorStyle.HandleSize * 0.5f, top, width - output, output + 10f);
        return card;
    }

    // A solid slab just behind the card, so a card is still something from behind or edge on. It sits beside
    // the canvas rather than in it, so the card's layout and batching never take it over; the graph's view
    // draws every card's slab as one mesh (ScriptCardBackings), from SlabOf.
    //
    // Solid and shared: it was frosted glass, a material of its own on every card, each reading and blurring
    // the screen behind it. A converted graph unpacks a few thousand cards, and that was a few thousand
    // screen reads a frame that could not share a single draw.
    //
    // The faces and the slab's material are made once a world, on a local slot of its own, and every card
    // uses them. Each card made its own two font providers, and each looked its fonts up again; a card's
    // slots now hold only what it draws. -xlinka
    private sealed class CardLook
    {
        public FontProvider Bold = null!;
        public FontProvider Semibold = null!;
        public Lumora.Core.Assets.UnlitMaterial Backing = null!;

        public bool IsGone => Bold.IsDestroyed || Semibold.IsDestroyed || Backing.IsDestroyed;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<World, CardLook> Looks = new();

    private static CardLook LookOf(World world)
    {
        if (Looks.TryGetValue(world, out var look) && !look.IsGone)
            return look;
        var holder = world.RootSlot.AddLocalSlot("Script Card Look");
        var regular = ImportDialog.ResolveFontUrl(world) ?? DashTheme.FontRegular;
        var material = holder.AttachComponent<Lumora.Core.Assets.UnlitMaterial>();
        material.TintColor.Value = DashTheme.MeshTint(DashTheme.Surface);
        material.Culling.Value = Lumora.Core.Assets.Culling.None;
        look = new CardLook
        {
            Bold = Face(holder, DashTheme.FontBold, regular),
            Semibold = Face(holder, DashTheme.FontSemibold, regular),
            Backing = material,
        };
        Looks.AddOrUpdate(world, look);
        return look;
    }

    public static Lumora.Core.Assets.UnlitMaterial BackingMaterial(World world) => LookOf(world).Backing;

    // This card's slab in its node's space, in metres.
    public ScriptCardSlab SlabOf()
    {
        float scale = ScriptEditorStyle.CardScale;
        return new ScriptCardSlab(
            new float3(Layout.Width * 0.5f, -Layout.Height * 0.5f, -BackingDepth) * scale,
            floatQ.Identity,
            new float2(Layout.Width + BackingMargin * 2f, Layout.Height + BackingMargin * 2f) * scale,
            (ScriptEditorStyle.CornerRadius + BackingMargin) * scale);
    }

    private static FontProvider Face(Slot slot, Uri url, Uri fallback)
    {
        var face = slot.AttachComponent<FontProvider>();
        face.URL.Value = url;
        face.FallbackURLs.Add(fallback);
        face.FallbackURLs.Add(DashTheme.FontRegular);
        return face;
    }

    // The category's colour as a band across the top of the card, square where it meets the body. It was a
    // band the colour of the card with a two-unit category line under it, a hairline that shimmered at any
    // distance. -xlinka
    private void BuildHeader(Slot content, FontProvider bold, ScriptGraphNode node, bool bad)
    {
        float width = Layout.Width;
        float height = ScriptEditorStyle.HeaderHeight;
        var tint = bad ? ScriptEditorStyle.Problem : ScriptEditorStyle.HeaderTint(node.Category);
        var header = content.AddSlot("Header");
        Place(header, 0f, 0f, width, height);
        var image = header.AttachComponent<Image>();
        image.Tint.Value = tint;
        image.CornerRadius.Value = ScriptEditorStyle.CornerRadius;
        var grab = header.AttachComponent<Button>();
        grab.Tip.Value = ScriptNodeHelp.For(node.Overload?.Definition);
        var handle = header.AttachComponent<ScriptCardHandle>();
        handle.Node = node;
        handle.Kind = ScriptCardHandleKind.Header;
        _handles.Add(handle);

        var squared = header.AddSlot("Band");
        Place(squared, 0f, height * 0.5f, width, height * 0.5f);
        var band = squared.AttachComponent<Image>();
        band.Tint.Value = tint;
        grab.SetupBackgroundColor(band.Tint);

        Label(header, bold, node.Title, 18f, 0f, width - (Layout.OptionsRows > 0 ? 86f : 36f),
            height, DashTheme.Text, size: ScriptEditorStyle.TitleSize);
        if (Layout.OptionsRows > 0)
        {
            // Sibling of the drag header, so the node tool does not mistake this button for a grab.
            var options = Builder(content, bold).Button("···", (_, _) => SetOptionsOpen(!Layout.OptionsOpen), ScriptEditorStyle.HeaderButton);
            Place(options.Slot, width - 58f, (height - 44f) * 0.5f, 44f, 44f);
        }
    }

    public void SetOptionsOpen(bool open)
    {
        Layout.OptionsOpen = open && Layout.OptionsRows > 0;
        if (Layout.OptionsOpen && _options == null && _content is { IsDestroyed: false } content && _font != null
            && _node is { IsDestroyed: false } node)
            BuildOptions(content, _font, node, node.Resolve(out _));
        if (_options != null)
            _options.ActiveSelf.Value = Layout.OptionsOpen;
        var rect = Root.GetComponent<RectTransform>()!;
        rect.OffsetMin.Value = new float2(0f, -Layout.Height);
    }

    private void BuildOptions(Slot content, FontProvider font, ScriptGraphNode node, NodeOverload? overload)
    {
        if (Layout.OptionsRows == 0)
            return;
        _options = content.AddSlot("Options");
        Place(_options, 12f, Layout.OptionsTop, Layout.Width - 24f, Layout.OptionsRows * ScriptEditorStyle.RowHeight);
        _options.ActiveSelf.Value = Layout.OptionsOpen;
        float width = Layout.Width - 24f;
        var ui = Builder(_options, font);
        var choices = ScriptGraphEditing.SignatureChoices(node);
        var signature = SignatureText(node, overload);
        // A Function's ports are typed out here, and a call's to another script's Function; a Return and a call in
        // this graph take theirs from their Function.
        bool ports = overload?.Definition.Kind == NodeKind.Function || overload?.Definition.IsScriptCall == true;
        bool typeRow = overload?.Definition.IsGeneric == true || choices.Count > 1 || overload == null || ports;
        if (typeRow)
        {
            Label(_options, font, ports ? "Ports" : "Type", 2f, 0f, 68f, ScriptEditorStyle.RowHeight, DashTheme.TextDim);
            float fieldWidth = width - 76f - (choices.Count > 1 ? 96f : 0f);
            if (overload?.Definition.IsGeneric == true || overload == null || ports)
                TypeEditor(_options, font, node, signature, 74f, fieldWidth, ports ? "a:int,b:float|sum:float" : "type");
            else
                Label(_options, font, signature, 74f, 0f, fieldWidth, ScriptEditorStyle.RowHeight, ScriptEditorStyle.PortText);
            if (choices.Count > 1)
            {
                var previous = ui.Button("‹", (_, _) => ScriptGraphEditing.CycleOverload(node, -1), DashTheme.Field);
                Place(previous.Slot, width - 88f, 3f, 42f, 42f);
                var next = ui.Button("›", (_, _) => ScriptGraphEditing.CycleOverload(node, 1), DashTheme.Field);
                Place(next.Slot, width - 42f, 3f, 42f, 42f);
            }
        }
        float top = typeRow ? ScriptEditorStyle.RowHeight : 0f;
        if (overload?.Definition.IsVariadic == true)
        {
            CountRow(ui, font, "Inputs", width, top, step => ScriptGraphEditing.ChangeInputCount(node, step));
            top += ScriptEditorStyle.RowHeight;
        }
        if (overload?.Definition.GrowsExits == true)
            CountRow(ui, font, "Outputs", width, top, step => ScriptGraphEditing.ChangeExitCount(node, step));
    }

    // One fewer or one more of a node's inputs or exits, beside what it is a count of.
    private void CountRow(UIBuilder ui, FontProvider font, string label, float width, float top, Func<int, bool> change)
    {
        Label(_options!, font, label, 2f, top, width - 92f, ScriptEditorStyle.RowHeight, DashTheme.TextDim);
        var fewer = ui.Button("−", (_, _) => change(-1), DashTheme.Field);
        Place(fewer.Slot, width - 88f, top + 3f, 42f, 42f);
        var more = ui.Button("+", (_, _) => change(1), DashTheme.Field);
        Place(more.Slot, width - 42f, top + 3f, 42f, 42f);
    }

    // The options drawer's type field, or a Function's ports. It commits when it lets go of focus; text the node
    // cannot take goes back to what the node has, and the card says why.
    private void TypeEditor(Slot header, FontProvider font, ScriptGraphNode node, string signature, float x, float width, string placeholder)
    {
        var ui = Builder(header, font);
        var input = ui.TextInput(signature, placeholder, null, false, DashTheme.Field);
        Place(input.Slot, x, 2f, width, ScriptEditorStyle.RowHeight - 4f);
        ClipField(input);
        input.EditingStarted += _ => _editing++;
        input.FocusLost += (field, text) =>
        {
            _editing = System.Math.Max(0, _editing - 1);
            bool taken = ScriptGraphEditing.TrySetSignature(node, text, out var refusal);
            if (node.IsDestroyed)
                return;
            // Said on the card, so text that goes back to what the node has does not go back without a reason.
            node.SignatureRefusal = taken ? null : refusal ?? "that does not fit this node";
            if (!taken && !input.IsDestroyed)
                input.Text.Value = SignatureText(node, node.Overload);
        };
    }

    // What the type field shows: the node's signature, or nothing for a Function with no ports yet, so the field
    // shows how ports are typed instead of a lone bar.
    private static string SignatureText(ScriptGraphNode node, NodeOverload? overload)
    {
        if (overload?.FunctionSignature is { Inputs.Count: 0, Outputs.Count: 0 })
            return string.Empty;
        return overload?.Signature ?? node.Signature.Value ?? string.Empty;
    }

    private void BuildPorts(Slot content, FontProvider font, ScriptGraphNode node)
    {
        float width = Layout.Width;
        float row = ScriptEditorStyle.RowHeight;
        for (int i = 0; i < Layout.Left.Count; i++)
        {
            var port = Layout.Left[i];
            float top = Layout.RowTop(i);
            bool hasEditor = port.Kind == ScriptCardHandleKind.DataInput && port.Type != null && node.FindInput(port.Name)?.Node.Target == null;
            Handle(content, node, port, -ScriptEditorStyle.HandleSize * 0.5f, top, -10f, EditorLeft + 6f);
            Label(content, font, port.Kind == ScriptCardHandleKind.FlowInput ? "›" : port.Name,
                ScriptEditorStyle.PortInset, top, Layout.InputLabelWidth, row, ScriptEditorStyle.PortText);

            if (hasEditor)
                ValueEditor(content, font, node, port.Name, port.Type!, EditorLeft, top);
        }

        for (int i = 0; i < Layout.Right.Count; i++)
        {
            var port = Layout.Right[i];
            float top = Layout.RowTop(i);
            float left = Layout.OutputLeft - 8f;
            Handle(content, node, port, width - ScriptEditorStyle.HandleSize * 0.5f, top, left, width - left + 10f);
            Label(content, font, port.Name, Layout.OutputLeft, top, Layout.OutputLabelWidth, row,
                ScriptEditorStyle.PortText, TextHorizontalAlignment.Right);
        }
    }

    private void BuildSettings(Slot content, FontProvider font, ScriptGraphNode node)
    {
        for (int i = 0; i < Layout.Settings.Count; i++)
        {
            var setting = Layout.Settings[i];
            float top = Layout.SettingTop(i);
            if (setting.Kind == ScriptCardHandleKind.Variable)
            {
                Handle(content, node, setting, 4f, top, 0f, Layout.Width);
                var target = node.FindValue(setting.Name)?.Reference.Target as ScriptGraphNode;
                Label(content, font, PointsAt(node, target), ScriptEditorStyle.PortInset, top, Layout.Width - ScriptEditorStyle.PortInset - 12f,
                    ScriptEditorStyle.RowHeight, target == null ? DashTheme.Warning : ScriptEditorStyle.PortText);
                continue;
            }

            Label(content, font, setting.Name, ScriptEditorStyle.PortInset, top, Layout.InputLabelWidth,
                ScriptEditorStyle.RowHeight, ScriptEditorStyle.PortText);
            if (setting.Type != null)
                ValueEditor(content, font, node, setting.Name, setting.Type, EditorLeft, top);
        }
    }

    // A Display's or Pulse's readout, kept current from what the host shows on the node; a Call's button.
    private void BuildExtra(Slot content, FontProvider font, ScriptGraphNode node)
    {
        if (!Layout.Readout && !Layout.CallButton)
            return;
        float top = Layout.ExtraTop + 4f;
        float height = Layout.ExtraHeight - 8f;
        float width = Layout.Width - 24f;
        if (Layout.CallButton)
        {
            var call = Builder(content, font).Button("Call", (_, _) => ScriptHost.Call(node), DashTheme.Accent);
            Place(call.Slot, 10f, top, width, height);
            return;
        }
        var well = content.AddSlot("Readout");
        Place(well, 10f, top, width, height);
        var image = well.AttachComponent<Image>();
        image.Tint.Value = DashTheme.Field;
        image.CornerRadius.Value = 8f;
        var text = Builder(well, font).Text(ReadoutText(node), ScriptEditorStyle.FontSize + 2f, DashTheme.Text);
        Fill(text.Slot);
        well.AttachComponent<Mask>().ShowMaskGraphic.Value = true;
        text.AutoSizeHorizontal.Value = text.AutoSizeVertical.Value = true;
        text.AutoSizeMin.Value = 16f;
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        _readout = text;
        _shows = node;
    }

    // Brings the readout up to what the host last showed, without rebuilding the card.
    public void Refresh()
    {
        RefreshPreviewControls();
        if (_readout == null || _readout.IsDestroyed || _shows == null || _shows.IsDestroyed)
            return;
        var text = ReadoutText(_shows);
        if (_readout.Content.Value != text)
            _readout.Content.Value = text;
    }

    // How far the card's ground is pulled toward the selection tint: all the way for a selected node, partway
    // while a hold is about to select or deselect it.
    public float Highlight { get; private set; }

    public void SetHighlight(float amount)
    {
        Highlight = amount;
        if (Root.FindChild("Content", recursive: false)?.GetComponent<RoundedPanel>() is { IsDestroyed: false } ground)
            ground.Color.Value = color.Lerp(ScriptEditorStyle.CardBackground, ScriptNodeSelection.Tint, amount * ScriptNodeSelection.Pull);
    }

    private static string ReadoutText(ScriptGraphNode node)
        => string.IsNullOrEmpty(node.Shown.Value) ? "..." : node.Shown.Value;

    // The dark square a shader node's preview sits in front of; the preview itself is the view's, not the card's.
    private void BuildPreviewWell(Slot content, FontProvider font, ScriptGraphNode node)
    {
        if (!Layout.Preview)
            return;
        var well = content.AddSlot("Preview");
        Place(well, 10f, Layout.PreviewTop, Layout.Width - 20f, ScriptCardLayout.PreviewSize);
        var image = well.AttachComponent<Image>();
        image.Tint.Value = DashTheme.Field;
        image.CornerRadius.Value = 8f;

        var outputs = node.Overload?.Outputs;
        if (outputs == null || outputs.Count == 0 || !ShaderTypes.TryFrom(outputs[0].Type, out var type) || !ShaderPreview.IsNumeric(type))
            return;

        float right = (Layout.Width + ScriptCardLayout.PreviewSize) * 0.5f + 2f;
        float controlsWidth = Layout.Width - right - 16f;
        var mode = Builder(content, font).Button("Curve", (_, _) =>
        {
            if (node.Space?.View?.PreviewOf(node)?.Material is { } material)
                material.Curve = !material.Curve;
            RefreshPreviewControls();
        }, DashTheme.Surface);
        Place(mode.Slot, right, Layout.PreviewTop + 8f, controlsWidth, 36f);
        _previewMode = mode.Slot.GetComponentInChildren<Text>();

        _previewRange = content.AddSlot("Preview range");
        Fill(_previewRange);
        var ui = Builder(_previewRange, font);
        var wider = ui.Button("Y ×10", (_, _) => ChangePreviewRange(node, 10f), DashTheme.Surface);
        Place(wider.Slot, right, Layout.PreviewTop + 52f, controlsWidth, 36f);
        var narrower = ui.Button("Y ÷10", (_, _) => ChangePreviewRange(node, 0.1f), DashTheme.Surface);
        Place(narrower.Slot, right, Layout.PreviewTop + 96f, controlsWidth, 36f);

        _previewAxes = content.AddSlot("Preview axes");
        Fill(_previewAxes);
        float leftWidth = (Layout.Width - ScriptCardLayout.PreviewSize) * 0.5f - 12f;
        _previewUpper = Label(_previewAxes, font, "+1", 18f, Layout.PreviewTop + 8f, leftWidth, 30f, DashTheme.TextDim, size: 19f);
        _previewLower = Label(_previewAxes, font, "−1", 18f, Layout.PreviewTop + 160f, leftWidth, 30f, DashTheme.TextDim, size: 19f);
        Label(_previewAxes, font, "U: 0–1", 18f, Layout.PreviewTop + 52f, leftWidth, 28f, DashTheme.TextDim, size: 18f);
        Label(_previewAxes, font, "V: 0.5", 18f, Layout.PreviewTop + 78f, leftWidth, 28f, DashTheme.TextDim, size: 18f);
        Label(_previewAxes, font, ShaderTypes.Width(type) == 1 ? "Value" : "XYZW"[..ShaderTypes.Width(type)],
            18f, Layout.PreviewTop + 116f, leftWidth, 28f, DashTheme.TextDim, size: 18f);
    }

    private static readonly Localization.LocaleText DrivesLabel = Localization.LocaleText.Keyed("Loom.Shader.Drives", "Drives");
    private static readonly Localization.LocaleText NoMaterialLabel = Localization.LocaleText.Keyed("Loom.Shader.NoMaterial",
        "no material yet: spawn its orb, or drop something here");
    private static readonly Localization.LocaleText SpawnOrbLabel = Localization.LocaleText.Keyed("Loom.Shader.SpawnOrb", "Spawn orb");
    private static readonly Localization.LocaleText DropHintLabel = Localization.LocaleText.Keyed("Loom.Shader.DropHint",
        "drop an orb, a material or an object");
    private static readonly Localization.LocaleText SwapLabel = Localization.LocaleText.Keyed("Loom.Shader.Swap", "Swap in");
    private static readonly Localization.LocaleText KeepLabel = Localization.LocaleText.Keyed("Loom.Shader.Keep", "Keep");

    // A Material Output's material well (ShaderOutputWell): a sphere wearing the graph's own material in front of a
    // dark square, the materials the graph drives listed beside it, a button for an orb of its own and a line for
    // what was last done. The sphere sits on a local slot beside the card, like a node's preview, so it is no part
    // of the canvas.
    private void BuildOutputWell(Slot content, FontProvider font, ScriptGraphNode node)
    {
        if (!Layout.OutputWell)
            return;
        float top = Layout.PreviewTop;
        float size = ScriptCardLayout.PreviewSize;
        float width = Layout.Width - 20f;
        float row = ScriptEditorStyle.RowHeight;

        var area = content.AddSlot("Material Well");
        Place(area, 10f, top, width, ScriptCardLayout.OutputWellHeight);
        var ground = area.AttachComponent<Image>();
        ground.Tint.Value = new color(DashTheme.Field.r, DashTheme.Field.g, DashTheme.Field.b, 0.45f);
        ground.CornerRadius.Value = 10f;
        area.AttachComponent<Button>();
        var well = area.AttachComponent<ShaderOutputWell>();
        well.Node = node;

        var square = area.AddSlot("Sphere Well");
        Place(square, 0f, 0f, size, size);
        var squareImage = square.AttachComponent<Image>();
        squareImage.Tint.Value = DashTheme.Field;
        squareImage.CornerRadius.Value = 8f;
        square.AttachComponent<Button>();
        var handle = square.AttachComponent<ScriptCardHandle>();
        handle.Node = node;
        handle.Kind = ScriptCardHandleKind.Material;
        handle.Port = "material";
        _handles.Add(handle);
        var pull = square.AttachComponent<ReferenceProxySource>();
        pull.GrabArea.Target = square;
        well.Pull = pull;
        var empty = Builder(square, font).Text(NoMaterialLabel.Resolve(), ScriptEditorStyle.FontSize - 5f, DashTheme.TextMuted);
        Place(empty.RectTransform, 14f, 14f, size - 28f, size - 28f);
        empty.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        empty.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        empty.WordWrap.Value = true;
        well.Empty = empty;

        float scale = ScriptEditorStyle.CardScale;
        float radius = (size * 0.5f - 18f) * scale;
        var sphere = node.Slot.AddLocalSlot("Card Material");
        sphere.LocalPosition.Value = new float3((10f + size * 0.5f) * scale, -(top + size * 0.5f) * scale, radius + 2f * scale);
        var mesh = sphere.AttachComponent<SphereMesh>();
        mesh.Radius.Value = radius;
        mesh.Segments.Value = 32;
        mesh.Rings.Value = 16;
        var renderer = sphere.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        sphere.ActiveSelf.Value = false;
        well.Sphere = sphere;
        well.SphereRenderer = renderer;

        float listX = size + 14f;
        float listWidth = width - listX - 8f;
        Label(area, font, DrivesLabel.Resolve().ToUpperInvariant(), listX, 4f, listWidth, 32f, DashTheme.TextMuted, size: ScriptEditorStyle.FontSize - 6f);
        const int rows = 4;
        const float rowHeight = 38f;
        for (int i = 0; i < rows; i++)
        {
            int index = i;
            var button = Builder(area, font).Button(string.Empty, (_, _) => well.SpawnRow(index), DashTheme.Surface);
            button.Slot.Name.Value = "Driven " + i;
            Place(button.Slot, listX, 40f + i * (rowHeight + 2f), listWidth, rowHeight);
            var chip = button.Slot.AddSlot("Chip");
            Place(chip, 10f, (rowHeight - 18f) * 0.5f, 18f, 18f);
            var chipImage = chip.AttachComponent<Image>();
            chipImage.CornerRadius.Value = 9f;
            var label = Label(button.Slot, font, string.Empty, 36f, 0f, listWidth - 44f, rowHeight, DashTheme.Text, size: ScriptEditorStyle.FontSize - 4f);
            button.Slot.ActiveSelf.Value = false;
            well.Rows.Add((button.Slot, chipImage, label));
        }
        well.More = Label(area, font, string.Empty, listX + 10f, 40f + (rows - 1) * (rowHeight + 2f), listWidth - 10f, rowHeight, DashTheme.TextMuted,
            size: ScriptEditorStyle.FontSize - 5f);
        well.None = Label(area, font, ShaderOutputWell.NoneLabel.Resolve(), listX + 4f, 40f, listWidth - 4f, rowHeight, DashTheme.TextMuted,
            size: ScriptEditorStyle.FontSize - 5f);

        var spawn = Builder(area, font).Button(SpawnOrbLabel, (_, _) => well.SpawnOwn(), DashTheme.Accent);
        Place(spawn.Slot, 0f, size + 6f, 180f, row - 10f);
        Label(area, font, DropHintLabel.Resolve(), 194f, size + 6f, width - 198f, row - 10f, DashTheme.TextMuted, size: ScriptEditorStyle.FontSize - 5f);

        var status = Label(area, font, string.Empty, 4f, size + row + 2f, width - 8f, row - 10f, DashTheme.TextDim, size: ScriptEditorStyle.FontSize - 5f);
        status.Slot.ActiveSelf.Value = false;
        well.Status = status;
        var offer = area.AddSlot("Offer");
        Place(offer, 0f, size + row + 2f, width, row - 10f);
        well.OfferText = Label(offer, font, string.Empty, 4f, 0f, width - 236f, row - 10f, DashTheme.Warning, size: ScriptEditorStyle.FontSize - 5f);
        var swap = Builder(offer, font).Button(SwapLabel, (_, _) => well.TakeOffer(true), DashTheme.Accent);
        Place(swap.Slot, width - 228f, 0f, 112f, row - 10f);
        var keep = Builder(offer, font).Button(KeepLabel, (_, _) => well.TakeOffer(false), DashTheme.Surface);
        Place(keep.Slot, width - 110f, 0f, 110f, row - 10f);
        offer.ActiveSelf.Value = false;
        well.OfferRow = offer;
    }

    private void ChangePreviewRange(ScriptGraphNode node, float factor)
    {
        if (node.Space?.View?.PreviewOf(node)?.Material is { } material)
            material.Range *= factor;
        RefreshPreviewControls();
    }

    private void RefreshPreviewControls()
    {
        if (_previewMode == null || _previewMode.IsDestroyed || _node == null)
            return;
        var material = _node.Space?.View?.PreviewOf(_node)?.Material;
        bool curve = material?.Curve ?? true;
        float valueRange = material?.Range ?? 1f;
        if (_previewShownCurve == curve && _previewShownRange == valueRange)
            return;
        _previewShownCurve = curve;
        _previewShownRange = valueRange;
        string mode = curve ? "Curve" : "Image";
        if (_previewMode.Content.Value != mode) _previewMode.Content.Value = mode;
        if (_previewAxes != null) _previewAxes.ActiveSelf.Value = curve;
        if (_previewRange != null) _previewRange.ActiveSelf.Value = curve;
        string range = valueRange.ToString("G3", CultureInfo.InvariantCulture);
        if (_previewUpper != null && _previewUpper.Content.Value != "+" + range) _previewUpper.Content.Value = "+" + range;
        if (_previewLower != null && _previewLower.Content.Value != "−" + range) _previewLower.Content.Value = "−" + range;
    }

    // What is wrong in red, then notes in the warning colour, then a run-time fault.
    private void BuildProblems(Slot content, FontProvider font, List<string> problems, IReadOnlyList<string> notes, string? fault)
    {
        var lines = new List<(string Text, color Tint)>();
        foreach (var problem in problems)
            lines.Add((problem, ScriptEditorStyle.Problem));
        foreach (var note in notes)
            lines.Add((note, DashTheme.Warning));
        if (fault != null)
            lines.Add((fault, ScriptEditorStyle.Fault));
        for (int i = 0; i < Layout.ProblemLines && i < lines.Count; i++)
        {
            Label(content, font, Shorten(lines[i].Text, 64), 12f, Layout.ProblemTop(i),
                Layout.Width - 24f, ScriptEditorStyle.RowHeight, lines[i].Tint, size: ScriptEditorStyle.FontSize - 3f);
        }
    }

    // A port answers across its side of the row, label and all, not only on its dot: a beam at arm's
    // length has to be able to take hold of it and land on it. The dot lights up while the beam is on it.
    private void Handle(Slot content, ScriptGraphNode node, in ScriptCardPort port, float dotX, float top, float left, float width)
    {
        var slot = content.AddSlot("Port " + port.Name);
        Place(slot, left, top, width, ScriptEditorStyle.RowHeight);
        var button = slot.AttachComponent<Button>();
        var handle = slot.AttachComponent<ScriptCardHandle>();
        handle.Node = node;
        handle.Port = port.Name;
        handle.Kind = port.Kind;
        _handles.Add(handle);

        var dot = slot.AddSlot("Dot");
        float size = ScriptEditorStyle.HandleSize;
        Place(dot, dotX - left, (ScriptEditorStyle.RowHeight - size) * 0.5f, size, size);
        var image = dot.AttachComponent<Image>();
        image.Tint.Value = port.Kind == ScriptCardHandleKind.Variable ? DashTheme.Warning : ScriptEditorStyle.Type(port.Type);
        image.CornerRadius.Value = port.Type == null ? 2f : size * 0.5f;
        button.SetupBackgroundColor(image.Tint);
    }

    // A typed constant is a text field; an object input is a row a reference card drops onto, with a clear
    // button beside it while it holds something.
    private void ValueEditor(Slot content, FontProvider font, ScriptGraphNode node, string port, ScriptType type, float x, float top)
    {
        var entry = node.FindValue(port);
        // Lists, dictionaries and JSON are made by nodes and never picked up in the world, so an unwired
        // one has nothing to drop in: the row says what the node gets, which is none.
        if (typeof(ScriptCollection).IsAssignableFrom(type.ClrType))
        {
            var none = Builder(content, font).Text("none", ScriptEditorStyle.FontSize - 3f, DashTheme.TextMuted);
            Place(none.RectTransform, x, top + 3f, EditorWidth, ScriptEditorStyle.RowHeight - 6f);
            none.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            none.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            return;
        }
        if (type.Name == "texture" && node.Space is { Kind.Value: GraphKind.Shader } shaderSpace)
        {
            TextureSlot(content, font, node, shaderSpace, x, top);
            return;
        }
        if (type.ClrType.IsEnum && node.Space is { Kind.Value: GraphKind.Shader })
        {
            Choice(content, font, node, port, type, x, top);
            return;
        }
        if (!ScriptValueText.IsTyped(type))
        {
            var bound = ScriptValueText.Display(entry);
            // What a bound row points at opens in an inspector from here: the field a drive writes, the slot
            // a node reads. -xlinka
            var inspected = bound.Length > 0 ? entry?.Reference.Target : null;
            bool inspectable = ElementInspector.CanOpen(inspected);
            var drop = content.AddSlot("Drop " + port);
            Place(drop, x, top + 3f, bound.Length > 0 ? EditorWidth - (inspectable ? 84f : 42f) : EditorWidth, ScriptEditorStyle.RowHeight - 6f);
            var image = drop.AttachComponent<Image>();
            image.Tint.Value = DashTheme.Field;
            image.CornerRadius.Value = 8f;
            drop.AttachComponent<Mask>().ShowMaskGraphic.Value = true;
            drop.AttachComponent<Button>();
            var receiver = drop.AttachComponent<ScriptPortDrop>();
            receiver.Node = node;
            receiver.Port = port;
            receiver.Type = type;
            if (bound.Length > 0)
            {
                var clear = Builder(content, font).TextColor(DashTheme.Negative).Button("Ø", (_, _) => receiver.Clear(), DashTheme.Field);
                Place(clear.Slot, x + EditorWidth - 40f, top + 3f, 40f, ScriptEditorStyle.RowHeight - 6f);
            }
            if (inspectable)
                InspectButton(content, font, node, inspected!, x + EditorWidth - 82f, top + 3f);
            var dropUi = Builder(drop, font);
            var label = dropUi.Text(bound.Length > 0 ? Shorten(bound, 16) : "drop " + type.Name, ScriptEditorStyle.FontSize - 3f,
                bound.Length > 0 ? DashTheme.Text : DashTheme.TextMuted);
            Fill(label.Slot);
            label.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
            label.VerticalAlignment.Value = TextVerticalAlignment.Middle;
            return;
        }

        if (type == ScriptTypes.Bool)
        {
            Flip(content, font, node, port, x, top);
            return;
        }

        // An unset value still has one, the port's default or the type's, and the field shows it rather than
        // an empty box: a Constant that reads blank and still answers false is the field lying.
        string fallback = DefaultText(node, port, type);
        var ui = Builder(content, font);
        // A text field takes line breaks: enter starts a new line, and clicking away keeps what was typed.
        var input = ui.TextInput(Shown(entry, fallback), type.Name, null, type == ScriptTypes.String, DashTheme.Field);
        Place(input.Slot, x, top + 3f, EditorWidth, ScriptEditorStyle.RowHeight - 6f);
        ClipField(input);
        // What is typed goes into the node as it is typed, whenever it reads as a value, so the graph
        // follows the field; half-typed text that does not parse yet waits for more. The whole typing
        // session is one undo step, closed when the field lets go of focus however that happens, and text
        // that never parsed goes back to the node's value then.
        // A number takes math against what the port held when typing began ("*2" doubles it), so every
        // keystroke works from the same start; text that never works out says why in red for a moment.
        string before = string.Empty;
        double start = 0d;
        bool number = ScriptValueText.IsNumber(type);
        if (number)
            input.Tip.Value = FieldMath.Tip;
        input.EditingStarted += _ =>
        {
            _editing++;
            before = node.IsDestroyed ? string.Empty : node.FindValue(port)?.Json.Value ?? string.Empty;
            if (number)
                start = StartNumber(node, port, fallback);
        };
        input.EditingChanged += (_, text) => Apply(node, port, type, text, fallback, start);
        input.FocusLost += (_, text) =>
        {
            _editing = System.Math.Max(0, _editing - 1);
            Apply(node, port, type, text, fallback, start);
            RecordSession(node, port, before);
            if (!node.IsDestroyed && !input.IsDestroyed)
            {
                var shown = Shown(node.FindValue(port), fallback);
                if (input.Text.Value != shown)
                    input.Text.Value = shown;
                if (number && !string.IsNullOrWhiteSpace(text) && FieldMath.Evaluate(text, start) is { Ok: false } refused)
                    FieldHint.Show(input, text, refused.Hint, () => input.Text.Value = Shown(node.FindValue(port), fallback));
            }
        };
    }

    private static double StartNumber(ScriptGraphNode node, string port, string fallback)
        => double.TryParse(node.IsDestroyed ? fallback : Shown(node.FindValue(port), fallback),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && double.IsFinite(value) ? value : 0d;

    private static string Shown(ScriptGraphValue? entry, string fallback)
    {
        var shown = ScriptValueText.Display(entry);
        return shown.Length == 0 ? fallback : shown;
    }

    // What an unset value reads as: the port's own default, else the type's zero. Text has none to show.
    private static string DefaultText(ScriptGraphNode node, string port, ScriptType type)
    {
        if (type == ScriptTypes.String)
            return string.Empty;
        if (type.ClrType == typeof(ScriptBigInt))
            return "0";
        var overload = node.Overload;
        int index = overload?.InputIndex(port) ?? -1;
        object? value = index >= 0 && overload!.Inputs[index].HasDefault ? overload.Inputs[index].Default : null;
        try
        {
            value ??= type.ClrType.IsValueType ? Activator.CreateInstance(type.ClrType) : null;
            return value == null ? string.Empty : ScriptTextForm.Format(type, value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    // A true or false value is one press to flip, and says which it is now, the default included.
    private void Flip(Slot content, FontProvider font, ScriptGraphNode node, string port, float x, float top)
    {
        bool on = CurrentBool(node, port);
        var button = Builder(content, font).Button(on ? "true" : "false", (_, _) => FlipValue(node, port),
            on ? DashTheme.Accent : DashTheme.Field);
        Place(button.Slot, x, top + 3f, EditorWidth, ScriptEditorStyle.RowHeight - 6f);
    }

    private static bool CurrentBool(ScriptGraphNode node, string port)
    {
        if (node.FindValue(port) is { } entry && !string.IsNullOrEmpty(entry.Json.Value))
        {
            try
            {
                return GraphValue.ParseJson(entry.Json.Value).Bool;
            }
            catch (GraphFormatException)
            {
            }
        }
        var overload = node.Overload;
        int index = overload?.InputIndex(port) ?? -1;
        return index >= 0 && overload!.Inputs[index].Default is true;
    }

    private static void FlipValue(ScriptGraphNode node, string port)
    {
        if (node.IsDestroyed)
            return;
        string before = node.FindValue(port)?.Json.Value ?? string.Empty;
        try
        {
            node.SetValue(port, GraphValue.Of(!CurrentBool(node, port)));
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        RecordSession(node, port, before);
    }

    // A shader node's texture: the material's property for it, as a small picture and a name, taking a held
    // texture the way the material panel's row does. A loose group of shader nodes has no material yet, and
    // its slots say so.
    private void TextureSlot(Slot content, FontProvider font, ScriptGraphNode node, ScriptGraphSpace space, float x, float top)
    {
        float height = ScriptEditorStyle.RowHeight - 6f;
        var slot = content.AddSlot("Texture");
        Place(slot, x, top + 3f, EditorWidth, height);
        var well = slot.AttachComponent<Image>();
        well.Tint.Value = DashTheme.Field;
        well.CornerRadius.Value = 8f;
        slot.AttachComponent<Mask>().ShowMaskGraphic.Value = true;
        slot.AttachComponent<Button>();

        float side = height - 4f;
        var frame = slot.AddSlot("Checker");
        Place(frame, 2f, 2f, side, side);
        var checker = frame.AttachComponent<Lumora.Core.Assets.CheckerTextureProvider>();
        checker.Width.Value = 16;
        checker.Height.Value = 16;
        checker.CellSize.Value = 4;
        checker.ColorA.Value = new color(0.20f, 0.20f, 0.24f, 1f);
        checker.ColorB.Value = new color(0.12f, 0.12f, 0.15f, 1f);
        frame.AttachComponent<RawImage>().Texture.Target = checker;
        var picture = frame.AddSlot("Picture");
        Fill(picture);
        var raw = picture.AttachComponent<RawImage>();

        var label = Builder(slot, font).Text(string.Empty, ScriptEditorStyle.FontSize - 3f, DashTheme.TextMuted);
        Place(label.RectTransform, side + 8f, 0f, EditorWidth - side - 10f, height);
        label.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        label.VerticalAlignment.Value = TextVerticalAlignment.Middle;

        string? name = null;
        if (node.FindValue("name") is { } named && !string.IsNullOrEmpty(named.Json.Value))
        {
            try
            {
                name = GraphValue.ParseJson(named.Json.Value).String;
            }
            catch (GraphFormatException)
            {
            }
        }
        var receiver = slot.AttachComponent<ShaderTextureSlot>();
        receiver.Space = space;
        receiver.Uniform = Lumora.Shading.ImageTexture.UniformName(node.NodeId.Value ?? string.Empty, name);
        receiver.Picture = raw;
        receiver.Label = label;
    }

    // A shader node's setting, stepped through its choices with the arrows either side of it: a hand in VR
    // steps through Opaque, Cutout, Transparent faster than it types one.
    private void Choice(Slot content, FontProvider font, ScriptGraphNode node, string port, ScriptType type, float x, float top)
    {
        float height = ScriptEditorStyle.RowHeight - 6f;
        var ui = Builder(content, font);
        var previous = ui.Button("\u2039", (_, _) => StepChoice(node, port, type, -1), DashTheme.Field);
        Place(previous.Slot, x, top + 3f, 38f, height);
        var shown = Builder(content, font).Text(CurrentChoice(node, port, type), ScriptEditorStyle.FontSize - 2f, DashTheme.Text);
        Place(shown.RectTransform, x + 40f, top + 3f, EditorWidth - 80f, height);
        shown.HorizontalAlignment.Value = TextHorizontalAlignment.Center;
        shown.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        shown.AutoSizeHorizontal.Value = true;
        shown.AutoSizeMin.Value = 14f;
        var next = ui.Button("\u203a", (_, _) => StepChoice(node, port, type, 1), DashTheme.Field);
        Place(next.Slot, x + EditorWidth - 38f, top + 3f, 38f, height);
    }

    private static string CurrentChoice(ScriptGraphNode node, string port, ScriptType type)
    {
        var names = Enum.GetNames(type.ClrType);
        if (node.FindValue(port) is { } entry && !string.IsNullOrEmpty(entry.Json.Value))
        {
            try
            {
                if (GraphValue.ParseJson(entry.Json.Value).String is { } text)
                {
                    foreach (var name in names)
                    {
                        if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                            return name;
                    }
                }
            }
            catch (GraphFormatException)
            {
            }
        }
        var overload = node.Overload;
        int index = overload?.InputIndex(port) ?? -1;
        return index >= 0 && overload!.Inputs[index].Default is { } fallback ? fallback.ToString() ?? names[0] : names[0];
    }

    private static void InspectButton(Slot content, FontProvider font, ScriptGraphNode node, IWorldElement target, float x, float y)
    {
        var button = Builder(content, font).Button(string.Empty, (button, _) =>
        {
            var at = button.Slot;
            if (!node.IsDestroyed && at != null && !at.IsDestroyed)
                ElementInspector.Open(node.World, target, at.GlobalPosition, at.GlobalRotation);
        }, DashTheme.Field);
        button.Slot.Name.Value = "Inspect";
        Place(button.Slot, x, y, 40f, ScriptEditorStyle.RowHeight - 6f);
        var glyph = button.Slot.AddSlot("Glass");
        var rect = glyph.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(-9f, -9f);
        rect.OffsetMax.Value = new float2(9f, 9f);
        var picture = glyph.AttachComponent<Lumora.Core.Assets.ImageProvider>();
        picture.GenerateMipmaps.Value = false;
        picture.AllowCompression.Value = false;
        picture.MaxSizeOverride.Value = -1;
        picture.URL.Value = new Uri("res://Assets/Textures/Icons/inspect.svg");
        var image = glyph.AttachComponent<Image>();
        image.Texture.Target = picture;
        image.Tint.Value = DashTheme.TextDim;
    }

    private static void StepChoice(ScriptGraphNode node, string port, ScriptType type, int step)
    {
        if (node.IsDestroyed)
            return;
        var names = Enum.GetNames(type.ClrType);
        int at = System.Math.Max(0, Array.IndexOf(names, CurrentChoice(node, port, type)));
        string chosen = names[((at + step) % names.Length + names.Length) % names.Length];
        string before = node.FindValue(port)?.Json.Value ?? string.Empty;
        try
        {
            node.SetValue(port, GraphValue.Of(chosen));
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        if (node.FindValue(port) is { } entry && entry.Json.Value != before)
            InspectorUndo.Record(node, new FieldEditUndoBatch(entry.Json, before, entry.Json.Value, ScriptGraphEditing.ValueStep));
    }

    private static void Apply(ScriptGraphNode node, string port, ScriptType type, string text, string fallback = "", double start = 0d)
    {
        if (node.IsDestroyed)
            return;
        try
        {
            var entry = node.FindValue(port);
            string current = entry?.Json.Value ?? string.Empty;
            bool bound = entry != null && !entry.Reference.Value.IsNull;
            // Nothing typed that differs from what the node holds, so a field only visited changes nothing.
            if (text == ScriptValueText.Display(entry) || (current.Length == 0 && !bound && text == fallback && fallback.Length > 0))
                return;
            // Text is kept exactly as typed, spaces and line breaks and all, and an emptied text field is the
            // empty string. Anything else left blank clears the value.
            if (type != ScriptTypes.String && string.IsNullOrWhiteSpace(text))
            {
                if (current.Length > 0 || bound)
                    node.ClearValue(port);
            }
            else if (ScriptValueText.TryParse(text, type, start, out var value, out _) && (value.ToJson() != current || bound))
            {
                node.SetValue(port, value);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RecordSession(ScriptGraphNode node, string port, string before)
    {
        if (node.IsDestroyed)
            return;
        var entry = node.FindValue(port);
        if (entry != null && entry.Json.Value != before)
            InspectorUndo.Record(node, new FieldEditUndoBatch(entry.Json, before, entry.Json.Value, ScriptGraphEditing.ValueStep));
    }

    // A Write Value's row says what it writes, a Return's what it returns from, a call's what it calls.
    private static string PointsAt(ScriptGraphNode node, ScriptGraphNode? target)
    {
        var kind = node.Overload?.Definition.Kind;
        if (kind is NodeKind.Return or NodeKind.CallFunction or NodeKind.EvaluateFunction)
        {
            if (target == null)
                return "drag onto a Function";
            return (kind == NodeKind.Return ? "returns from " : "calls ") + VariableName(target);
        }
        return target == null ? "drag onto a Stored Value" : "writes " + VariableName(target);
    }

    private static string VariableName(ScriptGraphNode variable)
    {
        var name = variable.FindValue("name");
        var text = ScriptValueText.Display(name);
        return text.Length > 0 ? text : variable.NodeId.Value ?? "?";
    }

    private static UIBuilder Builder(Slot slot, FontProvider font)
    {
        var ui = new UIBuilder(slot);
        ui.Font(font).FontSize(ScriptEditorStyle.FontSize).TextColor(DashTheme.Text).ForegroundColor(DashTheme.Accent)
            .BackgroundColor(DashTheme.Surface).CornerRadius(8f);
        return ui;
    }

    // Labels clip to their own rect. A long custom port name can shrink within its column but can never
    // paint over a value field or another port. The editor itself keeps its full value intact. It was a
    // masked slot holding the text, two slots and five components a label where one slot and two do.
    private static Text Label(Slot parent, FontProvider font, string value, float x, float y, float width,
        float height, color tint, TextHorizontalAlignment alignment = TextHorizontalAlignment.Left, float size = ScriptEditorStyle.FontSize)
    {
        var text = Builder(parent, font).Text(value, size, tint);
        text.Slot.Name.Value = "Label " + value;
        Place(text.RectTransform, x, y, width, height);
        text.ClipToRect.Value = true;
        text.WordWrap.Value = false;
        text.AutoSizeHorizontal.Value = true;
        text.AutoSizeMin.Value = 16f;
        text.HorizontalAlignment.Value = alignment;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        return text;
    }

    private static void ClipField(TextInput input)
    {
        input.Slot.AttachComponent<Mask>().ShowMaskGraphic.Value = true;
        var text = input.Slot.FindChild("Text")?.GetComponent<Text>();
        if (text != null)
        {
            text.Size.Value = ScriptEditorStyle.FontSize - 2f;
            text.WordWrap.Value = false;
            text.KeepCaretVisible.Value = true;
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    private static void Place(Slot slot, float x, float y, float w, float h)
        => Place(slot.GetComponent<RectTransform>() ?? slot.AttachComponent<RectTransform>(), x, y, w, h);

    // Top-left anchored, y down from the parent's top edge.
    private static void Place(RectTransform? rect, float x, float y, float w, float h)
    {
        if (rect == null)
            return;
        rect.AnchorMin.Value = rect.AnchorMax.Value = new float2(0f, 1f);
        rect.OffsetMin.Value = new float2(x, -(y + h));
        rect.OffsetMax.Value = new float2(x + w, -y);
    }

    private static void Fill(Slot slot)
    {
        var rect = slot.GetComponent<RectTransform>() ?? slot.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = float2.Zero;
        rect.AnchorMax.Value = float2.One;
        rect.OffsetMin.Value = rect.OffsetMax.Value = float2.Zero;
    }
}
