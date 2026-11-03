// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Logging;
using Lumora.Core.Persistence;
using Lumora.Core.Phos;
using Lumora.Nexus.Storage;

namespace Lumora.Core.Assets;

public class LocalDB : IDisposable
{
    public enum ImportLocation
    {
        Original,
        Copy,
        Move
    }

    private readonly string _basePath;
    private readonly string _machineId;
    private readonly Dictionary<string, LocalAssetRecord> _assetRecords = new();
    // Rows by table and id, kept apart from the assets so a row can never resolve as a file.
    private readonly Dictionary<string, LocalAssetRecord> _rows = new();
    // Records by the file they point at, so a reader handed only a path still finds the key that opens it.
    private readonly Dictionary<string, LocalAssetRecord> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _recordsGate = new(1, 1);
    private long _recordsChanged;
    private long _recordsWritten;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _initialized;
    // False when an index on disk could not be read: nothing on disk may then be judged by the records alone.
    private bool _recordsSound = true;

    // A received file resolved or written since this moment is in use this run, and is never let go.
    private readonly DateTime _openedUtc = DateTime.UtcNow;

    // CACHE FILES AT REST
    //
    // Every file this database writes is sealed (LumoraNexus SealedFile: AES-256-GCM per 64 KiB chunk) under a
    // random key of its own, and that key lives only in the record, inside the encrypted index. The file is
    // named by a random id with no extension, so the folder says nothing about what is in it; the format lives
    // in the record too. Readers never get a path to decode from: they get bytes or a decrypting stream through
    // ReadAssetBytesAsync / OpenRead. A file from before sealing still reads (the header tells), and the
    // background pass seals it where it lies. -xlinka

    // PLAIN THEORA
    //
    // Godot's built-in Theora decoder opens a video only by its path, and where there is no libVLC (the
    // Android builds: the standalone headsets and phones) it is the only decoder there is. On those platforms
    // a Theora video stays a plain file in the store so it can still play; everything else is sealed there as
    // anywhere. The platform is decided here and nowhere else. -xlinka
    public static bool KeepsTheoraPlain => Engine.CurrentPlatform == Platform.Android;

