// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Nexus.Cloud.Cdn;
using Lumora.Script;

namespace Lumora.Core.Persistence;

// One asset the save depends on, by content hash. What the manifest carries and what the service is
// told about, so a texture used by ten saves is stored once and counted once.
public sealed class CloudAssetEntry
{
    public string Hash = "";
    public string Name = "";
    public string Extension = "";
    public string Type = "other";
    public long SizeBytes;
    // The local:// address it was uploaded from, when it came from this machine.
    public string LocalUri = "";
}

// A saved graph references its textures, meshes and clips by URL, and on the machine that made it
// those are local:// records in the asset database. A save that leaves the machine cannot carry those
// URLs, so before the graph is uploaded every local:// asset is uploaded by its own hash (skipped
// when the store already has it) and the URL is rewritten to lumora:///<hash>. Loading does the
// reverse: every lumora:/// asset the graph names is brought into the local database before the
// graph is built, and the provider resolves the URL to that file. Hashes are SHA-256 of the bytes on
// both sides, so the local record and the cloud blob are the same name for the same thing. -xlinka
public static class CloudAssetPacker
{
    public const string ManifestKey = "CloudAssets";
    public const string CloudScheme = "lumora";

    // How many files a sync sends at once. Each one is a few round trips that mostly wait on the service,
    // so one at a time left a fast line idle. -xlinka
    public const int UploadWidth = 8;

    // Files up to this size go up ahead and together; bigger ones go one at a time, so a save of a few
    // large videos never holds them all in memory at once.
    private const long AheadLimit = 32L * 1024 * 1024;

    // Uploads every local:// asset the tree references, rewrites the URLs, and writes the manifest into
    // the tree. Returns the manifest. The upload is handed in: a file to the hash it was stored under, or
    // no hash and why not.
    //
    // A file this machine no longer has can never go up from here, so the save goes up without it: its URL
    // is cleared, a script constant naming it is blanked, and it is named in dropped for the person saving.
    // A file that is here but would not upload is a problem instead: the tree is left as it was and the
    // caller fails the save, which can be tried again. -xlinka
    public static Task<List<CloudAssetEntry>> PackAsync(DataTreeDictionary root, Func<string, Task<(string? Hash, string? Error)>> upload,
        LocalDB? db, List<string>? problems = null, List<string>? dropped = null)
    {
        var packing = new Packing(async (_, path) =>
        {
            var (hash, error) = await upload(path).ConfigureAwait(false);
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            return (hash, size, error);
        }, db);
        return PackAsync(root, packing, problems, dropped, null, null, null, 1, default);
    }

