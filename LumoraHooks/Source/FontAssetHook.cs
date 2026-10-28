// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using MathRect = Lumora.Core.Math.Rect;

namespace Lumora.Godot.Hooks;

[ImplementableHook(typeof(FontAsset))]
public sealed class FontAssetHook : AssetHook, IFontAssetHook
{
    // THE ATLAS GROWS.
    //
    // It used to be 2048 square from the first glyph: 16 MB of pixels here, 22 MB of video memory with its
    // mips, and the whole of it re-uploaded and re-mipped every time a glyph landed. A sign's font that
    // draws one word filled a corner of it. Now it starts at 512 and doubles when a glyph no longer fits,
    // copying what it has into the top-left of the larger one and scaling every stored UV to match; text
    // re-lays out on the generation bump, the same thing every new glyph already causes. -xlinka
    private const int MinAtlasSize = 512;
    private const int MaxAtlasSize = 2048;
    private const int Padding = 4;
    private int _atlasSize = MinAtlasSize;

    // One rasterization size for the whole atlas, scaled per Text on read. Coverage fonts raster at 96 so small
    // text has detail to downsample from; MSDF fonts raster at their msdf_size (a distance field is
    // resolution-independent, so the small base is enough and keeps the atlas tight). Set at load. -xlinka
    private int _rasterSize = 96;
    // Distance-field span the MSDF was generated with (the font's msdf_pixel_range); carried to the atlas + shader.
    private int _msdfPixelRange = DistanceRange;

    // What every font we draw is made into. 16 texels of field: at 8 a pixel of far panel text spanned the
    // whole field and edges went to mush. 64 em keeps corners on big headings square. -xlinka
    private const int DistanceRange = 16;
    private const int DistanceEm = 64;
    // MSDF was REQUESTED (imported with the flag, or a raw load asked for it) vs actually PRODUCED. The raw
    // dynamic rasterizer can silently emit LA8 coverage instead, so we confirm per-glyph before trusting it;
    // _isMsdf is what gates the distance-field path everywhere. -xlinka
    private bool _msdfIntended;
    private bool _msdfDetermined;
    private bool _isMsdf;

    private readonly Dictionary<int, GlyphEntry> _glyphs = new();
    private FontFile? _font;
    // Kept so a missing glyph can name the font it was missing from. -xlinka
    private string? _fontPath;
    // True only when WE created the FontFile (raw LoadDynamicFont). When the font came from ResourceLoader it's
    // a shared imported resource we must NOT dispose. -xlinka
    private bool _ownsFont;
    private TextureAsset? _atlasTexture;
    private byte[]? _atlasPixels;
    private int _penX;
    private int _penY;
    private int _rowHeight;
    private int _cacheGeneration;
    // Where this font's glyphs are kept between hooks and sessions (FontGlyphCache); null until a font loads.
    private string? _glyphKey;

    public bool IsValid => _font != null;
    public TextureAsset? AtlasTexture => _atlasTexture;
    public int PixelRange => _msdfPixelRange;
    public int CacheGeneration => _cacheGeneration;

