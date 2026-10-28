// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Helio.UI;

// emits one textured quad per glyph laid out within the RectTransform rect.
// Rich text (inline tags) is supported via RichTextParser when RichText is set.
// TODO - xlinka: kerning beyond pair table
[ComponentCategory("UI/Helio/Graphics")]
public class Text : Graphic, ILayoutElement
{
    public readonly Sync<string> Content;
    public readonly AssetRef<FontSet> Font;
    // Resolves inline <sprite=name> tags in rich text. Optional; when unset, sprite placeholders draw nothing. -xlinka
    public readonly SyncRef<SpriteSet> Sprites;
    // Resolves inline <font=name> tags in rich text. Optional; when unset, <font> runs use the default Font. -xlinka
    public readonly SyncRef<FontSetGroup> Fonts;
    public readonly AssetRef<MaterialAsset> Material;
    public readonly Sync<float> Size;
    public readonly Sync<color> Color;
    public readonly Sync<TextHorizontalAlignment> HorizontalAlignment;
    public readonly Sync<TextVerticalAlignment> VerticalAlignment;
    public readonly Sync<float> LineSpacing;
    public readonly Sync<bool> WordWrap;
    // When true, Content is parsed for inline tags (<color>, <alpha>, ...) - see RichTextParser.
    public readonly Sync<bool> RichText;
    // Editing visuals, driven by TextInput. CaretPosition < 0 = no caret (not editing);
    // SelectionStart < 0 = no selection. Indices are into the SHAPED string.
    public readonly Sync<int> CaretPosition;
    public readonly Sync<int> SelectionStart;
    public readonly Sync<color> CaretColor;
    public readonly Sync<color> SelectionColor;
    // Auto-size: shrink the rendered size to fit the rect on the enabled axis/axes, down to AutoSizeMin
    // (Size is the maximum). Off by default, so existing text is unchanged. On an auto-sized axis the
    // text reports no preferred size to layout - it adapts to whatever rect it's given.
    public readonly Sync<bool> AutoSizeHorizontal;
    public readonly Sync<bool> AutoSizeVertical;
    public readonly Sync<float> AutoSizeMin;
    // Opt-in for clipped editors: pan the shaped text to keep its active caret inside this rect.
    // Rendering only; it never changes layout, text, selection indices or the wrapping width.
    public readonly Sync<bool> KeepCaretVisible;
    // Trims what it draws to its own rect, as a Mask on a slot around it would, without that slot: a label
    // that runs long is cut at its edge.
    public readonly Sync<bool> ClipToRect;
    // Off: never hit-tested, so a press on it passes through to whatever lies under it.
    public readonly Sync<bool> InteractionTarget;
    // Shown while Content is null and nobody is editing it: the hint an empty field shows.
    public readonly Sync<string> NullContent;
    // Lines measured by each font's full line height, its line gap included, and as tall as the tallest font the
    // line's glyphs came from, the difference above the baseline: a line carrying a tall fallback symbol grows.
    // Off, every line takes the first font's ascent plus descent.
    public readonly Sync<bool> FullLineHeight;
    // The block the vertical alignment lines up: the lines' boxes, the glyphs drawn, or one line height above
    // the first baseline down to the last (TextBlockMeasure).
    public readonly Sync<TextBlockMeasure> BlockMeasure;
    // How a justified line fills the rect's width (TextJustification).
    public readonly Sync<TextJustification> Justification;
    // A line that runs past the rect is cut at the last glyph that fits and ends in an ellipsis; wrapped, the
    // lines past the rect's height go and the last one kept is cut the same way. Only what is drawn changes:
    // layout still measures the whole text, so a row with the room shows all of it. Never while editing.
    public readonly Sync<bool> Ellipsis;

    private string _content = string.Empty;
    private IAssetProvider<FontSet>? _font;
    private IAssetProvider<MaterialAsset>? _material;
    private float _size;
    private color _color;
    private TextHorizontalAlignment _hAlign;
    private TextVerticalAlignment _vAlign;
    private float _lineSpacing;
    private bool _wrap;
    private bool _richText;
    private int _caretPos;
    private float _caretElapsed;
    private bool _caretVisible = true;
    private bool _drawCaret = true;

    private int _selStart;
    private color _caretColor;
    private color _selColor;
    private bool _autoH;
    private bool _autoV;
    private float _autoMin;
    private bool _keepCaretVisible;
    private bool _clipToRect;
    private bool _ellipsis;
    // The last cut, keyed by what it was worked out for, so an unchanged label reshapes the cut text straight
    // from the shaper's cache instead of shaping the whole text and cutting it again on every re-mesh.
    private string? _cutFor;
    private string? _cutText;
    private float _cutSize;
    private float _cutWidth;
    private float _cutHeight;
    private bool _cutWrap;
    private int _cutGeneration;
    private float2 _editOffset;
    // Text actually fed to the shaper: the raw Content, or the tag-stripped text in rich mode.
    private string _shapedText = string.Empty;
    private readonly List<color> _colors = new();
    private readonly List<byte> _styles = new();
    private readonly List<float> _sizes = new();
    private readonly List<color> _marks = new();
    private readonly List<bool> _nobr = new();
    private readonly List<string?> _sprites = new();
    private readonly List<RichTextParser.AlignMark> _alignMarks = new();
    private readonly List<RichTextParser.LineHeightMark> _lineHeightMarks = new();
    private readonly List<RichTextParser.FontMark> _fontMarks = new();
    private readonly List<(int Index, FontSet? Font)> _resolvedFontMarks = new();
    private System.Collections.Generic.IReadOnlyList<(int Index, FontSet? Font)>? _fontMarksArg;
    private FontSetGroup? _fontGroup;
    private SpriteSet? _spriteSet;
    // Sprite names resolved to (texture, uv) on the MAIN thread; the worker emit reads only this snapshot,
    // never the live slot tree (SpriteSet.Get walks child slots, which is main-thread only). -xlinka
    private readonly Dictionary<string, (IAssetProvider<TextureAsset> texture, Rect uv, IAssetProvider<MaterialAsset>? material)> _resolvedSprites = new();
    // Width over height of each inline sprite, per shaped character, so a wide emote is not squashed
    // into a square; null when no sprite has a known size yet.
    private readonly List<float> _spriteAspects = new();
    private IReadOnlyList<float>? _spriteAspectsArg;
    private System.Collections.Generic.IReadOnlyList<float>? _sizeArg;
    private System.Collections.Generic.IReadOnlyList<bool>? _nobrArg;
    private readonly StringBuilder _richTextBuilder = new();
    private float _layoutPreferredWidth;
    private float _layoutPreferredHeight;
    private float _layoutMinHeight;
    // Font metrics snapshotted on the main thread (PreGraphicsCompute). The worker positions
    // glyphs from these instead of calling the font hook, which hits Godot's font server. - xlinka
    private float _ascent;
    private float _lineHeight;
    private float _rawLineHeight; // font line height WITHOUT _lineSpacing, for <line-height> overrides
    private bool _perFontLines;
    private TextBlockMeasure _blockMeasure;
    private TextJustification _justification;

    // Shared shaping engine with built-in result caching (see TextShaper).
    private readonly TextShaper _shaper = new();
    // Unwrapped shaping for the natural width (per-metric canvases only).
    private TextShaper? _naturalShaper;

