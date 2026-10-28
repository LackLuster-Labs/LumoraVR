// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Helio.UI.Listing;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components.UI;

// One app on the dash workspace: a title bar you drag it by, four buttons (pop out, minimize, fill, close),
// a body the app's screen lives in, a corner grip, and along any edge it shares with a tiled neighbour a strip
// that moves that edge for both of them. Resting on the fill button, or holding it, brings up the layouts the
// window can be sent to. The layout rect never moves; where the window sits is
// its StackLayer's offset and how high it sits is the layer's lift, so dragging and raising cost a chunk
// transform, and only a size change lays the app out again. The rect hangs off the canvas's top-left so a
// resize from the bottom-right leaves everything pinned to the top where its mesh already is. -xlinka
[ComponentCategory("Hidden")]
public sealed class DashWindow : UIComponent
{
    public const float BarHeight = 44f;
    public const float ButtonSize = 34f;
    public const float ButtonGap = 4f;
    public const float Corner = 16f;
    public const float BodyInset = 6f;
    public const float GripSize = 28f;
    // A window grows into place, shrinks a touch on its way out, and goes down to its dock plate and comes
    // back up from it. Short: the motion says where it went, not how.
    public const float OpenSeconds = 0.16f;
    public const float CloseSeconds = 0.12f;
    public const float DockSeconds = 0.18f;
    private const float IconTile = 26f;
    private const float IconGlyph = 16f;
    private const float ButtonGlyph = 16f;
    private const float BarPad = 6f;

    public enum WindowState
    {
        Closed,
        Open,
        Minimized,
        Out,
    }

    // Where a window is tiled on the desk. The desk is four cells, two across and two down; a tile is the
    // cells it covers. The numbers are stored.
    public enum WindowSnap
    {
        None,
        Left,
        Right,
        Fill,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    public enum TileEdge
    {
        Left,
        Right,
        Top,
        Bottom,
    }

    // How long a press on the fill button is held before it brings up the layouts instead of filling.
    private const double HoldSeconds = 0.45;
    private const float EdgeStrip = 8f;

    private static readonly color FocusEdge = DashTheme.Accent.WithAlpha(0.55f);

    private sealed class Control
    {
        public RoundedButton Button = null!;
        public RoundedPanel Plate = null!;
        public Image Glyph = null!;
        public ColorDriver? Fill;
        public ColorDriver? Ink;
    }

    private Dashboard? _dash;
    private DashWorkspace? _workspace;
    private RectTransform? _rect;
    private StackLayer? _layer;
    private RoundedPanel? _backing;
    private Slot? _body;
    private Text? _title;
    private Control? _pop;
    private Control? _min;
    private Control? _fill;
    private Control? _close;
    private readonly Slot?[] _edges = new Slot?[4];
    private double _holdStart = -1;
    private bool _swallowClick;
    private float2 _position;
    private float2 _size;
    private float2 _gripStart;
    private float2 _gripPoint;
    private bool _focusedShown;
    private bool _focusKnown;
    private float _scale = 1f;
    private float2 _shift;
    private double _motionStart = -1;
    private float _motionSeconds;
    private float _fromScale = 1f;
    private float _toScale = 1f;
    private float2 _fromShift;
    private float2 _toShift;
    private Action? _motionDone;

