// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Lumora.Core.Assets.Interop;

// Reader for the self-contained shared object package format.
//
// The container is an ordinary zip with a fixed layout:
//
//   R-Main.record                       JSON, the entry point
//   <id>.record                         JSON, one per additional record
//   Assets/<signature>                  raw asset bytes, stored without compression
//   Variants/<signature>/<variantId>    pre-baked texture variants
//   Metadata/<signature>.<kind>         JSON sidecars (bitmap, mesh, shader, ...)
//
// Every signature is a lowercased content hash, and references inside the object graph use a
// "packdb:///<signature>" URI. Nothing here reaches the network: a package carries every byte it
// needs, which is the whole reason we can convert one offline. -xlinka
public sealed class PackageArchive : IDisposable
{
    public const string AssetScheme = "packdb";
    public const string MainRecordId = "R-Main";

    private const string AssetsFolder = "Assets";
    private const string VariantsFolder = "Variants";
    private const string MetadataFolder = "Metadata";
    private const string RecordExtension = ".record";

    private ZipArchive? _archive;
    private readonly Dictionary<string, PackageRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZipArchiveEntry> _assets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, ZipArchiveEntry>> _variants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _metadataKinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZipArchiveEntry> _metadata = new(StringComparer.Ordinal);

    public string SourcePath { get; private set; } = string.Empty;

    public PackageRecord? MainRecord => _records.GetValueOrDefault(MainRecordId);
    public IReadOnlyCollection<PackageRecord> Records => _records.Values;
    public IReadOnlyCollection<string> AssetSignatures => _assets.Keys;
    public int AssetCount => _assets.Count;
    public int VariantCount => _variants.Values.Sum(v => v.Count);
    public int MetadataCount => _metadataKinds.Count;

    public static PackageArchive Open(string file)
    {
        var archive = new PackageArchive { SourcePath = file };
        archive.Load(File.OpenRead(file));
        return archive;
    }

    public static PackageArchive Open(Stream stream)
    {
        var archive = new PackageArchive();
        archive.Load(stream);
        return archive;
    }

    // The signature a "packdb:///<sig>" reference points at, or null for anything else. Package graphs
    // also carry http(s) URLs into content servers we deliberately never contact, so a null here is a
    // normal answer meaning "not something this package brought with it".
    public static string? SignatureOf(Uri? uri)
    {
        if (uri == null || !string.Equals(uri.Scheme, AssetScheme, StringComparison.OrdinalIgnoreCase))
            return null;
        if (uri.Segments.Length < 2)
            return null;
        return Path.GetFileNameWithoutExtension(uri.Segments[1]).ToLowerInvariant();
    }

    public bool HasAsset(string? signature) =>
        !string.IsNullOrEmpty(signature) && _assets.ContainsKey(signature.ToLowerInvariant());

    public long AssetLength(string signature) =>
        _assets.TryGetValue(signature.ToLowerInvariant(), out var e) ? e.Length : 0L;

    // Callers get a fresh stream each time; zip entry streams are forward-only and single-use.
    public Stream OpenAsset(string signature)
    {
        var key = signature.ToLowerInvariant();
        if (!_assets.TryGetValue(key, out var entry))
            throw new KeyNotFoundException($"Package has no asset '{signature}'");
        return entry.Open();
    }

    public byte[] ReadAsset(string signature)
    {
        var key = signature.ToLowerInvariant();
        if (!_assets.TryGetValue(key, out var entry))
            throw new KeyNotFoundException($"Package has no asset '{signature}'");
        return ReadEntry(entry);
    }

    // One pre-baked variant of an asset, by its identifier as VariantsOf lists it; null when there is none.
    public byte[]? ReadVariant(string signature, string variant)
    {
        if (!_variants.TryGetValue(signature.ToLowerInvariant(), out var set) || !set.TryGetValue(variant, out var entry))
            return null;
        return ReadEntry(entry);
    }

    // An asset's JSON sidecar, or null when it has none.
    public byte[]? ReadMetadata(string signature) =>
        _metadata.TryGetValue(signature.ToLowerInvariant(), out var entry) ? ReadEntry(entry) : null;

    // Straight into an array of the entry's own length. Copying through a growing buffer and then out of it
    // held each payload three times over on its way in.
    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        long length = entry.Length;
        if (length <= 0 || length > Array.MaxLength)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    // Just the leading bytes, for sniffing a container format without inflating a 40 MB texture.
    public byte[] PeekAsset(string signature, int count)
    {
        using var stream = OpenAsset(signature);
        var head = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(head, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        return read == count ? head : head[..read];
    }

    public IEnumerable<string> VariantsOf(string signature)
    {
        var key = signature.ToLowerInvariant();
        return _variants.TryGetValue(key, out var set) ? set.Keys : Enumerable.Empty<string>();
    }

    public string? MetadataKind(string signature) =>
        _metadataKinds.GetValueOrDefault(signature.ToLowerInvariant());

    private void Load(Stream stream)
    {
        _archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in _archive.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrEmpty(name))
                continue;

            if (RecordExtension.Equals(Path.GetExtension(name), StringComparison.OrdinalIgnoreCase))
            {
                ReadRecord(entry);
            }
            else if (name.StartsWith(AssetsFolder, StringComparison.OrdinalIgnoreCase))
            {
                _assets[Path.GetFileNameWithoutExtension(name).ToLowerInvariant()] = entry;
            }
            else if (name.StartsWith(VariantsFolder, StringComparison.OrdinalIgnoreCase))
            {
                // Variants/<signature>/<variantIdentifier>
                var signature = Path.GetFileName(Path.GetDirectoryName(name) ?? string.Empty).ToLowerInvariant();
                if (signature.Length == 0)
                    continue;
                if (!_variants.TryGetValue(signature, out var set))
                    _variants[signature] = set = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                set[Path.GetFileNameWithoutExtension(name)] = entry;
            }
            else if (name.StartsWith(MetadataFolder, StringComparison.OrdinalIgnoreCase))
            {
                var kind = Path.GetExtension(name).TrimStart('.');
                var signature = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                _metadataKinds[signature] = kind;
                _metadata[signature] = entry;
            }
        }
    }

    private void ReadRecord(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            var record = JsonSerializer.Deserialize<PackageRecord>(stream, PackageRecord.JsonOptions);
            if (record == null)
                return;
            record.RecordId ??= Path.GetFileNameWithoutExtension(entry.FullName);
            _records[record.RecordId] = record;
        }
        catch (JsonException)
        {
            // A record we cannot parse is reported by the inspector rather than failing the whole open:
            // a package with one odd sidecar still has a perfectly good main graph.
        }
    }

    public void Dispose()
    {
        _archive?.Dispose();
        _archive = null;
    }
}