    public Text()
    {
        Content = new Sync<string>(this, string.Empty);
        Font = new AssetRef<FontSet>(this);
        Sprites = new SyncRef<SpriteSet>(this);
        Fonts = new SyncRef<FontSetGroup>(this);
        Material = new AssetRef<MaterialAsset>(this);
        Size = new Sync<float>(this, 16f);
        Color = new Sync<color>(this, Lumora.Core.Math.color.White);
        HorizontalAlignment = new Sync<TextHorizontalAlignment>(this, TextHorizontalAlignment.Left);
        VerticalAlignment = new Sync<TextVerticalAlignment>(this, TextVerticalAlignment.Top);
        LineSpacing = new Sync<float>(this, 1f);
        WordWrap = new Sync<bool>(this, true);
        RichText = new Sync<bool>(this, false);
        CaretPosition = new Sync<int>(this, -1);
        SelectionStart = new Sync<int>(this, -1);
        CaretPosition.MarkUiState();
        SelectionStart.MarkUiState();
        CaretColor = new Sync<color>(this, Lumora.Core.Math.color.White);
        SelectionColor = new Sync<color>(this, new Lumora.Core.Math.color(0.25f, 0.5f, 1f, 0.4f));
        AutoSizeHorizontal = new Sync<bool>(this, false);
        AutoSizeVertical = new Sync<bool>(this, false);
        AutoSizeMin = new Sync<float>(this, 6f);
        KeepCaretVisible = new Sync<bool>(this, false);
        ClipToRect = new Sync<bool>(this, false);
        InteractionTarget = new Sync<bool>(this, true);
        NullContent = new Sync<string>(this, string.Empty);
        FullLineHeight = new Sync<bool>(this, false);
        BlockMeasure = new Sync<TextBlockMeasure>(this, TextBlockMeasure.LineBoxes);
        Justification = new Sync<TextJustification>(this, TextJustification.WordGaps);
        Ellipsis = new Sync<bool>(this, false);
    }

    // Blink locally without changing the selection, layout, or sending a field write each half second. -xlinka
    internal void ResetCaretBlink()
    {
        _caretElapsed = 0f;
        if (_caretVisible)
            return;
        _caretVisible = true;
        RectTransform?.MarkGraphicDirty();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (CaretPosition.Value < 0)
        {
            ResetCaretBlink();
            return;
        }
        _caretElapsed = (_caretElapsed + delta) % 1f;
        bool visible = _caretElapsed < 0.5f;
        if (visible == _caretVisible)
            return;
        _caretVisible = visible;
        RectTransform?.MarkGraphicDirty();
    }

    public override bool RequiresPreGraphicsCompute => true;

    // Glyph quads are trimmed (with their UVs) to RenderData.GeometryClipRect, so text can carry a clip window
    // that rides the chunk - see EmitGlyph.
    public override bool TrimsGeometryToClip => true;

    public float? MinWidth => 0f;
    public float? PreferredWidth => _layoutPreferredWidth;
    public float? FlexibleWidth => 0f;
    public float? MinHeight => _layoutMinHeight;
    public float? PreferredHeight => _layoutPreferredHeight;
    public float? FlexibleHeight => 0f;
    public float? Area => null;
    public int Priority => 0;
    public LayoutMetric ChangedMetrics { get; private set; }

    // The width the wrapped height above was measured at. A layout pass that hands the text another width has
    // placed it with a height for the old one; the canvas measures it again and lays out once more.
    private float _measuredWrapWidth = -1f;

    internal bool MeasuredAtOtherWidth(float width)
        => _wrap && _layoutPreferredHeight > 0f && System.MathF.Abs((width > 0f ? width : 0f) - _measuredWrapWidth) > 0.5f;

    // Signature of the LAYOUT-affecting fields at the last flag, so a pure color change (selection
    // highlight, hover tint) re-meshes the glyphs in place instead of triggering a full relayout.
    private int _layoutSignature = -1;

    private int ComputeLayoutSignature()
    {
        var hash = new System.HashCode();
        hash.Add(Content.Value);
        hash.Add(Content.Value == null ? NullContent.Value : null);
        hash.Add(Size.Value);
        hash.Add(HorizontalAlignment.Value);
        hash.Add(VerticalAlignment.Value);
        hash.Add(LineSpacing.Value);
        hash.Add(WordWrap.Value);
        hash.Add(RichText.Value);
        hash.Add(AutoSizeHorizontal.Value);
        hash.Add(AutoSizeVertical.Value);
        hash.Add(AutoSizeMin.Value);
        hash.Add(FullLineHeight.Value);
        hash.Add(BlockMeasure.Value);
        hash.Add(Justification.Value);
        hash.Add(Ellipsis.Value);
        hash.Add(CaretPosition.Value);
        hash.Add(SelectionStart.Value);
        hash.Add(Font.Target);
        hash.Add(Material.Target);
        return hash.ToHashCode();
    }

    // Atlas glyphs rasterize ASYNCHRONOUSLY: text shaped before its glyphs landed bakes without them, and
    // nothing used to re-drive the canvas when the atlas grew - fresh labels stayed invisible until some
    // unrelated dirty finally rebuilt the chunk (the "flick tabs to make the buttons appear" bug, and why
    // new screens loaded in like dialup). Capture the font generation at shape time and poll it here, same
    // as the world-space text renderer does; a moved generation re-drives this text once. -1 = never
    // shaped, nothing to re-drive. - xlinka
    private int _appliedFontGeneration = -1;

    // Armed when a compute ran with NO valid font: the generation stays -1 so the poll below can
    // never fire, and a font that loads later would leave this text invisible forever (baked empty,
    // chunk latched built). The arm re-drives ONCE when the asset turns valid. - xlinka
    private bool _fontRedriveArmed;

    // Which texts of a world wait on a font. The world's canvases look at each font's generation once a
    // frame and wake only the texts on a font that moved, or armed for one that has not loaded. Every text
    // was a component update a frame to read that number itself: a thousand open graph cards are six
    // thousand texts, and the reads cost a few milliseconds a frame for a number that moves only when a
    // glyph lands. -xlinka
    private sealed class FontGroup
    {
        public int Generation;
        public readonly HashSet<Text> Texts = new();
    }

    private sealed class FontWatch
    {
        public ulong Frame = ulong.MaxValue;
        public readonly Dictionary<FontSet, FontGroup> Groups = new();
        public readonly HashSet<Text> Armed = new();
        public readonly List<Text> Due = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<World, FontWatch> Watches = new();
    private FontSet? _watchedFont;
    private bool _armedWatched;

    // From each canvas's update; the first one of a world's frame does the looking.
    internal static void PollFonts(World? world)
    {
        if (world == null || !Watches.TryGetValue(world, out var watch))
            return;
        ulong frame = world.Time.UpdateIndex;
        if (watch.Frame == frame)
            return;
        watch.Frame = frame;
        watch.Due.Clear();
        foreach (var (font, group) in watch.Groups)
        {
            int generation = font.CacheGeneration;
            if (generation == group.Generation)
                continue;
            group.Generation = generation;
            watch.Due.AddRange(group.Texts);
        }
        watch.Due.AddRange(watch.Armed);
        foreach (var text in watch.Due)
        {
            if (text.IsDestroyed)
                text.Unwatch();
            else
                text.PollFont();
        }
        watch.Due.Clear();
    }

    private void WatchFont(FontSet font)
    {
        if (ReferenceEquals(_watchedFont, font) || World is not { } world)
            return;
        var watch = Watches.GetValue(world, static _ => new FontWatch());
        LeaveGroup(watch);
        if (!watch.Groups.TryGetValue(font, out var group))
        {
            group = new FontGroup { Generation = font.CacheGeneration };
            watch.Groups[font] = group;
        }
        group.Texts.Add(this);
        _watchedFont = font;
    }

    private void WatchArmed(bool armed)
    {
        if (_armedWatched == armed || World is not { } world)
            return;
        var watch = Watches.GetValue(world, static _ => new FontWatch());
        if (armed)
            watch.Armed.Add(this);
        else
            watch.Armed.Remove(this);
        _armedWatched = armed;
    }

    private void LeaveGroup(FontWatch watch)
    {
        if (_watchedFont == null)
            return;
        if (watch.Groups.TryGetValue(_watchedFont, out var group))
        {
            group.Texts.Remove(this);
            if (group.Texts.Count == 0)
                watch.Groups.Remove(_watchedFont);
        }
        _watchedFont = null;
    }

    private void Unwatch()
    {
        if (World is not { } world || !Watches.TryGetValue(world, out var watch))
            return;
        LeaveGroup(watch);
        watch.Armed.Remove(this);
        _armedWatched = false;
    }

    public override void OnDestroying()
    {
        Unwatch();
        base.OnDestroying();
    }

