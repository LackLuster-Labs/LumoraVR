using System;
using System.Collections.Generic;
using Lumora.Core.Math;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Phos;
using Lumora.Core.Logging;

namespace Lumora.Core.HelioUI.Rendering;

/// <summary>
/// UI mesh generator. Builds a batched quad mesh for Helio UI elements.
/// Panels, images, and text are drawn as colored quads.
/// Text uses an optimized 8x12 bitmap font with run-length merging for efficiency.
/// </summary>
public class HelioUIMesh : ProceduralMesh
{
    public readonly Sync<float2> CanvasSize;
    public readonly Sync<float> PixelScale;
    public readonly Sync<color> BackgroundColor;

    private HelioCanvas _canvas;
    private Slot _rendererSlot;
    private PhosTriangleSubmesh _submesh;

    private List<RenderItem> _cachedItems = new();
    private int _cachedQuadCount = 0;
    private float2 _cachedRefSize;
    private float _cachedPixelScale;

    public HelioUIMesh()
    {
        CanvasSize = new Sync<float2>(this, new float2(400f, 600f));
        PixelScale = new Sync<float>(this, 100f);
        BackgroundColor = new Sync<color>(this, new color(0.1f, 0.1f, 0.12f, 0.95f));
    }

    public void SetCanvas(HelioCanvas canvas)
    {
        _canvas = canvas;
    }

    public void SetRendererSlot(Slot rendererSlot)
    {
        _rendererSlot = rendererSlot;
    }

    public override void OnAwake()
    {
        base.OnAwake();
        // NOTE: Do NOT use SubscribeToChanges here!
        // The Canvas manages when to regenerate mesh via RequestVisualRebuild.
        // Auto-subscribing would cause double regeneration and kill performance.
    }

    protected override void PrepareAssetUpdateData()
    {
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        if (_canvas == null || _canvas.Slot == null)
            return;

        _cachedRefSize = CanvasSize.Value;
        _cachedPixelScale = PixelScale.Value <= 0.0001f ? 100f : PixelScale.Value;

        var newItems = new List<RenderItem>();
        float z = 0f;

        // Canvas background
        newItems.Add(RenderItem.MakeQuad(new HelioRect(float2.Zero, _cachedRefSize), BackgroundColor.Value, z));
        z += Z_INCREMENT;

        CollectItems(_canvas.Slot, newItems, ref z);

        int totalQuads = 0;
        foreach (var item in newItems)
            totalQuads += item.QuadCount;

        bool structureChanged = totalQuads != _cachedQuadCount || newItems.Count != _cachedItems.Count;
        if (!structureChanged)
        {
            for (int i = 0; i < newItems.Count; i++)
            {
                if (!_cachedItems[i].SameStructure(newItems[i]))
                {
                    structureChanged = true;
                    break;
                }
            }
        }

        _cachedItems = newItems;
        _cachedQuadCount = totalQuads;

        int totalVertices = totalQuads * 4;

        if (structureChanged || _submesh == null)
        {
            RebuildTopology(mesh, totalQuads, totalVertices);
            uploadHint[MeshUploadHint.Flag.Geometry] = true;
        }
        else
        {
            uploadHint[MeshUploadHint.Flag.Geometry] = false; // topology unchanged
        }

        // Always rewrite vertex attributes for now (safe, simple)
        WriteVertexData(mesh, _cachedItems);

        uploadHint[MeshUploadHint.Flag.Colors] = true;
        uploadHint[MeshUploadHint.Flag.Normals] = true;
        uploadHint[MeshUploadHint.Flag.Tangents] = true;
        uploadHint[MeshUploadHint.Flag.UV0] = true;
    }