    public DashboardScreen? Screen { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Label { get; private set; } = string.Empty;
    public string Icon { get; private set; } = string.Empty;
    public color Tint { get; private set; } = DashTheme.Accent;
    public bool InDock { get; private set; }
    public int DockGroup { get; private set; }
    public float2 MinSize { get; set; } = new(360f, 260f);
    public float2 DefaultSize { get; set; } = new(520f, 420f);
    public float2 MaxSize { get; set; } = new(3200f, 2000f);

    // Shared by floating windows, saved layouts and panels detached into the world.
    internal float2 ClampSize(float2 size, float2 available)
    {
        float maxX = MathF.Max(1f, MathF.Min(MaxSize.x, available.x));
        float maxY = MathF.Max(1f, MathF.Min(MaxSize.y, available.y));
        return new float2(System.Math.Clamp(size.x, MathF.Min(MinSize.x, maxX), maxX),
            System.Math.Clamp(size.y, MathF.Min(MinSize.y, maxY), maxY));
    }

    public WindowState State { get; internal set; }
    public WindowSnap Snap { get; internal set; }
    public bool IsFocused { get; private set; }
    // Placed at least once, so it has a rect of its own to come back to.
    public bool Placed { get; internal set; }
    // Where it was before a snap or a fill took it over.
    public Rect? RestoreBounds { get; internal set; }
    public DashWindowPanel? Popped { get; internal set; }
    // Moved, resized or tiled by hand at least once: from then on it opens where it was left.
    public bool UserArranged { get; internal set; }
    // Opens across the whole desk until it has been arranged by hand.
    public bool OpensFilled { get; set; }
    // The tile it was in when it was filled, so filling and back returns it to its tile.
    internal WindowSnap FillFrom { get; set; }

    // Top-down canvas units from the canvas's top-left: x right, y down.
    public float2 Position => _position;
    public float2 Size => _size;
    public Rect Bounds => new(_position.x, _position.y, _size.x, _size.y);

    public Slot? Body => _body;
    public DashWorkspace? Workspace => _workspace;
    public Dashboard? Dashboard => _dash;
    public bool Placeholder => Screen?.Placeholder.Value == true;

    internal void Build(Dashboard dash, DashWorkspace workspace, string key, string label, string icon, color tint,
        bool inDock, int dockGroup)
    {
        _dash = dash;
        _workspace = workspace;
        Key = key;
        Label = label;
        Icon = icon;
        Tint = tint;
        InDock = inDock;
        DockGroup = dockGroup;

        _rect = RectTransform ?? Slot.GetComponent<RectTransform>() ?? Slot.AttachComponent<RectTransform>();
        _rect.AnchorMin.Value = new float2(0f, 1f);
        _rect.AnchorMax.Value = new float2(0f, 1f);
        _layer = Slot.AttachComponent<StackLayer>();
        Slot.AttachComponent<GraphicChunkRoot>();

        // The backing lays down depth, and the canvas draws it ahead of everything of every window, the highest
        // window first, so whatever of a lower window it covers is gone rather than seen through it. It is glass
        // all the same, so the world behind the dash reads through it, and only the world.
        _backing = Slot.AttachComponent<RoundedPanel>();
        _backing.CornerRadius.Value = Corner;
        _backing.Color.Value = DashTheme.GlassWindow;
        _backing.OutlineColor.Value = DashTheme.OutlineStrong;
        _backing.OutlineThickness.Value = DashTheme.OutlineWidth;
        _backing.Material.Target = dash.BackingMaterial!;
        // Takes every press the window's own controls do not, so nothing under the window ever hears one.
        Slot.AttachComponent<InteractionElement>();

        BuildBar(dash);
        BuildBody();
        BuildGrip(dash);
        BuildEdges();
        SetSize(DefaultSize);
        ApplyFocus(false);
    }

    private void BuildBar(Dashboard dash)
    {
        var bar = SettingsUI.Child(Slot, "Bar", new float2(0f, 1f), new float2(1f, 1f),
            new float2(0f, -BarHeight), float2.Zero);
        bar.AttachComponent<GraphicChunkRoot>();
        bar.AttachComponent<DashWindowBar>().Window = this;

        var tile = SettingsUI.Child(bar, "Icon", new float2(0f, 0.5f), new float2(0f, 0.5f),
            new float2(12f, -IconTile * 0.5f), new float2(12f + IconTile, IconTile * 0.5f));
        SettingsUI.Panel(tile, Tint.WithAlpha(0.22f), color.Transparent, 8f);
        var glyph = SettingsUI.Child(tile, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(-IconGlyph * 0.5f, -IconGlyph * 0.5f), new float2(IconGlyph * 0.5f, IconGlyph * 0.5f));
        var image = glyph.AttachComponent<Image>();
        image.Texture.Target = dash.IconProvider(Icon);
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        image.Tint.Value = Tint;

        float buttons = BarPad + 4f * ButtonSize + 3f * ButtonGap;
        _title = SettingsUI.Label(bar, "Title", dash.FontBold.Target ?? dash.Font.Target, DashTheme.FontBody, DashTheme.Text,
            TextHorizontalAlignment.Left, float2.Zero, float2.One, new float2(12f + IconTile + 10f, 0f),
            new float2(-(buttons + 8f), 0f), shrinkToFit: true);
        _title.Content.Value = Label;
        _title.InteractionTarget.Value = false;

        float right = BarPad;
        _close = AddControl(dash, bar, "Close", "close", "Close", right, DashTheme.Negative.WithAlpha(0.16f), DashTheme.Negative,
            () => _workspace?.Close(this));
        right += ButtonSize + ButtonGap;
        _fill = AddControl(dash, bar, "Fill", "maximize", "Fill", right, DashTheme.SurfaceHover, DashTheme.Text, PressFill);
        _fill.Button.HoverEntered += _ => _dash?.SnapPicker?.FillHover(this, true);
        _fill.Button.HoverExited += _ => _dash?.SnapPicker?.FillHover(this, false);
        // Every press starts clean: a hold let go off the button never got to its release here, and would
        // otherwise eat the next plain click.
        _fill.Button.Pressed += _ =>
        {
            _swallowClick = false;
            _holdStart = World?.Time.TotalTime ?? -1;
        };
        // Held, it brings up the layouts instead, so a fill has to wait to see whether the press was a hold.
        _fill.Button.ActOnRelease.Value = true;
        _fill.Button.Released += _ => _holdStart = -1;
        right += ButtonSize + ButtonGap;
        _min = AddControl(dash, bar, "Minimize", "minimize", "Minimize", right, DashTheme.SurfaceHover, DashTheme.Text,
            () => _workspace?.Minimize(this));
        right += ButtonSize + ButtonGap;
        _pop = AddControl(dash, bar, "PopOut", "pop-out", "Pop out", right, DashTheme.AccentSoft, DashTheme.Accent,
            () => _workspace?.PopOutFromBar(this));
    }

    // A held press already brought up the layouts; its release is not a fill.
    private void PressFill()
    {
        if (_swallowClick)
        {
            _swallowClick = false;
            return;
        }
        _workspace?.ToggleFill(this);
    }

    // The layouts went away. A hold that brought them up and is still down keeps its swallow for its own
    // release; any other leaves nothing to swallow.
    internal void LayoutsClosed()
    {
        if (_fill == null || !_fill.Button.IsPressedNow)
            _swallowClick = false;
    }

    // The fill button in top-down canvas units, for the layouts to hang under.
    public Rect FillButtonRect
    {
        get
        {
            float right = BarPad + ButtonSize + ButtonGap;
            return new Rect(_position.x + _size.x - right - ButtonSize, _position.y + (BarHeight - ButtonSize) * 0.5f, ButtonSize, ButtonSize);
        }
    }

    // A control answers over its whole cell of the bar: the bar's full height and out to half the gap on either
    // side, the one nearest the edge out to the window's edge, so a press just off a glyph is that control and
    // never the drag strip behind it. The plate is drawn inset in the cell where it always was, square to the
    // hit and rounded only in paint; FillButtonRect still describes that plate.
    private Control AddControl(Dashboard dash, Slot bar, string name, string icon, string tip, float right,
        in color hoverFill, in color hoverInk, Action onClick)
    {
        float cellRight = right > BarPad ? right - ButtonGap * 0.5f : 0f;
        float cellLeft = right + ButtonSize + ButtonGap * 0.5f;
        var slot = SettingsUI.Child(bar, name, new float2(1f, 0f), new float2(1f, 1f),
            new float2(-cellLeft, 0f), new float2(-cellRight, 0f));
        var button = slot.AttachComponent<RoundedButton>();
        button.Tip.Value = tip;
        button.Clicked += (_, _) => onClick();

        float inset = right - cellRight;
        var plateSlot = SettingsUI.Child(slot, "Plate", new float2(1f, 0.5f), new float2(1f, 0.5f),
            new float2(-(inset + ButtonSize), -ButtonSize * 0.5f), new float2(-inset, ButtonSize * 0.5f));
        var plate = plateSlot.AttachComponent<RoundedPanel>();
        plate.CornerRadius.Value = 9f;
        plate.Color.Value = color.Transparent;
        plate.OutlineThickness.Value = 0f;

        var glyphSlot = SettingsUI.Child(plateSlot, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(-ButtonGlyph * 0.5f, -ButtonGlyph * 0.5f), new float2(ButtonGlyph * 0.5f, ButtonGlyph * 0.5f));
        var glyph = glyphSlot.AttachComponent<Image>();
        glyph.Texture.Target = dash.IconProvider(icon);
        glyph.PreserveAspect.Value = true;
        glyph.InteractionTarget.Value = false;
        glyph.Tint.Value = DashTheme.TextDim;

        var control = new Control { Button = button, Plate = plate, Glyph = glyph };
        control.Fill = button.AddColorDriver(plate.Color, color.Transparent, InteractionColorMode.Direct);
        control.Ink = button.AddColorDriver(glyph.Tint, DashTheme.TextDim, InteractionColorMode.Direct);
        PaintControl(control, true, hoverFill, hoverInk);
        return control;
    }

    // All four states written: a driver derives its own ramp from the base colour, and a transparent base
    // derives nothing useful. Disabled is the only one that differs.
    private static void PaintControl(Control control, bool enabled, in color hoverFill, in color hoverInk)
    {
        if (control.Fill is { IsDestroyed: false } fill)
        {
            fill.NormalColor.Value = color.Transparent;
            fill.HighlightColor.Value = hoverFill;
            fill.PressedColor.Value = hoverFill.WithAlpha(MathF.Min(1f, hoverFill.a * 1.6f));
            fill.DisabledColor.Value = color.Transparent;
            fill.Apply();
        }
        if (control.Ink is { IsDestroyed: false } ink)
        {
            ink.NormalColor.Value = DashTheme.TextDim;
            ink.HighlightColor.Value = hoverInk;
            ink.PressedColor.Value = hoverInk;
            ink.DisabledColor.Value = DashTheme.TextMuted.WithAlpha(0.45f);
            ink.Apply();
        }
        ListingStyle.SetInteractable(control.Button, enabled);
    }

    private void BuildBody()
    {
        _body = SettingsUI.Child(Slot, "Body", float2.Zero, float2.One,
            new float2(BodyInset, BodyInset), new float2(-BodyInset, -BarHeight));
        // The app stays inside its window whatever size the window is given; anything that does not fit is
        // cut at the frame instead of drawing across the next window.
        _body.AttachComponent<Mask>();
    }

    private void BuildGrip(Dashboard dash)
    {
        var grip = SettingsUI.Child(Slot, "Grip", new float2(1f, 0f), new float2(1f, 0f),
            new float2(-GripSize, 0f), new float2(0f, GripSize));
        grip.AttachComponent<GraphicChunkRoot>();
        var element = grip.AttachComponent<InteractionElement>();
        element.Tip.Value = "Resize";
        element.Pressed += context =>
        {
            _gripStart = _size;
            _gripPoint = context.LocalPoint;
        };
        element.Dragged += context => _workspace?.ResizeTo(this, GripTarget(context), false);
        element.Released += context => _workspace?.ResizeTo(this, GripTarget(context), true);

        var glyph = SettingsUI.Child(grip, "Glyph", new float2(0.5f, 0.5f), new float2(0.5f, 0.5f),
            new float2(-8f, -8f), new float2(6f, 6f));
        var image = glyph.AttachComponent<Image>();
        image.Texture.Target = dash.IconProvider("resize");
        image.PreserveAspect.Value = true;
        image.InteractionTarget.Value = false;
        image.Tint.Value = DashTheme.TextMuted;
    }

    // A strip along each edge, live only where the edge is shared with a tiled neighbour: dragging it moves
    // the line between them, and both windows follow. Lit faintly under the pointer so it can be found.
    private void BuildEdges()
    {
        for (int i = 0; i < 4; i++)
        {
            var edge = (TileEdge)i;
            var (min, max, offMin, offMax) = edge switch
            {
                TileEdge.Left => (float2.Zero, new float2(0f, 1f), float2.Zero, new float2(EdgeStrip, -BarHeight)),
                TileEdge.Right => (new float2(1f, 0f), float2.One, new float2(-EdgeStrip, 0f), new float2(0f, -BarHeight)),
                TileEdge.Top => (new float2(0f, 1f), float2.One, new float2(GripSize, -EdgeStrip), new float2(-(GripSize + 4f * ButtonSize + 3f * ButtonGap + BarPad), 0f)),
                _ => (float2.Zero, new float2(1f, 0f), float2.Zero, new float2(-GripSize, EdgeStrip)),
            };
            var slot = SettingsUI.Child(Slot, "Edge" + edge, min, max, offMin, offMax);
            slot.AttachComponent<GraphicChunkRoot>();
            var paint = SettingsUI.Panel(slot, color.Transparent, color.Transparent, EdgeStrip * 0.5f);
            var element = slot.AttachComponent<InteractionElement>();
            element.HoverEntered += _ => SettingsUI.SetFill(paint, DashTheme.Accent.WithAlpha(0.45f));
            element.HoverExited += _ => SettingsUI.SetFill(paint, color.Transparent);
            element.Dragged += context => DragEdge(edge, context, false);
            element.Released += context =>
            {
                SettingsUI.SetFill(paint, color.Transparent);
                DragEdge(edge, context, true);
            };
            slot.ActiveSelf.Value = false;
            _edges[i] = slot;
        }
    }

    private void DragEdge(TileEdge edge, in UIInteractionContext context, bool final)
    {
        if (_workspace == null || _dash == null)
            return;
        _workspace.DragEdge(this, edge, _dash.TopDown(context.LocalPoint), final);
    }

    // Which edges are shared with a tiled neighbour right now.
    internal void SetSharedEdges(bool left, bool right, bool top, bool bottom)
    {
        SetEdge(TileEdge.Left, left);
        SetEdge(TileEdge.Right, right);
        SetEdge(TileEdge.Top, top);
        SetEdge(TileEdge.Bottom, bottom);
    }

    private void SetEdge(TileEdge edge, bool live)
    {
        var slot = _edges[(int)edge];
        if (slot != null && !slot.IsDestroyed && slot.ActiveSelf.Value != live)
            slot.ActiveSelf.Value = live;
    }

    public bool EdgeLive(TileEdge edge) => _edges[(int)edge] is { IsDestroyed: false } slot && slot.ActiveSelf.Value;

    public Slot? EdgeSlot(TileEdge edge) => _edges[(int)edge];

    // Pointer travel in canvas space, y up; the window grows right and down.
    private float2 GripTarget(in UIInteractionContext context)
    {
        var delta = context.LocalPoint - _gripPoint;
        return new float2(_gripStart.x + delta.x, _gripStart.y - delta.y);
    }

    internal void Attach(DashboardScreen screen)
    {
        Screen = screen;
        if (_close != null)
            ListingStyle.SetInteractable(_close.Button, true);
    }

    internal void SetPosition(in float2 position)
    {
        _position = position;
        ApplyLayer();
    }

    internal void SetSize(in float2 size)
    {
        _size = size;
        if (_rect == null || _rect.IsDestroyed)
            return;
        SettingsUI.SetOffsets(_rect, new float2(0f, -size.y), new float2(size.x, 0f));
        if (_scale != 1f)
            ApplyLayer();
    }

    // The window's middle in canvas units from the canvas centre, y up, before its offset: the point the
    // drawn sheet grows and shrinks about.
    private float2 Pivot()
    {
        var canvas = _dash?.Size.Value ?? float2.Zero;
        return new float2(-canvas.x * 0.5f + _size.x * 0.5f, canvas.y * 0.5f - _size.y * 0.5f);
    }

    // Where the window's middle is drawn when it is at rest, in the same units.
    public float2 DrawnCenter => Pivot() + new float2(_position.x, -_position.y);

    private void ApplyLayer()
    {
        if (_layer == null || _layer.IsDestroyed)
            return;
        var offset = new float2(_position.x, -_position.y) + _shift;
        if (_layer.Offset.Value != offset)
            _layer.Offset.Value = offset;
        if (_layer.Scale.Value != _scale)
            _layer.Scale.Value = _scale;
        if (_scale != 1f)
        {
            var pivot = Pivot();
            if (_layer.Pivot.Value != pivot)
                _layer.Pivot.Value = pivot;
        }
    }

    // MOTION: only the drawn sheet moves, by the layer's scale and an extra slide; the rect and the hit area
    // stay where the window is. done runs once the motion ends, unless a newer one took its place.

    public bool Moving => _motionStart >= 0;

    internal void Animate(float fromScale, in float2 fromShift, float toScale, in float2 toShift, float seconds, Action? done)
    {
        _fromScale = fromScale;
        _toScale = toScale;
        _fromShift = fromShift;
        _toShift = toShift;
        _motionSeconds = seconds;
        _motionDone = done;
        _motionStart = World?.Time.TotalTime ?? 0.0;
        _scale = fromScale;
        _shift = fromShift;
        ApplyLayer();
    }

    internal void CancelMotion()
    {
        _motionStart = -1;
        _motionDone = null;
        _scale = 1f;
        _shift = float2.Zero;
        ApplyLayer();
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        if (_holdStart >= 0 && World != null && World.Time.TotalTime - _holdStart >= HoldSeconds)
        {
            _holdStart = -1;
            _swallowClick = true;
            _dash?.SnapPicker?.Open(this);
        }
        if (_motionStart < 0 || World == null)
            return;
        float t = _motionSeconds <= 0f ? 1f : (float)((World.Time.TotalTime - _motionStart) / _motionSeconds);
        if (t >= 1f)
        {
            _scale = _toScale;
            _shift = _toShift;
            _motionStart = -1;
            ApplyLayer();
            var done = _motionDone;
            _motionDone = null;
            done?.Invoke();
            return;
        }
        float eased = 1f - (1f - t) * (1f - t) * (1f - t);
        _scale = _fromScale + (_toScale - _fromScale) * eased;
        _shift = _fromShift + (_toShift - _fromShift) * eased;
        ApplyLayer();
    }

    // Place in the stack: OrderOffset puts it in tree order for the hit scan (last match wins), the lift
    // puts it in depth for drawing. Both written only on a change: a reorder is a relayout of the host.
    internal void SetStack(long order, float lift, bool focused)
    {
        if (Slot.OrderOffset.Value != order)
            Slot.OrderOffset.Value = order;
        if (_layer != null && !_layer.IsDestroyed && _layer.Lift.Value != lift)
            _layer.Lift.Value = lift;
        IsFocused = focused;
        ApplyFocus(focused);
    }

    private void ApplyFocus(bool focused)
    {
        if (_focusKnown && _focusedShown == focused)
            return;
        _focusKnown = true;
        _focusedShown = focused;
        if (_backing != null && !_backing.IsDestroyed)
            SettingsUI.SetPaint(_backing, DashTheme.GlassWindow, focused ? FocusEdge : DashTheme.OutlineStrong, focused ? 1.5f : DashTheme.OutlineWidth);
        if (_title != null && !_title.IsDestroyed)
        {
            var ink = focused ? DashTheme.Text : DashTheme.TextDim;
            if (!_title.Color.Value.Equals(ink))
                _title.Color.Value = ink;
        }
    }

    internal void SetShown(bool shown)
    {
        if (Slot.ActiveSelf.Value != shown)
            Slot.ActiveSelf.Value = shown;
    }

    internal void SetFilledGlyph(bool filled)
    {
        if (_fill?.Glyph is { IsDestroyed: false } glyph && _dash != null)
        {
            var target = _dash.IconProvider(filled ? "restore" : "maximize");
            if (!ReferenceEquals(glyph.Texture.Target, target))
                glyph.Texture.Target = target;
        }
        if (_fill?.Button is { IsDestroyed: false } button)
        {
            string tip = filled ? "Restore" : "Fill";
            if (button.Tip.Value != tip)
                button.Tip.Value = tip;
        }
    }

    internal void SetPopAvailable(bool available)
    {
        if (_pop == null)
            return;
        if (_pop.Button.Interactable.Value == available)
            return;
        PaintControl(_pop, available, DashTheme.AccentSoft, DashTheme.Accent);
    }
}
