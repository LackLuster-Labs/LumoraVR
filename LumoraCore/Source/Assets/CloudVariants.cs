// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Lumora.Core.Logging;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core.Assets;

// Where a cloud texture and its variants come from: the content service in a real run, something
// standing in for it in a test.
public interface ICloudVariantSource
{
    // Every variant registered for a blob: empty when there are none, null when the service could not be asked.
    Task<IReadOnlyList<VariantInfo>?> ListAsync(string hash);

    // A stored blob by its hash, or no bytes and why not.
    Task<(byte[]? Bytes, string? Error)> FetchAsync(string hash);
}

// Variants of cloud textures. The machine that uploads a texture makes its sidecar and rungs and
// registers them; the service only stores and hands them out. A cloud texture is asked about once per
// run, and whatever it has is fetched by name, checked against the hash it was registered under and
// filed under the derived addresses the local variant path already reads, so past this point nothing
// knows a rung came from the cloud. A texture with nothing registered loads its original. -xlinka
public static class CloudVariants
{
    private static readonly Dictionary<string, Task<IReadOnlyDictionary<string, VariantInfo>?>> _listed = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);

    // What a source answers for a blob it does not have, as against one it could not be asked about.
    public const string NotThere = "not there";
    private static readonly IReadOnlyDictionary<string, VariantInfo> Nothing = new Dictionary<string, VariantInfo>();
    private static readonly ICloudVariantSource EngineService = new ServiceSource(null);
    private static ICloudVariantSource? _source;

    // Null asks the content service through the engine's client. Setting another forgets every answer.
    public static ICloudVariantSource? Source
    {
        get => _source;
        set
        {
            lock (_listed)
            {
                _source = value;
                _listed.Clear();
            }
        }
    }

    private static ICloudVariantSource Current => _source ?? EngineService;

    // The content service through a client the caller already holds.
    public static ICloudVariantSource SourceFor(LumoraClient? client) => new ServiceSource(client);

    // The content hash a lumora address names, lowercased and without an extension, or null for anything else.
    public static string? HashOf(Uri? url)
    {
        var named = url == null ? null : Persistence.CloudAssetPacker.HashOf(url.OriginalString);
        if (named == null)
            return null;
        int dot = named.IndexOf('.');
        if (dot >= 0)
            named = named.Substring(0, dot);
        if (named.Length != 64)
            return null;
        foreach (char c in named)
        {
            if (!char.IsAsciiHexDigit(c))
                return null;
        }
        return named.ToLowerInvariant();
    }

    // Where a cloud texture is filed on this machine: its hash under this machine's id, the record its
    // original lands in when it comes down and the base its sidecar and rungs derive from.
    public static string? BaseUri(LocalDB? db, string hash) => db == null ? null : $"local://{db.MachineId}/{hash}";

    // The variants registered for a blob by id. One ask per hash per run, shared by every load that
    // comes in meanwhile; an ask the service could not answer is not kept, so a later load tries again.
    public static async Task<IReadOnlyDictionary<string, VariantInfo>> ListAsync(string hash, ICloudVariantSource? source = null)
    {
        Task<IReadOnlyDictionary<string, VariantInfo>?>? asking;
        lock (_listed)
        {
            if (!_listed.TryGetValue(hash, out asking))
            {
                var from = source ?? Current;
                asking = Task.Run(() => AskAsync(from, hash));
                _listed[hash] = asking;
            }
        }
        var listed = await asking.ConfigureAwait(false);
        if (listed == null)
        {
            lock (_listed)
            {
                if (_listed.TryGetValue(hash, out var kept) && kept == asking)
                    _listed.Remove(hash);
            }
        }
        return listed ?? Nothing;
    }

    // Answered from what an earlier ask already brought back; never asks by itself.
    public static bool IsRegistered(string hash, TextureVariantId id)
    {
        lock (_listed)
        {
            return _listed.TryGetValue(hash, out var asking) && asking.IsCompletedSuccessfully
                && asking.Result is { } listed && listed.ContainsKey(id.Storage.Identifier);
        }
    }

    // An older upload can still serve its existing mip chain without transferring the full source.
    // Keep its actual identifier so a later save can generate the smaller current encoding. -xlinka
    public static TextureVariantId ReadableVariant(string hash, TextureVariantId id)
    {
        if (id.IsOriginal || id.Version != TextureVariantId.CurrentVersion || IsRegistered(hash, id))
            return id;
        var legacy = new TextureVariantId(id.MaxSize, id.Mipmaps, id.Compression, version: 1);
        return IsRegistered(hash, legacy) ? legacy : id;
    }

    // The sidecar of a cloud texture, fetched and filed under its derived address when this machine has
    // no metadata for it and the uploader registered one. True when the metadata is here after.
    public static async Task<bool> EnsureMetadataAsync(LocalDB db, string baseUri, string hash, ICloudVariantSource? source = null)
    {
        var sidecar = TextureVariantStore.GetMetadataUri(baseUri);
        if (sidecar == null)
            return false;
        if (db.Exists(sidecar) || TextureMetadata.ReadFrom(db.GetAssetMetadata(baseUri)) != null)
            return true;
        var listed = await ListAsync(hash, source).ConfigureAwait(false);
        return listed.TryGetValue(TextureVariantStore.MetadataSuffix, out var info)
            && await KeepAsync(db, baseUri, hash, TextureVariantStore.MetadataSuffix, info, source ?? Current).ConfigureAwait(false);
    }

    // One registered rung's bytes, or null when it is not registered or did not come down whole. Not
    // filed here: the load files it once it has decoded, as it does a rung a peer sent.
    public static async Task<byte[]?> FetchVariantAsync(string hash, TextureVariantId id, ICloudVariantSource? source = null)
    {
        var listed = await ListAsync(hash, source).ConfigureAwait(false);
        if (!listed.TryGetValue(id.Storage.Identifier, out var info))
            return null;
        var from = source ?? Current;
        return await OnceAsync("blob:" + info.ResultHash,
            () => FetchCheckedAsync(from, info.ResultHash, $"{id.Storage.Identifier} of {Short(hash)}")).ConfigureAwait(false);
    }

    // Whether the service said it does not have this blob and it is not yet time to ask again. The download
    // cache keeps the one schedule per blob (ContentCache), whichever way the blob was asked for.
    public static bool IsMissing(string hash) => Engine.Current?.ContentCache?.IsMissing(hash) ?? false;

    // Lets the next load of a cloud address ask the service again, for its variants and its original.
    public static void Forget(Uri? url)
    {
        var hash = HashOf(url) ?? (url != null && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? HashOf(new Uri(Persistence.CloudAssetPacker.CloudScheme + ":///" + System.IO.Path.GetFileName(url.AbsolutePath))) : null);
        if (hash == null)
            return;
        lock (_listed)
            _listed.Remove(hash);
        Engine.Current?.ContentCache?.ForgetMissing(hash);
    }

    // The original, kept in the database once it is here, so the next load reads it from disk the way a
    // prefetched save's would. A blob the download cache already holds (a part that arrived late was fetched
    // there by LateCloudParts) is not downloaded a second time.
    public static Task<byte[]?> FetchOriginalAsync(LocalDB? db, string hash, ICloudVariantSource? source = null)
    {
        if (IsMissing(hash))
            return Task.FromResult<byte[]?>(null);
        var from = source ?? Current;
        return OnceAsync("original:" + hash, async () =>
        {
            var bytes = source == null && Engine.Current?.ContentCache is { } cache
                ? await cache.GetCached(hash).ConfigureAwait(false) : null;
            bytes ??= await FetchCheckedAsync(from, hash, Short(hash)).ConfigureAwait(false);
            if (bytes != null && db != null)
                await db.SaveAssetAsync(bytes, ExtensionOf(bytes), received: true).ConfigureAwait(false);
            return bytes;
        });
    }

    // For a save about to load: the sidecar and the smallest rung a load starts on, instead of the
    // original, when the uploader registered both. False when it did not or either did not come down, and
    // the original is wanted after all.
    public static async Task<bool> PrefetchAsync(LocalDB db, string hash, ICloudVariantSource source)
    {
        var baseUri = BaseUri(db, hash);
        if (baseUri == null)
            return false;
        var listed = await ListAsync(hash, source).ConfigureAwait(false);
        if (!listed.TryGetValue(TextureVariantStore.MetadataSuffix, out var sidecar))
            return false;
        foreach (int size in TextureLoadChain.LadderSizes)
        {
            var id = ReadableVariant(hash, new TextureVariantId(size, true, TextureCompressionKind.Block));
            if (!listed.TryGetValue(id.Identifier, out var rung))
                continue;
            return await KeepAsync(db, baseUri, hash, TextureVariantStore.MetadataSuffix, sidecar, source).ConfigureAwait(false)
                && await KeepAsync(db, baseUri, hash, id.Identifier, rung, source).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<IReadOnlyDictionary<string, VariantInfo>?> AskAsync(ICloudVariantSource source, string hash)
    {
        IReadOnlyList<VariantInfo>? variants;
        try
        {
            variants = await source.ListAsync(hash).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"CloudVariants: could not ask for the variants of {Short(hash)}: {ex.Message}");
            return null;
        }
        if (variants == null)
            return null;
        var byId = new Dictionary<string, VariantInfo>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (variant != null && !string.IsNullOrEmpty(variant.VariantId) && !string.IsNullOrEmpty(variant.ResultHash))
                byId.TryAdd(variant.VariantId, variant);
        }
        return byId;
    }

    // Fetched, checked and filed under the texture's derived address, unless it is there already. A sidecar
    // that does not read as one, or a rung that does not decode, is not kept.
    private static Task<bool> KeepAsync(LocalDB db, string baseUri, string hash, string suffix, VariantInfo info, ICloudVariantSource source)
    {
        var uri = LocalDB.DeriveUri(baseUri, suffix);
        if (uri == null)
            return Task.FromResult(false);
        if (db.Exists(uri))
            return Task.FromResult(true);
        return OnceAsync("keep:" + uri, async () =>
        {
            if (db.Exists(uri))
                return true;
            bool sidecar = suffix == TextureVariantStore.MetadataSuffix;
            var bytes = await FetchCheckedAsync(source, info.ResultHash, $"{suffix} of {Short(hash)}").ConfigureAwait(false);
            if (bytes == null)
                return false;
            if (sidecar ? TextureMetadata.FromSidecarBytes(bytes) == null : TextureVariantStore.Decode(bytes) == null)
            {
                Logger.Warn($"CloudVariants: the {suffix} registered for {Short(hash)} does not read as one; not kept");
                return false;
            }
            var kept = await db.SaveDerivedAssetAsync(baseUri, suffix, bytes,
                sidecar ? TextureVariantStore.MetadataExtension : TextureVariantStore.VariantExtension, received: true).ConfigureAwait(false);
            return kept != null;
        });
    }

    // One download of a blob, or one filing of a derived asset, however many loads want it at once: two
    // descriptors of one texture load side by side, and two writers on one file is a failure on Windows.
    private static Task<T> OnceAsync<T>(string key, Func<Task<T>> work)
    {
        lock (_running)
        {
            if (_running.TryGetValue(key, out var running))
                return (Task<T>)running;
            var task = Task.Run(work);
            _running[key] = task;
            _ = task.ContinueWith(_ =>
            {
                lock (_running)
                {
                    if (_running.TryGetValue(key, out var kept) && kept == task)
                        _running.Remove(key);
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    // Content addressed: bytes that do not hash to the name they were fetched by are never used or kept.
    private static async Task<byte[]?> FetchCheckedAsync(ICloudVariantSource source, string hash, string what)
    {
        (byte[]? Bytes, string? Error) fetched;
        try
        {
            fetched = await source.FetchAsync(hash).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fetched = (null, ex.Message);
        }
        if (fetched.Bytes == null || fetched.Bytes.Length == 0)
        {
            if (fetched.Error == NotThere)
            {
                int answers = Engine.Current?.ContentCache?.NoteMissing(hash) ?? 1;
                if (answers == 1)
                    Logger.Warn($"CloudVariants: {what} is not on the service; next ask in {ContentCache.WaitAfter(1).TotalSeconds:0} s, then less often");
                return null;
            }
            Logger.Warn($"CloudVariants: {what} did not come down: {fetched.Error ?? "no answer"}");
            return null;
        }
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(fetched.Bytes)), hash, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"CloudVariants: {what} came down as bytes its hash does not name; not used");
            return null;
        }
        return fetched.Bytes;
    }

    // The database knows a blob's kind only by the extension it is filed under.
    private static string ExtensionOf(byte[] bytes) => bytes switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0xFF, 0xD8, ..] => ".jpg",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ".webp",
        [0x47, 0x49, 0x46, 0x38, ..] => ".gif",
        [0x42, 0x4D, ..] => ".bmp",
        [0x23, 0x3F, ..] => ".hdr",
        [0x76, 0x2F, 0x31, 0x01, ..] => ".exr",
        [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..] => ".tif",
        _ => ".bin",
    };

    private static string Short(string hash) => hash.Length > 12 ? hash.Substring(0, 12) : hash;

    // The content service, through the client handed in or the engine's own at the time of asking.
    private sealed class ServiceSource : ICloudVariantSource
    {
        private readonly LumoraClient? _client;

        public ServiceSource(LumoraClient? client) => _client = client;

        private LumoraClient? Client => _client ?? Engine.Current?.CDNClient;

        public async Task<IReadOnlyList<VariantInfo>?> ListAsync(string hash)
        {
            var client = Client;
            if (client == null)
                return null;
            var listed = await client.ListVariants(hash).ConfigureAwait(false);
            // Nothing registered under a hash the service does not know is an answer, kept like any other.
            if (listed.Failed && listed.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                return new List<VariantInfo>();
            return listed.Failed ? null : listed.Data ?? new List<VariantInfo>();
        }

        public async Task<(byte[]? Bytes, string? Error)> FetchAsync(string hash)
        {
            var client = Client;
            if (client == null)
                return (null, "there is no content service to ask");
            var fetched = await client.FetchContent(hash).ConfigureAwait(false);
            if (fetched.Failed && fetched.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                return (null, NotThere);
            return fetched.Failed || fetched.Data == null ? (null, fetched.Message ?? "no answer") : (fetched.Data, null);
        }
    }
}
