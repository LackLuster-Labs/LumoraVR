// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Lumora.Core.Assets.Interop;

// A package carries its pictures twice: the creator's original file, and the same picture already block
// compressed (BC1, or BC3 with alpha) at every size it was asked for, one file per mip level down to a tail
// that holds the small ones together. The original is what goes into the database. The compressed levels,
// when they make up a whole chain at the original's size, are put together and filed in the renderer's
// compressed cache under the key the picture's own first load looks for, with the picture's facts beside it,
// so that load is a file read and one upload: no decode, no mips, no compression. Those three were most of a
// world's first load; transcoding the stored levels back to blocks is a few milliseconds a picture.
//
// Anything that does not fit (no chain, a gap in it, a swizzled normal map, sizes the block rows cannot be
// turned over on) is left alone and loads the long way, exactly as before. -xlinka
internal static class PackageTextureChains
{
    // Levels at or under this many pixels share one file with everything below them.
    private const int TailPixels = 128 * 128;

    private readonly record struct Rung(string Id, bool Bc3, int Width, int Height, bool Mips);

    // True when a chain was filed (or already was) for this picture. Never throws: the picture itself is already
    // saved, and whatever goes wrong here only means its first load takes the long way.
    public static async Task<bool> TryFileAsync(PackageArchive archive, string signature, byte[] source, string localUri, LocalDB db)
    {
        try
        {
            return await FileAsync(archive, signature, source, localUri, db).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"Package import: the stored compressed copy of {signature[..System.Math.Min(12, signature.Length)]} could not be used: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> FileAsync(PackageArchive archive, string signature, byte[] source, string localUri, LocalDB db)
    {
        if (TextureGpuCache.BlockWriter is not { } writer)
            return false;
        if (TextureMetadata.DetectHdr(source) || GifDecoder.IsGif(source) || WebpStill.IsAnimated(source))
            return false;
        if (!ImageHeader.TryProbe(source, out _, out int width, out int height) || width < 4 || height < 4)
            return false;

        List<Rung> rungs;
        lock (archive)
            rungs = Rungs(archive.VariantsOf(signature));
        if (rungs.Count == 0)
            return false;

        bool? bc3 = PickFamily(rungs, width, height, archive, signature);
        if (bc3 == null)
            return false;

        int levels = TextureMetadata.FullMipCount(width, height);
        var key = TextureGpuCache.BuildKey(localUri, new TextureVariantId(0, true, TextureCompressionKind.Block), width, height, levels,
            TextureGpuCache.Intent(bc3.Value, false, false));
        var path = TextureGpuCache.GetPath(db, key);
        if (path == null)
            return false;

        if (!System.IO.File.Exists(path))
        {
            var blocks = BuildChain(archive, signature, rungs, bc3.Value, width, height, levels);
            if (blocks == null || !writer(path, width, height, bc3.Value ? TextureFormatKind.BC3_RGBA : TextureFormatKind.BC1_RGB, true, blocks))
                return false;
        }

        // The facts the load reads before it decides how to load, as the load itself would have recorded
        // them. A record that already has them (an earlier load, a second import) keeps its own.
        if (TextureMetadata.ReadFrom(db.GetAssetMetadata(localUri)) is not { Width: > 0 })
        {
            var metadata = new TextureMetadata
            {
                Width = width,
                Height = height,
                ContentFormat = bc3.Value ? TextureFormatKind.RGBA8 : TextureFormatKind.RGB8,
                HasAlpha = bc3.Value,
                MipCount = levels,
                SourceBytes = source.LongLength,
                DecodedBytes = TextureMetadata.ComputeGpuBytes(TextureFormatKind.RGBA8, width, height, levels),
                SRgb = TextureMetadata.DetectSRgb(source),
            };
            await db.SetAssetMetadataAsync(localUri, bag => metadata.WriteTo(bag), writeIndex: false).ConfigureAwait(false);
            var sidecar = TextureVariantStore.GetMetadataUri(localUri);
            if (sidecar != null && !db.Exists(sidecar))
                await db.SaveDerivedAssetAsync(localUri, TextureVariantStore.MetadataSuffix, metadata.ToSidecarBytes(),
                    TextureVariantStore.MetadataExtension, writeIndex: false).ConfigureAwait(false);
        }
        return true;
    }

    private static List<Rung> Rungs(IEnumerable<string> variants)
    {
        var rungs = new List<Rung>();
        foreach (var id in variants)
        {
            string? compression = null, colour = null, alpha = null;
            int width = 0, height = 0, version = 0;
            bool mips = false;
            foreach (var part in id.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                var name = part.AsSpan(0, eq);
                var value = part.Substring(eq + 1);
                if (name.SequenceEqual("compression")) compression = value;
                else if (name.SequenceEqual("width")) int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                else if (name.SequenceEqual("height")) int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                else if (name.SequenceEqual("version")) int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
                else if (name.SequenceEqual("mips")) mips = value.Equals("True", StringComparison.OrdinalIgnoreCase);
                else if (name.SequenceEqual("colorPreprocess")) colour = value;
                else if (name.SequenceEqual("alphaPreprocess")) alpha = value;
            }
            if (version != 3 || width <= 0 || height <= 0)
                continue;
            if ((colour != null && colour != "None") || (alpha != null && alpha != "None"))
                continue;
            bool? isBc3 = compression switch
            {
                "BC1_Crunched" or "BC1_Crunched_Non_Perceptual" => false,
                "BC3_Crunched" or "BC3_Crunched_Non_Perceptual" => true,
                _ => null,
            };
            if (isBc3 != null)
                rungs.Add(new Rung(id, isBc3.Value, width, height, mips));
        }
        return rungs;
    }

    // The chain's levels, largest first: one file each down to the tail.
    private static IEnumerable<(int Width, int Height, bool Tail)> Steps(int width, int height)
    {
        int w = width, h = height;
        while (true)
        {
            bool tail = (long)w * h <= TailPixels;
            yield return (w, h, tail);
            if (tail)
                yield break;
            w = System.Math.Max(1, w >> 1);
            h = System.Math.Max(1, h >> 1);
        }
    }

    private static Rung? Find(List<Rung> rungs, bool bc3, int width, int height, bool mips)
    {
        foreach (var rung in rungs)
        {
            if (rung.Bc3 == bc3 && rung.Width == width && rung.Height == height && rung.Mips == mips)
                return rung;
        }
        return null;
    }

    private static bool Complete(List<Rung> rungs, bool bc3, int width, int height)
    {
        foreach (var (w, h, tail) in Steps(width, height))
        {
            // Turning the rows over works a block row at a time, so a level whose height splits a block
            // cannot be turned without moving pixels across blocks.
            if (h > 4 && (h & 3) != 0)
                return false;
            if (Find(rungs, bc3, w, h, tail) == null)
                return false;
        }
        return true;
    }

    // BC1 or BC3, whichever makes a whole chain; when both do, the one the picture's own record says it
    // needs. Null when neither does.
    private static bool? PickFamily(List<Rung> rungs, int width, int height, PackageArchive archive, string signature)
    {
        bool bc1 = Complete(rungs, false, width, height);
        bool bc3 = Complete(rungs, true, width, height);
        if (bc1 && bc3)
        {
            byte[]? json;
            lock (archive)
                json = archive.ReadMetadata(signature);
            bool opaque = json != null && System.Text.Encoding.UTF8.GetString(json).Contains("\"alphaData\":\"FullyOpaque\"", StringComparison.Ordinal);
            return !opaque;
        }
        return bc3 ? true : bc1 ? false : null;
    }

    private static byte[]? BuildChain(PackageArchive archive, string signature, List<Rung> rungs, bool bc3, int width, int height, int levels)
    {
        var parts = new List<byte[]>();
        long total = 0;
        foreach (var (w, h, tail) in Steps(width, height))
        {
            var rung = Find(rungs, bc3, w, h, tail)!.Value;
            byte[]? file;
            lock (archive)
                file = archive.ReadVariant(signature, rung.Id);
            if (file == null)
                return null;
            var blocks = CrnTranscoder.Transcode(file, out var header);
            int expectLevels = tail ? TextureMetadata.FullMipCount(w, h) : 1;
            if (blocks == null || header.Width != w || header.Height != h || header.Levels != expectLevels
                || (header.Format == CrnTranscoder.Format.Dxt5) != bc3)
                return null;
            parts.Add(blocks);
            total += blocks.LongLength;
        }

        var chain = new byte[total];
        int offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, chain, offset, part.Length);
            offset += part.Length;
        }

        // The stored levels count their rows from the bottom; ours run from the top.
        offset = 0;
        int blockBytes = bc3 ? 16 : 8;
        for (int level = 0; level < levels; level++)
        {
            int w = System.Math.Max(1, width >> level), h = System.Math.Max(1, height >> level);
            FlipLevel(chain, offset, w, h, blockBytes);
            offset += ((w + 3) >> 2) * ((h + 3) >> 2) * blockBytes;
        }
        return offset == chain.Length ? chain : null;
    }

