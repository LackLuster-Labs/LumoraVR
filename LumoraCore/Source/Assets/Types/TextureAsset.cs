using System;

namespace Lumora.Core.Assets;

/// <summary>
/// Asset containing texture/image data.
/// Supports RGBA8 pixel data with optional mipmaps.
/// </summary>
public class TextureAsset : DynamicImplementableAsset<ITextureAssetHook>
{
    private byte[] _pixelData;
    private int _width;
    private int _height;
    private bool _hasMipmaps;
    private int _activeRequestCount;

    /// <summary>
    /// Texture width in pixels.
    /// </summary>
    public int Width => _width;

    /// <summary>
    /// Texture height in pixels.
    /// </summary>
    public int Height => _height;

    /// <summary>
    /// The raw RGBA8 pixel data.
    /// </summary>
    public byte[] PixelData => _pixelData;

    /// <summary>
    /// Whether the texture has mipmaps.
    /// </summary>
    public bool HasMipmaps => _hasMipmaps;

    /// <summary>
    /// Memory size in bytes of the pixel data.
    /// </summary>
    public long MemorySize => _pixelData?.Length ?? 0;

    public override int ActiveRequestCount => _activeRequestCount;

    /// <summary>
    /// Set the texture image data.
    /// Expects RGBA8 format (4 bytes per pixel).
    /// </summary>
    /// <param name="pixels">RGBA8 pixel data</param>
    /// <param name="width">Width in pixels</param>
    /// <param name="height">Height in pixels</param>
    /// <param name="mipmaps">Whether mipmaps should be generated</param>
    public void SetImageData(byte[] pixels, int width, int height, bool mipmaps = false)
    {
        if (pixels == null)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        int expectedSize = width * height * 4; // RGBA8
        if (pixels.Length < expectedSize)
        {
            throw new ArgumentException($"Pixel data too small. Expected at least {expectedSize} bytes, got {pixels.Length}");
        }

        _pixelData = pixels;
        _width = width;
        _height = height;
        _hasMipmaps = mipmaps;
        Version++;

        // Upload to hook if available
        Hook?.UploadData(_pixelData, _width, _height, _hasMipmaps);
    }

    /// <summary>
    /// Create texture from existing RGBA8 data.
    /// </summary>
    public static TextureAsset FromRgba(byte[] rgbaData, int width, int height, bool mipmaps = false)
    {
        var texture = new TextureAsset();
        texture.InitializeDynamic();
        texture.SetImageData(rgbaData, width, height, mipmaps);
        return texture;
    }

    /// <summary>
    /// Add an active request for this texture.
    /// </summary>
    public void AddRequest()
    {
        _activeRequestCount++;
    }

    /// <summary>
    /// Remove an active request for this texture.
    /// </summary>
    public void RemoveRequest()
    {
        _activeRequestCount = System.Math.Max(0, _activeRequestCount - 1);
    }

    public override void Unload()
    {
        _pixelData = null;
        _width = 0;
        _height = 0;
        _hasMipmaps = false;
        _activeRequestCount = 0;
        base.Unload();
    }
}
