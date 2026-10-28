// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;

namespace Lumora.Core.Assets;

// Addressing for the renderer's machine-local compressed-texture cache.
//
// The renderer owns the contents of that cache (only it knows which block format the device took),
// but both sides have to agree on the NAME of a blob or the engine cannot tell whether a rung is
// already cheap to put on screen. The key lives here so it can only ever be computed one way. It
// hashes the identity of the pixels (address plus variant) together with the dimensions, so a blob
// can never be read back for a different image; dimensions are in there because a procedural resize
// reuses the same address. -xlinka
public static class TextureGpuCache
{
    // Blobs are sealed and named by a keyed hash of their key, with no extension. Files from before that carry
    // the plain key and this extension; they still read and the background pass seals them.
    public const string LegacyExtension = ".lvgpu";
    public const string NameScope = "gpu";

    public static string FileFor(string directory, string key)
    {
        Persistence.LocalEncryption.WireSealedStorage();
        return Path.Combine(directory, Lumora.Nexus.Storage.SealedKeys.NameFor(NameScope, key));
    }

    public static string LegacyFileFor(string directory, string key) => Path.Combine(directory, key + LegacyExtension);

    // Set once at startup by the render layer, which is the only thing that knows what this device can
    // sample. It is PART OF THE KEY: a blob baked as BPTC on a desktop and one baked as ASTC on a
    // headset are different bytes for the same picture, and without the tag they hash the same and the
    // wrong one gets handed over. Empty until set, which keys the same as the old scheme did. -xlinka
    public static string CompressionTag { get; set; } = string.Empty;

    // Files a block-compressed chain this machine did not have to make (a package's own copy) under a cache
    // path, in the renderer's own blob layout: path, width, height, format, whether it carries every mip, the
    // blocks. False when this device does not sample that format. Set by the render layer; null means no
    // renderer, and nothing is filed. -xlinka
    public delegate bool BlockChainWriter(string path, int width, int height, TextureFormatKind format, bool mipmaps, byte[] blocks);

    public static BlockChainWriter? BlockWriter { get; set; }

    // The per-texture inputs the format decision reads. Alpha is a property of the pixels and so of
    // the address, but the normal-map flag comes from the descriptor and two providers can load the
    // same address with it set differently; both would otherwise hash to one blob and one of them
    // would be handed the other's format. HDR is in for the same reason it is in the sidecar: a
    // blob baked as BC6H and one baked as BC1 must never share a name. -xlinka
    // An address whose last segment is the content's own hash names the same pixels for ever. Any other one
    // (a file the build ships, a path on this disk, a web address) can change under the same name, and a key
    // made of the name alone kept handing back the old pixels: an icon edited for an update stayed the old
    // icon on every machine that had seen it. Those keys also carry a token of the bytes they were read from.
    public static bool NamesItsContent(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return false;
        int end = uri.Length;
        int query = uri.IndexOfAny(new[] { '?', '#' });
        if (query >= 0)
            end = query;
        int slash = uri.LastIndexOf('/', end - 1);
        int start = slash + 1;
        int dot = uri.IndexOf('.', start);
        if (dot >= 0 && dot < end)
            end = dot;
        if (end - start != 64)
            return false;
        for (int i = start; i < end; i++)
        {
            char c = uri[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    // Small sources are hashed whole; a big one by its length, its two ends and an even spread of slices,
    // which any real edit of a picture moves, without reading megabytes through a hash on load.
    private const int WholeHashLimit = 1 << 20;
    private const int EdgeBytes = 256 * 1024;
    private const int SliceBytes = 4096;
    private const int Slices = 64;

    public static string SourceToken(byte[] source)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        if (source.Length <= WholeHashLimit)
            return Convert.ToHexString(sha.ComputeHash(source), 0, 8).ToLowerInvariant();
        sha.TransformBlock(BitConverter.GetBytes(source.LongLength), 0, 8, null, 0);
        sha.TransformBlock(source, 0, EdgeBytes, null, 0);
        sha.TransformBlock(source, source.Length - EdgeBytes, EdgeBytes, null, 0);
        long stride = (source.Length - SliceBytes) / Slices;
        for (int i = 0; i < Slices; i++)
            sha.TransformBlock(source, (int)(i * stride), SliceBytes, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!, 0, 8).ToLowerInvariant();
    }

    public static string Intent(bool hasAlpha, bool isNormalMap, bool isHdr) =>
        string.Concat(hasAlpha ? "a1" : "a0", isNormalMap ? "n1" : "n0", isHdr ? "h1" : "h0");

    public static string BuildKey(string uri, TextureVariantId? variant, int width, int height, int mipCount, string? intent = null)
    {
        string identity = $"{uri}|{variant?.Identifier ?? "src"}|{width}x{height}|{mipCount}|{CompressionTag}|{intent ?? string.Empty}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    public static string? GetPath(LocalDB? db, string? key)
    {
        if (db == null || string.IsNullOrEmpty(key))
            return null;
        var directory = db.GetGpuCachePath();
        return string.IsNullOrEmpty(directory) ? null : FileFor(directory, key!);
    }

    // True when this exact payload has already been compressed on this machine. The point of asking
    // is that a cached rung skips both the decode and the compression, so a second visit to the same
    // world can go straight to full quality instead of paying for preview rungs it will throw away
    // milliseconds later.
    public static bool IsCached(LocalDB? db, string uri, TextureVariantId? variant, int width, int height, int mipCount, string? intent = null)
    {
        if (db == null || string.IsNullOrEmpty(uri) || width <= 0 || height <= 0)
            return false;
        var key = BuildKey(uri, variant, width, height, mipCount, intent);
        var path = GetPath(db, key);
        try
        {
            return path != null && (File.Exists(path) || File.Exists(LegacyFileFor(Path.GetDirectoryName(path)!, key)));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