    private void PollFont()
    {
        if (_fontRedriveArmed)
        {
            var pending = Font.Asset;
            if (pending == null || !pending.IsValid)
                return;
            _fontRedriveArmed = false;
            WatchArmed(false);
            ChangedMetrics = LayoutMetric.MinWidth | LayoutMetric.PreferredWidth | LayoutMetric.MinHeight | LayoutMetric.PreferredHeight;
            RectTransform?.MarkChangeDirty();
            return;
        }
        if (_appliedFontGeneration < 0)
            return;
        var fontSet = Font.Asset;
        if (fontSet == null || fontSet.CacheGeneration == _appliedFontGeneration)
            return;
        _appliedFontGeneration = fontSet.CacheGeneration;
        // Glyph arrival changes advances/line widths, so take the content-change path (layout + re-mesh),
        // not the visual-only one - autosized rects grow when the real metrics land.
        ChangedMetrics = LayoutMetric.MinWidth | LayoutMetric.PreferredWidth | LayoutMetric.MinHeight | LayoutMetric.PreferredHeight;
        RectTransform?.MarkChangeDirty();
    }

    protected override void FlagChanges(RectTransform rect)
    {
        int signature = ComputeLayoutSignature();
        if (signature == _layoutSignature)
        {
            // Only a color/visual field changed - re-emit this glyph mesh, don't relayout.
            rect.MarkGraphicDirty();
            return;
        }
        _layoutSignature = signature;
        ChangedMetrics = LayoutMetric.MinWidth | LayoutMetric.PreferredWidth | LayoutMetric.MinHeight | LayoutMetric.PreferredHeight;
        rect.MarkChangeDirty();
    }

    public override void PrepareCompute()
    {
        _content = Content.Value ?? (CaretPosition.Value < 0 ? NullContent.Value : null) ?? string.Empty;
        _font = Font.Target;
        _spriteSet = Sprites.Target; // snapshot on the main thread; ComputeGraphic runs on the worker
        _fontGroup = Fonts.Target;
        _material = Material.Target;
        _size = Size.Value;
        _color = Color.Value;
        _hAlign = HorizontalAlignment.Value;
        _vAlign = VerticalAlignment.Value;
        _lineSpacing = LineSpacing.Value;
        _wrap = WordWrap.Value;
        _richText = RichText.Value;
        _caretPos = CaretPosition.Value;
        _drawCaret = _caretVisible;
        _selStart = SelectionStart.Value;
        _caretColor = CaretColor.Value;
        _selColor = SelectionColor.Value;
        _autoH = AutoSizeHorizontal.Value;
        _autoV = AutoSizeVertical.Value;
        _autoMin = AutoSizeMin.Value;
        _perFontLines = FullLineHeight.Value;
        _blockMeasure = BlockMeasure.Value;
        _justification = Justification.Value;
        _keepCaretVisible = KeepCaretVisible.Value;
        _clipToRect = ClipToRect.Value;
        _ellipsis = Ellipsis.Value;

        // Strip inline tags into _shapedText (+ per-char _colors) so shaping/layout is
        // identical to plain text; plain mode shapes Content directly.
        if (_richText)
        {
            RichTextParser.Parse(_content, _color, _richTextBuilder, _colors, _styles, _sizes, _marks, _sprites, _alignMarks, _lineHeightMarks, _fontMarks, _size);
            _shapedText = _richTextBuilder.ToString();
            _sizeArg = HasSizeVariation() ? _sizes : null; // null keeps the shaper cache for uniform text
            _nobrArg = BuildNoBreak() ? _nobr : null; // null keeps the shaper cache when nothing is nobr
            ResolveSprites();
            ResolveFontMarks();
        }
        else
        {
            _shapedText = _content;
            _sizeArg = null;
            _nobrArg = null;
            _fontMarksArg = null;
            _sprites.Clear();
            _resolvedSprites.Clear();
            _spriteAspects.Clear();
            _spriteAspectsArg = null;
            _alignMarks.Clear();
            _lineHeightMarks.Clear();
            _fontMarks.Clear();
            _resolvedFontMarks.Clear();
        }
    }

    public override ValueTask PreGraphicsCompute()
    {
        var fontSet = _font?.Asset;
        if (fontSet == null || !fontSet.IsValid)
        {
            // Baking with no font emits nothing; arm the re-drive so the asset landing later
            // re-measures and re-meshes this text instead of leaving it invisible.
            _fontRedriveArmed = true;
            WatchArmed(true);
            _ascent = _size * 0.8f;
            _lineHeight = _size;
            _rawLineHeight = _size;
            return default;
        }
        _fontRedriveArmed = false;
        WatchArmed(false);

        var rect = RectTransform?.LocalComputeRect ?? default;

        // Auto-size: shrink _size to fit the rect BEFORE rasterizing/shaping, so glyphs are requested and
        // metrics read at the final size. _size is the per-cycle working copy (reset from Size each compute),
        // so fitting it here doesn't accumulate across frames.
        if ((_autoH || _autoV) && !string.IsNullOrEmpty(_shapedText))
            _size = ComputeFittedSize(fontSet, rect.width, rect.height);

        // request rasterization for every codepoint we're about to draw - xlinka
        _appliedFontGeneration = fontSet.CacheGeneration;
        WatchFont(fontSet);
        TextShaper.RequestGlyphs(fontSet, _shapedText, _size, _fontMarksArg);

        // Shape and read font metrics HERE, on the main thread, and do NOT move this to the worker no
        // matter how tempting. ComputeGraphic runs on the canvas worker, and the font hook
        // (GetAscent/GetLineHeight/GetKerning) calls Godot's font server, which is not thread safe. I
        // learned this the hard way: off-main it hands back garbage metrics and text randomly
        // collapses/vanishes for a frame or two, which is an absolute nightmare to repro. The worker
        // only positions the already-shaped glyphs. - xlinka
        float wrapWidth = _wrap && rect.width > 0f ? rect.width : 0f;
        _shaper.Shape(fontSet, _shapedText, _size, _wrap, wrapWidth, _sizeArg, _nobrArg, _fontMarksArg, _spriteAspectsArg);
        if (_ellipsis && _caretPos < 0)
            ApplyEllipsis(fontSet, rect, wrapWidth);
        MeasureLineFonts(fontSet, _size);

        _ascent = fontSet.GetAscent(_size);
        _rawLineHeight = fontSet.GetLineHeight(_size);
        _lineHeight = _rawLineHeight * _lineSpacing;
        if (_lineHeight <= 0f)
            _lineHeight = _size;
        if (_rawLineHeight <= 0f)
            _rawLineHeight = _size;

        UpdateCaretViewport(rect);

        return default;
    }

    private const char EllipsisChar = '\u2026';

