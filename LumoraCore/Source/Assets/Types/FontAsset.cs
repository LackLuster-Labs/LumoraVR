// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Threading.Tasks;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Always loaded from its URL and shared by every requester of the same file. Rasterizing and the shared atlas
// live in the hook.
public class FontAsset : ImplementableAsset<IFontAssetHook>
{
    public bool IsValid => Hook?.IsValid ?? false;

    // shared atlas texture for all glyphs at the currently-resident sizes. - xlinka
    public TextureAsset? AtlasTexture => Hook?.AtlasTexture;

    // Changes whenever the atlas gains a glyph. Text shaping caches against it. - xlinka
    public int CacheGeneration => Hook?.CacheGeneration ?? 0;

    // A font the build ships (or one the user points at on disk) loads from its file. One in the asset
    // database is sealed there, so its bytes are read through the database and handed over in memory.
    protected override async Task LoadSelf()
    {
        if (AssetURL == null)
        {
            FailLoad("FontAsset has no address");
            return;
        }

        if (AssetURL.IsFile)
        {
            Hook?.LoadFromFile(AssetURL.LocalPath);
            Version++;
            return;
        }

        if (AssetURL.Scheme != "local")
        {
            FailLoad($"FontAsset cannot load a {AssetURL.Scheme} address");
            return;
        }

        var bytes = await AssetManager.RequestGather(AssetURL).ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0)
        {
            FailLoad($"No font data gathered for {AssetURL}");
            return;
        }
        Hook?.LoadFromData(bytes, AssetURL.OriginalString);
        Version++;
    }

    public virtual bool TryGetGlyph(int codepoint, float size, out GlyphMetrics metrics, out Rect uvRect)
    {
        if (Hook != null && Hook.TryGetGlyph(codepoint, size, out metrics, out uvRect))
        {
            return true;
        }

        metrics = default;
        uvRect = default;
        return false;
    }

    public virtual void RequestGlyph(int codepoint, float size) => Hook?.RequestGlyph(codepoint, size);

    public virtual float GetLineHeight(float size) => Hook?.GetLineHeight(size) ?? size;
    public virtual float GetAscent(float size) => Hook?.GetAscent(size) ?? size * 0.8f;
    public virtual float GetDescent(float size) => Hook?.GetDescent(size) ?? size * 0.2f;
    public virtual int PixelRange => Hook?.PixelRange ?? 4;

    public virtual float GetKerning(int leftCodepoint, int rightCodepoint, float size)
        => Hook?.GetKerning(leftCodepoint, rightCodepoint, size) ?? 0f;
}