    // The same through a sink, the way the inventory sync sends a save up. Every file is read through the
    // asset database, so one kept encrypted on this machine goes up as its plain bytes, and before anything
    // is sent the store is asked once which of the files it lacks: the others are not sent at all.
    //
    // The parts the tree already names by cloud address, from whatever it was spawned or copied from, go in
    // the same question and on the same list. The list is all the store keeps an item's files by, so a part
    // left off it is deleted once nothing else lists it, and a save of a spawned copy used to list none of
    // its parts. A part the store lacks goes up again from this machine's copy, the asset database's or the
    // download cache's. One this machine has no copy of either stays named and listed, the save goes up
    // anyway, and it is named in lost: the inventory shows the item as broken until a machine that has the
    // part saves it again. -xlinka
    public static Task<List<CloudAssetEntry>> PackAsync(DataTreeDictionary root, ICloudUploadSink sink, LocalDB db,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyCollection<string>?>>? missing, List<string>? problems,
        List<string>? dropped, CancellationToken ct, List<string>? lost = null, ContentCache? cache = null)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packing = new Packing(async (source, path) =>
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await db.ReadAssetBytesAsync(source).ConfigureAwait(false);
            if (bytes == null)
                return (null, 0L, "could not be read on this machine");
            var hash = ContentHash.FromBytes(bytes);
            if (present.Contains(hash))
                return (hash, bytes.LongLength, null);
            var (stored, error) = await sink.StoreAsync(bytes, db.GetExtension(source), ct).ConfigureAwait(false);
            return (stored, bytes.LongLength, error);
        }, db) { Cache = cache, CompactTextures = true, Token = ct };
        return PackAsync(root, packing, problems, dropped, lost, missing == null ? null : async asked =>
        {
            var lacking = await missing(asked, ct).ConfigureAwait(false);
            if (lacking == null)
                return null;
            var gone = new HashSet<string>(lacking, StringComparer.OrdinalIgnoreCase);
            foreach (var key in asked)
            {
                if (!gone.Contains(key))
                    present.Add(key);
            }
            return gone;
        }, async hash =>
        {
            ct.ThrowIfCancellationRequested();
            var (bytes, extension, localUri) = await LocalCopyAsync(db, cache, hash, ct).ConfigureAwait(false);
            if (bytes == null)
                return Restore.NotHere;
            var (stored, error) = await sink.StoreAsync(bytes, extension, ct).ConfigureAwait(false);
            return stored == null
                ? Restore.Refused(error ?? "no answer")
                : new Restore(true, bytes.LongLength, extension, localUri, null);
        }, UploadWidth, ct);
    }

    // This machine's copy of a part the store keeps: the asset database's record of its hash, else the download
    // cache's file. Only bytes that hash to the name count. The extension is the one the record was filed under,
    // none for the cache, which keeps no names; the address is the record's, which the variants are made from.
    public static async Task<(byte[]? Bytes, string Extension, string? LocalUri)> LocalCopyAsync(LocalDB? db, ContentCache? cache,
        string hash, CancellationToken ct = default)
    {
        hash = hash.ToLowerInvariant();
        if (db?.UriForHash(hash) is { } uri)
        {
            var bytes = await db.ReadAssetBytesAsync(uri).ConfigureAwait(false);
            if (bytes != null && ContentHash.FromBytes(bytes) == hash)
                return (bytes, KnownExtension(db.GetExtension(uri)), uri);
        }
        if (cache != null && await cache.GetCached(hash, ct).ConfigureAwait(false) is { } cached)
            return (cached, string.Empty, null);
        return (null, string.Empty, null);
    }

    // What a fetch files a blob under when its list does not say.
    private const string UnknownExtension = ".bin";

    private static string KnownExtension(string? path)
    {
        var extension = string.IsNullOrEmpty(path) ? string.Empty : Path.GetExtension(path) ?? string.Empty;
        return string.Equals(extension, UnknownExtension, StringComparison.OrdinalIgnoreCase) ? string.Empty : extension;
    }

    // What putting back one part the store lost came to: no copy here, back up from one, or a copy here the
    // store would not take.
    private readonly record struct Restore(bool Here, long Size, string Extension, string? LocalUri, string? Error)
    {
        public static Restore NotHere => new(false, 0, string.Empty, null, null);
        public static Restore Refused(string error) => new(true, 0, string.Empty, null, error);
    }

    // A save's files as this machine sees them: the ones it no longer has, named the way the person saving
    // knows them, and what the ones it has weigh. It only looks: nothing is uploaded, nothing stored again.
    public static async Task<(List<string> Missing, long LocalBytes)> CheckFilesAsync(DataTreeDictionary root, LocalDB db)
    {
        var packing = new Packing((_, _) => Task.FromResult<(string?, long, string?)>((null, 0L, "not sent")), db);
        await FindAsync(root, packing).ConfigureAwait(false);
        var named = new List<string>(packing.Missing.Count);
        if (packing.Missing.Count > 0)
        {
            var uses = new Dictionary<string, string>(StringComparer.Ordinal);
            DescribeUses(root, null, null, uses);
            foreach (var uri in packing.Missing)
                named.Add(packing.NameOf(uri, uses));
        }
        return (named, packing.FoundBytes());
    }

    // Every local file the tree names, as a URL or as a constant on an open script node, looked up.
    private static async Task<(List<string> Locals, List<(DataTreeDictionary Entry, string Uri)> Constants)> FindAsync(
        DataTreeDictionary root, Packing packing)
    {
        var locals = new List<string>();
        CollectUrls(root, "local", locals);

        // An open script graph keeps its constants on its node slots, each the JSON of one value.
        var constants = new List<(DataTreeDictionary Entry, string Uri)>();
        var entries = new List<(DataTreeDictionary Entry, string? Node)>();
        CollectNodeValues(root, null, entries);
        foreach (var (entry, node) in entries)
        {
            if (entry.TryGetNode("Json") is not DataTreeValue { IsUrl: false, Value: string } json)
                continue;
            GraphValue value;
            try
            {
                value = GraphValue.ParseJson(json.Extract<string>());
            }
            catch (GraphFormatException)
            {
                continue;
            }
            if (value.Kind != GraphValueKind.String)
                continue;
            var where = node != null ? $"a file named on the node \"{node}\"" : "a file named on a script node";
            // One typed in by cloud address is a part of the save from before, kept as it is.
            if (IsCloudAddress(value.String))
            {
                packing.Cloud.Add(value.String!);
                packing.NameIn(value.String!, where);
                continue;
            }
            if (!IsLocalAddress(value.String))
                continue;
            constants.Add((entry, value.String!));
            packing.NameIn(value.String!, where);
        }

        foreach (var uri in locals)
            await packing.FindAsync(uri).ConfigureAwait(false);
        foreach (var (_, uri) in constants)
            await packing.FindAsync(uri).ConfigureAwait(false);
        return (locals, constants);
    }

    // ask answers which of the hashes it is handed the store lacks, null when it could not say; restore puts
    // one the store lacks back from this machine. Without them nothing is asked and nothing put back, and the
    // parts the tree names by cloud address are only listed.
    private static async Task<List<CloudAssetEntry>> PackAsync(DataTreeDictionary root, Packing packing, List<string>? problems,
        List<string>? dropped, List<string>? lost, Func<IReadOnlyList<string>, Task<IReadOnlyCollection<string>?>>? ask,
        Func<string, Task<Restore>>? restore, int width, CancellationToken ct)
    {
        var earlier = new Dictionary<string, CloudAssetEntry>(StringComparer.Ordinal);
        foreach (var entry in ReadManifest(root))
        {
            if (PartOf(entry.Hash) is { } part)
                earlier.TryAdd(part, entry);
        }
        var (locals, constants) = await FindAsync(root, packing).ConfigureAwait(false);
        var stored = await StoredPartsAsync(root, packing, earlier.Keys).ConfigureAwait(false);

        // One question for the lot. A record is filed under the hash of its bytes, so the question needs no
        // reading; the upload still hashes what it reads, and only an answer for that hash skips a file.
        HashSet<string>? absent = null;
        if (ask != null)
        {
            var asked = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var uri in packing.Found)
            {
                var source = packing.SourceOf(uri);
                var key = source.Substring(source.IndexOf('/', 8) + 1);
                if (IsContentHash(key) && seen.Add(key))
                    asked.Add(key);
            }
            foreach (var (hash, _) in stored)
            {
                if (seen.Add(hash))
                    asked.Add(hash);
            }
            if (asked.Count > 0 && await ask(asked).ConfigureAwait(false) is { } lacking)
                absent = new HashSet<string>(lacking, StringComparer.OrdinalIgnoreCase);
        }
        if (width > 1)
            await packing.StoreAheadAsync(width, ct).ConfigureAwait(false);
        foreach (var uri in locals)
            await packing.UploadAsync(uri).ConfigureAwait(false);
        foreach (var (_, uri) in constants)
            await packing.UploadAsync(uri).ConfigureAwait(false);

        // Parts the store lost go back one at a time: it only happens to a save whose parts were cleaned away,
        // and a few big ones read at once would sit in memory together.
        var restored = new Dictionary<string, Restore>(StringComparer.Ordinal);
        var stranded = new List<(string Hash, string Uri)>();
        if (absent != null && restore != null)
        {
            foreach (var (hash, uri) in stored)
            {
                if (!absent.Contains(hash))
                    continue;
                var back = await restore(hash).ConfigureAwait(false);
                if (!back.Here)
                    stranded.Add((hash, uri));
                else if (back.Error != null)
                    packing.Problems.Add((uri, $"is missing on the server and did not go up again: {back.Error}"));
                else
                    restored[hash] = back;
            }
        }

        if (packing.Problems.Count > 0 || packing.Missing.Count > 0 || stranded.Count > 0)
        {
            var uses = new Dictionary<string, string>(StringComparer.Ordinal);
            DescribeUses(root, null, null, uses);
            foreach (var (uri, why) in packing.Problems)
                problems?.Add($"{packing.NameOf(uri, uses)} {why}");
            if (packing.Problems.Count > 0)
                return new List<CloudAssetEntry>();
            foreach (var uri in packing.Missing)
                dropped?.Add(packing.NameOf(uri, uses));
            foreach (var (hash, uri) in stranded)
                lost?.Add(earlier.TryGetValue(hash, out var was) && was.Name.Length > 0 && !string.Equals(was.Name, hash, StringComparison.OrdinalIgnoreCase)
                    ? was.Name + was.Extension
                    : packing.NameOf(uri, uses));
        }

        var gone = new HashSet<string>(packing.Missing, StringComparer.Ordinal);
        var byUri = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (uri, hash) in packing.Hashes)
        {
            if (hash != null)
                byUri[uri] = hash;
        }
        foreach (var (entry, uri) in constants)
        {
            if (byUri.TryGetValue(uri, out var hash))
                entry.Children["Json"] = new DataTreeValue(GraphValue.Of($"{CloudScheme}:///{hash}").ToJson());
            else if (gone.Contains(uri))
                entry.Children["Json"] = new DataTreeValue(string.Empty);
        }
        if (gone.Count > 0)
            Clear(root, gone);

        // Built-in assets the build shipped go by their published hash too, so the save stands on
        // its own; a build that was never published leaves them as they are.
        var builtins = new List<string>();
        CollectUrls(root, "res", builtins);
        CollectUrls(root, "lumres", builtins);
        foreach (var uri in builtins)
        {
            if (byUri.ContainsKey(uri))
                continue;
            var hash = Assets.BuiltinAssetRegistry.HashFor(uri);
            if (hash != null)
                byUri[uri] = hash;
        }

        if (byUri.Count > 0)
            Rewrite(root, uri => byUri.TryGetValue(uri, out var hash) ? new Uri($"{CloudScheme}:///{hash}") : null);

        // What went up now, then every part from before, each hash once.
        var manifest = new List<CloudAssetEntry>(packing.Manifest);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest)
            listed.Add(entry.Hash);
        Dictionary<string, LocalAssetRecord>? records = null;
        foreach (var (hash, _) in stored)
        {
            if (!listed.Add(hash))
                continue;
            records ??= packing.Records();
            var entry = Describe(hash, earlier, records, packing.Cache);
            if (restored.TryGetValue(hash, out var back))
            {
                entry.SizeBytes = back.Size;
                if (entry.Extension.Length == 0 && back.Extension.Length > 0)
                {
                    entry.Extension = back.Extension;
                    entry.Type = TypeOf(back.Extension);
                }
                entry.LocalUri = back.LocalUri ?? string.Empty;
            }
            manifest.Add(entry);
        }

        var list = new DataTreeList();
        foreach (var entry in manifest)
        {
            var node = new DataTreeDictionary();
            node.Add("hash", entry.Hash);
            node.Add("name", entry.Name);
            node.Add("ext", entry.Extension);
            node.Add("type", entry.Type);
            node.Add("size", entry.SizeBytes);
            list.Add(node);
        }
        root.AddOrUpdate(ManifestKey, list);
        return manifest;
    }

    public static List<CloudAssetEntry> ReadManifest(DataTreeDictionary root)
    {
        var manifest = new List<CloudAssetEntry>();
        var list = root.TryGetList(ManifestKey);
        if (list == null)
            return manifest;
        foreach (var node in list)
        {
            if (node is not DataTreeDictionary d)
                continue;
            manifest.Add(new CloudAssetEntry
            {
                Hash = d.ExtractOrDefault("hash", string.Empty),
                Name = d.ExtractOrDefault("name", string.Empty),
                Extension = d.ExtractOrDefault("ext", string.Empty),
                Type = d.ExtractOrDefault("type", "other"),
                SizeBytes = d.ExtractOrDefault("size", 0L),
            });
        }
        return manifest;
    }

    // Every part the tree names by cloud address, there before this save: its URLs, the script constants
    // FindAsync met, what the stored scripts among them name in turn when this machine has them to read, and
    // what the tree's own earlier list carried. Built-in files are the build's own, never missing and never
    // counted. Each hash once, lowercase, with the address it was first met under.
    private static async Task<List<(string Hash, string Uri)>> StoredPartsAsync(DataTreeDictionary root, Packing packing,
        IEnumerable<string> listedBefore)
    {
        var addresses = new List<string>();
        CollectUrls(root, CloudScheme, addresses);
        addresses.AddRange(packing.Cloud);
        foreach (var hash in listedBefore)
            addresses.Add($"{CloudScheme}:///{hash}");
        var parts = new List<(string Hash, string Uri)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Grows while it is walked: a stored script read here adds what it names.
        for (int i = 0; i < addresses.Count; i++)
        {
            var hash = PartOf(HashOf(addresses[i]));
            if (hash == null || !seen.Add(hash) || Assets.BuiltinAssetRegistry.ResUriFor(hash) != null)
                continue;
            parts.Add((hash, addresses[i]));
            await packing.ReadStoredScriptAsync(hash, addresses[i], addresses).ConfigureAwait(false);
        }
        return parts;
    }

    // A part from before as its earlier list described it, else as this machine's copy does: the extension it
    // was filed under, so a machine that fetches it files it the same way, and its plain size. With no copy
    // here the size stays 0; the store measures what it keeps on its own and never reads this one.
    private static CloudAssetEntry Describe(string hash, Dictionary<string, CloudAssetEntry> earlier,
        Dictionary<string, LocalAssetRecord> records, ContentCache? cache)
    {
        if (earlier.TryGetValue(hash, out var was))
            return new CloudAssetEntry { Hash = hash, Name = was.Name, Extension = was.Extension, Type = was.Type, SizeBytes = was.SizeBytes };
        var entry = new CloudAssetEntry { Hash = hash, Name = hash };
        if (records.TryGetValue(hash, out var record) && File.Exists(record.FilePath))
        {
            entry.Extension = KnownExtension(LocalDB.ExtensionOf(record));
            entry.Type = TypeOf(entry.Extension);
            entry.SizeBytes = record.FileSize > 0 ? record.FileSize : 0;
            var original = record.OriginalFileName;
            if (!string.IsNullOrEmpty(original) && !original.StartsWith(hash, StringComparison.OrdinalIgnoreCase))
                entry.Name = Path.GetFileNameWithoutExtension(original);
        }
        else if (cache != null && cache.LengthOf(hash) is var length && length > 0)
        {
            entry.SizeBytes = length;
        }
        return entry;
    }

    // A cloud part's hash the way the store names it, or null for anything that is not a content hash.
    private static string? PartOf(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
            return null;
        var key = hash.ToLowerInvariant();
        return IsContentHash(key) ? key : null;
    }

    // A whole value that names a stored part by its cloud address.
    private static bool IsCloudAddress(string? text)
        => !string.IsNullOrEmpty(text) && text.StartsWith(CloudScheme + ":", StringComparison.Ordinal) && PartOf(HashOf(text)) != null;

    // Brings every asset the tree names into the local database, skipping the ones already there. A
    // texture whose uploader registered its variants comes down as its sidecar and the smallest rung a
    // load starts on instead of the original: the load puts that up at once and fetches a bigger rung,
    // or the original, only when it is asked for one. Returns how many came down; problems collect the
    // ones that did not. -xlinka
    public static Task<int> PrefetchAsync(DataTreeDictionary root, LumoraClient client, LocalDB db, List<string>? problems = null)
        => PrefetchAsync(root, CloudVariants.SourceFor(client), db, problems);

    // The same with the service handed in.
    public static async Task<int> PrefetchAsync(DataTreeDictionary root, ICloudVariantSource source, LocalDB db, List<string>? problems = null)
    {
        var manifest = ReadManifest(root);
        var extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest)
        {
            extensions[entry.Hash] = entry.Extension;
            if (entry.Type == "texture")
                textures.Add(entry.Hash);
        }

        var cloud = new List<string>();
        CollectUrls(root, CloudScheme, cloud);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in cloud)
        {
            var hash = HashOf(uri);
            if (!string.IsNullOrEmpty(hash))
                wanted.Add(hash);
        }

        int fetched = 0;
        // Independent assets fill the connection together. One refusal must not prevent the other parts
        // from arriving; diagnostics are collected under the same lock for parallel failures. -xlinka
        var failures = new List<string>();
        await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = Engine.UseConstrainedResources ? 2 : UploadWidth }, async (hash, _) =>
        {
            try
            {
                if (db.Exists($"local://cloud/{hash}"))
                    return;
                if (textures.Contains(hash) && await CloudVariants.PrefetchAsync(db, hash, source).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref fetched);
                    return;
                }
                var (data, error) = await source.FetchAsync(hash).ConfigureAwait(false);
                if (data == null)
                {
                    lock (failures) failures.Add($"could not fetch {hash}: {error ?? "no answer"}");
                    return;
                }
                var ext = extensions.TryGetValue(hash, out var known) && !string.IsNullOrEmpty(known) ? known : UnknownExtension;
                if (await db.SaveAssetAsync(data, ext, received: true).ConfigureAwait(false) != null)
                    Interlocked.Increment(ref fetched);
                else
                    lock (failures) failures.Add($"could not store {hash}");
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add($"could not fetch {hash}: {ex.Message}");
            }
        }).ConfigureAwait(false);
        problems?.AddRange(failures);
        return fetched;
    }

    // The hash a lumora:///<hash> URL names, or null for anything else.
    public static string? HashOf(string uri)
    {
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith(CloudScheme + ":", StringComparison.Ordinal))
            return null;
        var trimmed = uri.Substring(CloudScheme.Length + 1).Trim('/');
        int slash = trimmed.IndexOf('/');
        if (slash >= 0)
            trimmed = trimmed.Substring(slash + 1);
        int query = trimmed.IndexOf('?');
        if (query >= 0)
            trimmed = trimmed.Substring(0, query);
        // An extension after the hash names the same blob.
        int dot = trimmed.IndexOf('.');
        if (dot >= 0)
            trimmed = trimmed.Substring(0, dot);
        return trimmed.Length == 0 ? null : trimmed;
    }

    // A record key that is the sha256 of its bytes, the way the asset database files them.
    private static bool IsContentHash(string key)
    {
        if (key.Length != 64)
            return false;
        foreach (var c in key)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }

    public static string TypeOf(string extension)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".png": case ".jpg": case ".jpeg": case ".webp": case ".tga": case ".bmp": case ".ktx": case ".ktx2": case ".dds": case ".exr": case ".hdr":
            case ".tif": case ".tiff":
                return "texture";
            case ".lmesh": case ".obj": case ".fbx": case ".glb": case ".gltf":
                return "mesh";
            case ".wav": case ".mp3": case ".ogg": case ".flac":
                return "audio";
            case ".lanm":
                return "animation";
            case ".lumshader": case ".gdshader":
                return "shader";
            default:
                return "other";
        }
    }

    // Every local file a tree names stays on this machine from now on, whatever brought it here: a world
    // saved here must still open when nobody who sent its files is around.
    public static void KeepLocalFiles(DataTreeNode root, LocalDB? db)
    {
        if (db == null)
            return;
        var locals = new List<string>();
        CollectUrls(root, "local", locals);
        foreach (var uri in locals)
            db.Keep(uri);
    }

    // TREE WALKS

    private static void CollectUrls(DataTreeNode node, string scheme, List<string> into)
    {
        switch (node)
        {
            case DataTreeDictionary d:
                foreach (var child in d.Children.Values)
                    CollectUrls(child, scheme, into);
                break;
            case DataTreeList l:
                foreach (var child in l.Children)
                    CollectUrls(child, scheme, into);
                break;
            case DataTreeValue v when v.IsUrl:
            {
                var text = ((string)v.Value!).Substring(1);
                if (text.StartsWith(scheme + ":", StringComparison.Ordinal))
                    into.Add(text);
                break;
            }
        }
    }

    // A value node cannot change what it holds, so a rewritten URL is a new node in the old node's place.
    // Every URL value naming one of the given cleared, the way an empty reference is saved.
    private static void Clear(DataTreeNode node, HashSet<string> gone)
    {
        switch (node)
        {
            case DataTreeDictionary d:
                foreach (var key in new List<string>(d.Children.Keys))
                {
                    if (d.Children[key] is DataTreeValue { IsUrl: true } v && gone.Contains(((string)v.Value!).Substring(1)))
                        d.Children[key] = new DataTreeValue((Uri?)null);
                    else
                        Clear(d.Children[key], gone);
                }
                break;
            case DataTreeList l:
                for (int i = 0; i < l.Children.Count; i++)
                {
                    if (l.Children[i] is DataTreeValue { IsUrl: true } v && gone.Contains(((string)v.Value!).Substring(1)))
                        l.Children[i] = new DataTreeValue((Uri?)null);
                    else
                        Clear(l.Children[i], gone);
                }
                break;
        }
    }

    private static void Rewrite(DataTreeNode node, Func<string, Uri?> map)
    {
        switch (node)
        {
            case DataTreeDictionary d:
            {
                var keys = new List<string>(d.Children.Keys);
                foreach (var key in keys)
                {
                    var child = d.Children[key];
                    if (child is DataTreeValue v && v.IsUrl)
                    {
                        var replacement = map(((string)v.Value!).Substring(1));
                        if (replacement != null)
                            d.Children[key] = new DataTreeValue(replacement);
                    }
                    else
                    {
                        Rewrite(child, map);
                    }
                }
                break;
            }
            case DataTreeList l:
            {
                for (int i = 0; i < l.Children.Count; i++)
                {
                    var child = l.Children[i];
                    if (child is DataTreeValue v && v.IsUrl)
                    {
                        var replacement = map(((string)v.Value!).Substring(1));
                        if (replacement != null)
                            l.Children[i] = new DataTreeValue(replacement);
                    }
                    else
                    {
                        Rewrite(child, map);
                    }
                }
                break;
            }
        }
    }

    private static readonly string NodeTypeName = typeof(Components.Scripting.ScriptGraphNode).FullName!;

    // The constants of every open script graph node in the tree, with the name of the node's slot.
    private static void CollectNodeValues(DataTreeNode node, string? slot, List<(DataTreeDictionary Entry, string? Node)> into)
    {
        switch (node)
        {
            case DataTreeDictionary d:
                if (d.ContainsKey("Components") && d.TryGetNode("Name") is DataTreeValue { IsUrl: false, Value: string } name)
                    slot = name.Extract<string>();
                if (d.TryGetNode("Type") is DataTreeValue { Value: string type } && type == NodeTypeName
                    && d.TryGetDictionary("Data")?.TryGetList("Values") is { } values)
                {
                    foreach (var value in values.Children)
                    {
                        if (value is DataTreeDictionary entry)
                            into.Add((entry, slot));
                    }
                    break;
                }
                foreach (var child in d.Children.Values)
                    CollectNodeValues(child, slot, into);
                break;
            case DataTreeList l:
                foreach (var child in l.Children)
                    CollectNodeValues(child, slot, into);
                break;
        }
    }

    // Where each URL in the tree is used, as a person would name the place: the slot and the component.
    private static void DescribeUses(DataTreeNode node, string? slot, string? component, Dictionary<string, string> uses)
    {
        switch (node)
        {
            case DataTreeDictionary d:
                if (d.ContainsKey("Components") && d.TryGetNode("Name") is DataTreeValue { IsUrl: false, Value: string } name)
                    slot = name.Extract<string>();
                if (d.ContainsKey("Data") && d.TryGetNode("Type") is DataTreeValue { Value: string type })
                    component = ShortTypeName(type);
                foreach (var child in d.Children.Values)
                    DescribeUses(child, slot, component, uses);
                break;
            case DataTreeList l:
                foreach (var child in l.Children)
                    DescribeUses(child, slot, component, uses);
                break;
            case DataTreeValue v when v.IsUrl:
                uses.TryAdd(((string)v.Value!).Substring(1), slot != null ? $"\"{slot}\" ({component})" : $"its {component ?? "asset"}");
                break;
        }
    }

    private static string ShortTypeName(string type)
    {
        int generic = type.IndexOfAny(new[] { '`', '[' });
        if (generic > 0)
            type = type.Substring(0, generic);
        return type.Substring(type.LastIndexOf('.') + 1);
    }

    // A whole value that is a local asset address and nothing else, in the form the asset store hands out:
    // local://<machine>/<record>. Text that only mentions one is not a URL value and stays as it is.
    private static bool IsLocalAddress(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith("local://", StringComparison.Ordinal))
            return false;
        var rest = text.AsSpan(8);
        int slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1 || rest.Slice(slash + 1).IndexOf('/') >= 0)
            return false;
        foreach (char c in rest)
        {
            if (c != '/' && c != '-' && c != '_' && c != '.' && c != '~' && !char.IsAsciiLetterOrDigit(c))
                return false;
        }
        return true;
    }

    // One save's files: every local file it names, found once and uploaded once, by the hash it went up
    // under.
    private sealed class Packing
    {
        // Stores one file: the local address its bytes are read from and its path on disk, to the hash it
        // went up under, the size that went up, or no hash and why not.
        private readonly Func<string, string, Task<(string? Hash, long Size, string? Error)>> _store;
        private readonly LocalDB? _db;
        private readonly Dictionary<string, string?> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScriptGraph> _graphs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _namedIn = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string? Hash, long Size, string? Error)> _ahead = new(StringComparer.Ordinal);

        public readonly Dictionary<string, string?> Hashes = new(StringComparer.Ordinal);
        public readonly List<CloudAssetEntry> Manifest = new();
        public readonly List<(string Uri, string Why)> Problems = new();

        // Files this machine no longer has, which the save goes up without, and the ones it has.
        public readonly List<string> Missing = new();
        public readonly List<string> Found = new();

        // Cloud addresses typed into script nodes, open or stored here: parts of the save from before.
        public readonly List<string> Cloud = new();

        // The download cache, where a part fetched for a load and never filed may still be.
        public ContentCache? Cache { get; init; }
        public bool CompactTextures { get; init; }
        public CancellationToken Token { get; init; }

        public string SourceOf(string uri) => _sources.TryGetValue(uri, out var source) ? source : uri;

        public Packing(Func<string, string, Task<(string? Hash, long Size, string? Error)>> store, LocalDB? db)
        {
            _store = store;
            _db = db;
        }

        public void NameIn(string uri, string where) => _namedIn.TryAdd(uri, where);

        // This machine's asset records by hash, read once.
        public Dictionary<string, LocalAssetRecord> Records()
        {
            var records = new Dictionary<string, LocalAssetRecord>(StringComparer.OrdinalIgnoreCase);
            if (_db == null)
                return records;
            foreach (var record in _db.GetAllRecords())
            {
                if (!string.IsNullOrEmpty(record.Hash))
                    records.TryAdd(record.Hash, record);
            }
            return records;
        }

        // A stored script met by its cloud address, when this machine has it to read: the cloud addresses on its
        // nodes are parts of the save as well.
        public async Task ReadStoredScriptAsync(string hash, string address, List<string> into)
        {
            if (_db?.UriForHash(hash) is not { } uri
                || !string.Equals(_db.GetExtension(uri), ScriptGraphAsset.Extension, StringComparison.OrdinalIgnoreCase))
                return;
            var bytes = await _db.ReadAssetBytesAsync(uri).ConfigureAwait(false);
            if (bytes == null)
                return;
            ScriptGraph graph;
            try
            {
                graph = ScriptGraph.Parse(bytes);
            }
            catch (GraphFormatException)
            {
                return;
            }
            foreach (var node in graph.Nodes)
            {
                foreach (var value in node.Values.Values)
                {
                    if (value.Kind == GraphValueKind.String && IsCloudAddress(value.String))
                    {
                        into.Add(value.String!);
                        NameIn(value.String!, address);
                    }
                }
            }
        }

        // What the files this machine has weigh on disk, each file once.
        public long FoundBytes()
        {
            long total = 0;
            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _files.Values)
            {
                if (path == null || !counted.Add(path))
                    continue;
                try
                {
                    total += new FileInfo(path).Length;
                }
                catch (IOException)
                {
                }
            }
            return total;
        }

        // The file behind a URL, and behind every URL constant of a stored script graph it names.
        public async Task FindAsync(string uri)
        {
            if (_files.ContainsKey(uri))
                return;
            var path = _db?.GetFilePath(uri);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _files[uri] = null;
                Missing.Add(uri);
                return;
            }
            if (CompactTextures && _db != null)
            {
                var source = await TextureStorage.ForTransferAsync(_db, uri, Token).ConfigureAwait(false);
                if (source != uri)
                {
                    _sources[uri] = source;
                    path = _db.GetFilePath(source);
                    _db.Keep(source);
                }
            }
            _files[uri] = path;
            Found.Add(uri);
            // A save names it, so it stays on this machine whatever brought it here.
            _db?.Keep(uri);
            if (!string.Equals(_db!.GetExtension(SourceOf(uri)), ScriptGraphAsset.Extension, StringComparison.OrdinalIgnoreCase))
                return;

            var bytes = await _db!.ReadAssetBytesAsync(uri).ConfigureAwait(false);
            if (bytes == null)
                return;
            ScriptGraph graph;
            try
            {
                graph = ScriptGraph.Parse(bytes);
            }
            catch (GraphFormatException)
            {
                return;
            }
            var named = new List<string>();
            foreach (var node in graph.Nodes)
            {
                foreach (var value in node.Values.Values)
                {
                    if (value.Kind != GraphValueKind.String)
                        continue;
                    if (IsLocalAddress(value.String))
                        named.Add(value.String!);
                    else if (IsCloudAddress(value.String))
                    {
                        Cloud.Add(value.String!);
                        NameIn(value.String!, uri);
                    }
                }
            }
            if (named.Count == 0)
                return;
            _graphs[uri] = graph;
            foreach (var inner in named)
            {
                NameIn(inner, uri);
                await FindAsync(inner).ConfigureAwait(false);
            }
        }

        // The plain files of the save stored ahead, several at once. A stored script that names files of its
        // own still goes in turn, once what it names is up, and so does anything over the size limit.
        public async Task StoreAheadAsync(int width, CancellationToken ct)
        {
            var plain = new List<(string Uri, string Path)>();
            foreach (var uri in Found)
            {
                if (_graphs.ContainsKey(uri) || !_files.TryGetValue(uri, out var path) || path == null)
                    continue;
                try
                {
                    if (new FileInfo(path).Length <= AheadLimit)
                        plain.Add((uri, path));
                }
                catch (IOException)
                {
                }
            }
            if (plain.Count < 2)
                return;
            var stored = new (string? Hash, long Size, string? Error)[plain.Count];
            var options = new ParallelOptions { MaxDegreeOfParallelism = width, CancellationToken = ct };
            await Parallel.ForEachAsync(System.Linq.Enumerable.Range(0, plain.Count), options,
                async (i, _) => stored[i] = await _store(SourceOf(plain[i].Uri), plain[i].Path).ConfigureAwait(false)).ConfigureAwait(false);
            for (int i = 0; i < plain.Count; i++)
                _ahead[plain[i].Uri] = stored[i];
        }

        // Uploads what a URL names and answers its hash, or null with the problem noted. A stored script graph
        // that names files of its own, a texture typed into a node, has those go up first and is stored again
        // naming them by cloud address, so the copy that goes up names nothing left behind here.
        public async Task<string?> UploadAsync(string uri)
        {
            if (Hashes.TryGetValue(uri, out var known))
                return known;
            Hashes[uri] = null;
            if (!_files.TryGetValue(uri, out var path) || path == null)
                return null;

            var source = SourceOf(uri);
            if (_graphs.TryGetValue(uri, out var graph))
            {
                foreach (var node in graph.Nodes)
                {
                    foreach (var port in new List<string>(node.Values.Keys))
                    {
                        var value = node.Values[port];
                        if (value.Kind != GraphValueKind.String || !IsLocalAddress(value.String))
                            continue;
                        // A file the script names that this machine no longer has goes out of it, as from the tree.
                        if (Missing.Contains(value.String!))
                        {
                            node.Values.Remove(port);
                            continue;
                        }
                        if (value.String == uri || await UploadAsync(value.String!).ConfigureAwait(false) is not { } inner)
                        {
                            Problems.Add((uri, "names a file that did not go up"));
                            return null;
                        }
                        node.Values[port] = GraphValue.Of($"{CloudScheme}:///{inner}");
                    }
                }
                var restored = await _db!.SaveAssetAsync(graph.ToUtf8Json(), ScriptGraphAsset.Extension).ConfigureAwait(false);
                path = restored == null ? null : _db.GetFilePath(restored);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Problems.Add((uri, "could not be stored again"));
                    return null;
                }
                source = restored!;
            }

            var (stored, size, error) = !_graphs.ContainsKey(uri) && _ahead.TryGetValue(uri, out var ahead)
                ? ahead
                : await _store(source, path).ConfigureAwait(false);
            if (stored == null)
            {
                Problems.Add((uri, $"did not upload: {error ?? "no answer"}"));
                return null;
            }
            Hashes[uri] = stored;
            // From the record, not the file: a sealed file's name is a random id with no extension.
            var extension = _db?.GetExtension(source) ?? Path.GetExtension(path) ?? string.Empty;
            Manifest.Add(new CloudAssetEntry
            {
                Hash = stored,
                Name = _db?.GetStoredName(source) ?? Path.GetFileNameWithoutExtension(path) ?? stored,
                Extension = extension,
                Type = TypeOf(extension),
                SizeBytes = size,
                LocalUri = source,
            });
            return stored;
        }

        // How the person saving would know a file: the name it was imported under, where the save uses it,
        // or the node or stored script that names it.
        public string NameOf(string uri, Dictionary<string, string> uses, int depth = 0)
        {
            if (FileName(uri) is { } file)
                return file;
            if (uses.TryGetValue(uri, out var place))
                return $"the file on {place}";
            if (_namedIn.TryGetValue(uri, out var namer) && depth < 8)
            {
                if (!IsLocalAddress(namer) && !IsCloudAddress(namer))
                    return namer;
                return $"a file named in the script on {(uses.TryGetValue(namer, out var script) ? script : NameOf(namer, uses, depth + 1))}";
            }
            return uri;
        }

        private string? FileName(string uri)
        {
            if (_db == null || !uri.StartsWith("local://", StringComparison.Ordinal))
                return null;
            var key = uri.Substring(uri.IndexOf('/', 8) + 1);
            foreach (var record in _db.GetAllRecords())
            {
                if (record.Hash == key && !string.IsNullOrEmpty(record.OriginalFileName)
                    && !record.OriginalFileName.StartsWith(record.Hash, StringComparison.OrdinalIgnoreCase))
                    return record.OriginalFileName;
            }
            return null;
        }
    }
}
