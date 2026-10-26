// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Phos;
using Helio.UI;

namespace Lumora.Core.Components;

// Text in the world: nameplates, labels, indicators.
// Text is engine-generated geometry, not a platform widget: glyph quads are
// emitted into the PhosMesh (shared TextShaper + font atlas pipeline, same as
// Helio UI text) and rendered through the standard MeshRenderer/material
// chain with a depth-tested TextMaterial. The platform layer never knows
// "text" exists - it uploads a mesh like any other. Each peer builds its own
// local renderer slot; only the sync fields replicate. - xlinka
[ComponentCategory("Rendering")]
public class TextRenderer : ProceduralMesh, IPrimaryColorSource
{
    // Newlines start new lines. There is no wrap here, only the breaks you type.
    public readonly Sync<string> Text;

    // The font size in metres: an em, the height the font's letters are designed in.
    public readonly Sync<float> Size;

    // vertex colour on every glyph
    public readonly Sync<color> Color;

    // Shaping and the glyph atlas both come from here. With no font this renders NOTHING.
    public readonly AssetRef<FontSet> Font;

    public readonly Sync<TextHorizontalAlignment> HorizontalAlign;
    public readonly Sync<TextVerticalAlignment> VerticalAlign;
    public readonly Sync<float> LineSpacing;

    // forwarded to the text material; transparent = no outline
    public readonly Sync<colorHDR> OutlineColor;

    // atlas texels, 0 = none; what makes floating text readable over anything
    public readonly Sync<float> OutlineThickness;

    // a band of light sweeping across the letters, 0 = none; forwarded to the text material
    public readonly Sync<float> Shimmer;
    public readonly Sync<colorHDR> ShimmerColor;

    // Metres past which this label stops drawing. 0 = draws at any distance, which is what a nameplate
    // wants. A sign bolted to a wall does not: the glyphs are sub-pixel long before you run out of world
    // to walk backwards into, and every one of them is still a mesh being drawn. Goes onto the child
    // renderer as a Godot visibility range with a fade band, so it dissolves rather than pops. -xlinka
    public readonly Sync<float> MaxViewDistance;

    // A text material whose look every page of this text copies: tint, outline, face, culling, depth, draw
    // order and stencil. The atlas each page binds stays its own. Empty: the outline above and the defaults.
    // One style can dress any number of texts. -xlinka
    public readonly SyncRef<TextMaterial> Style;

    // Read from the slot's backward (-Z) side instead of its forward (+Z) one: the glyphs turn half a turn
    // about the slot's up axis. Text brought in from a package reads this way.
    public readonly Sync<bool> FaceBackward;

    // Inline tags (<color>, <b>, <i>, <size>, <sub>, <align>, <line-height>, <br> ...) are read, as Helio text
    // reads them (RichTextParser). Off, the text draws as written.
    public readonly Sync<bool> RichText;

    // Drawn while Text is null, its tags read whatever RichText says.
    public readonly Sync<string> NullText;

    // The block the vertical alignment lines up with the slot's origin (TextBlockMeasure).
    public readonly Sync<TextBlockMeasure> BlockMeasure;

    // Lines measured by each font's full line height, its line gap included, and as tall as the tallest font
    // any glyph on the line came from. Off, every line takes the first font's ascent plus descent.
    public readonly Sync<bool> FullLineHeight;

    public const float ViewDistanceFadeMargin = 3f;

    // Width and height of the laid-out text in metres, local space, filled in during meshing. Zero with no
    // text or no font. Fit a backing panel to this rather than guessing at a size.
    public float2 RenderedSize { get; private set; }

    private readonly TextShaper _shaper = new();

    // Shaped state captured on the engine thread for mesh emission.
    private string _text = string.Empty;
    private float _size;
    private color _color;
    private TextHorizontalAlignment _hAlign;
    private TextVerticalAlignment _vAlign;
    private float _lineSpacing;
    private bool _faceBackward;
    private bool _richText;
    private TextBlockMeasure _blockMeasure;
    private bool _fullLineHeight;
    private FontSet? _fontSet;

