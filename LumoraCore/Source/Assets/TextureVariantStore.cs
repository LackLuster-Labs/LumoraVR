// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using K4os.Compression.LZ4;
using Lumora.Core.Logging;
using StbImageSharp;

namespace Lumora.Core.Assets;

// Optional render-platform acceleration. Null means unsupported or not worth a GPU round trip.
// The source is immutable: a renderer may reuse its upload across the resolution ladder.
public interface ITextureVariantAccelerator
{
    Task<byte[][]?> BuildAsync(byte[] rgba, int sourceWidth, int sourceHeight, int width, int height,
        bool mipmaps, CancellationToken cancellationToken = default);
}

public sealed class TextureVariantData
{
    public byte[][] Levels { get; init; } = Array.Empty<byte[]>();

    public int Width { get; init; }
    public int Height { get; init; }
    public bool HasAlpha { get; init; }
    public TextureFormatKind ContentFormat { get; init; } = TextureFormatKind.Unknown;

    public byte[] BaseLevel => Levels.Length > 0 ? Levels[0] : Array.Empty<byte>();
    public int MipCount => System.Math.Max(1, Levels.Length);

    // Counted, not estimated.
    public long DecodedBytes
    {
        get
        {
            long total = 0;
            foreach (var level in Levels)
                total += level?.LongLength ?? 0;
            return total;
        }
    }
}

// Generates, stores and loads texture resolution variants.
//
// Storage model: a variant is an ordinary local asset with a DERIVED address rather than a
// content address. Its URI is the base texture's URI with the variant identifier appended
// (local://machine/hash~v1-max512-mips1-compnone), which buys three things at once: the
// existing local:// fetch path serves it with no changes, the peer transferer already resolves the
// owner from the machine id in the URI, and a peer that has only ever seen the BASE uri can
// construct the address of any variant it wants and ask for it by name. No variant manifest has to
// be replicated. -xlinka
//
// Pixel prediction and Brotli keep cloud copies small without changing pixels or mip levels.
// LZ4 and raw payloads remain readable, and are kept when they are smaller. -xlinka
public static class TextureVariantStore
{
    public static ITextureVariantAccelerator? Accelerator { get; set; }
    private const uint Magic = 0x5854564C; // "LVTX" little-endian
    // 2 keeps top-down rows; 3 adds filtered compression. Both decode to the same pixels. -xlinka
    private const ushort ContainerVersion = 3;
    private const ushort FlagLz4 = 1 << 0;
    private const ushort FlagFilteredBrotli = 1 << 1;

    public const string VariantExtension = ".lvtex";

    public const string MetadataExtension = ".lvmeta";

    public const string MetadataSuffix = "meta";

    // ADDRESSING

    public static string? GetVariantUri(string? baseUri, TextureVariantId id) =>
        id.IsOriginal ? baseUri : LocalDB.DeriveUri(baseUri, id.Identifier);

    public static string? GetMetadataUri(string? baseUri) => LocalDB.DeriveUri(baseUri, MetadataSuffix);

    // DECODE

    // Every path that turns file bytes into pixels goes through here so the base texture and its variants can
    // never disagree about which way up the rows are. Rows stay TOP-DOWN, the decoder's own order: the
    // renderer samples V=0 at row 0, mesh UVs are top-origin (see MeshDecoder, no FlipUVs), the glyph atlas
    // and every procedural texture upload top-down, and the UI puts V=0 on the top edge of a quad. The
    // flip that used to live here turned every loaded picture upside down in the world browser and on
    // any model whose UVs were right. Cubemap faces take these rows as they are too. -xlinka
    // Formats our own decoder cannot read are handed to the platform, which usually can. Installed by
    // the rendering layer at startup; null means there is nothing to fall back to.
    //
    // WebP is the case that forced this. The built-in decoder handles PNG, JPEG, BMP, TGA, PSD and GIF
    // and nothing else, so a WebP texture failed outright even though the platform decodes it happily
    // one layer down - the failure just came too early to ever reach it. Imported avatars routinely
    // carry WebP. -xlinka
    public delegate byte[]? PlatformImageDecoder(byte[] encoded, out int width, out int height);

    // For an animated source the platform composites but cannot hand over frame by frame (animated WebP): every
    // frame on one FrameSheet, cap as FrameSheet.Builder.Begin takes it.
    public delegate FrameSheet? PlatformAnimatedDecoder(byte[] encoded, int cap);

    public static PlatformAnimatedDecoder? AnimatedDecoder { get; set; }

    public static PlatformImageDecoder? PlatformDecoder { get; set; }

    // Optional lossless file encoder; the upload path verifies its decoded pixels before using it. -xlinka
    public static Func<byte[], int, int, byte[]?>? LosslessWebpEncoder { get; set; }

    // Same shape for floating-point sources, returning RGBA half floats (8 bytes per texel). Only
    // EXR needs it: Radiance is decoded here. Null means EXR is not readable on this build. -xlinka
    public static PlatformImageDecoder? PlatformHdrDecoder { get; set; }

    // Floating-point sources decode to RGBA half floats and NEVER through DecodeRgba: the 8-bit
    // decoder tone-maps Radiance data on the way in without saying so, and the picture that comes
    // out is a clipped, gamma-bent version of what the creator made. Radiance goes through the
    // float path of the built-in decoder; EXR is the platform's, when it has one. -xlinka
    public static byte[]? DecodeRgbaHalf(byte[] encoded, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (encoded == null || encoded.Length == 0)
            return null;

        bool exr = encoded.Length >= 4 && encoded[0] == 0x76 && encoded[1] == 0x2F && encoded[2] == 0x31 && encoded[3] == 0x01;
        if (!exr)
        {
            using var stream = new MemoryStream(encoded);
            var result = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (result?.Data == null || result.Width <= 0 || result.Height <= 0)
                return null;
            width = result.Width;
            height = result.Height;
            return PackHalf(result.Data, width, height);
        }

        var platform = PlatformHdrDecoder;
        if (platform == null)
            return null;
        var pixels = platform(encoded, out width, out height);
        return pixels != null && width > 0 && height > 0 ? pixels : null;
    }