    private void ApplyEllipsis(FontSet fontSet, in Rect rect, float wrapWidth)
    {
        string source = _shapedText;
        int generation = fontSet.CacheGeneration;
        if (_cutFor == source && _cutSize == _size && _cutWidth == rect.width && _cutHeight == rect.height
            && _cutWrap == _wrap && _cutGeneration == generation)
        {
            if (_cutText != null)
                ShapeCut(fontSet, _cutText, wrapWidth);
            return;
        }
        _cutFor = source;
        _cutSize = _size;
        _cutWidth = rect.width;
        _cutHeight = rect.height;
        _cutWrap = _wrap;
        _cutGeneration = generation;
        _cutText = null;
        // The parser adds an entry for every character; only non-null entries are sprites.
        // A sprite sits on a character of its own, and the ellipsis would take its place.
        if (_richText && _sprites.Exists(static sprite => sprite != null))
            return;

        float lineHeight = fontSet.GetLineHeight(_size) * _lineSpacing;
        if (lineHeight <= 0f)
            lineHeight = _size;
        string current = source;
        for (int pass = 0; pass < 6; pass++)
        {
            var lines = _shaper.Lines;
            int over = -1;
            bool dropRest = false;
            float height = 0f;
            for (int i = 0; i < lines.Count; i++)
            {
                height += lineHeight * lines[i].MaxSizeScale;
                if (_wrap && i > 0 && height > rect.height + 0.5f)
                {
                    over = i - 1;
                    dropRest = true;
                    break;
                }
                if (lines[i].Width > rect.width + 0.5f)
                {
                    over = i;
                    dropRest = _wrap;
                    break;
                }
            }
            if (over < 0)
                break;

            var line = lines[over];
            float reserve = _size;
            int end = line.Glyphs.Count;
            if (end > 0 && line.Glyphs[end - 1].Codepoint == EllipsisChar)
            {
                reserve = line.Glyphs[end - 1].Metrics.Advance;
                end--;
            }
            float budget = rect.width - reserve;
            // Not even the ellipsis fits: a rect squeezed to nothing draws nothing, not the whole text.
            if (budget < 0f)
            {
                current = string.Empty;
                ShapeCut(fontSet, current, wrapWidth);
                break;
            }
            int keep = 0;
            while (keep < end && line.Glyphs[keep].X + line.Glyphs[keep].Metrics.Advance <= budget)
                keep++;
            int cut;
            if (keep < end)
                cut = line.Glyphs[keep].SourceIndex;
            else if (end > 0)
                cut = line.Glyphs[end - 1].SourceIndex + (line.Glyphs[end - 1].Codepoint > 0xFFFF ? 2 : 1);
            else
                cut = line.Glyphs.Count > 0 ? line.Glyphs[0].SourceIndex : 0;
            cut = System.Math.Clamp(cut, 0, current.Length);

            string next = current.Substring(0, cut).TrimEnd() + EllipsisChar;
            if (!dropRest && !_richText)
            {
                int newline = current.IndexOf('\n', cut);
                if (newline >= 0)
                    next += current.Substring(newline);
            }
            if (next == current)
                break;
            current = next;
            ShapeCut(fontSet, current, wrapWidth);
        }
        if (!ReferenceEquals(current, source))
            _cutText = current;
    }

    // The cut text is only ever shaped, never kept as the text: layout keeps measuring the whole of it.
    private void ShapeCut(FontSet fontSet, string text, float wrapWidth)
    {
        TextShaper.RequestGlyphs(fontSet, text, _size, _fontMarksArg);
        _shaper.Shape(fontSet, text, _size, _wrap, wrapWidth, _sizeArg, _nobrArg, _fontMarksArg, _spriteAspectsArg);
    }

    private void UpdateCaretViewport(in Rect rect)
    {
        var lines = _shaper.Lines;
        if (!_keepCaretVisible || _caretPos < 0 || lines.Count == 0 || rect.width <= 2f || rect.height <= 0f)
        {
            _editOffset = float2.Zero;
            return;
        }
        TryGetCaretPos(lines, _caretPos, out int lineIndex, out float caretX);
        float x = AlignLineStart(rect, lines[lineIndex].Width, LineAlign(lines[lineIndex])) + caretX + _editOffset.x;
        if (x > rect.xMax - 2f) _editOffset.x -= x - (rect.xMax - 2f);
        else if (x < rect.xMin) _editOffset.x += rect.xMin - x;

        float top = FirstLineTop(rect, lines);
        for (int i = 0; i < lineIndex; i++)
            top -= LineHeightFor(lines[i]) * lines[i].MaxSizeScale;
        top += _editOffset.y;
        float height = System.Math.Min(rect.height, LineHeightFor(lines[lineIndex]) * lines[lineIndex].MaxSizeScale);
        if (top > rect.yMax) _editOffset.y -= top - rect.yMax;
        else if (top - height < rect.yMin) _editOffset.y += rect.yMin - (top - height);
    }

    private Rect RenderRect(Rect rect) => Rect.FromMinMax(
        new float2(rect.xMin, rect.yMin) + _editOffset, new float2(rect.xMax, rect.yMax) + _editOffset);

    // Where the glyphs will actually land, worked out before anything is emitted so the chunk batcher can
    // decide whether this label may share a surface. The rect on its own is not an answer: with wrapping off
    // a long line runs straight past the edge, and a block taller than its rect spills out the bottom. So
    // walk the shaped lines the way LayoutAndEmit does and take the union of the line boxes and the glyph
    // quads. Keep this in step with LayoutAndEmit - a box smaller than what gets emitted is how you end up
    // with a label hiding behind a panel. Runs on the main thread, after PreGraphicsCompute has shaped. -xlinka
    public override Rect? MeasureBounds()
    {
        var rectTransform = RectTransform;
        if (rectTransform == null)
            return null;

        var rect = RenderRect(rectTransform.LocalComputeRect);
        var lines = _shaper.Lines;
        if (lines.Count == 0 || string.IsNullOrEmpty(_shapedText))
            return rect;

        float blockHeight = 0f;
        for (int i = 0; i < lines.Count; i++)
            blockHeight += LineHeightFor(lines[i]) * lines[i].MaxSizeScale;

        float top = FirstLineTop(rect, lines);

        // Start from the rect horizontally (justify spreads to both its edges) and from the text block
        // vertically, which is the part that can sit well inside a tall row or hang out of a short one.
        float xMin = rect.xMin, xMax = rect.xMax;
        float yMin = top - blockHeight, yMax = top;

        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var align = LineAlign(line);
            float penX = align == TextHorizontalAlignment.Justify ? rect.xMin : AlignLineStart(rect, line.Width, align);
            if (penX < xMin) xMin = penX;
            if (penX + line.Width > xMax) xMax = penX + line.Width;

            float penY = top - _ascent * line.MaxSizeScale - ExtraLineHeight(line);
            for (int i = 0; i < line.Glyphs.Count; i++)
            {
                var glyph = line.Glyphs[i];
                var metrics = glyph.Metrics;
                // Sprites sit on the baseline as-is; only real glyphs get the sub/superscript shift.
                float baseline = GlyphBaseline(glyph);
                float gx = penX + glyph.X + metrics.Offset.x;
                float gy = penY + baseline + metrics.Offset.y;
                if (gx < xMin) xMin = gx;
                if (gy < yMin) yMin = gy;
                if (gx + metrics.Size.x > xMax) xMax = gx + metrics.Size.x;
                if (gy + metrics.Size.y > yMax) yMax = gy + metrics.Size.y;
            }

            top -= LineHeightFor(line) * line.MaxSizeScale;
        }

        // Decorations draw outside the glyph boxes: italic shears the top to the right, faux bold re-emits at
        // an offset, underline sits under the baseline, the caret is a quad of its own, and a justified line
        // spreads its glyphs past where the walk above put them. A quarter of the font size plus a pixel
        // covers the lot, and plain left-aligned text (nearly all of it) pays nothing. -xlinka
        bool loose = _richText || _caretPos >= 0 || _hAlign == TextHorizontalAlignment.Justify;
        float pad = loose ? _size * 0.25f + 1f : 0f;
        return Rect.FromMinMax(new float2(xMin - pad, yMin - pad), new float2(xMax + pad, yMax + pad));
    }

    public override void ComputeGraphic(GraphicsChunk.RenderData renderData)
    {
        if (RectTransform == null) return;
        if (string.IsNullOrEmpty(_shapedText)) return;

        var fontSet = _font?.Asset;
        if (fontSet == null || !fontSet.IsValid) return;

        var mesh = renderData.Mesh;

        PrepareMesh(mesh);
        LayoutAndEmit(renderData, mesh);
    }

    public override bool IsPointInside(in float2 point)
        => InteractionTarget.Value && (RectTransform?.LocalComputeRect.Contains(point) ?? false);

    public void ClearChangedMetrics()
    {
        ChangedMetrics = LayoutMetric.None;
    }

