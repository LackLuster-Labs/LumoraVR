// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;

namespace Lumora.Godot.UI;

/// <summary>
/// Godot Control that renders the radial context menu.
///
/// Draws arc segments procedurally using _Draw(). Each segment represents
/// one ContextMenuItem. Supports open/close animation, hover highlight,
/// icon + label rendering, and center-disc tap to close.
///
/// Usage:
///   view.SetItems(dataList, "Title");
///   view.AnimateOpen();
///   view.ItemSelected   += i => system.SelectItem(page.Items[i]);
///   view.CloseRequested += () => system.Close();
/// </summary>
[Tool]
public partial class ContextMenuView : Control
{
    // ── Configuration (tweakable from inspector or ContextMenuHook) ────────────

    [Export] public Color BaseColor { get; set; } = new(0.10f, 0.10f, 0.10f, 0.90f);
    [Export] public Color HoverColor { get; set; } = new(0.24f, 0.24f, 0.24f, 0.96f);
    [Export] public Color DisabledColor { get; set; } = new(0.07f, 0.07f, 0.07f, 0.60f);
    [Export] public Color TextColor { get; set; } = Colors.White;
    [Export] public Color CenterColor { get; set; } = new(0.05f, 0.05f, 0.05f, 0.92f);
    [Export] public float CenterRadius { get; set; } = 24f;
    [Export] public float OutlineWidth { get; set; } = 2.0f;
    [Export] public int ArcResolution { get; set; } = 28;   // polygon vertices per arc face
    [Export] public float OpenSpeed { get; set; } = 10f;  // animation rate (units/sec)

    // ── Events ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when the user clicks an item. Argument is the index into the
    /// list passed to SetItems() — pass to ContextMenuSystem.SelectItem().
    /// </summary>
    public event Action<int>? ItemSelected;

    /// <summary>
    /// Fired when the user clicks the center disc or right-clicks.
    /// The caller should call ContextMenuSystem.Close() or PopPage().
    /// </summary>
    public event Action? CloseRequested;

    // ── Internal state ─────────────────────────────────────────────────────────

    private readonly List<ViewItem> _items = new();
    private int _hoveredIndex = -1;
    private float _openProgress = 0f;  // 0 = closed, 1 = fully open
    private bool _isOpen = false;
    private string _centerLabel = "";

    // ── Per-item cached data ───────────────────────────────────────────────────

    private sealed record ViewItem(
        string Label,
        string? IconPath,
        Color Fill,
        Color Outline,
        Color LabelCol,
        bool IsEnabled,
        bool IsToggled,
        float AngleStartDeg,
        float ArcLengthDeg,
        float RadiusStart,
        float Thickness,
        Texture2D? Icon
    )
    {
        public float AngleStartRad => Mathf.DegToRad(AngleStartDeg);
        public float ArcLengthRad => Mathf.DegToRad(ArcLengthDeg);
        public float AngleEndRad => Mathf.DegToRad(AngleStartDeg + ArcLengthDeg);
        public float AngleMidRad => Mathf.DegToRad(AngleStartDeg + ArcLengthDeg * 0.5f);
        public float RadiusEnd => RadiusStart + Thickness;
        public float RadiusMid => RadiusStart + Thickness * 0.5f;
    }

    // ── Godot lifecycle ────────────────────────────────────────────────────────

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        if (Engine.IsEditorHint())
            QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (Engine.IsEditorHint())
        {
            QueueRedraw();
            return;
        }

        float target = _isOpen ? 1f : 0f;
        float prev = _openProgress;
        _openProgress = Mathf.MoveToward(_openProgress, target, (float)delta * OpenSpeed);

        // Hide after close animation finishes
        if (!_isOpen && _openProgress == 0f && prev > 0f)
            Visible = false;

