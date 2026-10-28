// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Photos;

namespace Lumora.Core.Assets;

// Imported textures and cloud saves keep the pixels the decoder uses, at their original dimensions. Source files
// stay in the local database. In particular a 16-bit PNG need not upload channels the RGBA8 decoder drops.
// Animation and non-PNG formats keep their original bytes. -xlinka
internal static class TextureStorage
{
    private const string PngCacheKey = "upload.png.v1";
    private const string WebpCacheKey = "upload.texture.v2";
    private static readonly SemaphoreSlim Workers = new(2, 2);

    public static async Task<string> ForTransferAsync(LocalDB db, string uri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(db.GetExtension(uri), ".png", StringComparison.OrdinalIgnoreCase))
            return uri;
        bool webp = TextureVariantStore.LosslessWebpEncoder != null && TextureVariantStore.PlatformDecoder != null;
        string cacheKey = webp ? WebpCacheKey : PngCacheKey;
        var metadata = db.GetAssetMetadata(uri);
        if (metadata != null && metadata.TryGetValue(cacheKey, out var cached) && db.Exists(cached))
            return cached;
        await Workers.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var source = await db.ReadAssetBytesAsync(uri).ConfigureAwait(false);
            var packed = source == null ? null : await VariantWorkers.Run("packing texture", _ => Pack(source, webp), ct: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var result = packed == null ? uri : await db.SaveAssetAsync(packed.Value.Bytes, packed.Value.Extension).ConfigureAwait(false) ?? uri;
            if (result != uri && metadata != null)
                await db.SetAssetMetadataAsync(result, bag =>
                {
                    foreach (var entry in metadata)
                        if (entry.Key != PngCacheKey && entry.Key != WebpCacheKey) bag[entry.Key] = entry.Value;
                }).ConfigureAwait(false);
            await db.SetAssetMetadataAsync(uri, bag => bag[cacheKey] = result).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"TextureStorage: keeping original texture after packing failed ({ex.Message})");
            return uri;
        }
        finally { Workers.Release(); }
    }

    internal static byte[]? Compact(byte[] source) => Pack(source, webp: false)?.Bytes;

    private static (byte[] Bytes, string Extension)? Pack(byte[] source, bool webp)
    {
        if (source.Length < (webp ? 4096 : 65536) || source.Length > 128 * 1024 * 1024
            || !source.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return null;
        int width = BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(16));
        int height = BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(20));
        if (width <= 0 || height <= 0 || (long)width * height > 32 * 1024 * 1024)
            return null;
        using var colour = new MemoryStream();
        for (int at = 8; at + 12 <= source.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(at));
            if (length < 0 || length > source.Length - at - 12) return null;
            var type = System.Text.Encoding.ASCII.GetString(source, at + 4, 4);
            if (type == "acTL") return null;
            if (type is "sRGB" or "gAMA" or "cHRM" or "iCCP") colour.Write(source, at, length + 12);
            at += length + 12;
        }
        var rgba = TextureVariantStore.DecodeRgba(source, out int decodedWidth, out int decodedHeight);
        if (rgba == null || decodedWidth != width || decodedHeight != height) return null;
        var png = PhotoEncoder.Png(rgba, width, height, compact: true);
        // Profiled or gamma-tagged images keep their PNG chunks. For plain images the platform encoder
        // must preserve every channel, including RGB under transparent pixels, before its bytes are used. -xlinka
        if (webp && colour.Length == 0 && width <= 16383 && height <= 16383)
        {
            try
            {
                var encoded = TextureVariantStore.LosslessWebpEncoder?.Invoke(rgba, width, height);
                if (encoded is { Length: > 0 } && encoded.Length < System.Math.Min(source.Length, png.Length)
                    && ImageHeader.TryProbe(encoded, out var container, out int ew, out int eh)
                    && container == ImageContainer.WebP && ew == width && eh == height)
                {
                    var check = TextureVariantStore.DecodeRgba(encoded, out int cw, out int ch);
                    if (cw == width && ch == height && check != null && rgba.AsSpan().SequenceEqual(check))
                        return (encoded, ".webp");
                }
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"TextureStorage: keeping PNG after lossless encoding failed ({ex.Message})");
            }
        }
        if (png.LongLength + colour.Length >= source.LongLength) return null;
        using var output = new MemoryStream();
        output.Write(png, 0, 33);
        colour.Position = 0;
        colour.CopyTo(output);
        output.Write(png, 33, png.Length - 33);
        return (output.ToArray(), ".png");
    }
}