    private void CollectItems(Slot slot, List<RenderItem> items, ref float z)
    {
        if (slot == null || slot.ActiveSelf.Value == false)
            return;

        if (_rendererSlot != null && slot == _rendererSlot)
            return;

        var rect = slot.GetComponent<HelioRectTransform>();
        if (rect != null)
        {
            var computedRect = rect.Rect;

            var panel = slot.GetComponent<HelioPanel>();
            if (panel != null)
            {
                float borderWidth = panel.BorderWidth?.Value ?? 0f;
                color bgColor = panel.BackgroundColor.Value;
                color borderColor = panel.BorderColor?.Value ?? HelioUITheme.PanelBorder;

                if (borderWidth > 0f)
                {
                    float clampedBorder = MathF.Max(0f, MathF.Min(borderWidth, MathF.Min(computedRect.Size.x * 0.5f, computedRect.Size.y * 0.5f)));
                    var innerRect = new HelioRect(
                        computedRect.Min + new float2(clampedBorder, clampedBorder),
                        computedRect.Size - new float2(clampedBorder * 2f, clampedBorder * 2f));

                    if (innerRect.Size.x > 0f && innerRect.Size.y > 0f)
                    {
                        items.Add(RenderItem.MakeQuad(innerRect, bgColor, z));
                        z += Z_INCREMENT;
                    }
                    else
                    {
                        items.Add(RenderItem.MakeQuad(computedRect, bgColor, z));
                        z += Z_INCREMENT;
                    }

                    items.Add(RenderItem.MakeQuad(new HelioRect(
                        new float2(computedRect.Min.x, computedRect.Max.y - clampedBorder),
                        new float2(computedRect.Size.x, clampedBorder)), borderColor, z));
                    items.Add(RenderItem.MakeQuad(new HelioRect(
                        computedRect.Min,
                        new float2(computedRect.Size.x, clampedBorder)), borderColor, z));
                    items.Add(RenderItem.MakeQuad(new HelioRect(
                        computedRect.Min,
                        new float2(clampedBorder, computedRect.Size.y)), borderColor, z));
                    items.Add(RenderItem.MakeQuad(new HelioRect(
                        new float2(computedRect.Max.x - clampedBorder, computedRect.Min.y),
                        new float2(clampedBorder, computedRect.Size.y)), borderColor, z));
                    z += Z_INCREMENT;
                }
                else
                {
                    items.Add(RenderItem.MakeQuad(computedRect, bgColor, z));
                    z += Z_INCREMENT;
                }
            }

            var image = slot.GetComponent<HelioImage>();
            if (image != null)
            {
                items.Add(RenderItem.MakeQuad(computedRect, image.Tint.Value, z));
                z += Z_INCREMENT;
            }

            var text = slot.GetComponent<HelioText>();
            if (text != null)
            {
                var renderItem = RenderItem.MakeText(computedRect, text, z, BuildLines(text.Content.Value, GetCharAdvance(text), computedRect.Size.x, text.Overflow.Value));
                items.Add(renderItem);
                if (renderItem.QuadCount > 0)
                    z += renderItem.QuadCount * Z_INCREMENT;
            }
        }

        foreach (var child in slot.Children)
        {
            CollectItems(child, items, ref z);
        }
    }

    // Z increment per quad - very small to keep UI essentially flat
    // Even with 10000 quads, total depth is only 0.01 world units
    private const float Z_INCREMENT = 0.000001f;

    private List<string> BuildLines(string content, float charAdvance, float maxWidth, TextOverflow overflow)
    {
        var lines = new List<string>();
        var currentLine = "";

        foreach (char c in content)
        {
            if (c == '\n')
            {
                lines.Add(currentLine);
                currentLine = "";
                continue;
            }

            if (overflow == TextOverflow.Wrap)
            {
                float nextWidth = (currentLine.Length + 1) * charAdvance;
                if (nextWidth > maxWidth && currentLine.Length > 0)
                {
                    // Try word wrap - find last space
                    int lastSpace = currentLine.LastIndexOf(' ');
                    if (lastSpace > 0 && c != ' ')
                    {
                        lines.Add(currentLine.Substring(0, lastSpace));
                        currentLine = currentLine.Substring(lastSpace + 1) + c;
                    }
                    else
                    {
                        lines.Add(currentLine);
                        currentLine = c.ToString();
                    }
                    continue;
                }
            }

            currentLine += c;
        }

        if (currentLine.Length > 0)
            lines.Add(currentLine);

        return lines;
    }

    // ===== Font Data - 8x12 bitmap font stored as bytes (1 bit per pixel) =====
    private const int FONT_WIDTH = 8;
    private const int FONT_HEIGHT = 12;

    private static readonly Dictionary<char, byte[]> GlyphData = BuildGlyphData();