        if (!Mathf.IsEqualApprox(_openProgress, prev))
            QueueRedraw();
    }

    public override void _Draw()
    {
        if (Engine.IsEditorHint())
        {
            DrawEditorPreview();
            return;
        }

        if (_openProgress <= 0f) return;

        Vector2 center = Size / 2f;
        float s = _openProgress;  // expand scale

        // ── Arc segments ──────────────────────────────────────────────────────
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            bool hovered = i == _hoveredIndex;

            Color fill = !item.IsEnabled ? DisabledColor
                          : hovered ? HoverColor
                          : item.Fill;
            Color outline = item.Outline;

            // Brighten outline on toggle
            if (item.IsToggled)
                outline = new Color(
                    Mathf.Min(outline.R * 2f, 1f),
                    Mathf.Min(outline.G * 2f, 1f),
                    Mathf.Min(outline.B * 2f, 1f),
                    outline.A);

            DrawArcSegment(center,
                item.AngleStartRad, item.ArcLengthRad,
                item.RadiusStart * s, item.Thickness * s,
                fill, outline);

            // Icon + label in the middle of the arc
            Vector2 midPos = center + AngleVec(item.AngleMidRad) * item.RadiusMid * s;
            DrawItemContent(midPos, item, s);
        }

        // ── Center disc (tap to close) ────────────────────────────────────────
        DrawCircle(center, CenterRadius * s, CenterColor);

        if (!string.IsNullOrEmpty(_centerLabel))
        {
            var font = ThemeDB.FallbackFont;
            int fontSize = 10;
            var textSz = font.GetStringSize(_centerLabel, fontSize: fontSize);
            DrawString(font, center - textSz * 0.5f + new Vector2(0, fontSize * 0.35f),
                       _centerLabel, HorizontalAlignment.Left, -1, fontSize,
                       TextColor with { A = s });
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mm)
        {
            UpdateHover(mm.Position);
        }
        else if (@event is InputEventMouseButton btn && btn.Pressed)
        {
            if (btn.ButtonIndex == MouseButton.Left)
                HandleClick(btn.Position);
            else if (btn.ButtonIndex == MouseButton.Right)
                CloseRequested?.Invoke();
        }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Populate the menu with items. Call before AnimateOpen().
    /// </summary>
    public void SetItems(IReadOnlyList<ContextMenuViewData> items, string centerLabel = "")
    {
        _items.Clear();
        _hoveredIndex = -1;
        _centerLabel = centerLabel;

        foreach (var d in items)
        {
            Texture2D? icon = null;
            if (!string.IsNullOrEmpty(d.IconPath) && ResourceLoader.Exists(d.IconPath))
                icon = GD.Load<Texture2D>(d.IconPath);

            _items.Add(new ViewItem(
                d.Label,
                d.IconPath,
                ColorFrom(d.FillColor, 0.12f, 0.12f, 0.12f, 0.9f),
                ColorFrom(d.OutlineColor, 0.45f, 0.45f, 0.45f, 1.0f),
                ColorFrom(d.LabelColor, 1.00f, 1.00f, 1.00f, 1.0f),
                d.IsEnabled,
                d.IsToggled,
                d.AngleStartDeg,
                d.ArcLengthDeg,
                d.RadiusStart,
                d.Thickness,
                icon
            ));
        }

        QueueRedraw();
    }

    /// <summary>Animate the menu open (scale 0 → 1).</summary>
    public void AnimateOpen()
    {
        _isOpen = true;
        _openProgress = 0f;
        Visible = true;
        QueueRedraw();
    }

    /// <summary>Animate the menu closed (scale 1 → 0, then Visible = false).</summary>
    public void AnimateClose()
    {
        _isOpen = false;
    }

    // ── Drawing helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Drawn only in the Godot editor. Shows a 6-segment placeholder so the
    /// scene has a visible preview — replaced entirely by SetItems() at runtime.
    /// </summary>
    private void DrawEditorPreview()
    {
        if (Size.X < 1f || Size.Y < 1f) return;

        Vector2 center = Size / 2f;
        float minHalf = Mathf.Min(Size.X, Size.Y) * 0.5f;
        float innerR = minHalf * 0.30f;
        float thickness = minHalf * 0.32f;

        const int count = 6;
        float sepRad = Mathf.DegToRad(5f);
        float sliceRad = (Mathf.Tau - sepRad * count) / count;

        for (int i = 0; i < count; i++)
        {
            float start = -Mathf.Pi * 0.5f + i * (sliceRad + sepRad) + sepRad * 0.5f;
            float bright = 0.10f + i * 0.025f;
            Color fill = new(bright, bright, bright + 0.03f, 0.85f);
            Color outline = new(0.40f, 0.42f, 0.55f, 1f);
            DrawArcSegment(center, start, sliceRad, innerR, thickness, fill, outline);

            float midAngle = start + sliceRad * 0.5f;
            Vector2 labelPos = center + AngleVec(midAngle) * (innerR + thickness * 0.5f);
            var font = ThemeDB.FallbackFont;
            string text = $"Item {i + 1}";
            var sz = font.GetStringSize(text, fontSize: 9);
            DrawString(font, labelPos - sz * 0.5f + new Vector2(0, 4f),
                       text, HorizontalAlignment.Left, -1, 9,
                       new Color(0.75f, 0.75f, 0.75f, 0.9f));
        }

        DrawCircle(center, CenterRadius, CenterColor);

        var hintFont = ThemeDB.FallbackFont;
        const string hint = "ContextMenu\n(editor preview)";
        DrawString(hintFont, center - new Vector2(52f, 8f),
                   hint, HorizontalAlignment.Left, -1, 9,
                   new Color(0.5f, 0.5f, 0.5f, 0.7f));
    }

    /// <summary>Draw one pie-slice arc segment (filled polygon + outline).</summary>
    private void DrawArcSegment(Vector2 center,
                                float startRad, float arcRad,
                                float innerR, float thickness,
                                Color fill, Color outline)
    {
        float outerR = innerR + thickness;
        float endRad = startRad + arcRad;

        // Build closed polygon: outer arc CW, then inner arc CCW
        var outerPts = ArcPoints(center, startRad, endRad, outerR);
        var innerPts = ArcPoints(center, endRad, startRad, innerR); // reversed

        var poly = new Vector2[outerPts.Length + innerPts.Length];
        outerPts.CopyTo(poly, 0);
        innerPts.CopyTo(poly, outerPts.Length);

        DrawColoredPolygon(poly, fill);
        DrawPolyline(outerPts, outline, OutlineWidth, false);
    }

    private void DrawItemContent(Vector2 pos, ViewItem item, float alpha)
    {
        Color labelCol = item.LabelCol with { A = item.LabelCol.A * alpha };

        // Icon (centered above label when both present)
        if (item.Icon != null)
        {
            const float iconHalfSize = 13f;
            var rect = new Rect2(
                pos - new Vector2(iconHalfSize, iconHalfSize + (string.IsNullOrEmpty(item.Label) ? 0f : 7f)),
                new Vector2(iconHalfSize * 2f, iconHalfSize * 2f));
            DrawTextureRect(item.Icon, rect, false,
                            item.IsEnabled ? labelCol : labelCol with { A = labelCol.A * 0.4f });
        }

        // Label
        if (!string.IsNullOrEmpty(item.Label))
        {
            var font = ThemeDB.FallbackFont;
            int fontSize = 10;
            var textSz = font.GetStringSize(item.Label, fontSize: fontSize);
            float yOffset = item.Icon != null ? 8f : 4f;
            DrawString(font,
                       pos - new Vector2(textSz.X * 0.5f, -yOffset),
                       item.Label, HorizontalAlignment.Left, -1, fontSize,
                       item.IsEnabled ? labelCol : labelCol with { A = labelCol.A * 0.4f });
        }
    }

    /// <summary>Generate arc polygon vertices.</summary>
    private Vector2[] ArcPoints(Vector2 center, float fromRad, float toRad, float radius)
    {
        var pts = new Vector2[ArcResolution + 1];
        for (int i = 0; i <= ArcResolution; i++)
        {
            float t = i / (float)ArcResolution;
            float a = Mathf.Lerp(fromRad, toRad, t);
            pts[i] = center + AngleVec(a) * radius;
        }
        return pts;
    }

    private static Vector2 AngleVec(float radians)
        => new(Mathf.Cos(radians), Mathf.Sin(radians));

    // ── Interaction ────────────────────────────────────────────────────────────

    private void UpdateHover(Vector2 mousePos)
    {
        Vector2 delta = mousePos - Size / 2f;
        float dist = delta.Length();
        float angle = Mathf.Atan2(delta.Y, delta.X);
        int prev = _hoveredIndex;

        _hoveredIndex = -1;

        if (_items.Count > 0)
        {
            float maxR = _items[^1].RadiusEnd * _openProgress;
            if (dist >= CenterRadius && dist <= maxR)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (AngleInRange(angle, _items[i].AngleStartRad, _items[i].ArcLengthRad))
                    {
                        _hoveredIndex = i;
                        break;
                    }
                }
            }
        }

        if (_hoveredIndex != prev)
            QueueRedraw();
    }

    private void HandleClick(Vector2 mousePos)
    {
        Vector2 delta = mousePos - Size / 2f;
        float dist = delta.Length();

        // Tap center = close / go back
        if (dist <= CenterRadius) { CloseRequested?.Invoke(); return; }

        float angle = Mathf.Atan2(delta.Y, delta.X);
        for (int i = 0; i < _items.Count; i++)
        {
            if (AngleInRange(angle, _items[i].AngleStartRad, _items[i].ArcLengthRad)
                && _items[i].IsEnabled)
            {
                ItemSelected?.Invoke(i);
                return;
            }
        }
    }

    /// <summary>Check whether angle falls within [startRad, startRad + arcRad].</summary>
    private static bool AngleInRange(float angle, float startRad, float arcRad)
    {
        float endRad = startRad + arcRad;
        while (angle < startRad) angle += Mathf.Tau;
        while (angle > startRad + Mathf.Tau) angle -= Mathf.Tau;
        return angle <= endRad;
    }

    private static Color ColorFrom(float[]? c, float r, float g, float b, float a)
        => c is { Length: >= 4 } ? new Color(c[0], c[1], c[2], c[3]) : new Color(r, g, b, a);
}

/// <summary>
/// Plain data record passed from ContextMenuHook to ContextMenuView.SetItems().
/// Carries everything the view needs to draw one arc segment.
/// </summary>
public record ContextMenuViewData(
    string Label,
    float[]? FillColor,
    float[]? OutlineColor,
    float[]? LabelColor,
    string? IconPath,
    bool IsEnabled,
    bool IsToggled,
    float AngleStartDeg,
    float ArcLengthDeg,
    float RadiusStart,
    float Thickness
);