    // Turns one level upside down: block rows in reverse order, and the pixel rows inside each block too. A
    // level under four pixels high only turns the rows it has.
    private static void FlipLevel(byte[] data, int offset, int width, int height, int blockBytes)
    {
        int across = (width + 3) >> 2, down = (height + 3) >> 2;
        int rowBytes = across * blockBytes;
        var scratch = new byte[rowBytes];
        for (int top = 0, bottom = down - 1; top < bottom; top++, bottom--)
        {
            Buffer.BlockCopy(data, offset + top * rowBytes, scratch, 0, rowBytes);
            Buffer.BlockCopy(data, offset + bottom * rowBytes, data, offset + top * rowBytes, rowBytes);
            Buffer.BlockCopy(scratch, 0, data, offset + bottom * rowBytes, rowBytes);
        }

        int rows = System.Math.Min(4, height);
        if (rows < 2)
            return;
        for (int block = 0, at = offset; block < across * down; block++, at += blockBytes)
        {
            int colour = at;
            if (blockBytes == 16)
            {
                FlipAlphaRows(data, at + 2, rows);
                colour = at + 8;
            }
            // One selector byte a row.
            if (rows == 4)
            {
                (data[colour + 4], data[colour + 7]) = (data[colour + 7], data[colour + 4]);
                (data[colour + 5], data[colour + 6]) = (data[colour + 6], data[colour + 5]);
            }
            else
                (data[colour + 4], data[colour + 5]) = (data[colour + 5], data[colour + 4]);
        }
    }

    // Twelve bits a row, little-endian across six bytes.
    private static void FlipAlphaRows(byte[] data, int at, int rows)
    {
        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)data[at + i] << (8 * i);
        ulong flipped = bits & ~((1UL << (12 * rows)) - 1);
        for (int row = 0; row < rows; row++)
            flipped |= (bits >> (12 * row) & 0xFFF) << (12 * (rows - 1 - row));
        for (int i = 0; i < 6; i++)
            data[at + i] = (byte)(flipped >> (8 * i));
    }
}
