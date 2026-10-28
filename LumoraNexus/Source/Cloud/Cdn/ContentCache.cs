// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Diagnostics;
using Lumora.Nexus.Storage;

namespace Lumora.Nexus.Cloud.Cdn;

// Downloaded content as files on disk, read from disk whenever it is asked for. Memory holds one row per
// file (where, how big, when last wanted) and never the bytes. A download streams into a partial file, is
// checked against the length the server declared and against its hash when it was asked for by one, and
// only then moves into place whole. Files earlier runs left are found at startup with the time they were
// last wanted, so the trim takes the oldest first whatever run it came from. A file being read, or wanted
// a moment ago, is never deleted, and a trim never meets a file half moved in.
//
// Files are sealed (SealedFile, the key wrapped into each file) from the first byte a download writes, and named
// by the content hash encrypted under a key only this machine has, so the folder shows neither what a file is
// nor which blob it holds. Files from before sealing sit under their plain hash; they still read, and
// SealOlderFilesAsync seals them where they lie. -xlinka
public sealed class ContentCache : IDisposable
{
    private const string PartialFolder = ".partial";
    private const string PartialExtension = ".part";
    private const string LegacyPartialMarker = ".tmp.";
    private const int CopyChunk = 81920;

    // A partial file this old belongs to no download still running, here or in another instance.
    private static readonly TimeSpan StalePartial = TimeSpan.FromHours(1);

    // How often the last-wanted time is written onto the file itself, so the next run orders it right
    // without a write on every read.
    private static readonly TimeSpan StampEvery = TimeSpan.FromHours(1);

    private static readonly HttpClient SharedHttp = CreateHttp();

    private readonly LumoraClient _client;
    private readonly string _storagePath;
    private readonly string _partialPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string?>> _pending = new(StringComparer.Ordinal);
    // Keys the service answered "not there" for: how many answers in a row, and when it may be asked again.
    // Every read in between gets the answer from here, so a guest's avatar whose parts are gone costs no round
    // trip per rejoin, reload or copy. The wait starts short and doubles to LongestWait: a part its owner puts
    // back moments later (the wearer's repair upload lands a second or two after the first ask) shows on the
    // next ask, and one that is gone for good costs a few asks in its first minute and one per LongestWait
    // after, one schedule per key however many worlds want it. Only a read asks; LateCloudParts is what reads
    // again while a world still wants the part. ForgetMissing (the reload) asks at once and keeps the count.
    // -xlinka
    private readonly Dictionary<string, Miss> _missing = new(StringComparer.Ordinal);
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(2);

    private struct Miss
    {
        public int Answers;
        public long Due;
    }

    private bool MissingNow(string key)
        => _missing.TryGetValue(key, out var miss) && System.Diagnostics.Stopwatch.GetTimestamp() < miss.Due;

    // How long a key that was not there is left alone after its nth "not there" in a row.
    public static TimeSpan WaitAfter(int answers)
    {
        double seconds = FirstWait.TotalSeconds * System.Math.Pow(2, System.Math.Clamp(answers - 1, 0, 16));
        return TimeSpan.FromSeconds(System.Math.Min(seconds, LongestWait.TotalSeconds));
    }

    // Under the gate. The answers in a row, this one included.
    private int NoteMissingLocked(string key)
    {
        _missing.TryGetValue(key, out var miss);
        miss.Answers++;
        miss.Due = System.Diagnostics.Stopwatch.GetTimestamp()
            + (long)(WaitAfter(miss.Answers).TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        _missing[key] = miss;
        return miss.Answers;
    }
    private readonly SemaphoreSlim _trimTurn = new(1, 1);
    private readonly CancellationTokenSource _closing = new();

    private long _usedBytes;
    private long _capacityBytes = 10L * 1024 * 1024 * 1024;

    public string StoragePath => _storagePath;
    public long UsedBytes => Interlocked.Read(ref _usedBytes);
    public long CapacityBytes
    {
        get => Interlocked.Read(ref _capacityBytes);
        set
        {
            Interlocked.Exchange(ref _capacityBytes, Math.Max(0, value));
            if (UsedBytes > CapacityBytes)
                _ = Evict();
        }
    }
    public int EntryCount
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }
    public double UsageRatio => CapacityBytes > 0 ? (double)UsedBytes / CapacityBytes : 0;

