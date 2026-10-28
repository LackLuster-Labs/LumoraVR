// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lumora.Core.Assets;

public enum TextureCompressionKind
{
    // Always available.
    None = 0,

    // The renderer may block-compress this variant if the running GPU supports it. The engine
    // side only records the intent; picking the actual block format and doing the compression is
    // the renderer's job, because only it knows what the device accepts.
    Block = 1,
}

// Identifies one generated variant of a texture: a maximum edge length, whether a mip chain is
// baked in, and whether the renderer is allowed to block-compress it. Three axes, nothing else -
// every extra axis multiplies the number of blobs we generate and transfer, and these are the
// three that change what the GPU actually holds.
//
// The identifier string is deliberately filename- and URI-safe (unreserved characters only) so it
// can be appended straight onto a base asset's local:// URI to address the variant blob, with no
// escaping anywhere in the fetch path. It is also stable: the same inputs always produce the same
// identifier, so a peer can construct the URI for a variant it has never seen and ask the owner
// for it by name. -xlinka
public readonly struct TextureVariantId : IEquatable<TextureVariantId>
{
    // Bumped when the generator's output changes in a way that makes old blobs wrong (different
    // downscale filter, different container layout). Old blobs keep their old identifier and are
    // simply never requested again.
    public const int CurrentVersion = 2;

    // Descending. 128 is not a quality setting anyone picks; it exists so a progressive load always
    // has one rung that costs almost nothing to fetch and upload. Adding it is additive - the
    // identifier format did not change, so every blob already on disk stays addressable and a
    // regeneration pass just fills in the one that is missing. -xlinka
    public static readonly int[] SizeBuckets = { 2048, 1024, 512, 256, 128 };

    // The rung that always exists for any source big enough to have variants at all.
    public const int PreviewSize = 128;

    // 0 means no cap, i.e. the source resolution.
    public int MaxSize { get; }

    public bool Mipmaps { get; }

    public TextureCompressionKind Compression { get; }

    public int Version { get; }

    public TextureVariantId(int maxSize, bool mipmaps, TextureCompressionKind compression, int version = CurrentVersion)
    {
        MaxSize = System.Math.Max(0, maxSize);
        Mipmaps = mipmaps;
        Compression = compression;
        Version = version;
    }

    public bool IsOriginal => MaxSize <= 0;

    // The address a variant's BYTES live under. The compression axis is an upload intent: the
    // generated blob is RGBA8 either way, so a texture that opted out of block compression reads
    // the same rung everyone else does and only differs in what the renderer does with it. Without
    // this the opt-out silently lost the resolution cap, because nobody generates a "compnone"
    // blob and the load fell through to the full-size source. -xlinka
    public TextureVariantId Storage =>
        Compression == TextureCompressionKind.Block ? this : new TextureVariantId(MaxSize, Mipmaps, TextureCompressionKind.Block, Version);

    // Stable identifier, e.g. v1-max1024-mips1-compblock. Safe as both a URI path segment
    // and a filename fragment.
    public string Identifier =>
        string.Create(CultureInfo.InvariantCulture,
            $"v{Version}-max{MaxSize}-mips{(Mipmaps ? 1 : 0)}-comp{(Compression == TextureCompressionKind.Block ? "block" : "none")}");

    public static bool TryParse(string? identifier, out TextureVariantId id)
    {
        id = default;
        if (string.IsNullOrEmpty(identifier))
            return false;

        int version = 0, maxSize = -1;
        bool mips = false;
        var compression = TextureCompressionKind.None;

        foreach (var part in identifier!.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("v", StringComparison.Ordinal)
                && int.TryParse(part.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                version = v;
            else if (part.StartsWith("max", StringComparison.Ordinal)
                && int.TryParse(part.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m))
                maxSize = m;
            else if (part.StartsWith("mips", StringComparison.Ordinal))
                mips = part.EndsWith("1", StringComparison.Ordinal);
            else if (part.StartsWith("comp", StringComparison.Ordinal))
                compression = part.EndsWith("block", StringComparison.Ordinal)
                    ? TextureCompressionKind.Block : TextureCompressionKind.None;
            else
                return false;
        }

        if (version <= 0 || maxSize < 0)
            return false;

        id = new TextureVariantId(maxSize, mips, compression, version);
        return true;
    }

    // The set of variants worth generating for a source of these dimensions: every bucket strictly
    // smaller than the source's longest edge. A bucket at or above the source is skipped, since
    // generating it would either duplicate the original or upscale it - both a waste of disk and a
    // quality loss. A source already within the smallest bucket generates nothing and is served
    // from its own URI. -xlinka
    //
    // Emitted smallest first so a generation pass that is still running has already written the
    // cheap rung by the time a load comes looking for one.
    public static List<TextureVariantId> PlanFor(int width, int height, bool mipmaps, TextureCompressionKind compression)
    {
        var plan = new List<TextureVariantId>();
        int longest = System.Math.Max(width, height);
        for (int i = SizeBuckets.Length - 1; i >= 0; i--)
        {
            int bucket = SizeBuckets[i];
            if (bucket < longest)
                plan.Add(new TextureVariantId(bucket, mipmaps, compression));
        }
        return plan;
    }

    // Returns the smallest bucket that is at or above cap only when that bucket would actually shrink the
    // source; otherwise the original, because capping a 512px texture at 1024 has nothing to load but the
    // source itself.
    public static TextureVariantId Select(int sourceWidth, int sourceHeight, int cap, bool mipmaps, TextureCompressionKind compression)
    {
        int longest = System.Math.Max(sourceWidth, sourceHeight);
        if (cap <= 0 || longest <= 0 || longest <= cap)
            return new TextureVariantId(0, mipmaps, compression);

        // Snap the cap down onto a generated bucket so we never ask for a blob nobody makes.
        int chosen = 0;
        foreach (int bucket in SizeBuckets)
        {
            if (bucket <= cap && bucket < longest)
            {
                chosen = bucket;
                break;
            }
        }
        return new TextureVariantId(chosen, mipmaps, compression);
    }

    // Never upscales.
    public (int width, int height) ResolveSize(int sourceWidth, int sourceHeight)
    {
        if (IsOriginal || sourceWidth <= 0 || sourceHeight <= 0)
            return (sourceWidth, sourceHeight);

        int longest = System.Math.Max(sourceWidth, sourceHeight);
        if (longest <= MaxSize)
            return (sourceWidth, sourceHeight);

        double scale = (double)MaxSize / longest;
        int w = System.Math.Max(1, (int)System.Math.Round(sourceWidth * scale));
        int h = System.Math.Max(1, (int)System.Math.Round(sourceHeight * scale));
        return (w, h);
    }

    public bool Equals(TextureVariantId other) =>
        MaxSize == other.MaxSize && Mipmaps == other.Mipmaps
        && Compression == other.Compression && Version == other.Version;

    public override bool Equals(object? obj) => obj is TextureVariantId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(MaxSize, Mipmaps, Compression, Version);

    public override string ToString() => IsOriginal ? "original" : Identifier;

    public static bool operator ==(TextureVariantId a, TextureVariantId b) => a.Equals(b);
    public static bool operator !=(TextureVariantId a, TextureVariantId b) => !a.Equals(b);
}