    // Float RGBA to half RGBA, little-endian. NaN and infinity are clamped away here because a
    // single bad texel poisons every mip below it once the chain is filtered. -xlinka
    public static byte[] PackHalf(float[] rgba, int width, int height)
    {
        long count = (long)width * height * 4;
        var packed = new byte[count * 2];
        for (long i = 0, o = 0; i < count && i < rgba.Length; i++, o += 2)
        {
            float v = rgba[i];
            if (float.IsNaN(v))
                v = 0f;
            else if (float.IsInfinity(v))
                v = v > 0 ? 65504f : -65504f;
            ushort bits = BitConverter.HalfToUInt16Bits((Half)v);
            packed[o] = (byte)bits;
            packed[o + 1] = (byte)(bits >> 8);
        }
        return packed;
    }

    public static byte[]? DecodeRgba(byte[] encoded, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (encoded == null || encoded.Length == 0)
            return null;

        // Neither the built-in decoder nor the platform reads TIFF, so it never falls through: a TIFF
        // this cannot read throws with the reason.
        if (TiffDecoder.IsTiff(encoded))
            return TiffDecoder.Decode(encoded, out width, out height);

        // PNG and JPEG go to the platform's native readers first when it has them: the 215 PNGs of a big world
        // package decode in 2.6 s there against 4.4 s in the built-in decoder, one thread each, to the same
        // bytes. Only a PNG the two are known to read the same: up to eight bits a channel, and no gamma but the
        // usual sRGB one and no colour profile, which the native reader would convert where the built-in one
        // keeps the stored values. -xlinka
        var native = PlatformDecoder;
        if (native != null && (IsJpeg(encoded) || IsPlainPng(encoded)))
        {
            var read = native(encoded, out width, out height);
            if (read != null && width > 0 && height > 0)
                return read;
        }

        try
        {
            using var stream = new MemoryStream(encoded);
            var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (result?.Data != null && result.Width > 0 && result.Height > 0)
            {
                width = result.Width;
                height = result.Height;
                return result.Data;
            }
        }
        catch (Exception) when (PlatformDecoder != null)
        {
            // Fall through: an unreadable format is the platform's problem now, not a failure.
        }

        var platform = PlatformDecoder;
        if (platform == null)
            return null;

        var pixels = platform(encoded, out width, out height);
        return pixels != null && width > 0 && height > 0 ? pixels : null;
    }

    private static bool IsJpeg(byte[] data) => data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    private static bool IsPlainPng(byte[] data)
    {
        if (data.Length < 33 || data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4E || data[3] != 0x47 || data[24] > 8)
            return false;
        long offset = 8;
        while (offset + 8 <= data.Length)
        {
            long length = (long)data[offset] << 24 | (long)data[offset + 1] << 16 | (long)data[offset + 2] << 8 | data[offset + 3];
            int a = data[offset + 4], b = data[offset + 5], c = data[offset + 6], e = data[offset + 7];
            // 45455 is the stored form of 1/2.2, the gamma every sRGB picture carries.
            bool gamma = a == 'g' && b == 'A' && c == 'M' && e == 'A'
                && (offset + 12 > data.Length || (data[offset + 8] << 24 | data[offset + 9] << 16 | data[offset + 10] << 8 | data[offset + 11]) != 45455);
            bool primaries = a == 'c' && b == 'H' && c == 'R' && e == 'M';
            bool profile = a == 'i' && b == 'C' && c == 'C' && e == 'P';
            if (gamma || primaries || profile)
                return false;
            if (a == 'I' && b == 'D' && c == 'A' && e == 'T')
                return true;
            offset += 12 + length;
        }
        return false;
    }

    // RESAMPLING

    // Every destination pixel averages the full source rectangle it covers, so a large reduction keeps detail
    // instead of point-sampling it away. Alpha is averaged straight (not premultiplied): our textures are
    // sampled with straight alpha and premultiplying here would darken the fringes of cutout art.
    public static byte[] Downscale(byte[] source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        if (dstWidth == srcWidth && dstHeight == srcHeight)
            return source;

        var dst = new byte[(long)dstWidth * dstHeight * 4];
        DownscaleInto(source, srcWidth, srcHeight, dst, dstWidth, dstHeight);
        return dst;
    }