    // How long a file is safe after it was last wanted, however full the cache is: a load is handed a path
    // and opens it a moment later.
    public TimeSpan MinResidency { get; set; } = TimeSpan.FromSeconds(30);

    // Tries per download, and the wait before the second; each wait after is three times the one before.
    public int DownloadAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    // A body that sends nothing for this long is a dead connection, not a slow one.
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public event Action<string, TransferProgress>? TransferUpdated;
    public event Action<string>? ContentReady;
    public event Action<string>? ContentRemoved;

    // One cached file as a trim sees it.
    public readonly record struct CachedFile(string Key, long Size, DateTime LastUsedUtc, bool Evictable);

    // Without a key source (a tool running the cache on its own) files are kept as they come.
    private readonly bool _seal;

    public ContentCache(LumoraClient client, string storagePath)
    {
        _client = client;
        _storagePath = storagePath;
        _partialPath = Path.Combine(storagePath, PartialFolder);
        _seal = SealedKeys.Available;
        Directory.CreateDirectory(_storagePath);
        IndexExisting();
    }

    // Downloads still running stop and delete their partial files.
    public void Dispose() => _closing.Cancel();

    // The bytes behind an address, read from its cached file, downloaded first when there is none. The array
    // is the caller's; nothing here keeps it. Null when it cannot be had.
    public Task<byte[]?> Get(Uri uri, CancellationToken ct = default)
    {
        if (uri.Scheme == Uri.UriSchemeFile)
            return ReadLocal(uri.LocalPath, ct);
        var key = KeyOf(uri, out var external);
        return key == null ? Task.FromResult<byte[]?>(null) : Quiet(Read(key, external, ct));
    }

    public Task<byte[]?> Get(string hash, CancellationToken ct = default)
        => IsSha256(hash) ? Quiet(Read(hash.ToLowerInvariant(), null, ct)) : Task.FromResult<byte[]?>(null);

    // The cached file behind an address, downloaded first when there is none, for whatever reads a file
    // rather than an array. Open it soon: past MinResidency an unopened file can go in a trim.
    public Task<string?> GetFile(Uri uri, CancellationToken ct = default)
    {
        if (uri.Scheme == Uri.UriSchemeFile)
            return Task.FromResult(File.Exists(uri.LocalPath) ? uri.LocalPath : null);
        var key = KeyOf(uri, out var external);
        return key == null ? Task.FromResult<string?>(null) : Quiet(Locate(key, external, ct));
    }

    public Task<string?> GetFile(string hash, CancellationToken ct = default)
        => IsSha256(hash) ? Quiet(Locate(hash.ToLowerInvariant(), null, ct)) : Task.FromResult<string?>(null);

    public Task Prefetch(string hash, CancellationToken ct = default) => GetFile(hash, ct);
    public Task Prefetch(Uri uri, CancellationToken ct = default) => GetFile(uri, ct);

    // The bytes of a file already here, never downloaded: null when there is none, or when it does not hash
    // to its name (it goes then, like any damaged file). For putting back a blob the service lost.
    public Task<byte[]?> GetCached(string hash, CancellationToken ct = default)
        => IsSha256(hash) ? Quiet(ReadCached(hash.ToLowerInvariant(), ct)) : Task.FromResult<byte[]?>(null);

