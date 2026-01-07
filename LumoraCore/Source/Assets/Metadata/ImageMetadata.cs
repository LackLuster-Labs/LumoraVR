namespace Lumora.Core.Assets;

/// <summary>
/// Metadata for image/texture assets.
/// Contains dimensions, format, and other image-specific information.
/// </summary>
public class ImageMetadata : IAssetMetadata
{
    /// <summary>
    /// Image width in pixels.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Image height in pixels.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Image format string (e.g., "PNG", "JPEG", "WEBP").
    /// </summary>
    public string Format { get; set; }

    /// <summary>
    /// Whether the image has an alpha channel.
    /// </summary>
    public bool HasAlpha { get; set; }

    /// <summary>
    /// Number of mipmap levels if applicable.
    /// </summary>
    public int MipmapLevels { get; set; } = 1;

    /// <summary>
    /// Estimated memory size based on dimensions and alpha channel.
    /// Assumes RGBA8 format (4 bytes per pixel) or RGB8 (3 bytes per pixel).
    /// </summary>
    public long EstimatedMemorySize
    {
        get
        {
            int bytesPerPixel = HasAlpha ? 4 : 3;
            long baseSize = (long)Width * Height * bytesPerPixel;

            // Account for mipmaps (adds ~33% more memory)
            if (MipmapLevels > 1)
            {
                baseSize = (long)(baseSize * 1.33);
            }

            return baseSize;
        }
    }
}