    private static bool TryGetGlyph(char c, out byte[] glyph)
    {
        // Try exact match first
        if (GlyphData.TryGetValue(c, out glyph))
            return true;

        // Try uppercase
        if (GlyphData.TryGetValue(char.ToUpperInvariant(c), out glyph))
            return true;

        // Return space for unknown characters
        glyph = GlyphData.GetValueOrDefault(' ', new byte[FONT_HEIGHT]);
        return true;
    }

    /// <summary>
    /// Build glyph data. Each glyph is 8x12 pixels stored as 12 bytes (1 byte per row).
    /// Bit 7 (MSB) is leftmost pixel, bit 0 is rightmost.
    /// </summary>
    private static Dictionary<char, byte[]> BuildGlyphData()
    {
        var d = new Dictionary<char, byte[]>();

        // Uppercase letters - clean, readable 8x12 design
        d['A'] = new byte[] { 0x00, 0x18, 0x3C, 0x66, 0x66, 0x7E, 0x66, 0x66, 0x66, 0x66, 0x00, 0x00 };
        d['B'] = new byte[] { 0x00, 0x7C, 0x66, 0x66, 0x7C, 0x66, 0x66, 0x66, 0x66, 0x7C, 0x00, 0x00 };
        d['C'] = new byte[] { 0x00, 0x3C, 0x66, 0x60, 0x60, 0x60, 0x60, 0x60, 0x66, 0x3C, 0x00, 0x00 };
        d['D'] = new byte[] { 0x00, 0x78, 0x6C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x6C, 0x78, 0x00, 0x00 };
        d['E'] = new byte[] { 0x00, 0x7E, 0x60, 0x60, 0x7C, 0x60, 0x60, 0x60, 0x60, 0x7E, 0x00, 0x00 };
        d['F'] = new byte[] { 0x00, 0x7E, 0x60, 0x60, 0x7C, 0x60, 0x60, 0x60, 0x60, 0x60, 0x00, 0x00 };
        d['G'] = new byte[] { 0x00, 0x3C, 0x66, 0x60, 0x60, 0x6E, 0x66, 0x66, 0x66, 0x3E, 0x00, 0x00 };
        d['H'] = new byte[] { 0x00, 0x66, 0x66, 0x66, 0x7E, 0x66, 0x66, 0x66, 0x66, 0x66, 0x00, 0x00 };
        d['I'] = new byte[] { 0x00, 0x3C, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x3C, 0x00, 0x00 };
        d['J'] = new byte[] { 0x00, 0x1E, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x6C, 0x6C, 0x38, 0x00, 0x00 };
        d['K'] = new byte[] { 0x00, 0x66, 0x6C, 0x78, 0x70, 0x78, 0x6C, 0x66, 0x66, 0x66, 0x00, 0x00 };
        d['L'] = new byte[] { 0x00, 0x60, 0x60, 0x60, 0x60, 0x60, 0x60, 0x60, 0x60, 0x7E, 0x00, 0x00 };
        d['M'] = new byte[] { 0x00, 0x63, 0x77, 0x7F, 0x6B, 0x63, 0x63, 0x63, 0x63, 0x63, 0x00, 0x00 };
        d['N'] = new byte[] { 0x00, 0x66, 0x76, 0x7E, 0x7E, 0x6E, 0x66, 0x66, 0x66, 0x66, 0x00, 0x00 };
        d['O'] = new byte[] { 0x00, 0x3C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x00, 0x00 };
        d['P'] = new byte[] { 0x00, 0x7C, 0x66, 0x66, 0x66, 0x7C, 0x60, 0x60, 0x60, 0x60, 0x00, 0x00 };
        d['Q'] = new byte[] { 0x00, 0x3C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x6E, 0x3C, 0x0E, 0x00, 0x00 };
        d['R'] = new byte[] { 0x00, 0x7C, 0x66, 0x66, 0x7C, 0x78, 0x6C, 0x66, 0x66, 0x66, 0x00, 0x00 };
        d['S'] = new byte[] { 0x00, 0x3C, 0x66, 0x60, 0x3C, 0x06, 0x06, 0x66, 0x66, 0x3C, 0x00, 0x00 };
        d['T'] = new byte[] { 0x00, 0x7E, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x00, 0x00 };
        d['U'] = new byte[] { 0x00, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x00, 0x00 };
        d['V'] = new byte[] { 0x00, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x3C, 0x18, 0x00, 0x00 };
        d['W'] = new byte[] { 0x00, 0x63, 0x63, 0x63, 0x63, 0x6B, 0x7F, 0x77, 0x63, 0x63, 0x00, 0x00 };
        d['X'] = new byte[] { 0x00, 0x66, 0x66, 0x3C, 0x18, 0x18, 0x3C, 0x66, 0x66, 0x66, 0x00, 0x00 };
        d['Y'] = new byte[] { 0x00, 0x66, 0x66, 0x66, 0x3C, 0x18, 0x18, 0x18, 0x18, 0x18, 0x00, 0x00 };
        d['Z'] = new byte[] { 0x00, 0x7E, 0x06, 0x0C, 0x18, 0x30, 0x60, 0x60, 0x60, 0x7E, 0x00, 0x00 };

        // Lowercase letters
        d['a'] = new byte[] { 0x00, 0x00, 0x00, 0x3C, 0x06, 0x3E, 0x66, 0x66, 0x66, 0x3E, 0x00, 0x00 };
        d['b'] = new byte[] { 0x00, 0x60, 0x60, 0x7C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x7C, 0x00, 0x00 };
        d['c'] = new byte[] { 0x00, 0x00, 0x00, 0x3C, 0x66, 0x60, 0x60, 0x60, 0x66, 0x3C, 0x00, 0x00 };
        d['d'] = new byte[] { 0x00, 0x06, 0x06, 0x3E, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3E, 0x00, 0x00 };
        d['e'] = new byte[] { 0x00, 0x00, 0x00, 0x3C, 0x66, 0x7E, 0x60, 0x60, 0x66, 0x3C, 0x00, 0x00 };
        d['f'] = new byte[] { 0x00, 0x1C, 0x36, 0x30, 0x7C, 0x30, 0x30, 0x30, 0x30, 0x30, 0x00, 0x00 };
        d['g'] = new byte[] { 0x00, 0x00, 0x00, 0x3E, 0x66, 0x66, 0x66, 0x3E, 0x06, 0x66, 0x3C, 0x00 };
        d['h'] = new byte[] { 0x00, 0x60, 0x60, 0x7C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x00, 0x00 };
        d['i'] = new byte[] { 0x00, 0x18, 0x00, 0x38, 0x18, 0x18, 0x18, 0x18, 0x18, 0x3C, 0x00, 0x00 };
        d['j'] = new byte[] { 0x00, 0x0C, 0x00, 0x1C, 0x0C, 0x0C, 0x0C, 0x0C, 0x6C, 0x6C, 0x38, 0x00 };
        d['k'] = new byte[] { 0x00, 0x60, 0x60, 0x66, 0x6C, 0x78, 0x78, 0x6C, 0x66, 0x66, 0x00, 0x00 };
        d['l'] = new byte[] { 0x00, 0x38, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x3C, 0x00, 0x00 };
        d['m'] = new byte[] { 0x00, 0x00, 0x00, 0x76, 0x7F, 0x6B, 0x6B, 0x6B, 0x63, 0x63, 0x00, 0x00 };
        d['n'] = new byte[] { 0x00, 0x00, 0x00, 0x7C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x00, 0x00 };
        d['o'] = new byte[] { 0x00, 0x00, 0x00, 0x3C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x00, 0x00 };
        d['p'] = new byte[] { 0x00, 0x00, 0x00, 0x7C, 0x66, 0x66, 0x66, 0x7C, 0x60, 0x60, 0x60, 0x00 };
        d['q'] = new byte[] { 0x00, 0x00, 0x00, 0x3E, 0x66, 0x66, 0x66, 0x3E, 0x06, 0x06, 0x06, 0x00 };
        d['r'] = new byte[] { 0x00, 0x00, 0x00, 0x7C, 0x66, 0x60, 0x60, 0x60, 0x60, 0x60, 0x00, 0x00 };
        d['s'] = new byte[] { 0x00, 0x00, 0x00, 0x3E, 0x60, 0x3C, 0x06, 0x06, 0x66, 0x3C, 0x00, 0x00 };
        d['t'] = new byte[] { 0x00, 0x30, 0x30, 0x7C, 0x30, 0x30, 0x30, 0x30, 0x36, 0x1C, 0x00, 0x00 };
        d['u'] = new byte[] { 0x00, 0x00, 0x00, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x3E, 0x00, 0x00 };
        d['v'] = new byte[] { 0x00, 0x00, 0x00, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x3C, 0x18, 0x00, 0x00 };
        d['w'] = new byte[] { 0x00, 0x00, 0x00, 0x63, 0x63, 0x6B, 0x6B, 0x7F, 0x36, 0x36, 0x00, 0x00 };
        d['x'] = new byte[] { 0x00, 0x00, 0x00, 0x66, 0x66, 0x3C, 0x18, 0x3C, 0x66, 0x66, 0x00, 0x00 };
        d['y'] = new byte[] { 0x00, 0x00, 0x00, 0x66, 0x66, 0x66, 0x66, 0x3E, 0x06, 0x66, 0x3C, 0x00 };
        d['z'] = new byte[] { 0x00, 0x00, 0x00, 0x7E, 0x0C, 0x18, 0x30, 0x60, 0x60, 0x7E, 0x00, 0x00 };

        // Numbers
        d['0'] = new byte[] { 0x00, 0x3C, 0x66, 0x66, 0x6E, 0x76, 0x66, 0x66, 0x66, 0x3C, 0x00, 0x00 };
        d['1'] = new byte[] { 0x00, 0x18, 0x38, 0x78, 0x18, 0x18, 0x18, 0x18, 0x18, 0x7E, 0x00, 0x00 };
        d['2'] = new byte[] { 0x00, 0x3C, 0x66, 0x06, 0x0C, 0x18, 0x30, 0x60, 0x60, 0x7E, 0x00, 0x00 };
        d['3'] = new byte[] { 0x00, 0x3C, 0x66, 0x06, 0x1C, 0x06, 0x06, 0x06, 0x66, 0x3C, 0x00, 0x00 };
        d['4'] = new byte[] { 0x00, 0x0C, 0x1C, 0x3C, 0x6C, 0x6C, 0x7E, 0x0C, 0x0C, 0x0C, 0x00, 0x00 };
        d['5'] = new byte[] { 0x00, 0x7E, 0x60, 0x60, 0x7C, 0x06, 0x06, 0x06, 0x66, 0x3C, 0x00, 0x00 };
        d['6'] = new byte[] { 0x00, 0x3C, 0x66, 0x60, 0x7C, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x00, 0x00 };
        d['7'] = new byte[] { 0x00, 0x7E, 0x06, 0x0C, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x00, 0x00 };
        d['8'] = new byte[] { 0x00, 0x3C, 0x66, 0x66, 0x3C, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x00, 0x00 };
        d['9'] = new byte[] { 0x00, 0x3C, 0x66, 0x66, 0x66, 0x3E, 0x06, 0x06, 0x66, 0x3C, 0x00, 0x00 };

        // Punctuation and symbols
        d[' '] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        d['.'] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x00, 0x00 };
        d[','] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x08, 0x10, 0x00 };
        d[':'] = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x18, 0x00, 0x00, 0x18, 0x18, 0x00, 0x00, 0x00 };
        d[';'] = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x18, 0x00, 0x00, 0x18, 0x18, 0x08, 0x10, 0x00 };
        d['!'] = new byte[] { 0x00, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x00, 0x18, 0x18, 0x00, 0x00 };
        d['?'] = new byte[] { 0x00, 0x3C, 0x66, 0x06, 0x0C, 0x18, 0x18, 0x00, 0x18, 0x18, 0x00, 0x00 };
        d['-'] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x7E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        d['_'] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x7E, 0x00, 0x00 };
        d['+'] = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x18, 0x7E, 0x18, 0x18, 0x00, 0x00, 0x00, 0x00 };
        d['='] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x7E, 0x00, 0x7E, 0x00, 0x00, 0x00, 0x00, 0x00 };
        d['*'] = new byte[] { 0x00, 0x00, 0x66, 0x3C, 0xFF, 0x3C, 0x66, 0x00, 0x00, 0x00, 0x00, 0x00 };
        d['/'] = new byte[] { 0x00, 0x02, 0x06, 0x0C, 0x18, 0x30, 0x60, 0xC0, 0x80, 0x00, 0x00, 0x00 };
        d['\\'] = new byte[] { 0x00, 0x80, 0xC0, 0x60, 0x30, 0x18, 0x0C, 0x06, 0x02, 0x00, 0x00, 0x00 };
        d['('] = new byte[] { 0x00, 0x0C, 0x18, 0x30, 0x30, 0x30, 0x30, 0x30, 0x18, 0x0C, 0x00, 0x00 };
        d[')'] = new byte[] { 0x00, 0x30, 0x18, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x18, 0x30, 0x00, 0x00 };
        d['['] = new byte[] { 0x00, 0x3C, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x3C, 0x00, 0x00 };
        d[']'] = new byte[] { 0x00, 0x3C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x3C, 0x00, 0x00 };
        d['{'] = new byte[] { 0x00, 0x0E, 0x18, 0x18, 0x70, 0x18, 0x18, 0x18, 0x18, 0x0E, 0x00, 0x00 };
        d['}'] = new byte[] { 0x00, 0x70, 0x18, 0x18, 0x0E, 0x18, 0x18, 0x18, 0x18, 0x70, 0x00, 0x00 };
        d['<'] = new byte[] { 0x00, 0x00, 0x06, 0x1C, 0x70, 0x70, 0x1C, 0x06, 0x00, 0x00, 0x00, 0x00 };
        d['>'] = new byte[] { 0x00, 0x00, 0x60, 0x38, 0x0E, 0x0E, 0x38, 0x60, 0x00, 0x00, 0x00, 0x00 };
        d['\''] = new byte[] { 0x00, 0x18, 0x18, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        d['"'] = new byte[] { 0x00, 0x66, 0x66, 0x66, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        d['`'] = new byte[] { 0x00, 0x30, 0x18, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        d['~'] = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x76, 0xDC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        d['@'] = new byte[] { 0x00, 0x3C, 0x66, 0x6E, 0x6E, 0x6E, 0x60, 0x60, 0x66, 0x3C, 0x00, 0x00 };
        d['#'] = new byte[] { 0x00, 0x00, 0x36, 0x36, 0x7F, 0x36, 0x7F, 0x36, 0x36, 0x00, 0x00, 0x00 };
        d['$'] = new byte[] { 0x00, 0x18, 0x3E, 0x60, 0x3C, 0x06, 0x7C, 0x18, 0x18, 0x00, 0x00, 0x00 };
        d['%'] = new byte[] { 0x00, 0x00, 0x62, 0x66, 0x0C, 0x18, 0x30, 0x66, 0x46, 0x00, 0x00, 0x00 };
        d['&'] = new byte[] { 0x00, 0x38, 0x6C, 0x38, 0x30, 0x7A, 0xCC, 0xCC, 0xCC, 0x76, 0x00, 0x00 };
        d['^'] = new byte[] { 0x00, 0x18, 0x3C, 0x66, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        d['|'] = new byte[] { 0x00, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x00, 0x00 };

        return d;
    }

    protected override void ClearMeshData()
    {
    }

    // ===== Internal helpers =====

    private float GetCharAdvance(HelioText text)
    {
        float fontSize = MathF.Max(8f, text.FontSize.Value);
        float charWidth = fontSize * ((float)FONT_WIDTH / FONT_HEIGHT);
        return charWidth * 1.15f; // includes spacing
    }

    private void RebuildTopology(PhosMesh mesh, int totalQuads, int totalVertices)
    {
        mesh.Clear();
        mesh.HasNormals = true;
        mesh.HasTangents = true;
        mesh.HasUV0s = true;
        mesh.HasColors = true;
        mesh.IncreaseVertexCount(totalVertices);

        mesh.Submeshes.Clear();
        _submesh = new PhosTriangleSubmesh(mesh);
        mesh.Submeshes.Add(_submesh);

        // Build triangle indices once
        _submesh.AddTriangles(totalQuads * 2);
        int triangleIndex = 0;
        int vertexIndex = 0;
        for (int i = 0; i < totalQuads; i++)
        {
            _submesh.SetQuadAsTriangles(
                vertexIndex + 0,
                vertexIndex + 1,
                vertexIndex + 2,
                vertexIndex + 3,
                triangleIndex,
                triangleIndex + 1
            );
            vertexIndex += 4;
            triangleIndex += 2;
        }
    }

    private void WriteVertexData(PhosMesh mesh, List<RenderItem> items)
    {
        int v = 0;
        float zCursor = 0f; // items already carry z but we still step for text glyphs
        foreach (var item in items)
        {
            if (item.Kind == RenderKind.Quad)
            {
                WriteQuad(mesh, v, item.Rect, item.Color, item.Z);
                v += 4;
            }
            else if (item.Kind == RenderKind.Text && item.TextLayout.HasValue)
            {
                WriteText(mesh, ref v, item);
            }
        }
    }

    private void WriteQuad(PhosMesh mesh, int vertexStart, HelioRect rect, color quadColor, float z)
    {
        if (rect.Size.x <= 0 || rect.Size.y <= 0)
            return;

        float2 sizeWorld = rect.Size / _cachedPixelScale;
        float2 center = rect.Min + rect.Size * 0.5f;
        float2 worldCenter2D = (center - _cachedRefSize * 0.5f) / _cachedPixelScale;

        // Corner positions in local quad space
        float2 half = sizeWorld * 0.5f;
        float3 pos0 = new float3(-half.x, half.y, z);
        float3 pos1 = new float3(half.x, half.y, z);
        float3 pos2 = new float3(half.x, -half.y, z);
        float3 pos3 = new float3(-half.x, -half.y, z);

        pos0 += new float3(worldCenter2D.x, worldCenter2D.y, 0f);
        pos1 += new float3(worldCenter2D.x, worldCenter2D.y, 0f);
        pos2 += new float3(worldCenter2D.x, worldCenter2D.y, 0f);
        pos3 += new float3(worldCenter2D.x, worldCenter2D.y, 0f);

        var positions = mesh.RawPositions;
        var normals = mesh.RawNormals;
        var tangents = mesh.RawTangents;
        var colors = mesh.RawColors;
        var uvs = mesh.RawUV0s;

        positions[vertexStart + 0] = pos0;
        positions[vertexStart + 1] = pos1;
        positions[vertexStart + 2] = pos2;
        positions[vertexStart + 3] = pos3;

        normals[vertexStart + 0] = float3.Backward;
        normals[vertexStart + 1] = float3.Backward;
        normals[vertexStart + 2] = float3.Backward;
        normals[vertexStart + 3] = float3.Backward;

        var tangent = new float4(float3.Right, -1f);
        tangents[vertexStart + 0] = tangent;
        tangents[vertexStart + 1] = tangent;
        tangents[vertexStart + 2] = tangent;
        tangents[vertexStart + 3] = tangent;

        colors[vertexStart + 0] = quadColor;
        colors[vertexStart + 1] = quadColor;
        colors[vertexStart + 2] = quadColor;
        colors[vertexStart + 3] = quadColor;

        uvs[vertexStart + 0] = new float2(0f, 1f);
        uvs[vertexStart + 1] = new float2(1f, 1f);
        uvs[vertexStart + 2] = new float2(1f, 0f);
        uvs[vertexStart + 3] = new float2(0f, 0f);
    }

    private void WriteText(PhosMesh mesh, ref int vertexStart, RenderItem item)
    {
        if (!item.TextLayout.HasValue)
            return;

        var layout = item.TextLayout.Value;
        float fontSize = layout.FontSize;
        float charAdvance = layout.CharAdvance;

        float pixelSize = fontSize / FONT_HEIGHT;

        float zLayer = item.Z;

        foreach (var line in layout.Lines)
        {
            float xCursor = line.XStart;
            float yCursor = line.Y;
            foreach (char c in line.Text)
            {
                if (!TryGetGlyph(c, out var glyph))
                {
                    xCursor += charAdvance;
                    continue;
                }

                for (int row = 0; row < FONT_HEIGHT; row++)
                {
                    byte rowData = glyph[row];
                    if (rowData == 0) continue;

                    int col = 0;
                    while (col < FONT_WIDTH)
                    {
                        if ((rowData & (1 << (FONT_WIDTH - 1 - col))) == 0)
                        {
                            col++;
                            continue;
                        }

                        int runStart = col;
                        while (col < FONT_WIDTH && (rowData & (1 << (FONT_WIDTH - 1 - col))) != 0)
                        {
                            col++;
                        }
                        int runLength = col - runStart;

                        float px = xCursor + runStart * pixelSize;
                        float py = yCursor - row * pixelSize;
                        float pw = runLength * pixelSize;
                        float ph = pixelSize;

                        var glyphRect = new HelioRect(new float2(px, py - ph), new float2(pw, ph));
                        WriteQuad(mesh, vertexStart, glyphRect, layout.Color, zLayer);
                        vertexStart += 4;
                        zLayer += Z_INCREMENT;
                    }
                }

                xCursor += charAdvance;
            }

            // Next line
        }
    }

    private struct RenderItem
    {
        public RenderKind Kind;
        public HelioRect Rect;
        public color Color;
        public float Z;
        public int QuadCount;
        public TextRenderLayout? TextLayout;

        public static RenderItem MakeQuad(HelioRect rect, color c, float z) => new RenderItem
        {
            Kind = RenderKind.Quad,
            Rect = rect,
            Color = c,
            Z = z,
            QuadCount = 1
        };

        public static RenderItem MakeText(HelioRect rect, HelioText text, float z, List<string> lines)
        {
            var layout = TextRenderLayout.Build(rect, text, lines);
            return new RenderItem
            {
                Kind = RenderKind.Text,
                Rect = rect,
                Color = text.Color.Value,
                Z = z,
                QuadCount = layout.QuadCount,
                TextLayout = layout
            };
        }

        public bool SameStructure(RenderItem other)
        {
            if (Kind != other.Kind || QuadCount != other.QuadCount)
                return false;

            return true;
        }
    }

    private enum RenderKind
    {
        Quad,
        Text
    }

    private struct TextLine
    {
        public string Text;
        public float XStart;
        public float Y;
    }

    private struct TextRenderLayout
    {
        public List<TextLine> Lines;
        public float FontSize;
        public float LineHeight;
        public float CharAdvance;
        public color Color;
        public int QuadCount;

        public static TextRenderLayout Build(HelioRect rect, HelioText text, List<string> lines)
        {
            var layout = new TextRenderLayout
            {
                Lines = new List<TextLine>(),
                FontSize = MathF.Max(8f, text.FontSize.Value),
                LineHeight = MathF.Max(8f, text.FontSize.Value) * text.LineHeight.Value,
                Color = text.Color.Value
            };
            layout.CharAdvance = layout.FontSize * ((float)FONT_WIDTH / FONT_HEIGHT) * 1.15f;

            float yCursor = rect.Max.y - layout.FontSize;
            int totalQuads = 0;

            foreach (var line in lines)
            {
                if (yCursor + layout.FontSize < rect.Min.y)
                    break;

                float lineWidth = line.Length * layout.CharAdvance;
                float xStart = rect.Min.x;

                if (text.Alignment.Value == TextAlignment.Center)
                    xStart = rect.Min.x + (rect.Size.x - lineWidth) * 0.5f;
                else if (text.Alignment.Value == TextAlignment.Right)
                    xStart = rect.Max.x - lineWidth;

                layout.Lines.Add(new TextLine
                {
                    Text = line,
                    XStart = xStart,
                    Y = yCursor
                });

                // Count quads for this line
                foreach (char c in line)
                {
                    if (!TryGetGlyph(c, out var glyph))
                        continue;
                    totalQuads += CountGlyphQuads(glyph);
                }

                yCursor -= layout.LineHeight;
            }

            layout.QuadCount = totalQuads;
            return layout;
        }
    }

    private static int CountGlyphQuads(byte[] glyph)
    {
        int quads = 0;
        for (int row = 0; row < FONT_HEIGHT; row++)
        {
            byte rowData = glyph[row];
            if (rowData == 0)
                continue;
            int col = 0;
            while (col < FONT_WIDTH)
            {
                if ((rowData & (1 << (FONT_WIDTH - 1 - col))) == 0)
                {
                    col++;
                    continue;
                }
                while (col < FONT_WIDTH && (rowData & (1 << (FONT_WIDTH - 1 - col))) != 0)
                {
                    col++;
                }
                quads++;
            }
        }
        return quads;
    }
}
