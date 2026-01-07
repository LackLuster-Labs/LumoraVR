using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

/// <summary>
/// Local database for storing imported assets.
/// Provides local:// URI scheme for locally stored assets.
/// </summary>
public class LocalDB : IDisposable
{
    public enum ImportLocation
    {
        /// <summary>Reference file at original location</summary>
        Original,
        /// <summary>Copy file to local cache</summary>
        Copy,
        /// <summary>Move file to local cache</summary>
        Move
    }

    private readonly string _basePath;
    private readonly string _machineId;
    private readonly Dictionary<string, LocalAssetRecord> _assetRecords = new();
    private readonly object _lock = new();
    private bool _initialized;

    /// <summary>
    /// Get the machine-unique ID for this local database.
    /// </summary>
    public string MachineId => _machineId;

    /// <summary>
    /// Get the base path for local asset storage.
    /// </summary>
    public string BasePath => _basePath;

    public LocalDB(string basePath = null)
    {
        _basePath = basePath ?? GetDefaultBasePath();
        _machineId = GetOrCreateMachineId();
    }

    /// <summary>
    /// Initialize the local database.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        // Ensure directories exist
        Directory.CreateDirectory(_basePath);
        Directory.CreateDirectory(GetAssetCachePath());
        Directory.CreateDirectory(GetTempPath());

        // Load existing asset records
        await LoadAssetRecordsAsync();

        _initialized = true;
        Logger.Log($"LocalDB: Initialized at '{_basePath}' with machine ID '{_machineId}'");
    }

    /// <summary>
    /// Import a local file into the asset database.
    /// </summary>
    public async Task<string> ImportLocalAssetAsync(string filePath, ImportLocation location = ImportLocation.Copy)
    {
        if (!File.Exists(filePath))
        {
            Logger.Error($"LocalDB: File not found: {filePath}");
            return null;
        }

        // Calculate content hash for deduplication
        var hash = await ComputeFileHashAsync(filePath);
        var localUri = $"local://{_machineId}/{hash}";

        lock (_lock)
        {
            // Check if already imported
            if (_assetRecords.TryGetValue(hash, out var existing))
            {
                Logger.Log($"LocalDB: Asset already imported: {localUri}");
                return localUri;
            }
        }

        // Determine target path
        var extension = Path.GetExtension(filePath);
        var targetPath = Path.Combine(GetAssetCachePath(), hash + extension);

        try
        {
            switch (location)
            {
                case ImportLocation.Original:
                    // Just reference the original file
                    targetPath = filePath;
                    break;

                case ImportLocation.Copy:
                    await Task.Run(() => File.Copy(filePath, targetPath, true));
                    break;

                case ImportLocation.Move:
                    await Task.Run(() => File.Move(filePath, targetPath, true));
                    break;
            }

            // Create asset record
            var record = new LocalAssetRecord
            {
                Hash = hash,
                LocalUri = localUri,
                FilePath = targetPath,
                OriginalPath = filePath,
                OriginalFileName = Path.GetFileName(filePath),
                ImportedAt = DateTime.UtcNow,
                FileSize = new FileInfo(targetPath).Length
            };

            lock (_lock)
            {
                _assetRecords[hash] = record;
            }

            // Save records
            await SaveAssetRecordsAsync();

            Logger.Log($"LocalDB: Imported '{Path.GetFileName(filePath)}' -> {localUri}");
            return localUri;
        }
        catch (Exception ex)
        {
            Logger.Error($"LocalDB: Failed to import '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get the local file path for a local:// URI.
    /// </summary>
    public string GetFilePath(string localUri)
    {
        if (!localUri.StartsWith("local://"))
            return null;

        // Parse URI: local://[machineId]/[hash]
        var parts = localUri.Substring(8).Split('/');
        if (parts.Length < 2)
            return null;

        var hash = parts[1];

        lock (_lock)
        {
            if (_assetRecords.TryGetValue(hash, out var record))
            {
                return record.FilePath;
            }
        }

        return null;
    }

    /// <summary>
    /// Check if a local URI exists in the database.
    /// </summary>
    public bool Exists(string localUri)
    {
        var path = GetFilePath(localUri);
        return path != null && File.Exists(path);
    }

    /// <summary>
    /// Get a temporary file path for import operations.
    /// </summary>
    public string GetTempFilePath(string extension = null)
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

    /// <summary>
    /// Clean up old temporary files.
    /// </summary>
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

    /// <summary>
    /// Get all imported asset records.
    /// </summary>
    public IReadOnlyList<LocalAssetRecord> GetAllRecords()
    {
        lock (_lock)
        {
            return new List<LocalAssetRecord>(_assetRecords.Values);
        }
    }

    private string GetDefaultBasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "LumoraVR", "LocalDB");
    }

    private string GetAssetCachePath() => Path.Combine(_basePath, "Assets");
    private string GetTempPath() => Path.Combine(_basePath, "Temp");

    private string GetOrCreateMachineId()
    {
        var idPath = Path.Combine(_basePath, ".machine_id");

        try
        {
            Directory.CreateDirectory(_basePath);

            if (File.Exists(idPath))
            {
                return File.ReadAllText(idPath).Trim();
            }

            // Generate new machine ID
            var id = Guid.NewGuid().ToString("N").Substring(0, 16);
            File.WriteAllText(idPath, id);
            return id;
        }
        catch
        {
            // Fallback to random ID if file operations fail
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }
    }

    private async Task<string> ComputeFileHashAsync(string filePath)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await Task.Run(() => sha256.ComputeHash(stream));
        return Convert.ToHexString(hashBytes).ToLower();
    }

    private async Task LoadAssetRecordsAsync()
    {
        var recordsPath = Path.Combine(_basePath, "records.json");
        if (!File.Exists(recordsPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(recordsPath);
            // Simple JSON parsing (in production, use System.Text.Json)
            // For now, just log that we would load records
            Logger.Log("LocalDB: Would load asset records from JSON");
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocalDB: Failed to load records: {ex.Message}");
        }
    }

    private async Task SaveAssetRecordsAsync()
    {
        var recordsPath = Path.Combine(_basePath, "records.json");
        try
        {
            // Simple JSON serialization (in production, use System.Text.Json)
            var sb = new StringBuilder();
            sb.AppendLine("{\"records\":[");

            lock (_lock)
            {
                var first = true;
                foreach (var record in _assetRecords.Values)
                {
                    if (!first) sb.AppendLine(",");
                    first = false;
                    sb.Append($"  {{\"hash\":\"{record.Hash}\",\"uri\":\"{record.LocalUri}\",\"path\":\"{record.FilePath.Replace("\\", "\\\\")}\",\"name\":\"{record.OriginalFileName}\"}}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("]}");

            await File.WriteAllTextAsync(recordsPath, sb.ToString());
        }
        catch (Exception ex)
        {
            Logger.Warn($"LocalDB: Failed to save records: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Cleanup temp files older than 1 hour on dispose
        CleanupTempFiles(TimeSpan.FromHours(1));
    }
}

/// <summary>
/// Record of a locally imported asset.
/// </summary>
public class LocalAssetRecord
{
    public string Hash { get; set; }
    public string LocalUri { get; set; }
    public string FilePath { get; set; }
    public string OriginalPath { get; set; }
    public string OriginalFileName { get; set; }
    public DateTime ImportedAt { get; set; }
    public long FileSize { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}