    public void LoadFromFile(string path)
    {
        if (_ownsFont) _font?.Dispose();
        _font = null;
        _fontPath = path;
        _ownsFont = false;
        _glyphKey = null;
        _glyphs.Clear();
        ResetAtlas();

        // Godot strips the RAW source of imported files from exports (our font has a .ttf.import) and keeps only
        // the imported resource inside the .pck. But the engine resolved res:// to an OS DISK path
        // (ResourceRoot = GlobalizePath("res://")), which only has real files in the EDITOR - in an export that
        // dir is the exe folder and the font isn't there, so the raw read failed and text vanished. Recover the
        // res:// path and load the IMPORTED FontFile from the .pck via ResourceLoader; it carries the MSDF
        // import settings a raw load would not. Genuine external files (a user-imported font outside
        // ResourceRoot) still raw-load. -xlinka
        string? resPath = Lumora.Core.Engine.Current?.TryGetResourcePath(path);
        bool resExists = resPath != null && ResourceLoader.Exists(resPath);
        // DIAG (export-debug): shows the resolved path + whether the imported resource was found. Remove once
        // text is confirmed in the Linux/exported build. -xlinka
        Lumora.Core.Logging.Logger.Debug($"FontAssetHook.LoadFromFile: root='{Lumora.Core.Engine.Current?.ResourceRoot}' path='{path}' " +
                 $"resPath='{resPath}' resExists={resExists} rawExists={System.IO.File.Exists(path)}");
        if (resExists)
        {
            _font = ResourceLoader.Load<FontFile>(resPath);
            if (_font != null)
            {
                _ownsFont = false; // shared imported resource - do not dispose it
                Lumora.Core.Logging.Logger.Debug($"FontAssetHook: loaded imported FontFile from '{resPath}'");
                RequireDistanceField();
                ConfigureMsdf();
                _glyphKey = FontGlyphCache.KeyFor(resPath!, _rasterSize, _msdfPixelRange, _msdfIntended);
                return;
            }
            GD.PrintErr($"FontAssetHook: ResourceLoader.Load returned null for '{resPath}'");
        }

        _font = new FontFile();
        _ownsFont = true;
        var error = _font.LoadDynamicFont(path);
        if (error != Error.Ok)
        {
            error = _font.LoadBitmapFont(path);
        }

        if (error != Error.Ok)
        {
            _font.Dispose();
            _font = null;
            _ownsFont = false;
            GD.PrintErr($"FontAssetHook: Failed to load font '{path}' ({error})");
        }
        else
        {
            // Ask the raw rasterizer for MSDF as well. It may honor it (crisp) or silently emit LA8 coverage;
            // the per-glyph check in RequestGlyph downgrades _isMsdf when it falls back, so requesting it is
            // always safe. This has to happen AFTER the load: the loader resets the font's state first, so
            // anything set before it is thrown away and the font quietly comes up as coverage at raster 96.
            // That is how a fresh TTF with no import sidecar rendered soft next to the imported one. -xlinka
            _font.MultichannelSignedDistanceField = true;
            _font.MsdfPixelRange = DistanceRange;
            _font.MsdfSize = DistanceEm;
        }

        ConfigureMsdf();
        if (_font != null)
            _glyphKey = FontGlyphCache.KeyFor(path, _rasterSize, _msdfPixelRange, _msdfIntended);
        WarmCommonGlyphs();
    }

    // A font from the asset database arrives as bytes: its file there is sealed, so the renderer never gets a
    // path to it. Dynamic fonts only (TrueType, OpenType, WOFF); a bitmap font is a text file plus pictures and
    // has no single blob to hand over. -xlinka
    public void LoadFromData(byte[] data, string name)
    {
        if (_ownsFont) _font?.Dispose();
        _font = null;
        _fontPath = name;
        _ownsFont = false;
        _glyphKey = null;
        _glyphs.Clear();
        ResetAtlas();

        if (data == null || data.Length == 0)
        {
            GD.PrintErr($"FontAssetHook: no font data for '{name}'");
            return;
        }

        _font = new FontFile();
        _ownsFont = true;
        _font.Data = data;
        if (_font.GetFaceCount() <= 0)
        {
            _font.Dispose();
            _font = null;
            _ownsFont = false;
            GD.PrintErr($"FontAssetHook: '{name}' is not a font this renderer reads");
            return;
        }

        _font.MultichannelSignedDistanceField = true;
        _font.MsdfPixelRange = DistanceRange;
        _font.MsdfSize = DistanceEm;
        ConfigureMsdf();
        _glyphKey = FontGlyphCache.KeyFor(name, _rasterSize, _msdfPixelRange, _msdfIntended);
        WarmCommonGlyphs();
    }

