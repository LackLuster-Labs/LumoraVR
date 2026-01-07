// local cache with disk storage and lru eviction

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.CDN;

public sealed class ContentCache : IDisposable
{
    private readonly LumoraClient _client;
    private readonly string _storagePath;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _pending = new();
    private readonly SemaphoreSlim _evictionLock = new(1, 1);

    private long _usedBytes;
    private long _capacityBytes = 10L * 1024 * 1024 * 1024; // 10GB default

    public string StoragePath => _storagePath;
    public long UsedBytes => Interlocked.Read(ref _usedBytes);
    public long CapacityBytes
    {
        get => _capacityBytes;
        set => _capacityBytes = Math.Max(0, value);
    }
    public int EntryCount => _entries.Count;
    public double UsageRatio => _capacityBytes > 0 ? (double)UsedBytes / _capacityBytes : 0;

    public event Action<string, TransferProgress>? TransferUpdated;
    public event Action<string>? ContentReady;
    public event Action<string>? ContentRemoved;

    public ContentCache(LumoraClient client, string storagePath)
    {
        _client = client;
        _storagePath = storagePath;
        Directory.CreateDirectory(_storagePath);
        IndexExisting();
    }

    public void Dispose() => _evictionLock.Dispose();

    // get content by uri
    public Task<byte[]?> Get(Uri uri, CancellationToken ct = default)
    {
        if (uri.Scheme == "file")
            return ReadFile(uri.LocalPath, ct);

        if (uri.Scheme == ContentHash.Scheme)
            return Get(ContentHash.ParseHash(uri), ct);

        if (uri.Scheme is "http" or "https")
        {
            var hash = ContentHash.FromString(uri.ToString());
            return FetchExternal(hash, uri, ct);
        }

        return Task.FromResult<byte[]?>(null);
    }

    // get content by hash
    public async Task<byte[]?> Get(string hash, CancellationToken ct = default)
    {
        // check memory first
        if (_entries.TryGetValue(hash, out var entry))
        {
            entry.Touch();
            return entry.Bytes;
        }

        // check disk
        var diskPath = ResolvePath(hash);
        if (File.Exists(diskPath))
        {
            var bytes = await File.ReadAllBytesAsync(diskPath, ct);
            Store(hash, bytes, persisted: true);
            return bytes;
        }

        // dedupe concurrent requests
        if (_pending.TryGetValue(hash, out var existing))
            return await existing;

        var task = FetchFromCloud(hash, ct);
        _pending[hash] = task;

        try
        {
            return await task;
        }
        finally
        {
            _pending.TryRemove(hash, out _);
        }
    }

    // prefetch without returning
    public Task Prefetch(string hash, CancellationToken ct = default) => Get(hash, ct);
    public Task Prefetch(Uri uri, CancellationToken ct = default) => Get(uri, ct);

    // check if cached
    public bool Contains(string hash)
        => _entries.ContainsKey(hash) || File.Exists(ResolvePath(hash));

    // yeet from cache
    public void Remove(string hash)
    {
        if (_entries.TryRemove(hash, out var entry))
            Interlocked.Add(ref _usedBytes, -entry.Bytes.Length);

        var path = ResolvePath(hash);
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }

        ContentRemoved?.Invoke(hash);
    }

    // nuke everything
    public void Clear()
    {
        foreach (var hash in _entries.Keys.ToArray())
            Remove(hash);
    }

    // evict old shit to stay under capacity
    public async Task Evict()
    {
        if (!await _evictionLock.WaitAsync(0))
            return;

        try
        {
            while (UsedBytes > _capacityBytes && _entries.Count > 0)
            {
                string? oldest = null;
                var oldestTime = DateTime.MaxValue;

                foreach (var (hash, entry) in _entries)
                {
                    if (entry.LastAccess < oldestTime)
                    {
                        oldestTime = entry.LastAccess;
                        oldest = hash;
                    }
                }

                if (oldest != null)
                    Remove(oldest);
                else
                    break;
            }
        }
        finally
        {
            _evictionLock.Release();
        }
    }

    private async Task<byte[]?> FetchFromCloud(string hash, CancellationToken ct)
    {
        var progress = new Progress<TransferProgress>(p => TransferUpdated?.Invoke(hash, p));
        var result = await _client.FetchContent(hash, progress, ct);

        if (!result.Success || result.Data == null)
            return null;

        Store(hash, result.Data, persisted: false);
        await Persist(hash, result.Data);

        ContentReady?.Invoke(hash);
        return result.Data;
    }

    private async Task<byte[]?> FetchExternal(string hash, Uri uri, CancellationToken ct)
    {
        // check caches
        if (_entries.TryGetValue(hash, out var entry))
        {
            entry.Touch();
            return entry.Bytes;
        }

        var diskPath = ResolvePath(hash);
        if (File.Exists(diskPath))
        {
            var cached = await File.ReadAllBytesAsync(diskPath, ct);
            Store(hash, cached, persisted: true);
            return cached;
        }

        // dedupe
        if (_pending.TryGetValue(hash, out var pending))
            return await pending;

        var task = DownloadExternal(hash, uri, ct);
        _pending[hash] = task;

        try
        {
            return await task;
        }
        finally
        {
            _pending.TryRemove(hash, out _);
        }
    }

    private async Task<byte[]?> DownloadExternal(string hash, Uri uri, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(uri, ct);

            Store(hash, bytes, persisted: false);
            await Persist(hash, bytes);

            ContentReady?.Invoke(hash);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private void Store(string hash, byte[] bytes, bool persisted)
    {
        var entry = new CacheEntry(hash, bytes, persisted);

        if (_entries.TryAdd(hash, entry))
            Interlocked.Add(ref _usedBytes, bytes.Length);

        if (UsedBytes > _capacityBytes)
            _ = Evict();
    }

    private async Task Persist(string hash, byte[] bytes)
    {
        var path = ResolvePath(hash);
        var dir = Path.GetDirectoryName(path);

        if (dir != null)
            Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(path, bytes);
    }

    // shard into subdirs using first 2 chars
    private string ResolvePath(string hash)
    {
        var shard = hash.Length >= 2 ? hash[..2] : "00";
        return Path.Combine(_storagePath, shard, hash);
    }

    private async Task<byte[]?> ReadFile(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        return await File.ReadAllBytesAsync(path, ct);
    }

    private void IndexExisting()
    {
        if (!Directory.Exists(_storagePath))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(_storagePath, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                Interlocked.Add(ref _usedBytes, info.Length);
            }
        }
        catch { }
    }

    private sealed class CacheEntry
    {
        public string Hash { get; }
        public byte[] Bytes { get; }
        public DateTime LastAccess { get; private set; }
        public bool Persisted { get; }

        public CacheEntry(string hash, byte[] bytes, bool persisted)
        {
            Hash = hash;
            Bytes = bytes;
            Persisted = persisted;
            LastAccess = DateTime.UtcNow;
        }

        public void Touch() => LastAccess = DateTime.UtcNow;
    }
}