    public void EnsureValidMetrics(LayoutDirection direction)
    {
        var fontSet = _font?.Asset;
        if (fontSet == null || !fontSet.IsValid || string.IsNullOrEmpty(_shapedText))
        {
            // Measured before its font landed: the landing measures it again. Drawing arms the same thing, but
            // a text sized from these zeros gets an empty rect, is never drawn, and would stay zero for good.
            if (_font != null && (fontSet == null || !fontSet.IsValid))
            {
                _fontRedriveArmed = true;
                WatchArmed(true);
            }
            _layoutPreferredWidth = 0f;
            _layoutPreferredHeight = 0f;
            _layoutMinHeight = 0f;
            return;
        }

        // Measured at the width its layout gives it; a RectLerp only moves where it draws.
        var rect = RectTransform?.PlacedComputeRect ?? default;
        float wrapWidth = _wrap && rect.width > 0f ? rect.width : 0f;
        _shaper.Shape(fontSet, _shapedText, _size, _wrap, wrapWidth, _sizeArg, _nobrArg, _fontMarksArg, _spriteAspectsArg);
        MeasureLineFonts(fontSet, _size);
        _measuredWrapWidth = wrapWidth;

        float lineHeight = fontSet.GetLineHeight(_size) * _lineSpacing;
        if (lineHeight <= 0f)
        {
            lineHeight = _size;
        }

        float totalHeight = 0f;
        var measuredLines = _shaper.Lines;
        for (int i = 0; i < measuredLines.Count; i++)
            totalHeight += lineHeight * measuredLines[i].MaxSizeScale * measuredLines[i].FontHeightScale;

        // An auto-sized axis adapts to whatever rect it's given, so it reports no preferred size to layout
        // (driving the natural size would fight the fit).
        if (RectTransform?.Canvas is { PerMetricPriority.Value: true })
        {
            // Per-metric rules: the width the text would take unwrapped, which a row sizing to its content needs
            // (measuring at the width it already has only ever confirms that width).
            _naturalShaper ??= new TextShaper();
            _naturalShaper.Shape(fontSet, _shapedText, _size, false, 0f, _sizeArg, _nobrArg, _fontMarksArg, _spriteAspectsArg);
            _layoutPreferredWidth = _naturalShaper.MaxLineWidth;
        }
        else
        {
            _layoutPreferredWidth = _autoH ? 0f : _shaper.MaxLineWidth;
        }
        _layoutPreferredHeight = _autoV ? 0f : totalHeight;
        _layoutMinHeight = _layoutPreferredHeight;
        // Per-metric rules: a text that fits itself vertically asks for no minimum but still prefers its full
        // height, so a row sized to its content keeps room for it.
        if (_autoV && RectTransform?.Canvas is { PerMetricPriority.Value: true })
            _layoutPreferredHeight = totalHeight;
    }

    // Largest size in [AutoSizeMin, Size] whose shaped text fits the rect on the enabled axis/axes. Shapes
    // a few candidates (the shaper caches by size); the slight under-shoot helps converge when word-wrap
    // makes height non-linear in size. -xlinka
    private float ComputeFittedSize(FontSet fontSet, float availW, float availH)
    {
        float baseSize = _size;
        float min = _autoMin > 0f ? _autoMin : 1f;
        if (min > baseSize)
            min = baseSize;
        float size = baseSize;

        for (int iter = 0; iter < 6; iter++)
        {
            float wrapW = _wrap && availW > 0f ? availW : 0f;
            _shaper.Shape(fontSet, _shapedText, size, _wrap, wrapW, _sizeArg, _nobrArg, _fontMarksArg);
            MeasureLineFonts(fontSet, size);

            float textW = _shaper.MaxLineWidth;
            float lh = fontSet.GetLineHeight(size) * _lineSpacing;
            if (lh <= 0f)
                lh = size;
            float textH = 0f;
            var lines = _shaper.Lines;
            for (int i = 0; i < lines.Count; i++)
                textH += lh * lines[i].MaxSizeScale * lines[i].FontHeightScale;

            float ratio = 1f;
            if (_autoH && availW > 0f && textW > availW)
                ratio = System.Math.Min(ratio, availW / textW);
            if (_autoV && availH > 0f && textH > availH)
                ratio = System.Math.Min(ratio, availH / textH);
            if (ratio >= 0.999f)
                break;

            float next = System.Math.Max(min, size * ratio * 0.98f);
            if (next >= size - 0.01f)
            {
                size = next;
                break;
            }
            size = next;
        }

        return System.Math.Clamp(size, min, baseSize);
    }

    public LayoutMetric FilterChangedMetrics(LayoutMetric metrics)
    {
        return metrics;
    }

    public void LayoutRectWidthChanged()
    {
        ChangedMetrics |= LayoutMetric.MinHeight | LayoutMetric.PreferredHeight;
    }

    public void LayoutRectHeightChanged()
    {
    }

    // The window glyphs are trimmed to: the masks above, and this text's own rect when it clips to it.
    private Rect? ClipWindow(GraphicsChunk.RenderData renderData)
    {
        var clip = renderData.GeometryClipRect;
        if (!_clipToRect || RectTransform == null)
            return clip;
        var own = RectTransform.LocalComputeRect;
        return clip.HasValue ? clip.Value.Intersection(own) : own;
    }