    // A FONT THAT ARRIVES WITH SOMEBODY IS DRAWN BEFORE IT IS USED.
    //
    // Each glyph is made into a distance field the first time a canvas lays text out in it, on the main thread,
    // a few milliseconds a glyph: a guest's watch face in its own font, thirty-odd characters, held the frame
    // for a quarter of a second the moment they arrived. This runs on the loading thread before anything can
    // use the font, so the printable ASCII range is drawn into the font's own cache here and the first layout
    // only copies glyphs into the atlas. Glyphs already kept from an earlier session are skipped. Only a font
    // this hook loaded from its file: a shared imported font is in use elsewhere. -xlinka
    private void WarmCommonGlyphs()
    {
        if (_font == null || !_ownsFont)
            return;
        var sizeKey = new Vector2I(_rasterSize, 0);
        for (int codepoint = 0x21; codepoint <= 0x7E; codepoint++)
        {
            if (_glyphKey != null && FontGlyphCache.TryGet(_glyphKey, codepoint, out _))
                continue;
            int glyphIndex = GetGlyphIndex(codepoint, _rasterSize);
            if (glyphIndex != 0)
                _font.RenderGlyph(0, sizeKey, glyphIndex);
        }
    }

    // An imported font is drawn as a distance field whatever its import file says. The editor writes a fresh
    // .import with the field off, and that quietly turned every dash and inspector label into a coverage atlas:
    // soft from arm's length, strokes dropping out past it. The shared imported resource is left alone; the
    // copy is ours. -xlinka
    private void RequireDistanceField()
    {
        if (_font == null || (_font.MultichannelSignedDistanceField && _font.MsdfPixelRange >= DistanceRange))
            return;
        if (_font.Duplicate() is not FontFile copy)
            return;
        copy.MultichannelSignedDistanceField = true;
        copy.MsdfPixelRange = DistanceRange;
        copy.MsdfSize = DistanceEm;
        _font = copy;
        _ownsFont = true;
    }

    // Read MSDF intent + raster size from the loaded font. Imported fonts carry the flag from their import
    // settings; the raw path sets it above. Actual MSDF output is confirmed on the first real glyph. -xlinka
    private void ConfigureMsdf()
    {
        _msdfDetermined = false;
        _isMsdf = false;
        if (_font == null)
        {
            _msdfIntended = false;
            _rasterSize = 96;
            return;
        }

        _msdfIntended = _font.MultichannelSignedDistanceField;
        _msdfPixelRange = _font.MsdfPixelRange > 0 ? _font.MsdfPixelRange : DistanceRange;
        int msdfSize = _font.MsdfSize > 0 ? _font.MsdfSize : DistanceEm;
        // MSDF is resolution-independent, so raster at its native msdf_size; coverage needs a larger base to
        // downsample from. -xlinka
        _rasterSize = _msdfIntended ? msdfSize : 96;
    }

    // Confirm whether the font actually produced a distance field (varying RGB) vs coverage (flat white RGB with
    // detail in alpha). The raw dynamic rasterizer can silently ignore the MSDF request, so we never run the
    // distance reconstruction on coverage data. Runs once, on the first real glyph. -xlinka
    private void DetermineMsdf(byte[] rgba, int width, int height)
    {
        _msdfDetermined = true;
        _isMsdf = _msdfIntended && HasVaryingRgb(rgba, width, height);

        if (_atlasTexture != null)
        {
            _atlasTexture.IsMSDF = _isMsdf;
            _atlasTexture.MsdfPixelRange = _msdfPixelRange;
        }

        Lumora.Core.Logging.Logger.Debug($"FontAssetHook: MSDF intended={_msdfIntended} produced={_isMsdf} rasterSize={_rasterSize} pixelRange={_msdfPixelRange}");
    }

    private static bool HasVaryingRgb(byte[] rgba, int w, int h)
    {
        // Coverage glyphs are flat white in RGB (shape lives in alpha); an MSDF has per-channel distances, so
        // any pixel whose channels differ or dip below full is proof of a real distance field. -xlinka
        int stepY = Math.Max(1, h / 8);
        int stepX = Math.Max(1, w / 8);
        for (int j = 0; j < h; j += stepY)
        {
            for (int i = 0; i < w; i += stepX)
            {
                int p = (j * w + i) * 4;
                byte r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
                if (r != g || g != b || r < 253)
                    return true;
            }
        }
        return false;
    }