    // Rich-text reading, per shaped character, reused every regen.
    private readonly StringBuilder _richBuilder = new();
    private readonly List<color> _colors = new();
    private readonly List<byte> _styles = new();
    private readonly List<float> _sizes = new();
    private readonly List<color> _marks = new();
    private readonly List<string?> _sprites = new();
    private readonly List<bool> _nobr = new();
    private readonly List<RichTextParser.AlignMark> _alignMarks = new();
    private readonly List<RichTextParser.LineHeightMark> _lineHeightMarks = new();
    private readonly List<RichTextParser.FontMark> _fontMarks = new();
    // Per line of the last regen: its height and how far its baseline sits below its top.
    private readonly List<float> _lineHeights = new();
    private readonly List<float> _lineDrops = new();
    private TextMaterial? _watchedStyle;
    private Action<TextMaterial>? _onStyleChanged;

    // Font arrival/atlas growth detection (assets load async).
    private FontSet? _watchedFont;
    private int _watchedGeneration = -1;

    private Slot _rendererSlot = null!;
    private MeshRenderer _renderer = null!;
    private TextMaterial _material = null!;

    // Multi-atlas glyph routing. Text that mixes scripts (Latin + CJK + emoji) pulls
    // glyphs from several font atlases, and a material binds exactly one atlas, so each
    // atlas needs its own submesh + material. _materialPool lives on the renderer slot
    // and grows to the high-water-mark of distinct atlases ever seen - it's reused every
    // regen so typing doesn't leak a component per keystroke. _atlasToSubmesh + the
    // ordered _regenAtlases are rebuilt each UpdateMeshData; submesh index i lines up
    // with _materialPool[i] and Materials[i]. _material stays pool[0]/Materials[0], so
    // ordinary single-atlas text is byte-identical to before. - xlinka
    private readonly List<TextMaterial> _materialPool = new();
    private readonly Dictionary<TextureAsset, PhosTriangleSubmesh> _atlasToSubmesh = new();
    private readonly List<TextureAsset> _regenAtlases = new();

    public TextRenderer()
    {
        Text = new Sync<string>(this, string.Empty);
        Size = new Sync<float>(this, 0.1f);
        Color = new Sync<color>(this, Lumora.Core.Math.color.White);
        Font = new AssetRef<FontSet>(this);
        HorizontalAlign = new Sync<TextHorizontalAlignment>(this, TextHorizontalAlignment.Center);
        VerticalAlign = new Sync<TextVerticalAlignment>(this, TextVerticalAlignment.Middle);
        LineSpacing = new Sync<float>(this, 1f);
        OutlineColor = new Sync<colorHDR>(this, new colorHDR(0f, 0f, 0f, 0f));
        OutlineThickness = new Sync<float>(this, 0f);
        Shimmer = new Sync<float>(this, 0f);
        ShimmerColor = new Sync<colorHDR>(this, colorHDR.White);
        MaxViewDistance = new Sync<float>(this, 0f);
        Style = new SyncRef<TextMaterial>(this);
        FaceBackward = new Sync<bool>(this, false);
        RichText = new Sync<bool>(this, false);
        NullText = new Sync<string>(this, string.Empty);
        BlockMeasure = new Sync<TextBlockMeasure>(this, TextBlockMeasure.LineBoxes);
        FullLineHeight = new Sync<bool>(this, false);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        SubscribeToChanges(Text);
        SubscribeToChanges(Size);
        SubscribeToChanges(Color);
        SubscribeToChanges(HorizontalAlign);
        SubscribeToChanges(VerticalAlign);
        SubscribeToChanges(LineSpacing);
        SubscribeToChanges(FaceBackward);
        SubscribeToChanges(RichText);
        SubscribeToChanges(NullText);
        SubscribeToChanges(BlockMeasure);
        SubscribeToChanges(FullLineHeight);
        // Outline is a material parameter, not geometry - forward to the material, don't re-mesh.
        OutlineColor.OnChanged += _ => ApplyOutline();
        OutlineThickness.OnChanged += _ => ApplyOutline();
        Shimmer.OnChanged += _ => ApplyOutline();
        ShimmerColor.OnChanged += _ => ApplyOutline();
        MaxViewDistance.OnChanged += _ => ApplyViewDistance();
        Style.OnTargetChange += _ => WatchStyle();
    }

