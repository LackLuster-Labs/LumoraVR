using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

/// <summary>
/// Central asset management system for loading, caching, and managing assets.
/// Provides reference counting, caching, and async loading capabilities.
/// </summary>
public class AssetManager : IDisposable
{
    private class AssetEntry
    {
        public string Uri { get; set; }
        public object Asset { get; set; }
        public Type AssetType { get; set; }
        public int ReferenceCount { get; set; }
        public DateTime LastAccessed { get; set; }
        public long SizeInBytes { get; set; }
        public AssetLoadState State { get; set; }
        public List<Action<object>> LoadCallbacks { get; set; } = new List<Action<object>>();
    }

    public enum AssetLoadState
    {
        NotLoaded,
        Loading,
        Loaded,
        Failed
    }

    private readonly Dictionary<string, AssetEntry> _assetCache = new Dictionary<string, AssetEntry>();
    private readonly Queue<AssetEntry> _loadQueue = new Queue<AssetEntry>();
    private readonly object _cacheLock = new object();

    private bool _initialized = false;
    private bool _disposing = false;
    private long _totalCacheSize = 0;
    private long _maxCacheSize = 512 * 1024 * 1024; // 512 MB default

    // Asset loaders
    private readonly Dictionary<string, IAssetLoader> _loaders = new Dictionary<string, IAssetLoader>();

    // Statistics
    public int TotalAssetsLoaded { get; private set; }
    public int CurrentAssetCount => _assetCache.Count;
    public long CurrentCacheSize => _totalCacheSize;
    public long MaxCacheSize
    {
        get => _maxCacheSize;
        set => _maxCacheSize = value;
    }

    /// <summary>
    /// Initialize the asset manager.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        // Register default asset loaders
        RegisterLoader("texture", new TextureAssetLoader());
        RegisterLoader("mesh", new MeshAssetLoader());
        RegisterLoader("audio", new AudioAssetLoader());
        RegisterLoader("material", new MaterialAssetLoader());
        RegisterLoader("shader", new ShaderAssetLoader());

        _initialized = true;