    public bool TryGetGlyph(int codepoint, float displaySize, out GlyphMetrics metrics, out MathRect uvRect)
    {
        if (_glyphs.TryGetValue(codepoint, out var entry))
        {
            if (entry.Missing)
            {
                metrics = default;
                uvRect = default;
                return false;
            }

            // Cached metrics are stored at _rasterSize; scale to the requested display size. - xlinka
            float scale = displaySize / _rasterSize;
            metrics = new GlyphMetrics
            {
                Advance = entry.Metrics.Advance * scale,
                Offset = entry.Metrics.Offset * scale,
                Size = entry.Metrics.Size * scale,
            };
            uvRect = entry.UVRect;
            return true;
        }

        metrics = default;
        uvRect = default;
        return false;
    }

    public void RequestGlyph(int codepoint, float displaySize)
    {
        if (_font == null)
        {
            return;
        }

        if (_glyphs.ContainsKey(codepoint))
        {
            return;
        }

        EnsureAtlas();

        if (_glyphKey != null && FontGlyphCache.TryGet(_glyphKey, codepoint, out var kept))
        {
            PlaceKeptGlyph(codepoint, kept);
            return;
        }

        int glyphIndex = GetGlyphIndex(codepoint, _rasterSize);
        if (glyphIndex == 0 && codepoint != '?')
        {
            // Text falls back to '?' for these, so without this line a missing symbol is untraceable.
            // The _glyphs cache means it fires once per codepoint per font. -xlinka
            Lumora.Core.Logging.Logger.Debug($"FontAssetHook: no glyph for U+{codepoint:X4} in '{_fontPath}'");
            StoreMissingGlyph(codepoint);
            Keep(codepoint, new FontGlyphCache.Glyph { Kind = FontGlyphCache.Missing });
            return;
        }

        if (glyphIndex == 0)
        {
            StoreBlankGlyph(codepoint);
            Keep(codepoint, new FontGlyphCache.Glyph { Kind = FontGlyphCache.Blank, Advance = _glyphs[codepoint].Metrics.Advance });
            return;
        }

        var sizeKey = new Vector2I(_rasterSize, 0);
        _font.RenderGlyph(0, sizeKey, glyphIndex);

        var glyphOffset = _font.GetGlyphOffset(0, sizeKey, glyphIndex);
        var advance = _font.GetGlyphAdvance(0, _rasterSize, glyphIndex);
        var sourceRect = _font.GetGlyphUVRect(0, sizeKey, glyphIndex);
        int textureIndex = _font.GetGlyphTextureIdx(0, sizeKey, glyphIndex);

        int width = Math.Max(0, (int)Math.Ceiling(sourceRect.Size.X));
        int height = Math.Max(0, (int)Math.Ceiling(sourceRect.Size.Y));
        if (width == 0 || height == 0)
        {
            StoreBlankGlyph(codepoint, advance.X);
            Keep(codepoint, new FontGlyphCache.Glyph { Kind = FontGlyphCache.Blank, Advance = _glyphs[codepoint].Metrics.Advance });
            return;
        }

        if (!Reserve(width, height, out int dstX, out int dstY))
        {
            StoreBlankGlyph(codepoint, advance.X);
            return;
        }

        var sourceImage = _font.GetTextureImage(0, sizeKey, textureIndex);
        if (sourceImage == null)
        {
            StoreBlankGlyph(codepoint, advance.X);
            return;
        }

        if (ReadGlyphRegion(sourceImage, sourceRect, width, height, out var rgba, out int regionX, out int regionY, out int regionWidth, out int regionHeight))
        {
            BlitGlyph(rgba, regionWidth, regionHeight, dstX + regionX, dstY + regionY);
            Keep(codepoint, new FontGlyphCache.Glyph
            {
                Kind = FontGlyphCache.Drawn,
                Advance = advance.X,
                OffsetX = glyphOffset.X,
                OffsetY = glyphOffset.Y,
                Width = width,
                Height = height,
                RegionX = regionX,
                RegionY = regionY,
                RegionWidth = regionWidth,
                RegionHeight = regionHeight,
                Pixels = rgba,
            });
        }
        ScheduleUpload();
        StoreDrawnGlyph(codepoint, advance.X, glyphOffset.X, glyphOffset.Y, width, height, dstX, dstY);
    }