    // An Ogg file whose streams include Theora video: the identification header "\x80theora" sits in a
    // beginning-of-stream page near the front (after an Ogg skeleton's page when there is one). Ogg Vorbis
    // audio is decoded from memory like any sound and does not count.
    public static bool IsTheoraVideo(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4 || head[0] != 'O' || head[1] != 'g' || head[2] != 'g' || head[3] != 'S')
            return false;
        return head.IndexOf(TheoraMarker) >= 0;
    }

    // 0x80 then "theora". Not a u8 literal: that would write 0x80 as two UTF-8 bytes.
    private static ReadOnlySpan<byte> TheoraMarker => [0x80, (byte)'t', (byte)'h', (byte)'e', (byte)'o', (byte)'r', (byte)'a'];

    private const int TheoraHeadBytes = 4096;

    private static bool StaysPlain(ReadOnlySpan<byte> head) => KeepsTheoraPlain && IsTheoraVideo(head);

    // Only these are worth opening to look: the format a video is filed under is its container's.
    private static bool MayBeTheora(string? extension)
        => string.IsNullOrEmpty(extension) || extension.Equals(".ogv", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);

    public string MachineId => _machineId;

    public string BasePath => _basePath;

    // Done once the index has been read, so nothing acts on rows or records before they are there. The
    // engine starts the read without waiting for it.
    public Task Ready => _ready.Task;

    public LocalDB(string? dbPath = null)
    {
        LocalEncryption.WireSealedStorage();
        _basePath = dbPath ?? GetDefaultBasePath();
        _machineId = GetOrCreateMachineId();
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(GetAssetCachePath());
        Directory.CreateDirectory(GetTempPath());

        await LoadAssetRecordsAsync().ConfigureAwait(false);

        _initialized = true;
        _ready.TrySetResult();
        Logger.Log($"LocalDB: Initialized at '{_basePath}' with machine ID '{_machineId}'");

        // The engine seldom gets as far as Dispose, so temp files earlier runs left are swept here. Only stale
        // ones: another instance on this folder may be writing its own right now.
        _ = Task.Run(() => CleanupTempFiles(TimeSpan.FromHours(1)));
    }

    public async Task<string> ImportLocalAssetAsync(string filePath, ImportLocation location = ImportLocation.Copy)
    {
        if (!File.Exists(filePath))
        {
            Logger.Error($"LocalDB: File not found: {filePath}");
            return null!;
        }

        var hash = await ComputeFileHashAsync(filePath).ConfigureAwait(false);
        var localUri = $"local://{_machineId}/{hash}";

        // A record whose file is gone names nothing, so the import goes ahead and writes it again. One that
        // came from elsewhere is this machine's own once it is imported here.
        if (await HeldAsync(hash, promote: true, writeIndex: true).ConfigureAwait(false))
        {
            if (location == ImportLocation.Move)
                TryDelete(filePath);
            Logger.Debug($"LocalDB: Asset already imported: {localUri}");
            return localUri;
        }

        var extension = Path.GetExtension(filePath);
        try
        {
            if (location == ImportLocation.Original)
            {
                // Original references the source in place. That file is the user's, not this cache's, so it
                // stays as it is.
                var original = new LocalAssetRecord
                {
                    Hash = hash,
                    LocalUri = localUri,
                    FilePath = filePath,
                    OriginalPath = filePath,
                    OriginalFileName = Path.GetFileName(filePath),
                    Extension = extension,
                    ImportedAt = DateTime.UtcNow,
                    FileSize = new FileInfo(filePath).Length,
                };
                lock (_lock)
                    PutLocked(original);
                await SaveAssetRecordsAsync().ConfigureAwait(false);
                Logger.Debug($"LocalDB: Imported '{Path.GetFileName(filePath)}' -> {localUri}");
                return localUri;
            }

            bool plain = KeepsTheoraPlain && MayBeTheora(extension)
                && IsTheoraVideo(await ReadHeadAsync(filePath, TheoraHeadBytes).ConfigureAwait(false));
            return await OneWrite(hash, async () =>
            {
                var (path, key, size) = plain
                    ? await CopyPlainAsync(filePath).ConfigureAwait(false)
                    : await SealFileAsync(filePath).ConfigureAwait(false);
                if (location == ImportLocation.Move)
                    TryDelete(filePath);
                var record = new LocalAssetRecord
                {
                    Hash = hash,
                    LocalUri = localUri,
                    FilePath = path,
                    OriginalPath = filePath,
                    OriginalFileName = Path.GetFileName(filePath),
                    Extension = extension,
                    ImportedAt = DateTime.UtcNow,
                    FileSize = size,
                    Encrypted = key != null,
                    EncryptionKey = key,
                };
                string? replaced;
                lock (_lock)
                    replaced = PutLocked(record);
                Retire(replaced);
                await SaveAssetRecordsAsync().ConfigureAwait(false);
                Logger.Debug($"LocalDB: Imported '{Path.GetFileName(filePath)}' -> {localUri}");
                return localUri;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"LocalDB: Failed to import '{filePath}': {ex.Message}");
            return null!;
        }
    }

    // Used by the mesh-as-asset path (SaveMeshAsync) but format-agnostic.
    // Received marks bytes that came from elsewhere (a cloud blob) rather than made here; those can be let go
    // to keep the cache in budget and fetched again.
    //
    // writeIndex false leaves the index write to the caller's own SaveRecordsAsync once its batch is in. An
    // import saving a thousand assets one after another waited for the whole index to be serialised and
    // written after every one of them, which was most of its first ten seconds. -xlinka
    public async Task<string> SaveAssetAsync(byte[] data, string extension = ".lmesh", bool received = false, bool writeIndex = true)
    {
        if (data == null)
        {
            Logger.Error("LocalDB: SaveAssetAsync called with null data");
            return null!;
        }

        if (!string.IsNullOrEmpty(extension) && !extension.StartsWith("."))
            extension = "." + extension;

        var hash = ComputeBytesHash(data);
        var localUri = $"local://{_machineId}/{hash}";

        if (await HeldAsync(hash, promote: !received, writeIndex).ConfigureAwait(false))
        {
            Logger.Debug($"LocalDB: Asset already saved: {localUri}");
            return localUri;
        }

        try
        {
            return await OneWrite(hash, async () =>
            {
                // The hash addresses the PLAINTEXT, so dedup, transfer and the cloud see the same address
                // whatever this machine keeps on disk. -xlinka
                var (path, key) = await StoreBytesAsync(data).ConfigureAwait(false);
                var record = new LocalAssetRecord
                {
                    Hash = hash,
                    LocalUri = localUri,
                    FilePath = path,
                    OriginalPath = localUri,
                    OriginalFileName = hash + extension,
                    Extension = extension,
                    ImportedAt = DateTime.UtcNow,
                    FileSize = data.LongLength,
                    Encrypted = key != null,
                    EncryptionKey = key,
                    Received = received
                };
                string? replaced;
                lock (_lock)
                    replaced = PutLocked(record);
                Retire(replaced);

                if (writeIndex)
                    await SaveAssetRecordsAsync().ConfigureAwait(false);
                else
                    Interlocked.Increment(ref _recordsChanged);
                Logger.Debug($"LocalDB: Saved {data.Length}-byte asset -> {localUri}");
                return localUri;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"LocalDB: Failed to save asset {localUri}: {ex.Message}");
            return null!;
        }
    }

    // True when a record for the key has its file here. Promotion (a received file becoming this machine's
    // own) writes the index when asked to.
    private async Task<bool> HeldAsync(string key, bool promote, bool writeIndex)
    {
        bool held = false, promoted = false;
        lock (_lock)
        {
            if (_assetRecords.TryGetValue(key, out var existing) && File.Exists(existing.FilePath))
            {
                held = true;
                promoted = promote && Promote(existing);
            }
        }
        if (promoted && writeIndex)
            await SaveAssetRecordsAsync().ConfigureAwait(false);
        return held;
    }

    public Task<string> SaveMeshAsync(PhosMesh mesh)
    {
        if (mesh == null)
            return Task.FromResult<string>(null!);

        // Serialize refuses a mesh whose topology cannot round-trip, and it refuses it SYNCHRONOUSLY -
        // so on the import path that throw would escape a Task-returning method and take the whole
        // model down after several meshes had already been written. One unsupported submesh should
        // cost that mesh its bake and nothing else; the caller already falls back to the source URL.
        byte[] data;
        try
        {
            data = PhosMeshSerializer.Serialize(mesh);
        }
        // The body cap throws InvalidDataException, and only NotSupportedException was caught, so one
        // oversized mesh still took the whole import down with it. -xlinka
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
        {
            Logger.Warn($"LocalDB: mesh cannot be baked, falling back to the source model - {ex.Message}");
            return Task.FromResult<string>(null!);
        }

        return SaveAssetAsync(data, ".lmesh");
    }

    // Forgets one asset: the record goes in the same serialized index write as every other change, and the
    // file after it, so a crash between the two leaves a stray file rather than a record naming nothing. A
    // file outside the cache (an import kept where it was) is never this database's to delete. True once
    // the index no longer holds the record, and when there was none.
    public async Task<bool> DeleteAssetAsync(string localUri)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key))
            return false;

        LocalAssetRecord? record;
        lock (_lock)
        {
            if (!DropLocked(key!, out record))
                return true;
        }

        if (!await SaveAssetRecordsAsync().ConfigureAwait(false))
            return false;
        if (!string.IsNullOrEmpty(record!.FilePath) && IsInAssetCache(record.FilePath))
        {
            try { File.Delete(record.FilePath); } catch { /* a stray file now, but nothing names it */ }
        }
        return true;
    }

    // RECEIVED FILES
    //
    // A file a peer sent or the cloud handed down is kept as a cache: it can be had again, so it is the only
    // kind ever let go to keep the disk in budget. What was made or imported here is never touched. A
    // received file becomes this machine's own when it is imported here or a save names it, since a save
    // has to open with nobody around to send it again. Anything resolved or written this run counts as in
    // use and stays until a later run. -xlinka

    // One received file as a trim sees it: when it was last wanted, and whether it may go now.
    public readonly record struct ReceivedFile(string Key, long Size, DateTime LastUsedUtc, bool Evictable);

    public List<ReceivedFile> GetReceivedFiles()
    {
        var files = new List<ReceivedFile>();
        lock (_lock)
        {
            foreach (var record in _assetRecords.Values)
            {
                if (!record.Received)
                    continue;
                var used = record.LastUsedUtc > record.ImportedAt ? record.LastUsedUtc : record.ImportedAt;
                files.Add(new ReceivedFile(record.Hash, record.FileSize, used, !InUseThisRun(record) && IsInAssetCache(record.FilePath)));
            }
        }
        return files;
    }

    // Deletes a received file and forgets it, unless it is in use this run or open somewhere. The file goes
    // first: one that will not delete keeps its record. The index is written by SaveRecordsAsync after a batch.
    public bool TryReleaseReceived(string key)
    {
        lock (_lock)
        {
            if (!_assetRecords.TryGetValue(key, out var record) || !record.Received || InUseThisRun(record)
                || !IsInAssetCache(record.FilePath))
                return false;
            try
            {
                File.Delete(record.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            DropLocked(key, out _);
            return true;
        }
    }

    // The file behind an address is this machine's own from now on, whatever brought it here.
    public bool Keep(string localUri)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key))
            return false;
        bool promoted;
        lock (_lock)
            promoted = _assetRecords.TryGetValue(key!, out var record) && Promote(record);
        if (promoted)
            _ = SaveAssetRecordsAsync();
        return promoted;
    }

    public Task<bool> SaveRecordsAsync() => SaveAssetRecordsAsync();

    private static bool Promote(LocalAssetRecord record)
    {
        if (!record.Received)
            return false;
        record.Received = false;
        return true;
    }

    private bool IsReceived(string? localUri)
    {
        var key = string.IsNullOrEmpty(localUri) ? null : ExtractKey(localUri!);
        if (string.IsNullOrEmpty(key))
            return false;
        lock (_lock)
            return _assetRecords.TryGetValue(key!, out var record) && record.Received;
    }

    private bool InUseThisRun(LocalAssetRecord record) => record.LastUsedUtc >= _openedUtc || record.ImportedAt >= _openedUtc;

    private bool IsInAssetCache(string? path)
        => !string.IsNullOrEmpty(path) && path!.StartsWith(GetAssetCachePath(), StringComparison.OrdinalIgnoreCase);

    // DERIVED ASSETS
    //
    // Most entries here are content-addressed: hash the bytes, that's the key. A derived asset is
    // one that is COMPUTED from another asset (a downscaled texture variant, a metadata sidecar)
    // and must be addressable BEFORE it exists, by anyone who knows the base URI. So its key is the
    // base hash plus a suffix instead of its own content hash.
    //
    // That single decision is what makes derived assets transfer for free: the URI still carries
    // the owner's machine id in the same position, so the peer transferer resolves and serves it
    // exactly like any other local asset, and a joiner that has only ever seen the base URI can
    // build the address of a derivative it has never received and request it by name. No manifest
    // of derived assets has to be replicated anywhere. -xlinka

    // '~' is URI-unreserved and filename-safe, and never appears in a hex hash, so it is an
    // unambiguous split point in a URI.
    private const char DerivedSeparator = '~';

    // The URI of a derived asset: the base URI with suffix appended to its
    // hash, keeping the base's machine id so the owner still serves it. Null if the base is not a
    // local:// URI or is already itself a derivative.
    public static string? DeriveUri(string? baseLocalUri, string suffix)
    {
        if (string.IsNullOrEmpty(baseLocalUri) || string.IsNullOrEmpty(suffix))
            return null;
        if (!baseLocalUri!.StartsWith("local://", StringComparison.Ordinal))
            return null;
        if (baseLocalUri.IndexOf(DerivedSeparator) >= 0)
            return null;
        return baseLocalUri + DerivedSeparator + suffix;
    }

    public static string? GetBaseUri(string? localUri)
    {
        if (string.IsNullOrEmpty(localUri))
            return localUri;
        int separator = localUri!.IndexOf(DerivedSeparator);
        return separator < 0 ? localUri : localUri.Substring(0, separator);
    }

    // Overwrites any previous blob at that address, since a derivative is defined by its inputs: if the suffix
    // matches, the newer computation supersedes the older one.
    // A derivative is received when it came from elsewhere or its base did: either way it can be made or
    // fetched again.
    //
    // One write per address at a time. A guest's avatar arriving asks for each texture's sidecar and rungs from
    // several loads at once, and a re-ask after a slow answer brings the same bytes twice. A second save of an
    // address already being written waits for that one instead: same address, same bytes. -xlinka
    public Task<string> SaveDerivedAssetAsync(string baseLocalUri, string suffix, byte[] data, string extension, bool received = false,
        bool writeIndex = true)
    {
        var uri = DeriveUri(baseLocalUri, suffix);
        var key = uri == null ? null : ExtractKey(uri);
        if (uri == null || data == null || string.IsNullOrEmpty(key))
        {
            Logger.Error($"LocalDB: cannot derive asset '{suffix}' from '{baseLocalUri}'");
            return Task.FromResult<string>(null!);
        }
        return OneWrite(key!, () => WriteDerivedAssetAsync(baseLocalUri, uri, key!, data, extension, received, writeIndex));
    }

    private async Task<string> WriteDerivedAssetAsync(string baseLocalUri, string uri, string key, byte[] data, string extension,
        bool received, bool writeIndex)
    {
        if (!string.IsNullOrEmpty(extension) && !extension.StartsWith("."))
            extension = "." + extension;

        // Something received from its owner is often here already: the transfer adopts the file under this very
        // address before the load that asked for it gets to file it again. Those are the same bytes, so the
        // second write is skipped.
        if (received)
        {
            LocalAssetRecord? had;
            lock (_lock)
                _assetRecords.TryGetValue(key, out had);
            if (had != null && had.FileSize == data.LongLength && File.Exists(had.FilePath))
                return uri;
        }

        (string Path, byte[]? Key) written;
        try
        {
            written = await StoreBytesAsync(data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error($"LocalDB: failed to save derived asset {uri}: {ex.Message}");
            return null!;
        }

        var record = new LocalAssetRecord
        {
            Hash = key,
            LocalUri = uri,
            FilePath = written.Path,
            OriginalPath = baseLocalUri,
            OriginalFileName = key + extension,
            Extension = extension,
            ImportedAt = DateTime.UtcNow,
            FileSize = data.LongLength,
            Encrypted = written.Key != null,
            EncryptionKey = written.Key,
            Received = received || IsReceived(baseLocalUri),
        };

        string? replaced;
        lock (_lock)
            replaced = PutLocked(record);
        Retire(replaced);

        if (writeIndex)
            await SaveAssetRecordsAsync().ConfigureAwait(false);
        else
            Interlocked.Increment(ref _recordsChanged);
        return uri;
    }

    // ADOPTION
    //
    // A file that arrived over peer transfer is already addressed: the hash is right there in the URI we
    // asked for. Re-hashing it to find that out reads every byte of an avatar mesh a second time, on the
    // thread that is about to decode it, before anything can be drawn. So take the URI's word for the
    // address, register the record immediately, and check the claim afterwards on a background thread.
    // The window that opens is narrow and bounded: a peer that serves bytes not matching the hash it was
    // asked for gets one load out of it, then loses both the record and the file.
    //
    // The transfer lands its bytes sealed already (SealedScatterWriter) and hands the key over with the file,
    // so adopting stays a rename.
    //
    // Synchronous on purpose. The caller decodes straight after and the decoder resolves the format from this
    // record, so the record has to exist by the time this returns or the very first load after a transfer
    // races the write that would have told it what it is. -xlinka
    public string? AdoptGatheredAsset(string localUri, string filePath, string? format = null, byte[]? sealedWith = null)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        lock (_lock)
        {
            if (_assetRecords.TryGetValue(key!, out var existing) && File.Exists(existing.FilePath))
            {
                // Idempotent: called again with the file this record already points at, do nothing at all.
                // Deleting there would destroy the cache entry it was asked to create.
                if (string.Equals(existing.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    return existing.FilePath;

                // Someone else already landed these bytes. Drop the duplicate rather than churn the cache.
                try { File.Delete(filePath); } catch { /* temp sweep gets it */ }
                return existing.FilePath;
            }
        }

        if (sealedWith == null && SealedFile.IsSealed(filePath))
        {
            Logger.Error($"LocalDB: {localUri} arrived sealed with no key to open it");
            TryDelete(filePath);
            return null;
        }

        var extension = format;
        if (string.IsNullOrEmpty(extension))
            extension = Path.GetExtension(filePath);
        if (!string.IsNullOrEmpty(extension) && !extension!.StartsWith("."))
            extension = "." + extension;

        if (sealedWith != null && KeepsTheoraPlain && MayBeTheora(extension))
        {
            try
            {
                if (IsTheoraVideo(SealedHead(filePath, sealedWith)))
                {
                    var plainTemp = GetTempFilePath();
                    using (var source = SealedFile.OpenRead(filePath, sealedWith))
                    using (var target = new FileStream(plainTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                        source.CopyTo(target, 1 << 20);
                    TryDelete(filePath);
                    filePath = plainTemp;
                    sealedWith = null;
                }
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or InvalidDataException)
            {
                Logger.Error($"LocalDB: could not open {localUri} as it arrived: {ex.Message}");
                TryDelete(filePath);
                return null;
            }
        }

        var targetPath = NewCachePath();
        long size;
        try
        {
            // A rename inside our own base path, not a copy: the temp directory and the cache are the
            // same volume by construction.
            File.Move(filePath, targetPath);
            long onDisk = new FileInfo(targetPath).Length;
            size = sealedWith != null ? SealedFile.PlainLength(onDisk, SealedFile.HeaderLength) : onDisk;
        }
        catch (Exception ex)
        {
            Logger.Error($"LocalDB: failed to adopt gathered asset {localUri}: {ex.Message}");
            return null;
        }

        var record = new LocalAssetRecord
        {
            Hash = key!,
            LocalUri = localUri,
            FilePath = targetPath,
            OriginalPath = localUri,
            OriginalFileName = key + extension,
            Extension = extension,
            ImportedAt = DateTime.UtcNow,
            FileSize = size,
            Encrypted = sealedWith != null,
            EncryptionKey = sealedWith,
            Verified = false,
            Received = true
        };

        string? replaced;
        lock (_lock)
            replaced = PutLocked(record);
        Retire(replaced);

        _ = SaveAssetRecordsAsync();
        VerifyAdoptedInBackground(key!, targetPath, sealedWith);
        if (sealedWith == null)
            CacheSealing.Nudge();

        Logger.Debug($"LocalDB: adopted gathered asset -> {localUri} ({size} bytes, '{extension}')");
        return targetPath;
    }

    // A derived asset's key is a base hash plus a suffix, so it is not a hash of its own contents and
    // there is nothing here to check it against. Only a bare hash gets verified.
    private static bool IsContentHashKey(string key)
    {
        if (key.Length != 64)
            return false;
        foreach (var c in key)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!hex)
                return false;
        }
        return true;
    }

    private void VerifyAdoptedInBackground(string key, string path, byte[]? fileKey)
    {
        if (!IsContentHashKey(key))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                string actual;
                await using (var stream = AssetFiles.Open(path, fileKey))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false)).ToLowerInvariant();
                if (actual == key)
                {
                    lock (_lock)
                    {
                        if (_assetRecords.TryGetValue(key, out var record))
                            record.Verified = true;
                    }
                    await SaveAssetRecordsAsync();
                    return;
                }

                Logger.Error($"LocalDB: adopted asset '{key}' hashes to '{actual}'; a peer served content that is not what was asked for. Dropping it.");
                bool dropped;
                lock (_lock)
                {
                    // Only drop the entry that is still the one we adopted. A legitimate local import of
                    // the same hash can have replaced it while this was hashing, and that one is sound.
                    dropped = _assetRecords.TryGetValue(key, out var record)
                        && string.Equals(record.FilePath, path, StringComparison.OrdinalIgnoreCase)
                        && DropLocked(key, out _);
                }

                if (!dropped)
                    return;

                try { File.Delete(path); } catch { /* record is gone either way */ }
                await SaveAssetRecordsAsync();
            }
            catch (Exception ex)
            {
                Logger.Warn($"LocalDB: could not verify adopted asset '{key}': {ex.Message}");
            }
        });
    }

    // METADATA

    // Empty when the asset is unknown here.
    public IReadOnlyDictionary<string, string>? GetAssetMetadata(string localUri)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key))
            return null;
        lock (_lock)
        {
            return _assetRecords.TryGetValue(key, out var record) ? record.Metadata : null;
        }
    }

    // No-op when the asset is not in this database (a peer-owned asset we never received).
    public async Task SetAssetMetadataAsync(string localUri, Action<IDictionary<string, string>> mutate, bool writeIndex = true)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key) || mutate == null)
            return;

        lock (_lock)
        {
            if (!_assetRecords.TryGetValue(key, out var record))
                return;
            // Replaced, never changed in place: the index writer serialises its snapshot and GetAssetMetadata's
            // callers read theirs outside the lock, and a bag that grew under them threw "collection was modified"
            // and dropped that index write. -xlinka
            var metadata = record.Metadata == null ? new Dictionary<string, string>() : new Dictionary<string, string>(record.Metadata);
            mutate(metadata);
            record.Metadata = metadata;
        }

        if (writeIndex)
            await SaveAssetRecordsAsync().ConfigureAwait(false);
        else
            Interlocked.Increment(ref _recordsChanged);
    }

    // ROWS
    //
    // Not everything this database keeps is a file. A row is a set of named fields filed under a table in
    // the same encrypted index as the assets, for work this machine has to remember between runs: a save
    // that has not gone up yet, say. Rows live beside the asset records, never among them, so no row can
    // ever resolve as an asset. -xlinka

    public List<(string Id, Dictionary<string, string> Fields)> GetRows(string table)
    {
        var rows = new List<(string, Dictionary<string, string>)>();
        lock (_lock)
        {
            foreach (var record in _rows.Values)
            {
                if (record.Table == table)
                    rows.Add((record.Hash, new Dictionary<string, string>(record.Metadata)));
            }
        }
        return rows;
    }

    // True once the index on disk holds the row as given.
    public Task<bool> PutRowAsync(string table, string id, IReadOnlyDictionary<string, string> fields)
    {
        var record = new LocalAssetRecord
        {
            Hash = id,
            Table = table,
            ImportedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>(fields),
        };
        lock (_lock)
        {
            _rows[RowKey(table, id)] = record;
        }
        return SaveAssetRecordsAsync();
    }

    public Task<bool> DeleteRowAsync(string table, string id)
    {
        lock (_lock)
        {
            if (!_rows.Remove(RowKey(table, id)))
                return Task.FromResult(true);
        }
        return SaveAssetRecordsAsync();
    }

    private static string RowKey(string table, string id) => table + "/" + id;

    // The record key inside a local:// URI: everything after the machine id.
    private static string? ExtractKey(string localUri)
    {
        if (string.IsNullOrEmpty(localUri) || !localUri.StartsWith("local://", StringComparison.Ordinal))
            return null;
        var parts = localUri.Substring(8).Split('/');
        return parts.Length < 2 ? null : parts[1];
    }

    // Where the file behind an address sits, for existence checks and bookkeeping. Its bytes on disk are sealed:
    // read them through ReadAssetBytesAsync or OpenRead, never from this path.
    public string GetFilePath(string localUri)
    {
        var record = Touch(localUri);
        return record?.FilePath!;
    }

    // The record behind an address, marked as wanted now.
    private LocalAssetRecord? Touch(string localUri)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key))
            return null;
        lock (_lock)
        {
            if (!_assetRecords.TryGetValue(key!, out var record))
                return null;
            record.LastUsedUtc = DateTime.UtcNow;
            return record;
        }
    }

    public bool Exists(string localUri)
    {
        var path = GetFilePath(localUri);
        return path != null && File.Exists(path);
    }

    // The format an asset was filed as, with its dot ("" when unknown). A sealed file's name has no extension,
    // so the record is the only place that knows.
    public string GetExtension(string localUri)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key))
            return string.Empty;
        lock (_lock)
            return _assetRecords.TryGetValue(key!, out var record) ? ExtensionOf(record) : string.Empty;
    }

    // The name the file went by before cache files were sealed: its key, or for an import left where the user
    // keeps it, that file's own name. The cloud's list of a save's parts records it.
    public string? GetStoredName(string localUri)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key))
            return null;
        lock (_lock)
        {
            if (!_assetRecords.TryGetValue(key!, out var record))
                return null;
            return IsInAssetCache(record.FilePath) ? record.Hash : Path.GetFileNameWithoutExtension(record.FilePath);
        }
    }

    public static string ExtensionOf(LocalAssetRecord record)
    {
        if (!string.IsNullOrEmpty(record.Extension))
            return record.Extension!;
        var fromPath = string.IsNullOrEmpty(record.FilePath) ? null : Path.GetExtension(record.FilePath);
        if (!string.IsNullOrEmpty(fromPath))
            return fromPath!;
        return string.IsNullOrEmpty(record.OriginalFileName) ? string.Empty : Path.GetExtension(record.OriginalFileName) ?? string.Empty;
    }

    // The plaintext length of an asset held here, -1 when there is none.
    public long GetPlainSize(string localUri)
    {
        var key = ExtractKey(localUri);
        if (string.IsNullOrEmpty(key))
            return -1;
        lock (_lock)
            return _assetRecords.TryGetValue(key!, out var record) ? record.FileSize : -1;
    }

    // The key that opens a file this database wrote, found by its path; null for a file it does not know or
    // one it keeps unsealed.
    public byte[]? KeyForPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        lock (_lock)
            return _byPath.TryGetValue(PathKey(path), out var record) ? record.EncryptionKey : null;
    }

    public bool OwnsPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        lock (_lock)
            return _byPath.ContainsKey(PathKey(path));
    }

    // The local:// address of a record this machine holds for a content hash, or null. A cloud
    // asset that was fetched into the database is addressed this way so the variant machinery,
    // which only knows local:// bases, can work on it. -xlinka
    public string? UriForHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash))
            return null;
        lock (_lock)
        {
            if (_assetRecords.TryGetValue(hash!, out var record) && File.Exists(record.FilePath))
            {
                record.LastUsedUtc = DateTime.UtcNow;
                return $"local://{_machineId}/{hash}";
            }
        }
        return null;
    }

    public string GetTempFilePath(string extension = null!)
    {
        var fileName = Guid.NewGuid().ToString("N");
        if (!string.IsNullOrEmpty(extension))
        {
            if (!extension.StartsWith("."))
                extension = "." + extension;
            fileName += extension;
        }
        return Path.Combine(GetTempPath(), fileName);
    }

    public void CleanupTempFiles(TimeSpan maxAge = default)
    {
        if (maxAge == default)
            maxAge = TimeSpan.FromHours(24);

        var tempPath = GetTempPath();
        if (!Directory.Exists(tempPath))
            return;

        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var file in Directory.GetFiles(tempPath))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch { /* Ignore cleanup errors */ }
        }
    }

    public IReadOnlyList<LocalAssetRecord> GetAllRecords()
    {
        lock (_lock)
        {
            return new List<LocalAssetRecord>(_assetRecords.Values);
        }
    }

    private string GetDefaultBasePath()
    {
        return Lumora.Core.Persistence.PathResolver.Resolve("LocalDB");
    }

    private string GetAssetCachePath() => Path.Combine(_basePath, "Assets");
    private string GetTempPath() => Path.Combine(_basePath, "Temp");

    // Cache directory for renderer-side artifacts that are valid only on THIS machine's GPU stack
    // (block-compressed texture variants, above all). These are never content-addressed and never
    // transferred: what a given driver accepts is a property of the machine, not of the asset, so
    // shipping one to a peer would at best waste bandwidth and at worst hand them a format their
    // device cannot sample. Deleting this directory only costs a recompress. -xlinka
    public string GetGpuCachePath()
    {
        var path = Path.Combine(_basePath, "GpuCache");
        try { Directory.CreateDirectory(path); } catch { /* first use recreates it */ }
        return path;
    }

    // Cache directory for renderer-side geometry derived from an asset's own contents - LOD index
    // buffers, above all. Unlike GpuCache these blobs are device-independent (the simplifier is
    // deterministic), they are just expensive: seconds of CPU for a heavy model. Keyed by a hash of
    // the geometry itself, so the same mesh arriving under a new URL still hits. Deleting this
    // directory only costs one re-bake per mesh. -xlinka
    public string GetMeshCachePath()
    {
        var path = Path.Combine(_basePath, "MeshLod");
        try { Directory.CreateDirectory(path); } catch { /* first use recreates it */ }
        return path;
    }

    private string GetOrCreateMachineId()
    {
        // Prefer the install's self-certifying machine identity so local:// asset URIs carry the SAME id
        // the session stamps on User.MachineID. The asset transferer resolves which connected peer owns a
        // local:// asset from that id; when the two differed, a peer-imported mesh/texture could never be
        // relayed to other users. The id is base64url (no '/'), so it doesn't disturb URI parsing, and the
        // transferer reads it case-sensitively from the URI's original string.
        try
        {
            var identityId = Lumora.Core.Security.MachineIdentity.Local.MachineId;
            if (!string.IsNullOrEmpty(identityId))
                return identityId;
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocalDB: machine identity unavailable, using a local fallback id: {ex.Message}");
        }

        // Fallback: a stable random id persisted next to the cache.
        var idPath = Path.Combine(_basePath, ".machine_id");
        try
        {
            Directory.CreateDirectory(_basePath);

            if (File.Exists(idPath))
            {
                return File.ReadAllText(idPath).Trim();
            }

            var id = Guid.NewGuid().ToString("N").Substring(0, 16);
            File.WriteAllText(idPath, id);
            return id;
        }
        catch
        {
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }
    }

    // WRITING SEALED FILES
    //
    // Every write lands under a fresh random name, so a file and the key in its record always come from the
    // same write: two writes of one address race only over which record wins, never over one file. The file a
    // record no longer names is retired, and deleted a little later so a reader that looked it up a moment ago
    // can still open it. -xlinka

    private readonly Dictionary<string, Task<string>> _writes = new(StringComparer.Ordinal);

    private Task<string> OneWrite(string key, Func<Task<string>> write)
    {
        Task<string> task;
        lock (_writes)
        {
            // A finished write whose entry has not been cleared yet is not running any more.
            if (_writes.TryGetValue(key, out var running) && !running.IsCompleted)
                return running;
            task = Task.Run(write);
            _writes[key] = task;
        }
        _ = task.ContinueWith(_ =>
        {
            lock (_writes)
            {
                if (_writes.TryGetValue(key, out var kept) && kept == task)
                    _writes.Remove(key);
            }
        }, TaskScheduler.Default);
        return task;
    }

    private string NewCachePath() => Path.Combine(GetAssetCachePath(), Guid.NewGuid().ToString("N"));

    // Sealed, unless it is a video this platform keeps plain. A null key means plain.
    private async Task<(string Path, byte[]? Key)> StoreBytesAsync(byte[] data)
    {
        if (!StaysPlain(data.AsSpan(0, System.Math.Min(data.Length, TheoraHeadBytes))))
            return await SealBytesAsync(data).ConfigureAwait(false);
        var temp = GetTempFilePath();
        try
        {
            await File.WriteAllBytesAsync(temp, data).ConfigureAwait(false);
            var target = NewCachePath();
            File.Move(temp, target);
            return (target, null);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private async Task<(string Path, byte[]? Key, long Size)> CopyPlainAsync(string source)
    {
        var temp = GetTempFilePath();
        try
        {
            await Task.Run(() => File.Copy(source, temp)).ConfigureAwait(false);
            var target = NewCachePath();
            File.Move(temp, target);
            return (target, null, new FileInfo(target).Length);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static byte[] SealedHead(string path, byte[] key)
    {
        using var stream = SealedFile.OpenRead(path, key);
        var head = new byte[(int)System.Math.Min(stream.Length, TheoraHeadBytes)];
        stream.ReadExactly(head);
        return head;
    }

    private async Task<(string Path, byte[] Key)> SealBytesAsync(byte[] data)
    {
        var key = SealedKeys.NewKey();
        var temp = GetTempFilePath();
        try
        {
            await SealedFile.WriteAllBytesAsync(temp, data, key).ConfigureAwait(false);
            var target = NewCachePath();
            File.Move(temp, target);
            return (target, key);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    // Streams a file into a sealed cache file, a megabyte at a time, so an import of a big model never holds
    // it whole. Returns the plaintext length.
    private async Task<(string Path, byte[]? Key, long Size)> SealFileAsync(string source)
    {
        var key = SealedKeys.NewKey();
        var temp = GetTempFilePath();
        try
        {
            long size;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var output = new SealedWriteStream(new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1,
                    FileOptions.Asynchronous), key, leaveOpen: false);
                await using (output.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, 1 << 20).ConfigureAwait(false);
                    size = output.Length;
                    await output.CompleteAsync().ConfigureAwait(false);
                }
            }
            var target = NewCachePath();
            File.Move(temp, target);
            return (target, key, size);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try { File.Delete(path); } catch { /* the stray sweep gets it */ }
    }

    private static readonly TimeSpan RetireDelay = TimeSpan.FromMinutes(2);

    private void Retire(string? path)
    {
        if (string.IsNullOrEmpty(path) || !IsInAssetCache(path))
            return;
        _ = Task.Delay(RetireDelay).ContinueWith(_ =>
        {
            lock (_lock)
            {
                // Named again since (a re-root or an import of the same file): it stays.
                if (_byPath.ContainsKey(PathKey(path!)))
                    return;
            }
            TryDelete(path);
        }, TaskScheduler.Default);
    }

    // Under _lock. Files a record and returns the cache file it replaced, when that was a different one.
    private string? PutLocked(LocalAssetRecord record)
    {
        string? replaced = null;
        if (_assetRecords.TryGetValue(record.Hash, out var old) && !string.IsNullOrEmpty(old.FilePath))
        {
            var oldKey = PathKey(old.FilePath);
            if (_byPath.TryGetValue(oldKey, out var holder) && holder == old)
                _byPath.Remove(oldKey);
            if (!string.Equals(old.FilePath, record.FilePath, StringComparison.OrdinalIgnoreCase))
                replaced = old.FilePath;
        }
        _assetRecords[record.Hash] = record;
        if (!string.IsNullOrEmpty(record.FilePath))
            _byPath[PathKey(record.FilePath)] = record;
        return replaced;
    }

    // Under _lock.
    private bool DropLocked(string key, out LocalAssetRecord? record)
    {
        if (!_assetRecords.Remove(key, out record))
            return false;
        if (!string.IsNullOrEmpty(record.FilePath))
        {
            var pathKey = PathKey(record.FilePath);
            if (_byPath.TryGetValue(pathKey, out var holder) && holder == record)
                _byPath.Remove(pathKey);
        }
        return true;
    }

    // Records name their files by absolute paths written however the data folder was spelled that run.
    private static string PathKey(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    // READING

    // A cached asset's bytes, decrypted. A file from before sealing comes back as it is. Null when the address
    // does not resolve or the file will not open. A received file that fails its seal is dropped, so the next
    // load fetches it again instead of failing on the same bytes forever. - xlinka
    public async Task<byte[]?> ReadAssetBytesAsync(string localUri)
    {
        var record = Touch(localUri);
        if (record == null || string.IsNullOrEmpty(record.FilePath) || !File.Exists(record.FilePath))
            return null;

        try
        {
            return await AssetFiles.ReadAllBytesAsync(record.FilePath, record.EncryptionKey).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or EndOfStreamException)
        {
            Unreadable(record, ex);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"LocalDB: could not read cached asset '{localUri}': {ex.Message}");
            return null;
        }
    }

    // A seekable stream of an asset's plaintext, for whatever reads a file as it goes (a video, a font table,
    // a peer being sent the file). Null when the address does not resolve.
    public Stream? OpenRead(string localUri)
    {
        var record = Touch(localUri);
        if (record == null || string.IsNullOrEmpty(record.FilePath) || !File.Exists(record.FilePath))
            return null;
        try
        {
            return AssetFiles.Open(record.FilePath, record.EncryptionKey);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or EndOfStreamException)
        {
            Unreadable(record, ex);
            return null;
        }
    }

    private void Unreadable(LocalAssetRecord record, Exception ex)
    {
        Logger.Error($"LocalDB: cached asset '{record.Hash}' does not open ({ex.Message})");
        if (!record.Received)
            return;
        bool dropped;
        lock (_lock)
            dropped = _assetRecords.TryGetValue(record.Hash, out var current) && current == record && DropLocked(record.Hash, out _);
        if (!dropped)
            return;
        TryDelete(record.FilePath);
        _ = SaveAssetRecordsAsync();
    }

    // SEALING OLDER FILES
    //
    // A file written before sealing (or adopted from a peer on an older path) is sealed where it lies, one
    // file at a time at a bounded rate, so the pass never competes with a load. Each step is safe to stop at:
    // the sealed copy goes under a new name, the record moves to it, the index is written, and only then is the
    // old file retired. A stop before the index write leaves an unnamed sealed file for the stray sweep; a stop
    // after it leaves the old file, which the sweep also takes. -xlinka

    // The records this pass would seal, the ones it has not tried yet.
    private List<LocalAssetRecord> Unsealed(HashSet<string> tried)
    {
        var list = new List<LocalAssetRecord>();
        lock (_lock)
        {
            foreach (var record in _assetRecords.Values)
            {
                if (record.EncryptionKey == null && !tried.Contains(record.Hash) && IsInAssetCache(record.FilePath))
                    list.Add(record);
            }
        }
        return list;
    }

    // The index is written every so many files, not after each one: it is the whole store, and writing it two
    // thousand times over was most of what the pass cost. The old files wait for the write that drops them.
    private const int SealsPerIndexWrite = 64;

    public async Task<(int Sealed, long Bytes)> SealOlderFilesAsync(Func<long, Task> pace, CancellationToken ct)
    {
        await Ready.ConfigureAwait(false);
        int count = 0;
        long bytes = 0;
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var unnamed = new List<string>();
        if (KeepsTheoraPlain)
            count += await UnsealTheoraAsync(unnamed, pace, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            var batch = Unsealed(tried);
            if (batch.Count == 0)
                break;
            foreach (var record in batch)
            {
                if (ct.IsCancellationRequested)
                    break;
                tried.Add(record.Hash);
                if (!File.Exists(record.FilePath))
                    continue;
                try
                {
                    var oldPath = record.FilePath;
                    if (KeepsTheoraPlain && MayBeTheora(ExtensionOf(record))
                        && IsTheoraVideo(await ReadHeadAsync(oldPath, TheoraHeadBytes).ConfigureAwait(false)))
                        continue;
                    long size = await SealOneAsync(record, SealInPlaceAsync).ConfigureAwait(false);
                    if (size < 0)
                        continue;
                    unnamed.Add(oldPath);
                    count++;
                    bytes += size;
                    if (unnamed.Count >= SealsPerIndexWrite)
                        await WriteIndexThenRetire(unnamed).ConfigureAwait(false);
                    await pace(size).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warn($"LocalDB: could not seal cached asset '{record.Hash}' yet: {ex.Message}");
                }
            }
        }
        await WriteIndexThenRetire(unnamed).ConfigureAwait(false);
        if (!ct.IsCancellationRequested)
            SweepStrays();
        return (count, bytes);
    }

    private async Task WriteIndexThenRetire(List<string> unnamed)
    {
        if (unnamed.Count == 0)
            return;
        if (await SaveAssetRecordsAsync().ConfigureAwait(false))
        {
            foreach (var path in unnamed)
                Retire(path);
        }
        unnamed.Clear();
    }

    // Theora videos sealed here before this platform kept them plain, or by a build that did not: written out
    // plain again under a new name, the sealed copy retired with the rest. The number opened up.
    private async Task<int> UnsealTheoraAsync(List<string> unnamed, Func<long, Task> pace, CancellationToken ct)
    {
        var sealedVideos = new List<LocalAssetRecord>();
        lock (_lock)
        {
            foreach (var record in _assetRecords.Values)
            {
                if (record.EncryptionKey != null && IsInAssetCache(record.FilePath) && MayBeTheora(ExtensionOf(record)))
                    sealedVideos.Add(record);
            }
        }
        int count = 0;
        foreach (var record in sealedVideos)
        {
            if (ct.IsCancellationRequested)
                break;
            try
            {
                if (!File.Exists(record.FilePath) || !IsTheoraVideo(SealedHead(record.FilePath, record.EncryptionKey!)))
                    continue;
                var oldPath = record.FilePath;
                long size = await SealOneAsync(record, UnsealInPlaceAsync).ConfigureAwait(false);
                if (size < 0)
                    continue;
                unnamed.Add(oldPath);
                count++;
                await pace(size).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or InvalidDataException)
            {
                Logger.Warn($"LocalDB: could not open up stored video '{record.Hash}' yet: {ex.Message}");
            }
        }
        return count;
    }

    private async Task<long> UnsealInPlaceAsync(LocalAssetRecord record)
    {
        lock (_lock)
        {
            if (!_assetRecords.TryGetValue(record.Hash, out var current) || current != record || record.EncryptionKey == null)
                return -1;
        }
        var temp = GetTempFilePath();
        string newPath;
        try
        {
            await using (var source = SealedFile.OpenRead(record.FilePath, record.EncryptionKey))
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
                await source.CopyToAsync(target, 1 << 20).ConfigureAwait(false);
            newPath = NewCachePath();
            File.Move(temp, newPath);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        var plainRecord = record.Copy();
        plainRecord.FilePath = newPath;
        plainRecord.Encrypted = false;
        plainRecord.EncryptionKey = null;
        plainRecord.Extension = ExtensionOf(record);
        lock (_lock)
        {
            if (!_assetRecords.TryGetValue(record.Hash, out var current) || current != record)
            {
                TryDelete(newPath);
                return -1;
            }
            PutLocked(plainRecord);
        }
        Interlocked.Increment(ref _recordsChanged);
        return plainRecord.FileSize;
    }

    // The plaintext length moved, or -1 when the record changed under the pass or another write of the same
    // address is running (a later pass gets it).
    private async Task<long> SealOneAsync(LocalAssetRecord record, Func<LocalAssetRecord, Task<long>> work)
    {
        var turn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_writes)
        {
            if (_writes.TryGetValue(record.Hash, out var running) && !running.IsCompleted)
                return -1;
            _writes[record.Hash] = turn.Task;
        }
        try
        {
            return await work(record).ConfigureAwait(false);
        }
        finally
        {
            lock (_writes)
            {
                if (_writes.TryGetValue(record.Hash, out var kept) && kept == turn.Task)
                    _writes.Remove(record.Hash);
            }
            turn.TrySetResult(record.LocalUri);
        }
    }

    private async Task<long> SealInPlaceAsync(LocalAssetRecord record)
    {
        lock (_lock)
        {
            if (!_assetRecords.TryGetValue(record.Hash, out var current) || current != record)
                return -1;
        }
        string oldPath = record.FilePath;
        string newPath;
        byte[]? key;
        long size;
        if (LocalEncryption.IsEncrypted(await ReadHeadAsync(oldPath, 4).ConfigureAwait(false)))
        {
            var plain = LocalEncryption.Decrypt(await File.ReadAllBytesAsync(oldPath).ConfigureAwait(false));
            (newPath, key) = await SealBytesAsync(plain).ConfigureAwait(false);
            size = plain.LongLength;
        }
        else
        {
            (newPath, key, size) = await SealFileAsync(oldPath).ConfigureAwait(false);
        }

        var sealedRecord = record.Copy();
        sealedRecord.FilePath = newPath;
        sealedRecord.Encrypted = true;
        sealedRecord.EncryptionKey = key;
        sealedRecord.Extension = ExtensionOf(record);
        sealedRecord.FileSize = size;
        lock (_lock)
        {
            if (!_assetRecords.TryGetValue(record.Hash, out var current) || current != record)
            {
                TryDelete(newPath);
                return -1;
            }
            PutLocked(sealedRecord);
        }
        Interlocked.Increment(ref _recordsChanged);
        return size;
    }

    private static async Task<byte[]> ReadHeadAsync(string path, int count)
    {
        var head = new byte[count];
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.Asynchronous);
        int got = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false).ConfigureAwait(false);
        return got == head.Length ? head : head.AsSpan(0, got).ToArray();
    }

    // Files in the asset folder no record names, a day old or more: an older run's leftovers, a retired file
    // a reader held open, the plain original of a sealed one. Never run on an index that did not read, which
    // would make every file look unnamed. The age keeps it off a file another instance on this folder has
    // just written and not filed yet.
    private void SweepStrays()
    {
        if (!_recordsSound)
            return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        int removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(GetAssetCachePath()))
            {
                bool named;
                lock (_lock)
                    named = _byPath.ContainsKey(PathKey(file));
                if (named)
                    continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff)
                        continue;
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"LocalDB: could not sweep the asset folder: {ex.Message}");
        }
        if (removed > 0)
            Logger.Log($"LocalDB: swept {removed} unnamed file(s) from the asset folder");
    }

    private static async Task<string> ComputeFileHashAsync(string filePath)
    {
        var hashBytes = await Task.Run(() =>
        {
            using var stream = File.OpenRead(filePath);
            return SHA256.HashData(stream);
        }).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLower();
    }

    private static string ComputeBytesHash(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(data);
        return Convert.ToHexString(hashBytes).ToLower();
    }

    private async Task LoadAssetRecordsAsync()
    {
        var recordsPath = Path.Combine(_basePath, "records.json");
        if (!File.Exists(recordsPath))
            return;

        try
        {
            // Transparently handle both the encrypted store and a plain/legacy records.json.
            var raw = await File.ReadAllBytesAsync(recordsPath).ConfigureAwait(false);
            var json = Encoding.UTF8.GetString(LocalEncryption.Decrypt(raw));
            var records = JsonSerializer.Deserialize<List<LocalAssetRecord>>(json);
            if (records == null) return;

            lock (_lock)
            {
                string assetDir = GetAssetCachePath();
                foreach (var record in records)
                {
                    if (string.IsNullOrEmpty(record.Hash))
                        continue;
                    if (!string.IsNullOrEmpty(record.Table))
                    {
                        _rows[RowKey(record.Table, record.Hash)] = record;
                        continue;
                    }
                    // Records carry absolute paths and the database folder can move (the data folder was
                    // consolidated once already), so a path outside the current asset folder is re-rooted
                    // by file name instead of left dangling. -xlinka
                    if (!string.IsNullOrEmpty(record.FilePath)
                        && !record.FilePath.StartsWith(assetDir, StringComparison.OrdinalIgnoreCase))
                    {
                        string rerooted = Path.Combine(assetDir, Path.GetFileName(record.FilePath));
                        if (File.Exists(rerooted))
                            record.FilePath = rerooted;
                    }
                    PutLocked(record);
                }
            }

            Logger.Log($"LocalDB: Loaded {records.Count} asset records");
        }
        catch (Exception ex)
        {
            _recordsSound = false;
            Logger.Warn($"LocalDB: Failed to load records: {ex.Message}");
        }
    }

    // One write at a time, each of a snapshot taken in its own turn, so an older snapshot can never land
    // on top of a newer one. A writer whose change an earlier turn already wrote skips its own. The index
    // is written beside itself and moved over the old one, so a crash leaves one or the other whole.
    // True once the caller's change is on disk.
    //
    // Always written from the thread pool. The index of a store that has had a few avatars through it is
    // twelve thousand records and more, over 50 ms to serialise and encrypt, and a caller on the main
    // thread (an import's continuation comes back there) was paying all of it inside its frame. -xlinka
    private Task<bool> SaveAssetRecordsAsync()
    {
        long wanted = Interlocked.Increment(ref _recordsChanged);
        return Task.Run(() => WriteAssetRecordsAsync(wanted));
    }

    private static readonly JsonSerializerOptions RecordsJson = new();

    private async Task<bool> WriteAssetRecordsAsync(long wanted)
    {
        var recordsPath = Path.Combine(_basePath, "records.json");
        await _recordsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _recordsWritten) >= wanted)
                return true;

            List<LocalAssetRecord> snapshot;
            long version;
            lock (_lock)
            {
                version = Interlocked.Read(ref _recordsChanged);
                snapshot = new List<LocalAssetRecord>(_assetRecords.Count + _rows.Count);
                snapshot.AddRange(_assetRecords.Values);
                snapshot.AddRange(_rows.Values);
            }

            // Straight to UTF-8 and not indented: nobody reads it by eye through the encryption, and the
            // indented text was most of what each write cost and allocated.
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, RecordsJson);
            // The records store (asset list, original paths, per-asset keys) is encrypted at rest.
            var bytes = LocalEncryption.Encrypt(json);
            var tempPath = recordsPath + ".tmp";
            await File.WriteAllBytesAsync(tempPath, bytes).ConfigureAwait(false);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, recordsPath, true);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    // Something else (a scanner, a backup) can hold the old index open for a moment.
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            Interlocked.Exchange(ref _recordsWritten, version);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocalDB: Failed to save records: {ex.Message}");
            return false;
        }
        finally
        {
            _recordsGate.Release();
        }
    }

    public void Dispose()
    {
        // Cleanup temp files older than 1 hour on dispose
        CleanupTempFiles(TimeSpan.FromHours(1));
    }
}