    public override void OnDestroy()
    {
        if (_watchedStyle != null && _onStyleChanged != null)
            _watchedStyle.LookChanged -= _onStyleChanged;
        _watchedStyle = null;
        base.OnDestroy();
    }

    // The style is a component of its own that changes on its own; its pages follow it by its change event.
    private void WatchStyle()
    {
        var style = Style.Target;
        if (!ReferenceEquals(style, _watchedStyle))
        {
            _onStyleChanged ??= _ => ApplyOutline();
            if (_watchedStyle != null)
                _watchedStyle.LookChanged -= _onStyleChanged;
            _watchedStyle = style;
            if (style != null)
                style.LookChanged += _onStyleChanged;
        }
        ApplyOutline();
    }

    // Outline is a material parameter shared by every atlas material, so push it to the
    // whole pool (not just _material) when it changes. Safe when the pool is empty (the
    // renderer isn't built yet) - the loop just no-ops. - xlinka
    private void ApplyOutline()
    {
        for (int i = 0; i < _materialPool.Count; i++)
        {
            var m = _materialPool[i];
            if (m == null || m.IsDestroyed)
                continue;
            ApplyLook(m);
        }
    }

    // What a page wears besides its atlas: the style's look, or with no style the outline set here and the
    // defaults for the rest.
    private void ApplyLook(TextMaterial m)
    {
        var style = Style.Target;
        if (style != null && style.IsDestroyed)
            style = null;
        m.TintColor.Value = style?.TintColor.Value ?? colorHDR.White;
        m.OutlineColor.Value = style?.OutlineColor.Value ?? OutlineColor.Value;
        m.OutlineThickness.Value = style?.OutlineThickness.Value ?? OutlineThickness.Value;
        m.FaceDilate.Value = style?.FaceDilate.Value ?? 0f;
        m.FaceSoftness.Value = style?.FaceSoftness.Value ?? 0f;
        m.Shimmer.Value = style?.Shimmer.Value ?? Shimmer.Value;
        m.ShimmerColor.Value = style?.ShimmerColor.Value ?? ShimmerColor.Value;
        m.AlphaClip.Value = style?.AlphaClip.Value ?? true;
        m.AlphaCutoff.Value = style?.AlphaCutoff.Value ?? 0.5f;
        m.BlendMode.Value = style?.BlendMode.Value ?? BlendMode.Alpha;
        m.Culling.Value = style?.Culling.Value ?? Culling.None;
        m.ZWrite.Value = style?.ZWrite.Value ?? ZWrite.Auto;
        m.ZTest.Value = style?.ZTest.Value ?? ZTest.LessOrEqual;
        m.RenderQueue.Value = style?.RenderQueue.Value ?? 0;
        m.ColorMask.Value = style?.ColorMask.Value ?? ColorMask.RGBA;
        m.StencilComparison.Value = style?.StencilComparison.Value ?? StencilComparison.Always;
        m.StencilOperation.Value = style?.StencilOperation.Value ?? StencilOperation.Keep;
        m.StencilID.Value = style?.StencilID.Value ?? (byte)0;
        m.StencilWriteMask.Value = style?.StencilWriteMask.Value ?? byte.MaxValue;
        m.StencilReadMask.Value = style?.StencilReadMask.Value ?? byte.MaxValue;
        m.OffsetFactor.Value = style?.OffsetFactor.Value ?? 0f;
        m.OffsetUnits.Value = style?.OffsetUnits.Value ?? 0f;
        m.AutoBackgroundColor.Value = style?.AutoBackgroundColor.Value ?? true;
        m.BackgroundColor.Value = style?.BackgroundColor.Value ?? new colorHDR(0f, 0f, 0f, 0f);
    }