    private void Keep(int codepoint, FontGlyphCache.Glyph glyph)
    {
        if (_glyphKey != null)
            FontGlyphCache.Put(_glyphKey, codepoint, glyph);
    }

    // A glyph this font made before, in this session or an earlier one: placed from its kept pixels.
    private void PlaceKeptGlyph(int codepoint, FontGlyphCache.Glyph kept)
    {
        if (kept.Kind == FontGlyphCache.Missing)
        {
            StoreMissingGlyph(codepoint);
            return;
        }
        if (kept.Kind == FontGlyphCache.Blank || kept.Pixels == null
            || !Reserve(kept.Width, kept.Height, out int dstX, out int dstY))
        {
            StoreBlankGlyph(codepoint, kept.Advance);
            return;
        }
        BlitGlyph(kept.Pixels, kept.RegionWidth, kept.RegionHeight, dstX + kept.RegionX, dstY + kept.RegionY);
        ScheduleUpload();
        StoreDrawnGlyph(codepoint, kept.Advance, kept.OffsetX, kept.OffsetY, kept.Width, kept.Height, dstX, dstY);
    }

    private void StoreDrawnGlyph(int codepoint, float advance, float offsetX, float offsetY, int width, int height, int dstX, int dstY)
    {
        // Metrics are stored at _rasterSize coordinates; TryGetGlyph scales on read. Quad size
        // matches the atlas region (width x height), so UV-to-quad ratio is 1:1. - xlinka
        var metrics = new GlyphMetrics
        {
            Advance = advance,
            Offset = new float2(offsetX, -offsetY - height),
            Size = new float2(width, height)
        };

        var uv = new MathRect(
            dstX / (float)_atlasSize,
            dstY / (float)_atlasSize,
            width / (float)_atlasSize,
            height / (float)_atlasSize
        );

        _glyphs[codepoint] = new GlyphEntry(metrics, uv, Missing: false);
        _cacheGeneration++;
    }

    // The font's height, ascent and descent at the raster size, read once. Every line of every text asked the
    // font server for them again, and an imported menu's first layout spent a fifth of its time in those calls.
    private float _rawHeight = float.NaN, _rawAscent = float.NaN, _rawDescent = float.NaN;
    private FontFile? _metricsFont;
    private int _metricsSize;

    private void EnsureLineMetrics()
    {
        if (ReferenceEquals(_metricsFont, _font) && _metricsSize == _rasterSize && !float.IsNaN(_rawHeight))
            return;
        _metricsFont = _font;
        _metricsSize = _rasterSize;
        _rawHeight = _font!.GetHeight(_rasterSize);
        _rawAscent = _font.GetAscent(_rasterSize);
        _rawDescent = _font.GetDescent(_rasterSize);
    }

    public float GetLineHeight(float displaySize)
    {
        if (_font == null) return displaySize;
        EnsureLineMetrics();
        return _rawHeight * displaySize / _rasterSize;
    }

    public float GetAscent(float displaySize)
    {
        if (_font == null) return displaySize * 0.8f;
        EnsureLineMetrics();
        return _rawAscent * displaySize / _rasterSize;
    }

    public float GetDescent(float displaySize)
    {
        if (_font == null) return displaySize * 0.2f;
        EnsureLineMetrics();
        return _rawDescent * displaySize / _rasterSize;
    }