public class LocalAssetRecord
{
    public string Hash { get; set; } = null!;
    public string LocalUri { get; set; } = null!;
    public string FilePath { get; set; } = null!;
    public string OriginalPath { get; set; } = null!;
    public string OriginalFileName { get; set; } = null!;
    public DateTime ImportedAt { get; set; }
    // The PLAINTEXT length.
    public long FileSize { get; set; }

    // True when the file is sealed at rest. Readers go by the file's own header, so a stale flag never
    // decides how a file is opened.
    public bool Encrypted { get; set; }

    // The random key the file is sealed under (AES-256), null for a file kept as it is: one from before
    // sealing, or an import left where the user keeps it. Only ever stored inside the encrypted index.
    public byte[]? EncryptionKey { get; set; }

    // The format the asset was filed as, with its dot. A sealed file's name has none.
    public string? Extension { get; set; }

    // False only between a peer-gathered file being adopted under the hash in its URI and the background
    // check confirming the bytes hash to that. Defaults true so every record written before this existed,
    // and every record whose bytes we hashed ourselves, reads back as settled.
    public bool Verified { get; set; } = true;

    // True for a file a peer sent or the cloud handed down, which can be had again; false for one made or
    // imported here. Only received files are ever let go to keep the cache in budget.
    public bool Received { get; set; }

    // When a load last resolved this file, for which received file goes first.
    public DateTime LastUsedUtc { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = new();

    // Null for an asset. For a row (see LocalDB.PutRowAsync) the table it is filed under: Hash is then
    // the row's id, Metadata its fields, and there is no file.
    public string? Table { get; set; }

    internal LocalAssetRecord Copy() => (LocalAssetRecord)MemberwiseClone();
}