        await Task.CompletedTask;
        Logger.Log("AssetManager: Initialized with default loaders");
    }

    /// <summary>
    /// Register a custom asset loader for a specific asset type.
    /// </summary>
    public void RegisterLoader(string assetType, IAssetLoader loader)
    {
        _loaders[assetType.ToLower()] = loader;
        Logger.Log($"AssetManager: Registered loader for '{assetType}' assets");
    }

    /// <summary>
    /// Load an asset asynchronously.
    /// </summary>
    public async Task<T> LoadAssetAsync<T>(string uri) where T : class
    {
        if (string.IsNullOrEmpty(uri))
            return null;

        // Check cache first
        TaskCompletionSource<T> loadingTask = null;
        lock (_cacheLock)
        {
            // Check if already loaded
            if (_assetCache.TryGetValue(uri, out var existingEntry))
            {
                if (existingEntry.State == AssetLoadState.Loaded)
                {
                    existingEntry.ReferenceCount++;
                    existingEntry.LastAccessed = DateTime.Now;
                    return existingEntry.Asset as T;
                }
                else if (existingEntry.State == AssetLoadState.Loading)
                {
                    // Asset is already being loaded, wait for it
                    loadingTask = new TaskCompletionSource<T>();
                    existingEntry.LoadCallbacks.Add(asset => loadingTask.SetResult(asset as T));
                }
            }
        }

        // Wait for loading task if it exists (outside of lock)
        if (loadingTask != null)
        {
            return await loadingTask.Task;
        }

        // Create new entry
        var entry = new AssetEntry
        {
            Uri = uri,
            AssetType = typeof(T),
            State = AssetLoadState.Loading,
            ReferenceCount = 1,
            LastAccessed = DateTime.Now
        };

        lock (_cacheLock)
        {
            _assetCache[uri] = entry;
        }

        try
        {
            // Determine loader based on URI or type
            var loader = GetLoaderForAsset(uri, typeof(T));
            if (loader == null)
            {
                throw new NotSupportedException($"No loader registered for asset type: {typeof(T).Name}");
            }

            // Load the asset
            var asset = await loader.LoadAsync<T>(uri);

            if (asset != null)
            {
                entry.Asset = asset;
                entry.State = AssetLoadState.Loaded;
                entry.SizeInBytes = loader.EstimateSize(asset);

                lock (_cacheLock)
                {
                    _totalCacheSize += entry.SizeInBytes;
                    TotalAssetsLoaded++;

                    // Trigger callbacks
                    foreach (var callback in entry.LoadCallbacks)
                    {
                        callback?.Invoke(asset);
                    }
                    entry.LoadCallbacks.Clear();
                }

                // Check cache size and evict if necessary
                await EnsureCacheSizeAsync();

                Logger.Log($"AssetManager: Loaded '{uri}' ({entry.SizeInBytes / 1024}KB)");
                return asset;
            }
            else
            {
                entry.State = AssetLoadState.Failed;
                Logger.Error($"AssetManager: Failed to load '{uri}'");
                return null;
            }
        }
        catch (Exception ex)
        {
            entry.State = AssetLoadState.Failed;
            Logger.Error($"AssetManager: Exception loading '{uri}': {ex.Message}");

            lock (_cacheLock)
            {
                _assetCache.Remove(uri);
            }

            throw;
        }
    }

    /// <summary>
    /// Get an asset if it's already loaded, otherwise return null.
    /// </summary>
    public T GetAsset<T>(string uri) where T : class
    {
        if (string.IsNullOrEmpty(uri))
            return null;

        lock (_cacheLock)
        {
            if (_assetCache.TryGetValue(uri, out var entry) && entry.State == AssetLoadState.Loaded)
            {
                entry.LastAccessed = DateTime.Now;
                return entry.Asset as T;
            }
        }

        return null;
    }

    /// <summary>
    /// Release a reference to an asset.
    /// </summary>
    public void ReleaseAsset(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return;

        lock (_cacheLock)
        {
            if (_assetCache.TryGetValue(uri, out var entry))
            {
                entry.ReferenceCount--;

                if (entry.ReferenceCount <= 0)
                {
                    // Mark for potential eviction but don't remove immediately
                    entry.ReferenceCount = 0;
                }
            }
        }
    }

    /// <summary>
    /// Preload multiple assets.
    /// </summary>
    public async Task PreloadAssetsAsync<T>(params string[] uris) where T : class
    {
        var tasks = new List<Task<T>>();

        foreach (var uri in uris)
        {
            tasks.Add(LoadAssetAsync<T>(uri));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Update the asset manager (process load queue, etc).
    /// </summary>
    public void Update(float deltaTime)
    {
        // Process any pending operations
        // This could be extended to handle progressive loading, streaming, etc.
    }

    /// <summary>
    /// Clear all cached assets.
    /// </summary>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            foreach (var entry in _assetCache.Values)
            {
                if (entry.Asset is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _assetCache.Clear();
            _totalCacheSize = 0;
            Logger.Log("AssetManager: Cache cleared");
        }
    }

    /// <summary>
    /// Ensure cache size is within limits by evicting old assets.
    /// </summary>
    private async Task EnsureCacheSizeAsync()
    {
        if (_totalCacheSize <= _maxCacheSize)
            return;

        await Task.Run(() =>
        {
            lock (_cacheLock)
            {
                // Sort by last accessed time and reference count
                var sortedEntries = new List<AssetEntry>(_assetCache.Values);
                sortedEntries.Sort((a, b) =>
                {
                    // Keep referenced assets
                    if (a.ReferenceCount > 0 && b.ReferenceCount == 0) return 1;
                    if (a.ReferenceCount == 0 && b.ReferenceCount > 0) return -1;

                    // Sort by last accessed
                    return a.LastAccessed.CompareTo(b.LastAccessed);
                });

                // Evict oldest unreferenced assets
                foreach (var entry in sortedEntries)
                {
                    if (_totalCacheSize <= _maxCacheSize)
                        break;

                    if (entry.ReferenceCount == 0)
                    {
                        _assetCache.Remove(entry.Uri);
                        _totalCacheSize -= entry.SizeInBytes;

                        if (entry.Asset is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }

                        Logger.Log($"AssetManager: Evicted '{entry.Uri}' from cache");
                    }
                }
            }
        });
    }

    /// <summary>
    /// Get the appropriate loader for an asset.
    /// </summary>
    private IAssetLoader GetLoaderForAsset(string uri, Type assetType)
    {
        // Determine by file extension
        var extension = Path.GetExtension(uri)?.ToLower();

        switch (extension)
        {
            case ".png":
            case ".jpg":
            case ".jpeg":
            case ".bmp":
            case ".tga":
                return _loaders.GetValueOrDefault("texture");

            case ".obj":
            case ".fbx":
            case ".gltf":
            case ".glb":
                return _loaders.GetValueOrDefault("mesh");

            case ".wav":
            case ".mp3":
            case ".ogg":
                return _loaders.GetValueOrDefault("audio");

            case ".mat":
                return _loaders.GetValueOrDefault("material");

            case ".shader":
            case ".glsl":
                return _loaders.GetValueOrDefault("shader");
        }

        // Determine by type name
        var typeName = assetType.Name.ToLower();

        if (typeName.Contains("texture"))
            return _loaders.GetValueOrDefault("texture");
        if (typeName.Contains("mesh"))
            return _loaders.GetValueOrDefault("mesh");
        if (typeName.Contains("audio"))
            return _loaders.GetValueOrDefault("audio");
        if (typeName.Contains("material"))
            return _loaders.GetValueOrDefault("material");
        if (typeName.Contains("shader"))
            return _loaders.GetValueOrDefault("shader");

        return null;
    }

    /// <summary>
    /// Dispose of the asset manager.
    /// </summary>
    public void Dispose()
    {
        if (_disposing)
            return;

        _disposing = true;
        ClearCache();
        _loaders.Clear();
        _initialized = false;

        Logger.Log("AssetManager: Disposed");
    }
}

/// <summary>
/// Interface for asset loaders.
/// </summary>
public interface IAssetLoader
{
    Task<T> LoadAsync<T>(string uri) where T : class;
    long EstimateSize(object asset);
}

/// <summary>
/// Base implementation for asset loaders.
/// </summary>
public abstract class BaseAssetLoader : IAssetLoader
{
    public abstract Task<T> LoadAsync<T>(string uri) where T : class;

    public virtual long EstimateSize(object asset)
    {
        // Default estimation
        return 1024; // 1KB minimum
    }
}

/// <summary>
/// Texture asset loader.
/// </summary>
public class TextureAssetLoader : BaseAssetLoader
{
    public override async Task<T> LoadAsync<T>(string uri) where T : class
    {
        // Placeholder implementation
        await Task.Delay(10); // Simulate loading
        return default(T);
    }

    public override long EstimateSize(object asset)
    {
        // Estimate based on texture dimensions
        return 1024 * 1024; // 1MB placeholder
    }
}

/// <summary>
/// Mesh asset loader.
/// </summary>
public class MeshAssetLoader : BaseAssetLoader
{
    public override async Task<T> LoadAsync<T>(string uri) where T : class
    {
        // Placeholder implementation
        await Task.Delay(10); // Simulate loading
        return default(T);
    }

    public override long EstimateSize(object asset)
    {
        // Estimate based on vertex count
        return 512 * 1024; // 512KB placeholder
    }
}

/// <summary>
/// Audio asset loader.
/// </summary>
public class AudioAssetLoader : BaseAssetLoader
{
    public override async Task<T> LoadAsync<T>(string uri) where T : class
    {
        // Placeholder implementation
        await Task.Delay(10); // Simulate loading
        return default(T);
    }

    public override long EstimateSize(object asset)
    {
        // Estimate based on duration and sample rate
        return 2 * 1024 * 1024; // 2MB placeholder
    }
}

/// <summary>
/// Material asset loader.
/// </summary>
public class MaterialAssetLoader : BaseAssetLoader
{
    public override async Task<T> LoadAsync<T>(string uri) where T : class
    {
        // Placeholder implementation
        await Task.Delay(10); // Simulate loading
        return default(T);
    }

    public override long EstimateSize(object asset)
    {
        return 4 * 1024; // 4KB placeholder
    }
}

/// <summary>
/// Shader asset loader.
/// </summary>
public class ShaderAssetLoader : BaseAssetLoader
{
    public override async Task<T> LoadAsync<T>(string uri) where T : class
    {
        // Placeholder implementation
        await Task.Delay(10); // Simulate loading
        return default(T);
    }

    public override long EstimateSize(object asset)
    {
        return 8 * 1024; // 8KB placeholder
    }
}