    public float GetKerning(int leftCodepoint, int rightCodepoint, float displaySize)
    {
        if (_font == null)
        {
            return 0f;
        }

        int left = GetGlyphIndex(leftCodepoint, _rasterSize);
        int right = GetGlyphIndex(rightCodepoint, _rasterSize);
        if (left == 0 || right == 0)
        {
            return 0f;
        }

        // Kerning is queried at _rasterSize and scaled to the display size, same path as metrics, with one
        // catch: the font server hands the pair back in 26.6 fixed point (64ths of a pixel), not pixels
        // like the advances. Monospace fonts have no kern table so this never showed; the first
        // proportional face kerned "Yo" by -704 units and pushed the rest of the word off the panel. -xlinka
        var kerning = _font.GetKerning(0, _rasterSize, new Vector2I(left, right));
        return kerning.X / 64f * displaySize / _rasterSize;
    }

    public override void Unload()
    {
        if (_ownsFont) _font?.Dispose();
        _font = null;
        _ownsFont = false;
        _atlasTexture?.Unload();
        _atlasTexture = null;
        _atlasPixels = null;
        _glyphs.Clear();
    }

    private int GetGlyphIndex(int codepoint, int pixelSize)
    {
        if (_font == null)
        {
            return 0;
        }

        return _font.GetGlyphIndex(pixelSize, codepoint, 0);
    }

    private void StoreBlankGlyph(int codepoint, float advance = 0f)
    {
        if (_font != null && advance <= 0f)
        {
            advance = _font.GetCharSize(codepoint, _rasterSize).X;
        }

        if (advance <= 0f)
        {
            advance = _rasterSize * 0.5f;
        }

        _glyphs[codepoint] = new GlyphEntry(
            new GlyphMetrics { Advance = advance, Offset = float2.Zero, Size = float2.Zero },
            MathRect.Zero,
            Missing: false
        );
        _cacheGeneration++;
    }

    private void StoreMissingGlyph(int codepoint)
    {
        _glyphs[codepoint] = new GlyphEntry(default, MathRect.Zero, Missing: true);
        _cacheGeneration++;
    }

    private void EnsureAtlas()
    {
        if (_atlasTexture != null && _atlasPixels != null)
        {
            return;
        }

        _atlasTexture = new TextureAsset();
        _atlasTexture.InitializeDynamic();
        ResetAtlas();
    }

    private void ResetAtlas()
    {
        _atlasSize = MinAtlasSize;
        _atlasPixels = new byte[_atlasSize * _atlasSize * 4];
        _penX = Padding;
        _penY = Padding;
        _rowHeight = 0;
        _cacheGeneration++;
        UploadAtlas();
    }

    private bool Reserve(int width, int height, out int x, out int y)
    {
        if (_penX + width + Padding > _atlasSize)
        {
            _penX = Padding;
            _penY += _rowHeight + Padding;
            _rowHeight = 0;
        }

        while (_penY + height + Padding > _atlasSize || width + 2 * Padding > _atlasSize)
        {
            if (_atlasSize >= MaxAtlasSize)
            {
                x = 0;
                y = 0;
                return false;
            }
            Grow();
        }

        x = _penX;
        y = _penY;
        _penX += width + Padding;
        _rowHeight = Math.Max(_rowHeight, height);
        return true;
    }

    // Doubles the atlas. The shelf the pen is on simply gets longer; the space to the right of the rows
    // above stays unused, which costs at most what the old atlas was.
    private void Grow()
    {
        if (_atlasPixels == null)
            return;
        int old = _atlasSize;
        int size = old * 2;
        var pixels = new byte[size * size * 4];
        for (int row = 0; row < old; row++)
            Buffer.BlockCopy(_atlasPixels, row * old * 4, pixels, row * size * 4, old * 4);
        _atlasPixels = pixels;
        _atlasSize = size;

        float scale = old / (float)size;
        var codepoints = new List<int>(_glyphs.Keys);
        foreach (int codepoint in codepoints)
        {
            var entry = _glyphs[codepoint];
            var uv = entry.UVRect;
            _glyphs[codepoint] = entry with { UVRect = new MathRect(uv.x * scale, uv.y * scale, uv.width * scale, uv.height * scale) };
        }
        _cacheGeneration++;
        ScheduleUpload();
        Lumora.Core.Logging.Logger.Debug($"FontAssetHook: atlas for '{_fontPath}' grew to {size} ({_glyphs.Count} glyphs)");
    }