    // The same filter into a buffer the caller owns.
    public static void DownscaleInto(ReadOnlySpan<byte> source, int srcWidth, int srcHeight, Span<byte> dst, int dstWidth, int dstHeight)
    {
        double xRatio = (double)srcWidth / dstWidth;
        double yRatio = (double)srcHeight / dstHeight;

        for (int y = 0; y < dstHeight; y++)
        {
            int y0 = (int)(y * yRatio);
            int y1 = System.Math.Max(y0 + 1, System.Math.Min(srcHeight, (int)System.Math.Ceiling((y + 1) * yRatio)));

            for (int x = 0; x < dstWidth; x++)
            {
                int x0 = (int)(x * xRatio);
                int x1 = System.Math.Max(x0 + 1, System.Math.Min(srcWidth, (int)System.Math.Ceiling((x + 1) * xRatio)));

                long r = 0, g = 0, b = 0, a = 0;
                int count = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * srcWidth * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int o = row + sx * 4;
                        r += source[o];
                        g += source[o + 1];
                        b += source[o + 2];
                        a += source[o + 3];
                        count++;
                    }
                }

                if (count == 0)
                    count = 1;
                int d = (y * dstWidth + x) * 4;
                dst[d] = (byte)(r / count);
                dst[d + 1] = (byte)(g / count);
                dst[d + 2] = (byte)(b / count);
                dst[d + 3] = (byte)(a / count);
            }
        }
    }

    // Each level is filtered from the one above it, which is both cheaper and smoother than resampling the base
    // every time.
    public static byte[][] BuildMipChain(byte[] baseLevel, int width, int height)
    {
        int levelCount = TextureMetadata.FullMipCount(width, height);
        var levels = new byte[levelCount][];
        levels[0] = baseLevel;

        int w = width, h = height;
        for (int level = 1; level < levelCount; level++)
        {
            int nw = System.Math.Max(1, w >> 1);
            int nh = System.Math.Max(1, h >> 1);
            levels[level] = Downscale(levels[level - 1], w, h, nw, nh);
            w = nw;
            h = nh;
        }

        return levels;
    }

    // CONTAINER

    public static byte[] Encode(TextureVariantData data)
    {
        long payloadLength = data.DecodedBytes;
        if (payloadLength > int.MaxValue)
            throw new InvalidOperationException($"Texture variant payload too large: {payloadLength} bytes");

        var payload = VariantWorkers.Scratch(0, (int)payloadLength);
        int offset = 0;
        foreach (var level in data.Levels)
        {
            if (level == null)
                continue;
            Buffer.BlockCopy(level, 0, payload, offset, level.Length);
            offset += level.Length;
        }
        return EncodePayload(payload.AsSpan(0, (int)payloadLength), data.Width, data.Height, data.ContentFormat, data.Levels.Length, data.HasAlpha);
    }

    // payload is the levels back to back. On a variant worker the payload and the compressor's output live in
    // the worker's own buffers, so the finished blob is the only new array a rung costs: three of them the size
    // of the rung each, megabytes for a big one, were what the collector stopped the frame for. -xlinka
    private static byte[] EncodePayload(ReadOnlySpan<byte> payload, int width, int height, TextureFormatKind format, int levelCount, bool hasAlpha)
    {
        // Try LZ4 and keep it only if it genuinely shrank. Blobs that don't compress stay raw and
        // say so in the flags, so the reader never pays a decompress that buys nothing.
        int bound = LZ4Codec.MaximumOutputSize(payload.Length);
        var scratch = VariantWorkers.Scratch(1, bound);
        int written = LZ4Codec.Encode(payload, scratch.AsSpan(0, bound), LZ4Level.L00_FAST);
        bool lz4 = written > 0 && written < payload.Length;
        ReadOnlySpan<byte> stored = lz4 ? scratch.AsSpan(0, written) : payload;
        ushort flags = lz4 ? FlagLz4 : (ushort)0;

        using var filtered = new MemoryStream();
        int offset = 0, w = width, h = height;
        for (int level = 0; level < levelCount; level++)
        {
            int size = checked(w * h * 4);
            TextureRowFilter.Write(filtered, payload.Slice(offset, size), w, h);
            offset += size;
            w = System.Math.Max(1, w >> 1);
            h = System.Math.Max(1, h >> 1);
        }
        var predicted = filtered.GetBuffer().AsSpan(0, (int)filtered.Length);
        var packed = new byte[BrotliEncoder.GetMaxCompressedLength(predicted.Length)];
        if (BrotliEncoder.TryCompress(predicted, packed, out int packedLength, quality: 5, window: 22)
            && packedLength < stored.Length)
        {
            stored = packed.AsSpan(0, packedLength);
            flags = FlagFilteredBrotli;
        }

        // Written straight into a blob of its final size, the layout a little-endian BinaryWriter gave.
        var blob = new byte[HeaderBytes + stored.Length];
        var header = blob.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4), ContainerVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6), flags);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12), height);
        header[16] = (byte)format;
        header[17] = (byte)System.Math.Min(byte.MaxValue, levelCount);
        header[18] = (byte)(hasAlpha ? 1 : 0);
        header[19] = 0; // reserved
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(20), payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24), stored.Length);
        stored.CopyTo(blob.AsSpan(HeaderBytes));
        return blob;
    }

    private const int HeaderBytes = 28;

    // Null on a truncated or foreign blob.
    public static TextureVariantData? Decode(byte[]? blob)
    {
        if (blob == null || blob.Length < 24)
            return null;

        try
        {
            using var ms = new MemoryStream(blob, writable: false);
            using var r = new BinaryReader(ms);

            if (r.ReadUInt32() != Magic)
                return null;
            ushort version = r.ReadUInt16();
            if (version != 2 && version != ContainerVersion)
                return null;

            ushort flags = r.ReadUInt16();
            int width = r.ReadInt32();
            int height = r.ReadInt32();
            var format = (TextureFormatKind)r.ReadByte();
            int levelCount = r.ReadByte();
            bool hasAlpha = r.ReadByte() != 0;
            r.ReadByte(); // reserved
            int plainLength = r.ReadInt32();
            int storedLength = r.ReadInt32();

            if (width <= 0 || height <= 0 || width > 16384 || height > 16384
                || levelCount <= 0 || plainLength <= 0 || storedLength <= 0)
                return null;
            if (storedLength > ms.Length - ms.Position)
                return null;
            if ((flags & ~(FlagLz4 | FlagFilteredBrotli)) != 0
                || flags == (FlagLz4 | FlagFilteredBrotli) || (version == 2 && (flags & FlagFilteredBrotli) != 0)
                || levelCount > TextureMetadata.FullMipCount(width, height))
                return null;
            long expected = 0, rows = 0;
            int mw = width, mh = height;
            for (int level = 0; level < levelCount; level++)
            {
                expected += (long)mw * mh * 4;
                rows += mh;
                mw = System.Math.Max(1, mw >> 1);
                mh = System.Math.Max(1, mh >> 1);
            }
            if (expected != plainLength || expected + rows > 256 * 1024 * 1024)
                return null;

            // Read in place: a copy of the stored bytes first was one more array the size of the rung.
            int storedAt = (int)ms.Position;
            byte[] payload;
            int payloadAt;
            if ((flags & FlagFilteredBrotli) != 0)
            {
                var filtered = new byte[checked(plainLength + (int)rows)];
                if (!BrotliDecoder.TryDecompress(blob.AsSpan(storedAt, storedLength), filtered, out int decoded)
                    || decoded != filtered.Length)
                    return null;
                payload = new byte[plainLength];
                int from = 0, into = 0;
                mw = width;
                mh = height;
                for (int level = 0; level < levelCount; level++)
                {
                    int size = checked(mw * mh * 4);
                    int filteredSize = size + mh;
                    if (!TextureRowFilter.Read(filtered.AsSpan(from, filteredSize), payload.AsSpan(into, size), mw, mh))
                        return null;
                    from += filteredSize;
                    into += size;
                    mw = System.Math.Max(1, mw >> 1);
                    mh = System.Math.Max(1, mh >> 1);
                }
                payloadAt = 0;
            }
            else if ((flags & FlagLz4) != 0)
            {
                payload = new byte[plainLength];
                int decoded = LZ4Codec.Decode(blob.AsSpan(storedAt, storedLength), payload);
                if (decoded != plainLength)
                    return null;
                payloadAt = 0;
            }
            else
            {
                if (storedLength != plainLength)
                    return null;
                payload = blob;
                payloadAt = storedAt;
            }

            var levels = new byte[levelCount][];
            int offset = 0;
            int w = width, h = height;
            for (int level = 0; level < levelCount; level++)
            {
                int size = w * h * 4;
                if (offset + size > plainLength)
                    return null;
                var data = new byte[size];
                Buffer.BlockCopy(payload, payloadAt + offset, data, 0, size);
                levels[level] = data;
                offset += size;
                w = System.Math.Max(1, w >> 1);
                h = System.Math.Max(1, h >> 1);
            }

            return new TextureVariantData
            {
                Levels = levels,
                Width = width,
                Height = height,
                HasAlpha = hasAlpha,
                ContentFormat = format,
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"TextureVariantStore: malformed variant container ({ex.Message})");
            return null;
        }
    }

    // GENERATION

    // The base level is area-downscaled to the variant's size, then the mip chain is built off that.
    public static TextureVariantData BuildVariant(byte[] baseRgba, int srcWidth, int srcHeight, TextureVariantId id, bool hasAlpha, TextureFormatKind contentFormat)
    {
        var (width, height) = id.ResolveSize(srcWidth, srcHeight);
        byte[] level0 = (width == srcWidth && height == srcHeight)
            ? baseRgba
            : Downscale(baseRgba, srcWidth, srcHeight, width, height);

        var levels = id.Mipmaps ? BuildMipChain(level0, width, height) : new[] { level0 };

        return new TextureVariantData
        {
            Levels = levels,
            Width = width,
            Height = height,
            HasAlpha = hasAlpha,
            ContentFormat = contentFormat,
        };
    }

    // One rung, filtered and packed on the calling worker: the levels the renderer's accelerator made when it
    // has one, BuildVariant's own filter straight into the payload when not. Same bytes either way.
    private static byte[] BuildRung(byte[] rgba, int sourceWidth, int sourceHeight, TextureVariantId id, bool hasAlpha, TextureFormatKind format,
        CancellationToken ct, out int width, out int height, out int levelCount, out long decodedBytes)
    {
        (width, height) = id.ResolveSize(sourceWidth, sourceHeight);
        levelCount = id.Mipmaps ? TextureMetadata.FullMipCount(width, height) : 1;
        if (Accelerator is { } accelerator)
        {
            try
            {
                var levels = accelerator.BuildAsync(rgba, sourceWidth, sourceHeight, width, height, id.Mipmaps, ct).GetAwaiter().GetResult();
                ct.ThrowIfCancellationRequested();
                bool valid = levels?.Length == levelCount;
                long total = 0;
                int w = width, h = height;
                for (int i = 0; valid && i < levelCount; i++)
                {
                    valid = levels![i]?.LongLength == (long)w * h * 4;
                    total += (long)w * h * 4;
                    w = System.Math.Max(1, w >> 1);
                    h = System.Math.Max(1, h >> 1);
                }
                if (valid)
                {
                    decodedBytes = total;
                    var payload = VariantWorkers.Scratch(0, (int)total);
                    int at = 0;
                    foreach (var level in levels!)
                    {
                        Buffer.BlockCopy(level, 0, payload, at, level.Length);
                        at += level.Length;
                    }
                    return EncodePayload(payload.AsSpan(0, at), width, height, format, levelCount, hasAlpha);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Logger.Warn($"TextureVariantStore: accelerator unavailable, using CPU ({ex.Message})");
            }
        }

        // Level 0 from the source, each level after it from the one before, back to back in the payload: the
        // container's own layout, so no level is ever an array of its own.
        bool same = width == sourceWidth && height == sourceHeight;
        int size = same ? rgba.Length : width * height * 4;
        long length = size;
        int lw = width, lh = height;
        for (int level = 1; level < levelCount; level++)
        {
            lw = System.Math.Max(1, lw >> 1);
            lh = System.Math.Max(1, lh >> 1);
            length += (long)lw * lh * 4;
        }
        if (length > int.MaxValue)
            throw new InvalidOperationException($"Texture variant payload too large: {length} bytes");
        decodedBytes = length;
        var chain = VariantWorkers.Scratch(0, (int)length);
        if (same)
            Buffer.BlockCopy(rgba, 0, chain, 0, size);
        else
            DownscaleInto(rgba, sourceWidth, sourceHeight, chain.AsSpan(0, size), width, height);
        int offset = 0, cw = width, ch = height;
        for (int level = 1; level < levelCount; level++)
        {
            ct.ThrowIfCancellationRequested();
            int nw = System.Math.Max(1, cw >> 1);
            int nh = System.Math.Max(1, ch >> 1);
            int next = nw * nh * 4;
            DownscaleInto(chain.AsSpan(offset, cw * ch * 4), cw, ch, chain.AsSpan(offset + size, next), nw, nh);
            offset += size;
            size = next;
            cw = nw;
            ch = nh;
        }
        return EncodePayload(chain.AsSpan(0, (int)length), width, height, format, levelCount, hasAlpha);
    }

    // Decodes the source exactly once and reuses that buffer for every bucket. Safe to call more than once for
    // the same asset: variants that already exist are skipped, so a re-import or a second world load costs
    // nothing.
    //
    // Returns the metadata for the BASE asset, which the caller can hand straight to a loaded
    // texture without a second decode. Cancelling ct drops the ladder before or between its rungs and throws;
    // the rungs already made stay. Shutdown answers null.
    public static Task<TextureMetadata?> GenerateAsync(
        LocalDB db,
        string baseLocalUri,
        bool isNormalMap = false,
        bool mipmaps = true,
        TextureCompressionKind compression = TextureCompressionKind.Block,
        CancellationToken ct = default)
    {
        if (db == null || string.IsNullOrEmpty(baseLocalUri))
            return Task.FromResult<TextureMetadata?>(null);
        return OneAtATimeAsync(baseLocalUri, () => GenerateNowAsync(db, baseLocalUri, isNormalMap, mipmaps, compression, ct), ct);
    }

    // The sidecar and every rung a save uploads, made only where one is missing: when all of them are
    // here already nothing is decoded. The normal-map mark already on record is passed through, so a
    // save's pass does not reset what the import set. -xlinka
    public static Task<TextureMetadata?> EnsureLadderAsync(LocalDB db, string baseLocalUri, CancellationToken ct = default)
    {
        if (db == null || string.IsNullOrEmpty(baseLocalUri))
            return Task.FromResult<TextureMetadata?>(null);
        return OneAtATimeAsync(baseLocalUri, async () =>
        {
            var known = await TryLoadMetadataAsync(db, baseLocalUri).ConfigureAwait(false);
            if (known != null && known.Width > 0 && HasLadder(db, baseLocalUri, known))
                return known;
            return await GenerateNowAsync(db, baseLocalUri, known?.IsNormalMap ?? false, ct: ct).ConfigureAwait(false) ?? known;
        }, ct);
    }

    // The rungs a save uploads for a texture: the full-chain ladder under the block-compressible address
    // every load reads, and none for an HDR source.
    public static List<TextureVariantId> UploadPlan(TextureMetadata metadata)
    {
        if (metadata.IsHdr || metadata.IsAnimated || metadata.Width <= 0)
            return new List<TextureVariantId>();
        var plan = TextureVariantId.PlanFor(metadata.Width, metadata.Height, true, TextureCompressionKind.Block);
        int cap = EngineSettings.MaxTextureSize;
        if (cap > 0)
            plan.RemoveAll(id => id.MaxSize > cap);
        return plan;
    }

    private static bool HasLadder(LocalDB db, string baseLocalUri, TextureMetadata metadata)
    {
        var sidecar = GetMetadataUri(baseLocalUri);
        if (sidecar == null || !db.Exists(sidecar))
            return false;
        foreach (var id in UploadPlan(metadata))
        {
            var uri = GetVariantUri(baseLocalUri, id);
            if (uri == null || !db.Exists(uri))
                return false;
        }
        return true;
    }

    // One generation per texture at a time. An import's own pass, a first load's background fill and a
    // save's upload can reach the same texture together, and two writers on one derived file fail on
    // Windows or hand a reader half a rung. Keyed by the record, not the machine in the address. -xlinka
    private static readonly Dictionary<string, (SemaphoreSlim Gate, int Users)> _turns = new(StringComparer.Ordinal);

    private static async Task<T> OneAtATimeAsync<T>(string baseLocalUri, Func<Task<T>> work, CancellationToken ct = default)
    {
        string key = baseLocalUri.Substring(baseLocalUri.LastIndexOf('/') + 1);
        SemaphoreSlim gate;
        lock (_turns)
        {
            _turns.TryGetValue(key, out var turn);
            gate = turn.Gate ?? new SemaphoreSlim(1, 1);
            _turns[key] = (gate, turn.Users + 1);
        }
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await work().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            lock (_turns)
            {
                var turn = _turns[key];
                if (turn.Users <= 1)
                    _turns.Remove(key);
                else
                    _turns[key] = (turn.Gate, turn.Users - 1);
            }
        }
    }

    // GENERATION WORKERS.
    //
    // Every texture a world loads for the first time queues its ladder, and each one decodes its source
    // whole and downscales it into every rung. Run all at once they took every core and held every source
    // decoded together while the world was still coming up. They go through VariantWorkers: a couple of
    // threads below the frame's priority, one on a phone or a headset. The ladder is background work, it helps
    // the next load, and it finishes when it finishes. -xlinka
    private static async Task<TextureMetadata?> GenerateNowAsync(
        LocalDB db,
        string baseLocalUri,
        bool isNormalMap = false,
        bool mipmaps = true,
        TextureCompressionKind compression = TextureCompressionKind.Block,
        CancellationToken ct = default)
    {
        if (db == null || string.IsNullOrEmpty(baseLocalUri))
            return null;

        object? traced = TextureLoadTrace.Enabled ? string.Intern(baseLocalUri) : null;
        try
        {
            return await VariantWorkers.Run("ladder of " + baseLocalUri, token =>
            {
                TextureLoadTrace.Mark(traced, "gen-start", baseLocalUri);
                try
                {
                    return GenerateOnWorker(db, baseLocalUri, isNormalMap, mipmaps, compression, token);
                }
                finally
                {
                    TextureLoadTrace.Mark(traced, "gen-done", baseLocalUri);
                }
            }, urgent: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The workers stopped (the engine is going away); nothing is missing that the next run cannot make.
            return null;
        }
    }

    // One texture's ladder, start to finish on a variant worker. Every file goes down whole (LocalDB writes beside
    // and moves into place) and the index is written once at the end rather than twice a rung: on a store a few
    // avatars old each index write is tens of milliseconds and megabytes of garbage, and a ladder of five rungs
    // asked for twelve of them. -xlinka
    private static TextureMetadata? GenerateOnWorker(
        LocalDB db,
        string baseLocalUri,
        bool isNormalMap,
        bool mipmaps,
        TextureCompressionKind compression,
        CancellationToken ct)
    {
        bool changed = false;
        try
        {
            var sourceBytes = db.ReadAssetBytesAsync(baseLocalUri).GetAwaiter().GetResult();
            if (sourceBytes == null || sourceBytes.Length == 0)
            {
                Logger.Warn($"TextureVariantStore: no source bytes for '{baseLocalUri}'");
                return null;
            }
            ct.ThrowIfCancellationRequested();

            byte[]? rgba;
            int width, height, sourceWidth, sourceHeight;
            // Generation works from the source as this machine may hold it: under a size cap the base is
            // decoded already reduced, rungs above the cap are not made here, and the sidecar still
            // names the source's own size.
            int cap = EngineSettings.MaxTextureSize;

            // An HDR source gets its sidecar (so a joiner learns the dimensions and the HDR fact
            // without pulling pixels) and nothing else. Every rung of the ladder is RGBA8, and an
            // RGBA8 rung of a radiance map is a clipped picture with a wrong name.
            if (TextureMetadata.DetectHdr(sourceBytes))
            {
                byte[]? half;
                try
                {
                    half = CappedImageDecoder.DecodeRgbaHalf(sourceBytes, cap, out width, out height, out sourceWidth, out sourceHeight);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"TextureVariantStore: cannot decode HDR '{baseLocalUri}': {ex.Message}");
                    return null;
                }
                if (half == null || width <= 0 || height <= 0)
                    return null;

                int hdrMips = mipmaps ? TextureMetadata.FullMipCount(sourceWidth, sourceHeight) : 1;
                var hdrMeta = AsSource(TextureMetadata.AnalyzeHalf(half, width, height, hdrMips,
                    sourceBytes.LongLength,
                    TextureMetadata.ComputeGpuBytes(TextureFormatKind.RGBA16F, sourceWidth, sourceHeight, hdrMips),
                    isNormalMap), sourceWidth, sourceHeight);
                ct.ThrowIfCancellationRequested();
                changed = true;
                db.SetAssetMetadataAsync(baseLocalUri, bag => hdrMeta.WriteTo(bag), writeIndex: false).GetAwaiter().GetResult();
                var hdrSidecar = GetMetadataUri(baseLocalUri);
                if (hdrSidecar != null && !db.Exists(hdrSidecar))
                {
                    db.SaveDerivedAssetAsync(baseLocalUri, MetadataSuffix, hdrMeta.ToSidecarBytes(), MetadataExtension, writeIndex: false)
                        .GetAwaiter().GetResult();
                }
                return hdrMeta;
            }

            try
            {
                rgba = CappedImageDecoder.DecodeRgba(sourceBytes, cap, out width, out height, out sourceWidth, out sourceHeight);
            }
            catch (Exception ex)
            {
                Logger.Warn($"TextureVariantStore: cannot decode '{baseLocalUri}': {ex.Message}");
                return null;
            }

            if (rgba == null || width <= 0 || height <= 0)
                return null;
            ct.ThrowIfCancellationRequested();

            // An animated source gets a sidecar that says so and nothing else: every rung would be its first frame
            // alone, and a load that found one showed a still for good. The sidecar is written over an older one,
            // which is exactly what a GIF imported before frames were counted carries. -xlinka
            int frames = SourceFrames(sourceBytes);
            if (frames > 1)
            {
                var animated = AsSource(TextureMetadata.Analyze(
                    rgba, width, height, 1,
                    sourceBytes.LongLength, (long)sourceWidth * sourceHeight * 4,
                    TextureMetadata.DetectSRgb(sourceBytes), isNormalMap), sourceWidth, sourceHeight).WithFrames(frames);
                changed = true;
                db.SetAssetMetadataAsync(baseLocalUri, bag => animated.WriteTo(bag), writeIndex: false).GetAwaiter().GetResult();
                db.SaveDerivedAssetAsync(baseLocalUri, MetadataSuffix, animated.ToSidecarBytes(), MetadataExtension, writeIndex: false)
                    .GetAwaiter().GetResult();
                return animated;
            }

            // Base metadata describes the SOURCE as it will actually be uploaded: source resolution,
            // the mip count the renderer will hold, and the measured channel usage.
            int baseMips = mipmaps ? TextureMetadata.FullMipCount(sourceWidth, sourceHeight) : 1;
            long baseDecoded = mipmaps
                ? TextureMetadata.ComputeGpuBytes(TextureFormatKind.RGBA8, sourceWidth, sourceHeight, baseMips)
                : (long)sourceWidth * sourceHeight * 4;

            var metadata = AsSource(TextureMetadata.Analyze(
                rgba, width, height, baseMips,
                sourceBytes.LongLength, baseDecoded,
                TextureMetadata.DetectSRgb(sourceBytes), isNormalMap), sourceWidth, sourceHeight);

            changed = true;
            db.SetAssetMetadataAsync(baseLocalUri, bag => metadata.WriteTo(bag), writeIndex: false).GetAwaiter().GetResult();

            var sidecarUri = GetMetadataUri(baseLocalUri);
            if (sidecarUri != null && !db.Exists(sidecarUri))
            {
                db.SaveDerivedAssetAsync(baseLocalUri, MetadataSuffix, metadata.ToSidecarBytes(), MetadataExtension, writeIndex: false)
                    .GetAwaiter().GetResult();
            }

            var plan = TextureVariantId.PlanFor(sourceWidth, sourceHeight, mipmaps, compression);
            int heldEdge = System.Math.Max(width, height);
            foreach (var id in plan)
            {
                var uri = GetVariantUri(baseLocalUri, id);
                if (uri == null || db.Exists(uri) || id.MaxSize > heldEdge)
                    continue;
                ct.ThrowIfCancellationRequested();

                var blob = BuildRung(rgba, width, height, id, metadata.HasAlpha, metadata.ContentFormat, ct,
                    out int rungWidth, out int rungHeight, out int rungLevels, out long rungDecoded);
                ct.ThrowIfCancellationRequested();
                if (db.SaveDerivedAssetAsync(baseLocalUri, id.Identifier, blob, VariantExtension, writeIndex: false).GetAwaiter().GetResult() == null)
                    continue;

                var variantMeta = new TextureMetadata
                {
                    Width = rungWidth,
                    Height = rungHeight,
                    ContentFormat = metadata.ContentFormat,
                    HasAlpha = metadata.HasAlpha,
                    MipCount = System.Math.Max(1, rungLevels),
                    SourceBytes = blob.LongLength,
                    DecodedBytes = rungDecoded,
                    SRgb = metadata.SRgb,
                    IsNormalMap = isNormalMap,
                    VariantId = id.Identifier,
                };
                db.SetAssetMetadataAsync(uri, bag => variantMeta.WriteTo(bag), writeIndex: false).GetAwaiter().GetResult();

                Logger.Debug($"TextureVariantStore: {baseLocalUri} -> {id.Identifier} " +
                           $"({rungWidth}x{rungHeight}, {rungLevels} mips, {blob.Length / 1024} KB on disk)");
            }

            return metadata;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"TextureVariantStore: variant generation failed for '{baseLocalUri}': {ex.Message}");
            return null;
        }
        finally
        {
            if (changed)
                _ = db.SaveRecordsAsync();
        }
    }

    // A first load's own scan of the source, filed as the base's metadata and sidecar so the next load knows
    // the picture without decoding it, and can go straight to the renderer's compressed copy. The ladder still
    // comes from the background pass, which leaves both alone. Same per-texture turn as generation. -xlinka
    public static async Task RecordAnalysisAsync(LocalDB db, string baseLocalUri, TextureMetadata analyzed, int sourceWidth, int sourceHeight)
    {
        try
        {
            await OneAtATimeAsync(baseLocalUri, async () =>
            {
                var metadata = AsSource(analyzed, sourceWidth, sourceHeight);
                bool changed = false;
                // A record from before frames were counted (only a GIF or WebP is read again for it) is replaced.
                var known = TextureMetadata.ReadFrom(db.GetAssetMetadata(baseLocalUri));
                bool stale = known is { Frames: 0 } && metadata.Frames > 0;
                if (known is not { Width: > 0 } || stale)
                {
                    await db.SetAssetMetadataAsync(baseLocalUri, bag => metadata.WriteTo(bag), writeIndex: false).ConfigureAwait(false);
                    changed = true;
                }
                var sidecarUri = GetMetadataUri(baseLocalUri);
                if (sidecarUri != null && (stale || !db.Exists(sidecarUri)))
                {
                    await db.SaveDerivedAssetAsync(baseLocalUri, MetadataSuffix, metadata.ToSidecarBytes(), MetadataExtension, writeIndex: false)
                        .ConfigureAwait(false);
                    changed = true;
                }
                if (changed)
                    await db.SaveRecordsAsync().ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"TextureVariantStore: could not record the analysis of '{baseLocalUri}': {ex.Message}");
        }
    }

    private static TextureMetadata AsSource(TextureMetadata analyzed, int sourceWidth, int sourceHeight)
    {
        if (analyzed.Width == sourceWidth && analyzed.Height == sourceHeight)
            return analyzed;
        return new TextureMetadata
        {
            Width = sourceWidth,
            Height = sourceHeight,
            ContentFormat = analyzed.ContentFormat,
            HasAlpha = analyzed.HasAlpha,
            MipCount = analyzed.MipCount,
            SourceBytes = analyzed.SourceBytes,
            DecodedBytes = analyzed.DecodedBytes,
            SRgb = analyzed.SRgb,
            IsNormalMap = analyzed.IsNormalMap,
            IsHdr = analyzed.IsHdr,
            VariantId = analyzed.VariantId,
            Frames = analyzed.Frames,
        };
    }

    // Pictures in an encoded source: more than one only for an animated GIF or WebP.
    public static int SourceFrames(ReadOnlySpan<byte> encoded)
    {
        int frames = GifDecoder.IsGif(encoded) ? GifDecoder.CountFrames(encoded) : WebpStill.CountFrames(encoded);
        return System.Math.Max(1, frames);
    }

    // A record from before frames were counted, on a file of a kind that can animate: its ladder may be the first
    // frame of an animation, so the load goes to the file instead and records the count.
    public static bool MayBeAnimated(LocalDB db, string baseLocalUri, TextureMetadata metadata)
    {
        if (metadata.Frames != 0)
            return false;
        var extension = db.GetExtension(baseLocalUri);
        return string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase);
    }

    // Read straight off the URI, the same way the peer transferer resolves an owner.
    public static bool IsOwnedLocally(LocalDB db, string? localUri)
    {
        if (db == null || string.IsNullOrEmpty(localUri) || !localUri!.StartsWith("local://", StringComparison.Ordinal))
            return true;
        int slash = localUri.IndexOf('/', 8);
        var machineId = slash > 8 ? localUri.Substring(8, slash - 8) : localUri.Substring(8);
        return string.Equals(machineId, db.MachineId, StringComparison.Ordinal);
    }

    // Read a base texture's metadata without decoding a single pixel: the local record first, then
    // the sidecar blob on disk, then - for a texture another machine owns - the sidecar fetched
    // from that peer.
    //
    // That last hop is the point of having a sidecar at all. It is a couple of hundred bytes and it
    // tells a joiner the source's real dimensions, which is exactly what it needs to work out WHICH
    // variant to ask for. Without it a joiner would have to pull the full-resolution original just
    // to discover it should have asked for the 512px one. Fetched sidecars are written into the
    // local cache under the same derived address so the hop happens once. -xlinka
    public static async Task<TextureMetadata?> TryLoadMetadataAsync(LocalDB? db, string? baseLocalUri, AssetManager? manager = null)
    {
        if (db == null || string.IsNullOrEmpty(baseLocalUri))
            return null;

        var fromRecord = TextureMetadata.ReadFrom(db.GetAssetMetadata(baseLocalUri!));
        if (fromRecord != null)
            return fromRecord;

        var sidecarUri = GetMetadataUri(baseLocalUri);
        if (sidecarUri == null)
            return null;

        if (db.Exists(sidecarUri))
        {
            var local = await db.ReadAssetBytesAsync(sidecarUri).ConfigureAwait(false);
            return TextureMetadata.FromSidecarBytes(local);
        }

        // Only worth a network hop for an asset somebody else owns, and only inside a session.
        if (manager == null
            || IsOwnedLocally(db, baseLocalUri)
            || Networking.Session.SessionAssetTransferer.ForLocalAsset(baseLocalUri!) == null)
            return null;

        byte[]? bytes;
        try
        {
            bytes = await manager.RequestGather(new Uri(sidecarUri)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"TextureVariantStore: metadata fetch failed for {sidecarUri}: {ex.Message}");
            return null;
        }

        var metadata = TextureMetadata.FromSidecarBytes(bytes);
        if (metadata != null && bytes != null)
            await db.SaveDerivedAssetAsync(baseLocalUri!, MetadataSuffix, bytes, MetadataExtension, received: true).ConfigureAwait(false);
        return metadata;
    }

    // Assets whose generation is already running, so a texture referenced by twenty slots queues
    // one generation pass and not twenty.
    private static readonly HashSet<string> _generating = new(StringComparer.Ordinal);

    private const int LadderPatienceMs = 30000;

    // Queue variant generation for an asset that does not have any yet, without blocking the
    // caller. This is the catch-all: the import path generates eagerly, but textures reach the
    // engine by other routes too (a model's embedded textures, an asset received from a peer, a
    // world someone else built), and this makes the first load of any of them the trigger for
    // generating the variants that every load after it will use.
    //
    // Only for assets this machine owns. A texture a peer owns is theirs to generate; we ask them
    // for the variant and get the original if they have not made one yet.
    //
    // ct belongs to whoever asked (a texture asset: cancelled when it unloads, so a deleted import or a closed
    // world takes its queued ladders with it). The next load of the picture asks again. -xlinka
    public static void EnsureGeneratedInBackground(LocalDB? db, string? baseLocalUri, bool isNormalMap = false, bool mipmaps = true,
        CancellationToken ct = default)
    {
        if (db == null || string.IsNullOrEmpty(baseLocalUri) || !IsOwnedLocally(db, baseLocalUri) || ct.IsCancellationRequested)
            return;

        lock (_generating)
        {
            if (!_generating.Add(baseLocalUri!))
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // A world arriving asks for its pictures and their ladders together. The ladder only helps
                // the next load, so it starts once the pictures being waited for are delivered, or after half
                // a minute of a stream that never stops. -xlinka
                await Task.WhenAny(TextureAsset.LoadsIdle, Task.Delay(LadderPatienceMs, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                await GenerateAsync(db, baseLocalUri!, isNormalMap, mipmaps, ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Logger.Warn($"TextureVariantStore: background generation failed for '{baseLocalUri}': {ex.Message}");
            }
            finally
            {
                lock (_generating)
                    _generating.Remove(baseLocalUri!);
            }
        });
    }

    public static List<TextureVariantId> ListCachedVariants(LocalDB? db, string? baseLocalUri)
    {
        var found = new List<TextureVariantId>();
        if (db == null || string.IsNullOrEmpty(baseLocalUri))
            return found;

        foreach (int bucket in TextureVariantId.SizeBuckets)
        {
            foreach (bool mips in new[] { true, false })
            {
                foreach (var compression in new[] { TextureCompressionKind.Block, TextureCompressionKind.None })
                {
                    var id = new TextureVariantId(bucket, mips, compression);
                    var uri = GetVariantUri(baseLocalUri, id);
                    if (uri != null && db.Exists(uri))
                        found.Add(id);
                }
            }
        }
        return found;
    }
}
