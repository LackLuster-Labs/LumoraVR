using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StbImageSharp;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

/// <summary>
/// Component that provides texture assets from URLs.
/// Loads image files and creates TextureAsset instances.
/// </summary>
public class ImageProvider : UrlAssetProvider<TextureAsset, ImageMetadata>
{
    /// <summary>
    /// Whether this is a normal map (affects compression).
    /// </summary>
    public readonly Sync<bool> IsNormalMap;

    /// <summary>
    /// Horizontal texture wrap mode.
    /// </summary>
    public readonly Sync<TextureWrapMode> WrapModeU;

    /// <summary>
    /// Vertical texture wrap mode.
    /// </summary>
    public readonly Sync<TextureWrapMode> WrapModeV;

    /// <summary>
    /// Whether to generate mipmaps for the texture.
    /// </summary>
    public readonly Sync<bool> GenerateMipmaps;

    public ImageProvider()
    {
        IsNormalMap = new Sync<bool>(this, false);
        WrapModeU = new Sync<TextureWrapMode>(this, TextureWrapMode.Repeat);
        WrapModeV = new Sync<TextureWrapMode>(this, TextureWrapMode.Repeat);
        GenerateMipmaps = new Sync<bool>(this, true);
    }

    protected override async Task<ImageMetadata> LoadMetadata(Uri url, CancellationToken token)
    {
        // Read image header to get dimensions without loading full image
        byte[] headerBytes = await ReadImageHeader(url, token);
        return ParseImageMetadata(headerBytes, url);
    }

    protected override async Task<TextureAsset> LoadAssetData(Uri url, ImageMetadata metadata, CancellationToken token)
    {
        byte[] fileData = await LoadFileBytes(url, token);
        if (token.IsCancellationRequested) return null;

        byte[] rgbaPixels = DecodeImageToRgba(fileData, metadata);
        if (rgbaPixels == null) return null;

        var asset = new TextureAsset();
        asset.InitializeDynamic();  // Create the hook before setting data
        asset.SetImageData(rgbaPixels, metadata.Width, metadata.Height, GenerateMipmaps.Value);
        return asset;
    }

    protected override void OnAssetLoaded()
    {
        base.OnAssetLoaded();

        // Apply wrap modes to the texture hook
        if (Asset?.Hook != null)
        {
            Asset.Hook.SetWrapMode(WrapModeU.Value, WrapModeV.Value);
        }
    }

    private async Task<byte[]> ReadImageHeader(Uri url, CancellationToken token)
    {
        // Read first 32 bytes for header parsing
        const int headerSize = 32;

        if (url.IsFile)
        {
            using var stream = File.OpenRead(url.LocalPath);
            var buffer = new byte[System.Math.Min(headerSize, stream.Length)];
            await stream.ReadAsync(buffer, 0, buffer.Length, token);
            return buffer;
        }

        // For HTTP/HTTPS/lumora URLs, load full content via ContentCache then take header
        if (url.Scheme is "http" or "https" or "lumora")
        {
            var contentCache = Engine.Current?.ContentCache;
            if (contentCache != null)
            {
                var data = await contentCache.Get(url, token);
                if (data != null && data.Length > 0)
                {
                    var headerLen = System.Math.Min(headerSize, data.Length);
                    var header = new byte[headerLen];
                    Array.Copy(data, header, headerLen);
                    return header;
                }
            }
        }

        return new byte[0];
    }

    private ImageMetadata ParseImageMetadata(byte[] header, Uri url)
    {
        var metadata = new ImageMetadata();

        // Default values
        metadata.Width = 0;
        metadata.Height = 0;
        metadata.HasAlpha = true;
        metadata.Format = "Unknown";

        if (header.Length < 8) return metadata;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
        {
            metadata.Format = "PNG";
            if (header.Length >= 24)
            {
                // Width at offset 16, Height at offset 20 (big-endian)
                metadata.Width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                metadata.Height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                // Color type at offset 25 determines alpha
                if (header.Length >= 26)
                {
                    int colorType = header[25];
                    metadata.HasAlpha = colorType == 4 || colorType == 6;
                }
            }
            return metadata;
        }

        // JPEG: FF D8 FF
        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            metadata.Format = "JPEG";
            metadata.HasAlpha = false;
            // JPEG dimensions require parsing SOF marker - defer to full decode
            return metadata;
        }

        // WEBP: RIFF....WEBP
        if (header.Length >= 12 &&
            header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
            header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
        {
            metadata.Format = "WEBP";
            metadata.HasAlpha = true; // Assume alpha, actual detection requires more parsing
            return metadata;
        }

        // Try to get info from file extension
        string ext = Path.GetExtension(url.IsFile ? url.LocalPath : url.AbsolutePath)?.ToLowerInvariant();
        switch (ext)
        {
            case ".png":
                metadata.Format = "PNG";
                metadata.HasAlpha = true;
                break;
            case ".jpg":
            case ".jpeg":
                metadata.Format = "JPEG";
                metadata.HasAlpha = false;
                break;
            case ".webp":
                metadata.Format = "WEBP";
                metadata.HasAlpha = true;
                break;
            case ".bmp":
                metadata.Format = "BMP";
                metadata.HasAlpha = false;
                break;
        }

        return metadata;
    }

    private async Task<byte[]> LoadFileBytes(Uri url, CancellationToken token)
    {
        if (url.IsFile)
        {
            return await File.ReadAllBytesAsync(url.LocalPath, token);
        }

        // For HTTP/HTTPS/lumora URLs, use ContentCache
        if (url.Scheme is "http" or "https" or "lumora")
        {
            var contentCache = Engine.Current?.ContentCache;
            if (contentCache != null)
            {
                var data = await contentCache.Get(url, token);
                if (data != null)
                {
                    return data;
                }
            }
            throw new InvalidOperationException($"Failed to load content from URL: {url}");
        }

        throw new NotSupportedException($"URL scheme not supported: {url.Scheme}");
    }

    private byte[] DecodeImageToRgba(byte[] fileData, ImageMetadata metadata)
    {
        if (fileData == null || fileData.Length == 0)
        {
            AquaLogger.Warn("ImageProvider: Empty file data");
            return null;
        }

        try
        {
            using var stream = new MemoryStream(fileData);
            var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            // Update metadata with actual dimensions from decoded image
            metadata.Width = result.Width;
            metadata.Height = result.Height;

            AquaLogger.Debug($"ImageProvider: Decoded {result.Width}x{result.Height} image");
            return FlipRgbaVertical(result.Data, result.Width, result.Height);
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"ImageProvider: Failed to decode image - {ex.Message}");
            return null;
        }
    }

    private static byte[] FlipRgbaVertical(byte[] rgba, int width, int height)
    {
        if (rgba == null || width <= 0 || height <= 0)
        {
            return rgba;
        }

        int stride = width * 4;
        if (rgba.Length < stride * height)
        {
            return rgba;
        }

        var flipped = new byte[rgba.Length];
        for (int y = 0; y < height; y++)
        {
            int src = y * stride;
            int dst = (height - 1 - y) * stride;
            Buffer.BlockCopy(rgba, src, flipped, dst, stride);
        }

        return flipped;
    }
}