    // The glyph's rectangle is read in one piece. Asking for it a pixel at a time was a call into the platform
    // for each of the nine thousand or so pixels of a distance-field glyph, and an imported menu's first draw
    // spent half a second on it. The font's own image is shared with the font server, so only the copy of the
    // rectangle is converted, never the image itself. -xlinka
    // The glyph's pixels as RGBA, and where they sit inside its width x height box.
    private static bool ReadGlyphRegion(Image source, Rect2 sourceRect, int width, int height, out byte[] rgba,
        out int regionX, out int regionY, out int regionWidth, out int regionHeight)
    {
        rgba = Array.Empty<byte>();
        int srcX = (int)Math.Floor(sourceRect.Position.X);
        int srcY = (int)Math.Floor(sourceRect.Position.Y);
        int x0 = Math.Max(0, srcX);
        int y0 = Math.Max(0, srcY);
        int x1 = Math.Min(source.GetWidth(), srcX + width);
        int y1 = Math.Min(source.GetHeight(), srcY + height);
        regionX = x0 - srcX;
        regionY = y0 - srcY;
        regionWidth = x1 - x0;
        regionHeight = y1 - y0;
        if (x1 <= x0 || y1 <= y0)
            return false;

        using (var region = source.GetRegion(new Rect2I(x0, y0, regionWidth, regionHeight)))
        {
            if (region.GetFormat() != Image.Format.Rgba8)
                region.Convert(Image.Format.Rgba8);
            rgba = region.GetData();
        }
        return rgba.Length >= regionWidth * regionHeight * 4;
    }

    private void BlitGlyph(byte[] rgba, int w, int h, int atlasX, int atlasY)
    {
        if (_atlasPixels == null)
        {
            return;
        }

        if (!_msdfDetermined)
        {
            DetermineMsdf(rgba, w, h);
        }

        for (int y = 0; y < h; y++)
        {
            int src = y * w * 4;
            int dst = ((atlasY + y) * _atlasSize + atlasX) * 4;
            for (int x = 0; x < w; x++, src += 4, dst += 4)
            {
                if (_isMsdf)
                {
                    // Keep the 3-channel distance field in RGB; the shader rebuilds the glyph from their median.
                    // Alpha carries no shape, so leave it opaque. - xlinka
                    _atlasPixels[dst + 0] = rgba[src + 0];
                    _atlasPixels[dst + 1] = rgba[src + 1];
                    _atlasPixels[dst + 2] = rgba[src + 2];
                    _atlasPixels[dst + 3] = 255;
                }
                else
                {
                    // Coverage path: the rasterizer outputs LA8 (flat white RGB, coverage in alpha). Store
                    // coverage in our atlas alpha; the shader applies fwidth-based AA on it. - xlinka
                    _atlasPixels[dst + 0] = 255;
                    _atlasPixels[dst + 1] = 255;
                    _atlasPixels[dst + 2] = 255;
                    _atlasPixels[dst + 3] = rgba[src + 3];
                }
            }
        }
    }

    private bool _uploadPending;

    private void ScheduleUpload()
    {
        if (_uploadPending)
            return;
        _uploadPending = true;
        Callable.From(FlushAtlasUpload).CallDeferred();
    }

    private void FlushAtlasUpload()
    {
        _uploadPending = false;
        UploadAtlas();
    }

    private void UploadAtlas()
    {
        if (_atlasTexture != null && _atlasPixels != null)
        {
            _atlasTexture.SetImageData(_atlasPixels, _atlasSize, _atlasSize, true);
        }
    }

    private readonly record struct GlyphEntry(GlyphMetrics Metrics, MathRect UVRect, bool Missing);
}