    private void LayoutAndEmit(GraphicsChunk.RenderData renderData, PhosMesh mesh)
    {
        var rect = RenderRect(RectTransform!.LocalComputeRect);
        var clip = ClipWindow(renderData);
        // Shaping + metrics were done on the main thread in PreGraphicsCompute; the worker only
        // positions the already-shaped glyphs (no font-server calls off-main). - xlinka
        var lines = _shaper.Lines;
        if (lines.Count == 0) return;

        float ascent = _ascent;
        float blockTop = FirstLineTop(rect, lines);

        // Highlight backgrounds behind marked runs (rich-text <mark>), under everything else.
        if (_richText)
            EmitMarkBackgrounds(renderData, rect, lines, blockTop);

        // Selection (behind text) + caret, when an editor is driving this text.
        if (_caretPos >= 0)
            EmitEditingVisuals(renderData, rect, lines, blockTop);

        float yCursor = blockTop;
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            float lineH = LineHeightFor(line) * line.MaxSizeScale;
            var lineAlign = LineAlign(line);
            float penX = AlignLineStart(rect, line.Width, lineAlign);
            float penY = yCursor - ascent * line.MaxSizeScale - ExtraLineHeight(line);

            // Justify spreads the leftover width across word gaps on every line except the last (and never the
            // line that ends a paragraph). justifyOffset accumulates as we cross each gap. Justified by glyphs, a
            // line two thirds full or more stretches every glyph's place along it by the same factor. -xlinka
            float justifyPerGap = 0f;
            float stretch = 1f;
            if (lineAlign == TextHorizontalAlignment.Justify && _justification == TextJustification.Glyphs)
            {
                if (line.Width > 0f && rect.width < line.Width * 1.5f)
                    stretch = rect.width / line.Width;
            }
            else if (lineAlign == TextHorizontalAlignment.Justify && _wrap && lineIndex < lines.Count - 1 && !line.HardBreak)
            {
                int gaps = CountWordGaps(line);
                float extra = rect.width - line.Width;
                if (gaps > 0 && extra > 0f)
                    justifyPerGap = extra / gaps;
            }
            float justifyOffset = 0f;

            for (int i = 0; i < line.Glyphs.Count; i++)
            {
                var glyph = line.Glyphs[i];
                if (justifyPerGap > 0f && i > 0 && IsWordGap(line, i))
                    justifyOffset += justifyPerGap;
                if (stretch != 1f)
                    justifyOffset = glyph.X * (stretch - 1f);

                // Inline sprite placeholder: draw the sprite texture instead of a font glyph.
                if (glyph.Codepoint == TextShaper.SpriteGlyph)
                {
                    EmitSprite(renderData, glyph, penX + glyph.X + justifyOffset, penY, clip);
                    continue;
                }

                PhosTriangleSubmesh submesh;
                if (_material != null)
                {
                    submesh = renderData.GetSubmesh(_material);
                }
                else
                {
                    // Key by atlas identity; the shared text material is created at submit (main).
                    var atlas = glyph.Font.AtlasTexture;
                    if (atlas == null) continue;
                    submesh = renderData.GetSubmesh(null, atlas, GraphicsChunk.RenderData.TextAtlas);
                }
                byte style = GlyphStyle(glyph);
                float bold = _material == null && (style & RichTextParser.StyleBold) != 0 && glyph.Font.AtlasTexture is { IsMSDF: true } boldAtlas
                    ? 0.5f / System.Math.Max(boldAtlas.MsdfPixelRange, 1)
                    : 0f;
                EmitGlyph(renderData, submesh, mesh, penX + glyph.X + justifyOffset, penY + GlyphBaseline(glyph), glyph.Metrics, glyph.UV, GlyphColor(glyph), style, GlyphSize(glyph), bold, clip);
            }

            yCursor -= lineH;
        }
    }

    private static float AlignLineStart(in Rect rect, float lineWidth, TextHorizontalAlignment align)
    {
        return align switch
        {
            TextHorizontalAlignment.Center => rect.xMin + (rect.width - lineWidth) * 0.5f,
            TextHorizontalAlignment.Right => rect.xMax - lineWidth,
            _ => rect.xMin, // Left and Justify both start at the left; justify then spreads the slack
        };
    }

    private static int LineFirstSourceIndex(TextShaper.Line line)
        => line.Glyphs.Count > 0 ? line.Glyphs[0].SourceIndex : -1;

    // Alignment active on this line: the last <align> marker at or before the line's first char, else the
    // Text-wide default. Markers are stored in source order. -xlinka
    private TextHorizontalAlignment LineAlign(TextShaper.Line line)
    {
        if (_alignMarks.Count == 0)
            return _hAlign;
        int idx = LineFirstSourceIndex(line);
        byte a = 0;
        for (int i = 0; i < _alignMarks.Count && _alignMarks[i].Index <= idx; i++)
            a = _alignMarks[i].Align;
        return a > 0 ? (TextHorizontalAlignment)(a - 1) : _hAlign;
    }

    // Line height active on this line: a <line-height> override (multiplier of the raw font height) or the
    // default (font height x line spacing). -xlinka
    private float LineHeightFor(TextShaper.Line line)
    {
        if (_lineHeightMarks.Count == 0)
            return _lineHeight * line.FontHeightScale;
        int idx = LineFirstSourceIndex(line);
        float h = 0f;
        for (int i = 0; i < _lineHeightMarks.Count && _lineHeightMarks[i].Index <= idx; i++)
            h = _lineHeightMarks[i].Height;
        return (h > 0f ? _rawLineHeight * h : _lineHeight) * line.FontHeightScale;
    }

    // What a taller font added to this line, which sits above its baseline.
    private float ExtraLineHeight(TextShaper.Line line)
        => line.FontHeightScale > 1f ? LineHeightFor(line) * line.MaxSizeScale * (1f - 1f / line.FontHeightScale) : 0f;

    // Where the first line's top goes for the vertical alignment. Lines differ in height with rich-text <size>
    // (a line is as tall as its tallest glyph), a <line-height> override or a taller fallback font. The block
    // lined up with the rect is the lines' boxes, the glyphs drawn (a line with none counts from three quarters
    // of a line above its baseline) or one line height above the first baseline down to the last baseline. -xlinka
    private float FirstLineTop(in Rect rect, System.Collections.Generic.IReadOnlyList<TextShaper.Line> lines)
    {
        // The block's top and bottom, measured from the first line's top.
        float top = 0f, bottom = 0f, lineTop = 0f;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            float lineHeight = LineHeightFor(line) * line.MaxSizeScale;
            float baseline = lineTop - _ascent * line.MaxSizeScale - ExtraLineHeight(line);
            bool first = i == 0, last = i == lines.Count - 1;
            if (_blockMeasure == TextBlockMeasure.Baselines)
            {
                if (first) top = baseline + lineHeight;
                if (last) bottom = baseline;
            }
            else if (_blockMeasure == TextBlockMeasure.Glyphs && (first || last))
            {
                float high = float.NegativeInfinity, low = float.PositiveInfinity;
                for (int g = 0; g < line.Glyphs.Count; g++)
                {
                    var glyph = line.Glyphs[g];
                    if (glyph.Metrics.Size.y <= 0f)
                        continue;
                    float y = baseline + GlyphBaseline(glyph) + glyph.Metrics.Offset.y;
                    high = System.MathF.Max(high, y + glyph.Metrics.Size.y);
                    low = System.MathF.Min(low, y);
                }
                bool drawn = high > low;
                if (first) top = drawn ? high : baseline + lineHeight * 0.75f;
                if (last) bottom = drawn ? low : baseline;
            }
            lineTop -= lineHeight;
        }
        if (_blockMeasure == TextBlockMeasure.LineBoxes)
            bottom = lineTop;

        return _vAlign switch
        {
            TextVerticalAlignment.Middle => (rect.yMin + rect.yMax - top - bottom) * 0.5f,
            TextVerticalAlignment.Bottom => rect.yMin - bottom,
            _ => rect.yMax - top,
        };
    }

    // Main thread, after shaping: each line's tallest full font height against the first font's ascent plus
    // descent; a line with no glyph of its own takes the first font's. Fonts are asked once per run of glyphs
    // from the same font.
    private void MeasureLineFonts(FontSet fontSet, float size)
    {
        var lines = _shaper.Lines;
        float first = _perFontLines ? fontSet.GetLineHeight(size) : 0f;
        if (first <= 0f || fontSet.Fonts.Count == 0)
        {
            for (int l = 0; l < lines.Count; l++)
                lines[l].FontHeightScale = 1f;
            return;
        }
        float firstRatio = FullHeight(fontSet.Fonts[0], size) / first;
        FontAsset? last = null;
        float ratio = 1f;
        for (int l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            float scale = 0f;
            for (int i = 0; i < line.Glyphs.Count; i++)
            {
                var font = line.Glyphs[i].Font;
                if (font == null || line.Glyphs[i].Codepoint == TextShaper.SpriteGlyph)
                    continue;
                if (!ReferenceEquals(font, last))
                {
                    last = font;
                    float height = FullHeight(font, size);
                    ratio = height > 0f ? height / first : 1f;
                }
                if (ratio > scale)
                    scale = ratio;
            }
            line.FontHeightScale = scale > 0f ? scale : firstRatio > 0f ? firstRatio : 1f;
        }
    }

    private static float FullHeight(FontAsset font, float size)
    {
        float gap = FontLineGap.PerEm(font.AssetURL) * size;
        return font.GetLineHeight(size) + gap;
    }

    // A word gap sits before glyph i when there's visible whitespace between it and the previous glyph - the
    // previous glyph's advance doesn't cover the distance to it. Reads off glyph spacing (not source chars), so
    // a non-breaking space (which IS a glyph) correctly produces no gap after it. -xlinka
    private bool IsWordGap(TextShaper.Line line, int i)
    {
        var prev = line.Glyphs[i - 1];
        float gap = line.Glyphs[i].X - prev.X - prev.Metrics.Advance;
        return gap > _size * 0.2f;
    }

    private int CountWordGaps(TextShaper.Line line)
    {
        int gaps = 0;
        for (int i = 1; i < line.Glyphs.Count; i++)
            if (IsWordGap(line, i)) gaps++;
        return gaps;
    }

    // Per-glyph color from the rich-text color map (rich mode), else the uniform _color.
    private color GlyphColor(in TextShaper.PositionedGlyph glyph)
    {
        if (_richText && glyph.SourceIndex >= 0 && glyph.SourceIndex < _colors.Count)
            return _colors[glyph.SourceIndex];
        return _color;
    }

    private byte GlyphStyle(in TextShaper.PositionedGlyph glyph)
    {
        if (_richText && glyph.SourceIndex >= 0 && glyph.SourceIndex < _styles.Count)
            return _styles[glyph.SourceIndex];
        return 0;
    }

    private bool HasSizeVariation()
    {
        for (int i = 0; i < _sizes.Count; i++)
            if (_sizes[i] != 1f)
                return true;
        return false;
    }

    // Fill _nobr from the parsed style flags: true where a char is inside a <nobr> run, so the shaper keeps
    // its spaces from becoming wrap points. Returns false when nothing is nobr, keeping the shaper cache warm. -xlinka
    private bool BuildNoBreak()
    {
        _nobr.Clear();
        bool any = false;
        for (int i = 0; i < _styles.Count; i++)
        {
            bool nb = (_styles[i] & RichTextParser.StyleNoBreak) != 0;
            _nobr.Add(nb);
            any |= nb;
        }
        return any;
    }

    // Per-char highlight color from the rich-text mark map (a<=0 = no highlight).
    private color GlyphMark(in TextShaper.PositionedGlyph glyph)
    {
        if (_richText && glyph.SourceIndex >= 0 && glyph.SourceIndex < _marks.Count)
            return _marks[glyph.SourceIndex];
        return default;
    }

    // Vertical offset (canvas Y-up) for sub/superscript glyphs: superscript rides above the baseline,
    // subscript drops below it, by a share of the size of the text around them. Sprites sit on the baseline.
    private float GlyphBaseline(in TextShaper.PositionedGlyph glyph)
    {
        if (glyph.Codepoint == TextShaper.SpriteGlyph)
            return 0f;
        byte style = GlyphStyle(glyph);
        if ((style & (RichTextParser.StyleSup | RichTextParser.StyleSub)) == 0)
            return 0f;
        float around = GlyphSize(glyph) / RichTextParser.SubSupScale;
        return (style & RichTextParser.StyleSup) != 0 ? around * RichTextParser.SupRise : -around * RichTextParser.SubDrop;
    }

    // The size a glyph is drawn at, its rich-text size included.
    private float GlyphSize(in TextShaper.PositionedGlyph glyph)
        => _richText && glyph.SourceIndex >= 0 && glyph.SourceIndex < _sizes.Count ? _size * _sizes[glyph.SourceIndex] : _size;

    private void EmitGlyph(GraphicsChunk.RenderData renderData, PhosTriangleSubmesh submesh, PhosMesh mesh, float penX, float penY, in GlyphMetrics metrics, in Rect uv, in color glyphColor, byte style, float glyphSize, float boldDilate, Rect? clipRect)
    {
        float xMin = penX + metrics.Offset.x;
        float yMin = penY + metrics.Offset.y;
        float xMax = xMin + metrics.Size.x;
        float yMax = yMin + metrics.Size.y;
        float uMin = uv.xMin;
        float uMax = uv.xMax;
        float vMin = uv.yMin;
        float vMax = uv.yMax;

        if (clipRect.HasValue)
        {
            var clip = clipRect.Value;
            if (xMax <= clip.xMin || xMin >= clip.xMax || yMax <= clip.yMin || yMin >= clip.yMax)
            {
                return;
            }

            if (xMin < clip.xMin)
            {
                float t = (clip.xMin - xMin) / (xMax - xMin);
                uMin = Lerp(uMin, uMax, t);
                xMin = clip.xMin;
            }
            if (xMax > clip.xMax)
            {
                float t = (clip.xMax - xMin) / (xMax - xMin);
                uMax = Lerp(uMin, uMax, t);
                xMax = clip.xMax;
            }
            if (yMin < clip.yMin)
            {
                float t = (clip.yMin - yMin) / (yMax - yMin);
                vMax = Lerp(vMax, vMin, t);
                yMin = clip.yMin;
            }
            if (yMax > clip.yMax)
            {
                float t = (clip.yMax - yMin) / (yMax - yMin);
                vMin = Lerp(vMax, vMin, t);
                yMax = clip.yMax;
            }
        }

        // Faux italic: shear x by skew*(vertexY - baseline) so the top leans right.
        float skew = (style & RichTextParser.StyleItalic) != 0 ? 0.2f : 0f;
        var col = glyphColor; // local copy: an 'in' param can't be captured by the local function

        void WriteQuad(float dx)
        {
            int v0 = mesh.VertexCount;
            mesh.IncreaseVertexCount(4);

            mesh.RawPositions[v0 + 0] = new float3(xMin + dx + skew * (yMax - penY), yMax, 0f);
            mesh.RawPositions[v0 + 1] = new float3(xMax + dx + skew * (yMax - penY), yMax, 0f);
            mesh.RawPositions[v0 + 2] = new float3(xMax + dx + skew * (yMin - penY), yMin, 0f);
            mesh.RawPositions[v0 + 3] = new float3(xMin + dx + skew * (yMin - penY), yMin, 0f);

            mesh.RawColors[v0 + 0] = col;
            mesh.RawColors[v0 + 1] = col;
            mesh.RawColors[v0 + 2] = col;
            mesh.RawColors[v0 + 3] = col;

            // Godot UV is Y-down (V=0 at the top of the texture, V=1 at the bottom). The atlas
            // stores glyphs with uv.yMin = top row and uv.yMax = bottom row, so the screen-TOP
            // vertex (yMax in Y-up world) needs the SMALLER V to land on atlas-top. - xlinka
            mesh.SetUV(0, v0 + 0, new float2(uMin, vMin));
            mesh.SetUV(0, v0 + 1, new float2(uMax, vMin));
            mesh.SetUV(0, v0 + 2, new float2(uMax, vMax));
            mesh.SetUV(0, v0 + 3, new float2(uMin, vMax));

            if (boldDilate > 0f || mesh.UVChannelCount > 1)
            {
                var dilate = new float2(boldDilate, 0f);
                for (int k = 0; k < 4; k++)
                    mesh.SetUV(1, v0 + k, dilate);
            }

            submesh.AddQuadAsTriangles(v0, v0 + 1, v0 + 2, v0 + 3);
        }

        WriteQuad(0f);
        // Bold on a distance-field atlas thickens in place: UV1.x pushes the glyph edge out in the shader,
        // half an atlas texel, and the advance stays. A coverage atlas has no distance to push, so it
        // draws a second, offset copy instead. -xlinka
        if ((style & RichTextParser.StyleBold) != 0 && boldDilate <= 0f)
            WriteQuad(glyphSize * 0.04f);

        // Underline and strikethrough as solid quads spanning the glyph advance: an underline a tenth of the size
        // under the baseline, 0.075 thick; a strike 0.3 above it, 0.1 thick.
        if ((style & (RichTextParser.StyleUnderline | RichTextParser.StyleStrike)) != 0)
        {
            float adv = metrics.Advance > 0f ? metrics.Advance : metrics.Size.x;
            float x0 = penX;
            float x1 = penX + adv;
            if ((style & RichTextParser.StyleUnderline) != 0)
                EmitSolidQuad(renderData, x0, penY - glyphSize * 0.1375f, x1, penY - glyphSize * 0.0625f, glyphColor, clipRect);
            if ((style & RichTextParser.StyleStrike) != 0)
                EmitSolidQuad(renderData, x0, penY + glyphSize * 0.25f, x1, penY + glyphSize * 0.35f, glyphColor, clipRect);
        }
    }

    // Selection rects + caret, emitted into a solid (untextured) submesh requested
    // BEFORE the glyph submeshes so they render behind the glyphs. -xlinka
    // Draw a highlight quad behind each run of same-colored <mark> glyphs, per line, before the glyphs. -xlinka
    private void EmitMarkBackgrounds(GraphicsChunk.RenderData renderData,
        in Rect rect, System.Collections.Generic.IReadOnlyList<TextShaper.Line> lines, float blockTop)
    {
        if (lines.Count == 0 || _marks.Count == 0) return;
        var clip = ClipWindow(renderData);
        float top = blockTop;
        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            float lh = LineHeightFor(line) * line.MaxSizeScale;
            float ls = AlignLineStart(rect, line.Width, LineAlign(line));
            int gi = 0;
            while (gi < line.Glyphs.Count)
            {
                var mark = GlyphMark(line.Glyphs[gi]);
                if (mark.a <= 0f) { gi++; continue; }

                float startX = line.Glyphs[gi].X;
                float endX = (gi + 1 < line.Glyphs.Count) ? line.Glyphs[gi + 1].X : line.Width;
                int gj = gi + 1;
                while (gj < line.Glyphs.Count)
                {
                    var next = GlyphMark(line.Glyphs[gj]);
                    if (next.a <= 0f || !SameColor(next, mark)) break;
                    endX = (gj + 1 < line.Glyphs.Count) ? line.Glyphs[gj + 1].X : line.Width;
                    gj++;
                }
                EmitSolidQuad(renderData, ls + startX, top - lh, ls + endX, top, mark, clip);
                gi = gj;
            }
            top -= lh;
        }
    }

    private static bool SameColor(in color a, in color b)
        => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    private void EmitEditingVisuals(GraphicsChunk.RenderData renderData,
        in Rect rect, System.Collections.Generic.IReadOnlyList<TextShaper.Line> lines, float blockTop)
    {
        if (lines.Count == 0) return;
        var clip = ClipWindow(renderData);

        if (_selStart >= 0 && _selStart != _caretPos)
        {
            int a = _selStart < _caretPos ? _selStart : _caretPos;
            int b = _selStart < _caretPos ? _caretPos : _selStart;
            float top = blockTop;
            for (int li = 0; li < lines.Count; li++)
            {
                var line = lines[li];
                float lh = LineHeightFor(line) * line.MaxSizeScale;
                float startX = -1f;
                float endX = -1f;
                for (int gi = 0; gi < line.Glyphs.Count; gi++)
                {
                    var g = line.Glyphs[gi];
                    if (g.SourceIndex >= a && g.SourceIndex < b)
                    {
                        if (startX < 0f) startX = g.X;
                        endX = (gi + 1 < line.Glyphs.Count) ? line.Glyphs[gi + 1].X : line.Width;
                    }
                }
                if (startX >= 0f && endX > startX)
                {
                    float ls = AlignLineStart(rect, line.Width, LineAlign(line));
                    EmitSolidQuad(renderData, ls + startX, top - lh, ls + endX, top, _selColor, clip);
                }
                top -= lh;
            }
        }

        if (!_drawCaret)
            return;
        TryGetCaretPos(lines, _caretPos, out int caretLine, out float caretX);
        float caretTop = blockTop;
        for (int li = 0; li < caretLine; li++)
            caretTop -= LineHeightFor(lines[li]) * lines[li].MaxSizeScale;
        float caretLh = LineHeightFor(lines[caretLine]) * lines[caretLine].MaxSizeScale;
        float lineStart = AlignLineStart(rect, lines[caretLine].Width, LineAlign(lines[caretLine]));
        float cx = lineStart + caretX;
        float w = _size * 0.06f;
        if (w < 1f) w = 1f;
        EmitSolidQuad(renderData, cx, caretTop - caretLh, cx + w, caretTop, _caretColor, clip);
    }

    private static void TryGetCaretPos(System.Collections.Generic.IReadOnlyList<TextShaper.Line> lines, int charIndex, out int lineIndex, out float localX)
    {
        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            for (int gi = 0; gi < line.Glyphs.Count; gi++)
            {
                if (line.Glyphs[gi].SourceIndex >= charIndex)
                {
                    lineIndex = li;
                    localX = line.Glyphs[gi].X;
                    return;
                }
            }
        }
        lineIndex = lines.Count - 1;
        localX = lines[lineIndex].Width;
    }

    // Inline sprites keep their own art colors (no text tint) - running the text color over emoji-style
    // sprite art would just discolor it. -xlinka
    private static readonly color SpriteTint = color.White;

    // Resolve each <font> marker's name to a FontSet on the main thread (walks the FontSetGroup's child slots),
    // so the shaper worker only reads resolved FontSet references. -xlinka
    private void ResolveFontMarks()
    {
        _resolvedFontMarks.Clear();
        for (int i = 0; i < _fontMarks.Count; i++)
        {
            var m = _fontMarks[i];
            FontSet? f = (m.Name != null && _fontGroup != null) ? _fontGroup.Get(m.Name) : null;
            _resolvedFontMarks.Add((m.Index, f));
        }
        _fontMarksArg = _resolvedFontMarks.Count > 0 ? _resolvedFontMarks : null;
    }

    // Resolve every inline sprite name to its (texture, uv) on the main thread, so the worker emit never
    // touches the live slot tree. Distinct names only. -xlinka
    private void ResolveSprites()
    {
        _resolvedSprites.Clear();
        if (_spriteSet == null)
            return;
        for (int i = 0; i < _sprites.Count; i++)
        {
            var name = _sprites[i];
            if (string.IsNullOrEmpty(name) || _resolvedSprites.ContainsKey(name))
                continue;
            var sprite = _spriteSet.Get(name);
            var tex = sprite?.Texture.Target;
            if (sprite != null && tex != null)
            {
                var uv = sprite.UVRect.Value;
                // A frame sheet shows one frame at a time; the material moves the window along it.
                var asset = tex.Asset;
                if (asset != null && asset.AnimationFrames > 1)
                {
                    var cell = asset.AnimationFrameRect(0);
                    uv = new Rect(cell.x + uv.x * cell.width, cell.y + uv.y * cell.height, uv.width * cell.width, uv.height * cell.height);
                }
                _resolvedSprites[name] = (tex, uv, sprite.Material.Target);
            }
        }

        _spriteAspects.Clear();
        bool anyAspect = false;
        for (int i = 0; i < _sprites.Count; i++)
        {
            float aspect = 1f;
            var name = _sprites[i];
            if (!string.IsNullOrEmpty(name) && _resolvedSprites.TryGetValue(name, out var resolved))
            {
                var asset = resolved.texture.Asset;
                if (asset != null && asset.Width > 0 && asset.Height > 0)
                {
                    float w = resolved.uv.width * asset.Width;
                    float h = resolved.uv.height * asset.Height;
                    if (w > 0f && h > 0f)
                    {
                        aspect = w / h;
                        anyAspect = true;
                    }
                }
            }
            _spriteAspects.Add(aspect);
        }
        _spriteAspectsArg = anyAspect ? _spriteAspects : null;
    }

    // Re-shape on demand: a sprite's picture or material arrived after the row was laid out.
    public void Refresh()
    {
        RectTransform?.MarkChangeDirty();
    }

    // Draw the sprite named for this placeholder glyph as a textured quad at the reserved square, from the
    // main-thread snapshot. Draws nothing if the name wasn't resolved (no SpriteSet, or unknown name). -xlinka
    private void EmitSprite(GraphicsChunk.RenderData renderData, in TextShaper.PositionedGlyph glyph, float x, float y, Rect? clip)
    {
        if (glyph.SourceIndex < 0 || glyph.SourceIndex >= _sprites.Count)
            return;
        var name = _sprites[glyph.SourceIndex];
        if (name == null || !_resolvedSprites.TryGetValue(name, out var resolved))
            return;

        var m = glyph.Metrics;
        float x0 = x + m.Offset.x;
        float y0 = y + m.Offset.y;
        var rect = Rect.FromMinMax(new float2(x0, y0), new float2(x0 + m.Size.x, y0 + m.Size.y));
        var submesh = renderData.GetSubmesh(resolved.material, resolved.texture, GraphicsChunk.RenderData.ImageTexture);
        RawImage.GenerateImage(submesh.Mesh, submesh, rect, resolved.uv, null, false, in SpriteTint, clip);
        if (resolved.texture.Asset is { AnimationFrames: > 1 } sheet)
            renderData.NotePlaying(sheet.AnimationFps);
    }

    private static void EmitSolidQuad(GraphicsChunk.RenderData renderData, float x0, float y0, float x1, float y1, in color c, Rect? clip)
    {
        if (x1 <= x0 || y1 <= y0) return;
        var submesh = renderData.GetSubmesh(null, null, GraphicsChunk.RenderData.ImageTexture);
        RawImage.GenerateImage(submesh.Mesh, submesh, Rect.FromMinMax(new float2(x0, y0), new float2(x1, y1)), Rect.UnitRect, null, false, in c, clip);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static void PrepareMesh(PhosMesh mesh)
    {
        mesh.HasColors = true;
        mesh.SetHasUV(0, true);
    }
}