    // How long a file already here is, -1 when there is none.
    public long LengthOf(string hash)
    {
        var key = hash.ToLowerInvariant();
        if (!IsSha256(key))
            return -1;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
                return entry.PlainSize;
        }
        var info = new FileInfo(ResolvePath(key));
        if (info.Exists)
            return _seal ? SealedFile.PlainLength(info.Length, SealedFile.KeyedHeaderLength) : info.Length;
        info = new FileInfo(LegacyPath(key));
        return info.Exists ? info.Length : -1;
    }

    public bool Contains(string hash)
    {
        var key = hash.ToLowerInvariant();
        if (!IsSha256(key))
            return false;
        lock (_gate)
        {
            if (_entries.ContainsKey(key))
                return true;
        }
        return File.Exists(ResolvePath(key)) || File.Exists(LegacyPath(key));
    }

    // Forgets a file and deletes it. One that is open somewhere stays, row and all, for a later trim.
    public void Remove(string hash)
    {
        var key = hash.ToLowerInvariant();
        if (!IsSha256(key))
            return;
        bool removed;
        lock (_gate)
        {
            var path = _entries.TryGetValue(key, out var entry) ? entry.Location : ResolvePath(key);
            removed = DeleteLocked(key, path);
        }
        if (removed)
            ContentRemoved?.Invoke(key);
    }

    // Forgets what a plain web link was last downloaded as, so the next read fetches it again: what a link
    // points at can change. A lumora address or a content service link names its blob by hash, which never
    // changes, and stays. True when a file went.
    public bool ForgetLink(Uri uri)
    {
        var key = KeyOf(uri, out var external);
        if (key == null || external == null)
            return false;
        bool had;
        lock (_gate)
        {
            had = _entries.ContainsKey(key);
            _missing.Remove(key);
        }
        Remove(key);
        return had;
    }

    // Whether the service said it does not have what an address names and it is not yet time to ask again.
    public bool IsMissing(Uri uri)
    {
        var key = uri.Scheme == Uri.UriSchemeFile ? null : KeyOf(uri, out _);
        if (key == null)
            return false;
        lock (_gate)
            return MissingNow(key);
    }

    public bool IsMissing(string hash)
    {
        if (!IsSha256(hash))
            return false;
        lock (_gate)
            return MissingNow(hash.ToLowerInvariant());
    }

    // Lets the next read of an address that was not there ask the service again at once. The count stays, so
    // asking by hand does not start a gone part's schedule over.
    public void ForgetMissing(Uri uri)
    {
        var key = uri.Scheme == Uri.UriSchemeFile ? null : KeyOf(uri, out _);
        if (key != null)
            AskNow(key);
    }

    public void ForgetMissing(string hash)
    {
        if (IsSha256(hash))
            AskNow(hash.ToLowerInvariant());
    }

    private void AskNow(string key)
    {
        lock (_gate)
        {
            if (_missing.TryGetValue(key, out var miss))
                _missing[key] = miss with { Due = 0 };
        }
    }

    // A "not there" for a blob fetched without this cache (a cloud texture's original goes straight into the
    // asset database): it waits on the same schedule, so every way of asking for a blob shares one. The answers
    // in a row so far.
    public int NoteMissing(string hash)
    {
        if (!IsSha256(hash))
            return 0;
        lock (_gate)
            return NoteMissingLocked(hash.ToLowerInvariant());
    }

    // The content hash an address names when it is a cloud blob (a lumora address or a content service link),
    // null for anything else.
    public static string? ContentHashOf(Uri? uri)
    {
        if (uri == null || uri.Scheme == Uri.UriSchemeFile)
            return null;
        var key = KeyOf(uri, out var external);
        return external == null ? key : null;
    }

    public void Clear()
    {
        List<string> keys;
        lock (_gate)
            keys = new List<string>(_entries.Keys);
        foreach (var key in keys)
            Remove(key);
    }

    // Trims to the capacity, the file wanted longest ago first. Files being read, or wanted within
    // MinResidency, stay.
    public Task Evict()
    {
        long cutoff = (DateTime.UtcNow - MinResidency).Ticks;
        return Task.Run(() => TrimAsync(cutoff));
    }

    // Every file with its size and age, for a trim that weighs this cache against other stores.
    public IReadOnlyList<CachedFile> Snapshot()
    {
        long cutoff = (DateTime.UtcNow - MinResidency).Ticks;
        lock (_gate)
        {
            var files = new List<CachedFile>(_entries.Count);
            foreach (var entry in _entries.Values)
            {
                files.Add(new CachedFile(entry.Key, entry.Size, new DateTime(entry.LastUsedTicks, DateTimeKind.Utc),
                    entry.Readers == 0 && entry.LastUsedTicks < cutoff));
            }
            return files;
        }
    }

    // Drops one file unless it is in use. True once it is gone.
    public bool TryEvict(string key) => TryDrop(key.ToLowerInvariant(), (DateTime.UtcNow - MinResidency).Ticks);

    private async Task TrimAsync(long cutoffTicks)
    {
        if (UsedBytes <= CapacityBytes)
            return;
        await _trimTurn.WaitAsync().ConfigureAwait(false);
        try
        {
            long capacity = CapacityBytes;
            if (UsedBytes <= capacity)
                return;
            var oldest = new List<(string Key, long Used)>();
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    if (entry.Readers == 0 && entry.LastUsedTicks < cutoffTicks)
                        oldest.Add((entry.Key, entry.LastUsedTicks));
                }
            }
            oldest.Sort((a, b) => a.Used.CompareTo(b.Used));
            foreach (var (key, _) in oldest)
            {
                if (UsedBytes <= capacity)
                    break;
                TryDrop(key, cutoffTicks);
            }
        }
        finally
        {
            _trimTurn.Release();
        }
    }

    private bool TryDrop(string key, long cutoffTicks)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Readers > 0 || entry.LastUsedTicks >= cutoffTicks)
                return false;
            if (!DeleteLocked(key, entry.Location))
                return false;
        }
        ContentRemoved?.Invoke(key);
        return true;
    }

    // Under the gate, so no download can be moving a file into the same place meanwhile.
    private bool DeleteLocked(string key, string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (_entries.Remove(key, out var entry))
            Interlocked.Add(ref _usedBytes, -entry.Size);
        return true;
    }

    private async Task<byte[]?> Read(string key, Uri? external, CancellationToken ct)
    {
        for (int round = 0; round < 3; round++)
        {
            var entry = Acquire(key);
            if (entry == null)
            {
                if (await Download(key, external, ct).ConfigureAwait(false) == null)
                    return null;
                continue;
            }

            byte[]? bytes = null;
            bool damaged = false;
            try
            {
                bytes = await SealedFile.ReadAnyAsync(entry.Location, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or EndOfStreamException)
            {
                damaged = true;
            }
            finally
            {
                Release(entry);
            }
            if (damaged)
            {
                Discard(entry);
                continue;
            }
            if (bytes == null)
            {
                Forget(entry);
                continue;
            }

            // Named by its hash: bytes that do not hash to the name are damaged or tampered with, and are
            // fetched again rather than used. Once a run per file.
            if (external == null && !entry.Verified)
            {
                if (!Matches(SHA256.HashData(bytes), key))
                {
                    Discard(entry);
                    continue;
                }
                entry.Verified = true;
            }
            return bytes;
        }
        return null;
    }

    private async Task<byte[]?> ReadCached(string key, CancellationToken ct)
    {
        var entry = Acquire(key);
        if (entry == null)
            return null;
        byte[]? bytes = null;
        bool damaged = false;
        try
        {
            bytes = await SealedFile.ReadAnyAsync(entry.Location, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or EndOfStreamException)
        {
            damaged = true;
        }
        finally
        {
            Release(entry);
        }
        if (damaged)
        {
            Discard(entry);
            return null;
        }
        if (bytes == null)
        {
            Forget(entry);
            return null;
        }
        if (!entry.Verified)
        {
            if (!Matches(SHA256.HashData(bytes), key))
            {
                Discard(entry);
                return null;
            }
            entry.Verified = true;
        }
        return bytes;
    }

    private async Task<string?> Locate(string key, Uri? external, CancellationToken ct)
    {
        for (int round = 0; round < 3; round++)
        {
            var entry = Acquire(key);
            if (entry == null)
            {
                if (await Download(key, external, ct).ConfigureAwait(false) == null)
                    return null;
                continue;
            }

            bool sound = false, damaged = false;
            try
            {
                if (external == null && !entry.Verified)
                {
                    sound = await FileMatches(entry.Location, key, ct).ConfigureAwait(false);
                    damaged = !sound;
                    entry.Verified = sound;
                }
                else
                {
                    sound = File.Exists(entry.Location);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or EndOfStreamException)
            {
                damaged = true;
            }
            finally
            {
                Release(entry);
            }
            if (sound)
                return entry.Location;
            if (damaged)
                Discard(entry);
            else
                Forget(entry);
        }
        return null;
    }

    // The row for a key with a reader counted on it, found on disk when the index has none (a file another
    // instance put there). Null when there is no file.
    private Entry? Acquire(string key)
    {
        Entry? entry;
        bool stamp = false;
        long now = DateTime.UtcNow.Ticks;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry))
            {
                var info = new FileInfo(ResolvePath(key));
                bool sealedFile = _seal;
                if (!info.Exists)
                {
                    info = new FileInfo(LegacyPath(key));
                    sealedFile = false;
                }
                if (!info.Exists)
                    return null;
                entry = new Entry(key, info.FullName, info.Length, info.LastWriteTimeUtc.Ticks) { Sealed = sealedFile };
                _entries[key] = entry;
                Interlocked.Add(ref _usedBytes, entry.Size);
            }
            entry.Readers++;
            entry.LastUsedTicks = now;
            if (now - entry.StampTicks > StampEvery.Ticks)
            {
                entry.StampTicks = now;
                stamp = true;
            }
        }
        if (stamp)
        {
            try
            {
                File.SetLastWriteTimeUtc(entry.Location, new DateTime(now, DateTimeKind.Utc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return entry;
    }

    private void Release(Entry entry)
    {
        lock (_gate)
            entry.Readers--;
    }

    // The file went missing under us: the row goes, nothing to delete.
    private void Forget(Entry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Key, out var current) && current == entry && _entries.Remove(entry.Key))
                Interlocked.Add(ref _usedBytes, -entry.Size);
        }
    }

    // The file is not what its name says: the row goes and so does the file.
    private void Discard(Entry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Key, out var current) && current == entry && _entries.Remove(entry.Key))
                Interlocked.Add(ref _usedBytes, -entry.Size);
            try
            {
                File.Delete(entry.Location);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        NexusLog.Warn($"ContentCache: {entry.Key} on disk does not hash to its name; fetching it again");
        ContentRemoved?.Invoke(entry.Key);
    }

    // One download per key however many ask at once. An asker that gives up stops waiting; the download runs
    // on for the rest and stops only when the cache closes.
    private Task<string?> Download(string key, Uri? external, CancellationToken ct)
    {
        Task<string?>? task;
        lock (_gate)
        {
            if (MissingNow(key))
                return Task.FromResult<string?>(null);
            if (!_pending.TryGetValue(key, out task))
            {
                var started = Task.Run(() => DownloadWithRetries(key, external));
                _pending[key] = task = started;
                _ = started.ContinueWith(_ =>
                {
                    lock (_gate)
                    {
                        if (_pending.TryGetValue(key, out var kept) && kept == started)
                            _pending.Remove(key);
                    }
                }, TaskScheduler.Default);
            }
        }
        return task.WaitAsync(ct);
    }

    private async Task<string?> DownloadWithRetries(string key, Uri? external)
    {
        var ct = _closing.Token;
        var delay = RetryDelay;
        int missedBefore;
        lock (_gate)
            missedBefore = _missing.TryGetValue(key, out var before) ? before.Answers : 0;
        for (int attempt = 1; ; attempt++)
        {
            string? path;
            bool transient, missing;
            try
            {
                (path, transient, missing) = await DownloadOnce(key, external, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NexusLog.Error($"ContentCache: download of {external?.ToString() ?? key} failed: {ex.Message}");
                return null;
            }
            if (path != null)
            {
                if (missedBefore > 0)
                    NexusLog.Log($"ContentCache: {external?.ToString() ?? key} is on the service now, after {missedBefore} answer(s) that it was not");
                return path;
            }
            if (missing)
            {
                int answers;
                lock (_gate)
                    answers = NoteMissingLocked(key);
                if (answers == 1)
                    NexusLog.Warn($"ContentCache: {external?.ToString() ?? key} is not on the service; next ask in {WaitAfter(1).TotalSeconds:0} s, then less often");
                else
                    NexusLog.Debug($"ContentCache: {external?.ToString() ?? key} still not on the service ({answers} answers); next ask in {WaitAfter(answers).TotalSeconds:0} s");
                return null;
            }
            if (!transient || attempt >= DownloadAttempts || ct.IsCancellationRequested)
            {
                NexusLog.Warn($"ContentCache: could not download {external?.ToString() ?? key} after {attempt} attempt(s)");
                return null;
            }
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            delay *= 3;
        }
    }

    // One try: the body into a partial file, checked, then moved into place whole. No partial file outlives
    // the try, whatever happens. Says whether another try could go differently.
    private async Task<(string? Path, bool Transient, bool Missing)> DownloadOnce(string key, Uri? external, CancellationToken ct)
    {
        string? partial = null;
        try
        {
            Directory.CreateDirectory(_partialPath);
            partial = Path.Combine(_partialPath, Guid.NewGuid().ToString("N") + PartialExtension);
            long length;
            byte[]? digest = null;
            await using (var disk = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyChunk, FileOptions.Asynchronous))
            {
                var sealing = _seal ? new SealedWriteStream(disk, null, leaveOpen: true) : null;
                Stream file = sealing != null ? sealing : disk;
                if (external == null)
                {
                    using var sha = SHA256.Create();
                    await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true))
                    {
                        var fetched = await _client.FetchContentTo(key, hashing, new Relay(this, key), ct).ConfigureAwait(false);
                        if (fetched.Failed)
                            return (null, Transient(fetched.Status) && Connectivity.IsOnline, NotThere(fetched.Status));
                        length = fetched.Data;
                        await hashing.FlushFinalBlockAsync(ct).ConfigureAwait(false);
                    }
                    digest = sha.Hash;
                }
                else
                {
                    var (copied, transient, missing) = await CopyFromUrl(key, external, file, ct).ConfigureAwait(false);
                    if (copied < 0)
                        return (null, transient, missing);
                    length = copied;
                }
                if (sealing != null)
                {
                    await sealing.CompleteAsync(ct).ConfigureAwait(false);
                    await sealing.DisposeAsync().ConfigureAwait(false);
                }
                await disk.FlushAsync(ct).ConfigureAwait(false);
                length = disk.Length;
            }

            if (digest != null && !Matches(digest, key))
            {
                NexusLog.Warn($"ContentCache: {key} came down as bytes that do not hash to it; not kept");
                return (null, true, false);
            }

            var final = ResolvePath(key);
            long landed = Land(key, partial, final, length, verified: digest != null, sealedFile: _seal);
            partial = null;
            ContentReady?.Invoke(key);
            await TrimAsync(Math.Min((DateTime.UtcNow - MinResidency).Ticks, landed)).ConfigureAwait(false);
            return (final, false, false);
        }
        catch (OperationCanceledException)
        {
            return (null, !ct.IsCancellationRequested, false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            return (null, true, false);
        }
        finally
        {
            if (partial != null)
            {
                try
                {
                    File.Delete(partial);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    // Moves a checked download into place and files its row in one step under the gate, so a trim never
    // sees the file without its row or the row without its file. Returns the moment it was filed.
    private long Land(string key, string partial, string final, long length, bool verified, bool sealedFile)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            try
            {
                File.Move(partial, final, overwrite: true);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(final))
            {
                // The file is there already and open somewhere; that copy serves, checked on its first read.
                try
                {
                    File.Delete(partial);
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
                {
                }
                length = new FileInfo(final).Length;
                verified = false;
            }

            _missing.Remove(key);
            long now = DateTime.UtcNow.Ticks;
            if (_entries.TryGetValue(key, out var entry))
            {
                // A file from before sealing, which the sealed one replaces.
                if (!string.Equals(entry.Location, final, StringComparison.OrdinalIgnoreCase) && entry.Readers == 0)
                {
                    try { File.Delete(entry.Location); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                Interlocked.Add(ref _usedBytes, length - entry.Size);
                entry.Location = final;
                entry.Sealed = sealedFile;
                entry.Size = length;
                entry.Verified = verified;
                entry.LastUsedTicks = now;
                entry.StampTicks = now;
            }
            else
            {
                _entries[key] = new Entry(key, final, length, now) { Verified = verified, StampTicks = now, Sealed = sealedFile };
                Interlocked.Add(ref _usedBytes, length);
            }
            return now;
        }
    }

    // An outside link streamed into the file. The length, or -1 and whether another try could help.
    private async Task<(long Length, bool Transient, bool Missing)> CopyFromUrl(string key, Uri url, Stream file, CancellationToken ct)
    {
        // Not through LumoraClient, so it announces itself: otherwise a texture streaming off a link would be
        // invisible to the Debug screen and the world's load readout.
        TransferRegistry.BeginDownload(key);
        bool ok = false;
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);
            using var response = await SharedHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (-1, Transient(response.StatusCode), NotThere(response.StatusCode));

            long? declared = response.Content.Headers.ContentLength;
            var state = new TransferProgress { Hash = key, TotalBytes = declared ?? 0, State = TransferState.Active };
            await using var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            var chunk = ArrayPool<byte>.Shared.Rent(CopyChunk);
            try
            {
                long received = 0;
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    int read = await body.ReadAsync(chunk.AsMemory(0, CopyChunk), stall.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    await file.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (declared is long most && received > most)
                        return (-1, true, false);
                    Report(key, state with { TransferredBytes = received });
                }
                // A body that stops short of the declared length is a broken transfer, not a smaller file.
                if (declared is long expected && received != expected)
                    return (-1, true, false);
                ok = true;
                return (received, false, false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (-1, true, false);
        }
        finally
        {
            TransferRegistry.EndDownload(key, ok);
        }
    }

    private void Report(string key, TransferProgress state)
    {
        TransferRegistry.Report(state, upload: false);
        TransferUpdated?.Invoke(key, state);
    }

    private void RaiseTransfer(string key, TransferProgress state) => TransferUpdated?.Invoke(key, state);

    private string ResolvePath(string key)
    {
        if (!_seal)
            return LegacyPath(key);
        var name = SealedKeys.HideHash(key)!;
        return Path.Combine(_storagePath, name[..2], name);
    }

    private string LegacyPath(string key) => Path.Combine(_storagePath, key[..2], key);

    private static async Task<byte[]?> ReadLocal(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        return await SealedFile.ReadAnyAsync(path, null, ct).ConfigureAwait(false);
    }

    // Every file earlier runs left, with when it was last wanted, so they trim in age order with this run's.
    // Partial files nothing is still writing are deleted.
    private void IndexExisting()
    {
        long staleBefore = (DateTime.UtcNow - StalePartial).Ticks;
        try
        {
            foreach (var folder in new DirectoryInfo(_storagePath).EnumerateDirectories())
            {
                bool partials = folder.Name == PartialFolder;
                foreach (var file in folder.EnumerateFiles())
                {
                    if (partials || file.Name.Contains(LegacyPartialMarker, StringComparison.Ordinal))
                    {
                        if (file.LastWriteTimeUtc.Ticks < staleBefore)
                        {
                            try
                            {
                                file.Delete();
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                            }
                        }
                        continue;
                    }
                    var key = file.Name.ToLowerInvariant();
                    bool sealedFile = false;
                    if (!IsSha256(key))
                    {
                        key = _seal ? SealedKeys.RevealHash(file.Name)! : null!;
                        if (key == null)
                            continue;
                        sealedFile = true;
                    }
                    if (_entries.TryGetValue(key, out var had))
                    {
                        // Both a file from before sealing and its sealed copy: the sealed one counts.
                        if (!sealedFile || had.Sealed)
                            continue;
                        _usedBytes -= had.Size;
                    }
                    long used = file.LastWriteTimeUtc.Ticks;
                    _entries[key] = new Entry(key, file.FullName, file.Length, used) { StampTicks = used, Sealed = sealedFile };
                    _usedBytes += file.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NexusLog.Warn($"ContentCache: could not index '{_storagePath}': {ex.Message}");
        }
    }

    // The key a file is kept under. A lumora address and a link to the content service name a blob by its
    // hash and are checked as that blob; any other link is kept under the hash of the link itself.
    private static string? KeyOf(Uri uri, out Uri? external)
    {
        external = null;
        if (uri.Scheme == ContentHash.Scheme)
        {
            var hash = ContentHash.ParseHash(uri);
            return IsSha256(hash) ? hash.ToLowerInvariant() : null;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (ServiceHash(uri) is { } blob)
            return blob;
        external = uri;
        return ContentHash.FromString(uri.ToString());
    }

    private static string? ServiceHash(Uri uri)
    {
        var text = uri.GetLeftPart(UriPartial.Path);
        var config = ServiceConfig.Current;
        foreach (var root in new[] { config.CdnBase, config.ContentBase })
        {
            var prefix = root.TrimEnd('/') + "/content/";
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = text.Substring(prefix.Length);
            int dot = name.IndexOf('.');
            if (dot >= 0)
                name = name[..dot];
            return IsSha256(name) ? name.ToLowerInvariant() : null;
        }
        return null;
    }

    private static bool IsSha256(string? text)
    {
        if (text == null || text.Length != 64)
            return false;
        foreach (char c in text)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }
        return true;
    }

    private static bool Matches(byte[] digest, string key)
        => string.Equals(Convert.ToHexString(digest), key, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> FileMatches(string path, string key, CancellationToken ct)
    {
        await using var stream = SealedFile.OpenAny(path);
        return Matches(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false), key);
    }

    // SEALING OLDER FILES
    //
    // A file from before sealing, sealed under its hidden name and moved in under the gate exactly like a
    // download landing; the old file goes once nobody is reading it. One at a time, at the caller's pace.
    public async Task<(int Files, long Bytes)> SealOlderFilesAsync(Func<long, Task> pace, CancellationToken ct)
    {
        if (!_seal)
            return (0, 0);
        int count = 0;
        long bytes = 0;
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        while (!ct.IsCancellationRequested && !_closing.IsCancellationRequested)
        {
            Entry? next = null;
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    if (!entry.Sealed && entry.Readers == 0 && !skipped.Contains(entry.Key))
                    {
                        next = entry;
                        break;
                    }
                }
            }
            if (next == null)
                break;
            skipped.Add(next.Key);
            string? partial = null;
            try
            {
                string old = next.Location;
                var plain = await File.ReadAllBytesAsync(old, ct).ConfigureAwait(false);
                if (SealedFile.HasMagic(plain))
                    continue;
                Directory.CreateDirectory(_partialPath);
                partial = Path.Combine(_partialPath, Guid.NewGuid().ToString("N") + PartialExtension);
                await SealedFile.WriteAllBytesAsync(partial, plain, null, ct).ConfigureAwait(false);
                long length = new FileInfo(partial).Length;
                bool current;
                lock (_gate)
                    current = _entries.TryGetValue(next.Key, out var now) && now == next && !now.Sealed;
                if (!current)
                    continue;
                Land(next.Key, partial, ResolvePath(next.Key), length, next.Verified, sealedFile: true);
                partial = null;
                if (File.Exists(old))
                {
                    try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                count++;
                bytes += plain.LongLength;
                await pace(plain.LongLength).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                if (partial != null)
                {
                    try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        return (count, bytes);
    }

    private static bool Transient(HttpStatusCode status)
        => (int)status >= 500 || status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests;

    // The service's own answer that it has nothing under that name, as against a failure to ask it.
    private static bool NotThere(HttpStatusCode status) => status is HttpStatusCode.NotFound or HttpStatusCode.Gone;

    // An asker that gave up, or a disk that would not read, is no answer rather than an exception.
    private static async Task<T?> Quiet<T>(Task<T?> work) where T : class
    {
        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NexusLog.Warn($"ContentCache: read failed: {ex.Message}");
            return null;
        }
    }

    // One client for every outside link, so connections are pooled instead of opened per download.
    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class Relay : IProgress<TransferProgress>
    {
        private readonly ContentCache _cache;
        private readonly string _key;

        public Relay(ContentCache cache, string key)
        {
            _cache = cache;
            _key = key;
        }

        public void Report(TransferProgress value) => _cache.RaiseTransfer(_key, value);
    }

    private sealed class Entry
    {
        public readonly string Key;
        public string Location;
        // What the file takes on disk, which a trim weighs; a sealed file's plaintext is a little less.
        public long Size;
        public bool Sealed;
        public long LastUsedTicks;
        public long StampTicks;
        public int Readers;
        public bool Verified;

        public Entry(string key, string location, long size, long lastUsedTicks)
        {
            Key = key;
            Location = location;
            Size = size;
            LastUsedTicks = lastUsedTicks;
        }

        public long PlainSize => Sealed ? SealedFile.PlainLength(Size, SealedFile.KeyedHeaderLength) : Size;
    }
}