    public override void OnStart()
    {
        EnsureLocalRenderer();
        base.OnStart();
        // A style loaded with the text was set before anything listened for it.
        WatchStyle();
    }

    // Fonts load asynchronously and the atlas fills lazily; poll the asset
    // ref and atlas generation, regenerating when either moves. Cheap when
    // idle (two reference compares + an int). - xlinka
    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();

        var font = Font.Target?.Asset;
        int generation = font?.CacheGeneration ?? -1;
        if (!ReferenceEquals(font, _watchedFont) || generation != _watchedGeneration)
        {
            _watchedFont = font;
            _watchedGeneration = generation;
            RegenerateMesh();
        }
    }

    private void EnsureLocalRenderer()
    {
        if (_rendererSlot != null && !_rendererSlot.IsDestroyed)
            return;

        _rendererSlot = Slot.AddLocalSlot("TextRenderer");
        _material = _rendererSlot.AttachComponent<TextMaterial>();
        _renderer = _rendererSlot.AttachComponent<MeshRenderer>();
        _renderer.Mesh.Target = this;
        _renderer.Material.Target = _material; // Materials[0] = _material (the first atlas)

        // Seed the pool with the primary material so pool index 0 == Materials[0]; any
        // extra atlases attach more materials lazily during meshing. - xlinka
        _materialPool.Clear();
        _materialPool.Add(_material);
        ApplyOutline();

        // A label is a flat sheet of glyph quads with an alpha-cut texture on it. In a shadow cascade that
        // is either nothing at all or a solid black rectangle where the text should be, and either way it
        // is a second pass over every glyph in the world for a silhouette nobody wants. -xlinka
        _renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        ApplyViewDistance();
    }

    private void ApplyViewDistance()
    {
        if (_renderer == null || _renderer.IsDestroyed)
            return;
        float distance = MathF.Max(0f, MaxViewDistance.Value);
        float margin = distance > 0f ? ViewDistanceFadeMargin : 0f;
        if (_renderer.MaxViewDistance == distance && _renderer.ViewDistanceFadeMargin == margin)
            return;
        _renderer.MaxViewDistance = distance;
        _renderer.ViewDistanceFadeMargin = margin;
        _renderer.MarkChangeDirty();
    }

    protected override void PrepareAssetUpdateData()
    {
        _text = Text.Value ?? NullText.Value ?? string.Empty;
        _richText = RichText.Value || Text.Value == null;
        _blockMeasure = BlockMeasure.Value;
        _fullLineHeight = FullLineHeight.Value;
        _size = Size.Value;
        _color = Color.Value;
        _hAlign = HorizontalAlign.Value;
        _vAlign = VerticalAlign.Value;
        _lineSpacing = LineSpacing.Value;
        _faceBackward = FaceBackward.Value;
        _fontSet = Font.Target?.Asset;
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        uploadHint.SetAll();

        mesh.Clear(); // also empties Submeshes; per-atlas submeshes are created lazily below
        mesh.HasColors = true;
        mesh.SetHasUV(0, true);
        _atlasToSubmesh.Clear();
        _regenAtlases.Clear();

        var font = _fontSet;
        if (font == null || !font.IsValid || string.IsNullOrEmpty(_text))
        {
            RenderedSize = float2.Zero;
            return; // empty mesh (Clear left 0 submeshes); nothing renders
        }

        string shaped = _text;
        IReadOnlyList<float>? sizeArg = null;
        IReadOnlyList<bool>? nobrArg = null;
        bool anyBold = false;
        if (_richText)
        {
            RichTextParser.Parse(_text, _color, _richBuilder, _colors, _styles, _sizes, _marks, _sprites, _alignMarks, _lineHeightMarks, _fontMarks, _size);
            shaped = _richBuilder.ToString();
            bool sized = false, unbroken = false;
            _nobr.Clear();
            for (int i = 0; i < _styles.Count; i++)
            {
                sized |= _sizes[i] != 1f;
                bool nb = (_styles[i] & RichTextParser.StyleNoBreak) != 0;
                _nobr.Add(nb);
                unbroken |= nb;
                anyBold |= (_styles[i] & RichTextParser.StyleBold) != 0;
            }
            sizeArg = sized ? _sizes : null;
            nobrArg = unbroken ? _nobr : null;
        }
        if (string.IsNullOrEmpty(shaped))
        {
            RenderedSize = float2.Zero;
            return;
        }
        // A bold glyph pushes its distance-field edge out by its UV1.x (Text_Unlit).
        if (anyBold)
            mesh.SetHasUV(1, true);

        TextShaper.RequestGlyphs(font, shaped, _size);
        _shaper.Shape(font, shaped, _size, wrap: false, maxWidth: 0f, sizeArg, nobrArg);
        var lines = _shaper.Lines;
        if (lines.Count == 0)
        {
            RenderedSize = float2.Zero;
            return; // empty mesh; nothing renders
        }

        MeasureLines(font, lines);

        float maxLineWidth = 0f;
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Width > maxLineWidth)
                maxLineWidth = lines[i].Width;
        float blockTop = FirstLineTop(lines, out float blockHeight);
        RenderedSize = new float2(maxLineWidth, blockHeight);

        // Route every glyph to the submesh for its own atlas, so glyphs from the font
        // fallback chain (CJK, emoji, symbols) render instead of being dropped. Each
        // distinct atlas gets a submesh + a material bound to it. - xlinka
        float lineTop = blockTop;
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            float penX = LineAlign(line) switch
            {
                TextHorizontalAlignment.Center => -line.Width * 0.5f,
                TextHorizontalAlignment.Right => -line.Width,
                _ => 0f,
            };
            float penY = lineTop - _lineDrops[lineIndex];

            for (int i = 0; i < line.Glyphs.Count; i++)
            {
                var glyph = line.Glyphs[i];
                var atlas = glyph.Font?.AtlasTexture;
                if (atlas == null)
                    continue; // atlas not built yet; a CacheGeneration bump re-drives this

                var submesh = GetOrCreateSubmesh(mesh, atlas);
                if (!_richText)
                {
                    EmitGlyph(mesh, submesh, penX + glyph.X, penY, glyph.Metrics, glyph.UV);
                    continue;
                }
                byte style = GlyphStyle(glyph);
                float glyphSize = GlyphSize(glyph);
                float dilate = (style & RichTextParser.StyleBold) != 0 && atlas.IsMSDF ? 0.5f / System.Math.Max(atlas.MsdfPixelRange, 1) : 0f;
                EmitStyledGlyph(mesh, submesh, penX + glyph.X, penY + GlyphBaseline(style, glyphSize), glyph.Metrics, glyph.UV,
                    _colors[glyph.SourceIndex], style, dilate, glyphSize);
            }
            lineTop -= _lineHeights[lineIndex];
        }

        BindAtlasMaterials();
    }

    // Each line's height and the drop from its top to its baseline. A line is as tall as its largest glyph
    // (<size>) times its line spacing or its <line-height>, and with FullLineHeight as tall as the tallest font
    // its glyphs came from, line gap included; what a taller font adds sits above the baseline. -xlinka
    private void MeasureLines(FontSet font, IReadOnlyList<TextShaper.Line> lines)
    {
        _lineHeights.Clear();
        _lineDrops.Clear();
        float ascent = font.GetAscent(_size);
        float first = _fullLineHeight && font.Fonts.Count > 0 ? FullHeight(font.Fonts[0]) : font.GetLineHeight(_size);
        if (first <= 0f)
            first = _size;
        for (int l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            float tallest = first;
            if (_fullLineHeight)
            {
                FontAsset? last = null;
                for (int i = 0; i < line.Glyphs.Count; i++)
                {
                    var glyphFont = line.Glyphs[i].Font;
                    if (glyphFont == null || ReferenceEquals(glyphFont, last))
                        continue;
                    last = glyphFont;
                    tallest = MathF.Max(tallest, FullHeight(glyphFont));
                }
            }
            float height = tallest * LineSpacingFor(line) * line.MaxSizeScale;
            _lineHeights.Add(height);
            float extra = tallest > first ? height * (1f - first / tallest) : 0f;
            _lineDrops.Add(ascent * line.MaxSizeScale + extra);
        }
    }

    private float FullHeight(FontAsset font)
    {
        var url = font.AssetURL;
        float gap = url != null && url.IsFile ? FontLineGap.PerEm(url.LocalPath) * _size : 0f;
        return font.GetLineHeight(_size) + gap;
    }

    // The line spacing on a line: its <line-height>, else LineSpacing.
    private float LineSpacingFor(TextShaper.Line line)
    {
        if (!_richText || _lineHeightMarks.Count == 0)
            return _lineSpacing;
        int index = line.Glyphs.Count > 0 ? line.Glyphs[0].SourceIndex : -1;
        float height = 0f;
        for (int i = 0; i < _lineHeightMarks.Count && _lineHeightMarks[i].Index <= index; i++)
            height = _lineHeightMarks[i].Height;
        return height > 0f ? height : _lineSpacing;
    }

    // The alignment on a line: its <align>, else HorizontalAlign. Unbounded text has no width to justify across.
    private TextHorizontalAlignment LineAlign(TextShaper.Line line)
    {
        var align = _hAlign;
        if (_richText && _alignMarks.Count > 0)
        {
            int index = line.Glyphs.Count > 0 ? line.Glyphs[0].SourceIndex : -1;
            byte a = 0;
            for (int i = 0; i < _alignMarks.Count && _alignMarks[i].Index <= index; i++)
                a = _alignMarks[i].Align;
            if (a > 0)
                align = (TextHorizontalAlignment)(a - 1);
        }
        return align == TextHorizontalAlignment.Justify ? TextHorizontalAlignment.Left : align;
    }

    // Where the first line's top sits for the vertical alignment about the origin, and the height of the block
    // that alignment lines up: the lines' boxes, the glyphs drawn (a line with none counts from three quarters
    // of a line above its baseline) or one line height above the first baseline down to the last baseline. -xlinka
    private float FirstLineTop(IReadOnlyList<TextShaper.Line> lines, out float blockHeight)
    {
        float top = 0f, bottom = 0f, lineTop = 0f;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            float baseline = lineTop - _lineDrops[i];
            bool first = i == 0, last = i == lines.Count - 1;
            if (_blockMeasure == TextBlockMeasure.Baselines)
            {
                if (first) top = baseline + _lineHeights[i];
                if (last) bottom = baseline;
            }
            else if (_blockMeasure == TextBlockMeasure.Glyphs && (first || last))
            {
                float high = float.NegativeInfinity, low = float.PositiveInfinity;
                for (int g = 0; g < line.Glyphs.Count; g++)
                {
                    var glyph = line.Glyphs[g];
                    if (glyph.Metrics.Size.y <= 0f || glyph.Codepoint == TextShaper.SpriteGlyph)
                        continue;
                    float y = baseline + (_richText ? GlyphBaseline(GlyphStyle(glyph), GlyphSize(glyph)) : 0f) + glyph.Metrics.Offset.y;
                    high = MathF.Max(high, y + glyph.Metrics.Size.y);
                    low = MathF.Min(low, y);
                }
                bool drawn = high > low;
                if (first) top = drawn ? high : baseline + _lineHeights[i] * 0.75f;
                if (last) bottom = drawn ? low : baseline;
            }
            lineTop -= _lineHeights[i];
        }
        if (_blockMeasure == TextBlockMeasure.LineBoxes)
            bottom = lineTop;

        blockHeight = top - bottom;
        return _vAlign switch
        {
            TextVerticalAlignment.Middle => -(top + bottom) * 0.5f,
            TextVerticalAlignment.Bottom => -bottom,
            _ => -top,
        };
    }

    private byte GlyphStyle(in TextShaper.PositionedGlyph glyph)
        => (uint)glyph.SourceIndex < (uint)_styles.Count ? _styles[glyph.SourceIndex] : (byte)0;

    private float GlyphSize(in TextShaper.PositionedGlyph glyph)
        => (uint)glyph.SourceIndex < (uint)_sizes.Count ? _size * _sizes[glyph.SourceIndex] : _size;

    // Superscript rides up, subscript drops, by a share of the size of the text around them.
    private static float GlyphBaseline(byte style, float glyphSize)
    {
        if ((style & (RichTextParser.StyleSup | RichTextParser.StyleSub)) == 0)
            return 0f;
        float around = glyphSize / RichTextParser.SubSupScale;
        return (style & RichTextParser.StyleSup) != 0 ? around * RichTextParser.SupRise : -around * RichTextParser.SubDrop;
    }

    private void EmitGlyph(PhosMesh mesh, PhosTriangleSubmesh submesh, float penX, float penY, in GlyphMetrics metrics, in Rect uv)
        => EmitStyledGlyph(mesh, submesh, penX, penY, metrics, uv, _color, 0, 0f, _size);

    private void EmitStyledGlyph(PhosMesh mesh, PhosTriangleSubmesh submesh, float penX, float penY, in GlyphMetrics metrics, in Rect uv,
        in color glyphColor, byte style, float boldDilate, float glyphSize)
    {
        WriteGlyphQuad(mesh, submesh, penX, penY, metrics, uv, glyphColor, style, boldDilate);
        // A coverage atlas has no edge to push, so a bold glyph there is drawn twice, a little apart.
        if ((style & RichTextParser.StyleBold) != 0 && boldDilate <= 0f)
            WriteGlyphQuad(mesh, submesh, penX + glyphSize * 0.04f, penY, metrics, uv, glyphColor, style, 0f);
    }

    private void WriteGlyphQuad(PhosMesh mesh, PhosTriangleSubmesh submesh, float penX, float penY, in GlyphMetrics metrics, in Rect uv,
        in color glyphColor, byte style, float boldDilate)
    {
        float xMin = penX + metrics.Offset.x;
        float yMin = penY + metrics.Offset.y;
        float xMax = xMin + metrics.Size.x;
        float yMax = yMin + metrics.Size.y;
        // Italic leans the top right by a fifth of the height above the baseline.
        float skew = (style & RichTextParser.StyleItalic) != 0 ? 0.2f : 0f;
        float topShift = skew * (yMax - penY), bottomShift = skew * (yMin - penY);
        float x0 = xMin + topShift, x1 = xMax + topShift, x2 = xMax + bottomShift, x3 = xMin + bottomShift;
        // Turned half a turn about Y: in the z = 0 plane that is X running the other way, and the side the
        // glyphs read from (the winding) comes round with it.
        if (_faceBackward)
        {
            x0 = -x0;
            x1 = -x1;
            x2 = -x2;
            x3 = -x3;
        }

        int v0 = mesh.VertexCount;
        mesh.IncreaseVertexCount(4);

        mesh.RawPositions[v0 + 0] = new float3(x0, yMax, 0f);
        mesh.RawPositions[v0 + 1] = new float3(x1, yMax, 0f);
        mesh.RawPositions[v0 + 2] = new float3(x2, yMin, 0f);
        mesh.RawPositions[v0 + 3] = new float3(x3, yMin, 0f);

        mesh.RawColors[v0 + 0] = glyphColor;
        mesh.RawColors[v0 + 1] = glyphColor;
        mesh.RawColors[v0 + 2] = glyphColor;
        mesh.RawColors[v0 + 3] = glyphColor;

        // Godot UV is Y-down: the screen-TOP vertex samples the atlas-top row
        // (smaller V) - same convention as Helio Text glyph emission. - xlinka
        mesh.SetUV(0, v0 + 0, new float2(uv.xMin, uv.yMin));
        mesh.SetUV(0, v0 + 1, new float2(uv.xMax, uv.yMin));
        mesh.SetUV(0, v0 + 2, new float2(uv.xMax, uv.yMax));
        mesh.SetUV(0, v0 + 3, new float2(uv.xMin, uv.yMax));

        if (mesh.HasUV1s)
        {
            var dilate = new float2(boldDilate, 0f);
            for (int k = 0; k < 4; k++)
                mesh.SetUV(1, v0 + k, dilate);
        }

        submesh.AddQuadAsTriangles(v0, v0 + 1, v0 + 2, v0 + 3);
    }

    // Return the submesh batching glyphs for `atlas`, creating it on first use this
    // regen. Submeshes are added in first-seen order and recorded in _regenAtlases, so
    // submesh index i == _regenAtlases[i] == the material it binds. - xlinka
    private PhosTriangleSubmesh GetOrCreateSubmesh(PhosMesh mesh, TextureAsset atlas)
    {
        if (_atlasToSubmesh.TryGetValue(atlas, out var existing))
            return existing;

        var submesh = new PhosTriangleSubmesh(mesh);
        mesh.Submeshes.Add(submesh);
        _atlasToSubmesh[atlas] = submesh;
        _regenAtlases.Add(atlas);
        return submesh;
    }

    // Bind one material per atlas submesh, index-aligned: surface i renders with
    // Materials[i], whose DirectTexture is _regenAtlases[i]. The pool only grows, so
    // Materials may hold more entries than we used this regen - those trailing ones map
    // to no surface (the mesh has none past _regenAtlases.Count, and the hook clamps
    // surface->material), so they're harmless and we never have to trim the list. - xlinka
    private void BindAtlasMaterials()
    {
        // A join/decode can fire RegenerateMesh (via a Sync change during Decode) BEFORE
        // OnStart/EnsureLocalRenderer runs, so the renderer slot + pool don't exist yet.
        // Skip binding then - the geometry still builds, and the first post-start update
        // re-drives and binds. Mirrors the old UpdateAtlasBinding null-material guard, and
        // avoids mutating the data model (AttachComponent) mid-decode. - xlinka
        if (_rendererSlot == null || _rendererSlot.IsDestroyed || _materialPool.Count == 0)
            return;

        for (int i = 0; i < _regenAtlases.Count; i++)
        {
            var material = AcquireMaterial(i);
            if (material == null || material.IsDestroyed)
                continue;

            var atlas = _regenAtlases[i];
            material.DirectTexture = atlas;
            // Match the shader path to the atlas: an MSDF atlas (distance field in RGB, opaque alpha) must use
            // the median-reconstruction branch, or the coverage-in-alpha path fills the glyph quads solid. Set
            // per atlas because a fallback chain can mix MSDF and coverage atlases. - xlinka
            material.UseMSDF.Value = atlas.IsMSDF;
            material.PixelRange.Value = atlas.MsdfPixelRange;
            ApplyLook(material);
            material.ForceUpdate();
        }
    }

    // Pooled material for submesh index `i`. Reuses an existing one (zero allocation on
    // the common keystroke path); otherwise attaches a new TextMaterial to the renderer
    // slot and appends a matching Materials entry, keeping pool[i] == Materials[i]. - xlinka
    private TextMaterial AcquireMaterial(int index)
    {
        if (index < _materialPool.Count)
            return _materialPool[index];

        var material = _rendererSlot.AttachComponent<TextMaterial>();
        _materialPool.Add(material);
        _renderer.Materials.Add().Target = material;
        return material;
    }

    protected override void ClearMeshData()
    {
        // Drop per-regen routing, but keep the material pool (reused next regen). - xlinka
        _atlasToSubmesh.Clear();
        _regenAtlases.Clear();
    }

    public void SetText(string text)
    {
        Text.Value = text ?? string.Empty;
    }

    // The glyph colour is baked into the mesh's vertex colours, so the TextMaterial on the child
    // renderer slot is a white passthrough and reading THAT would hand back white for every label.
    // This field is the one that carries the colour. -xlinka
    public bool TryGetPrimaryColor(out colorHDR color)
    {
        color = Color.Value;
        return true;
    }
}
