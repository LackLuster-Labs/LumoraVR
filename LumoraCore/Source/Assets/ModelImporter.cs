using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

/// <summary>
/// Settings for model import.
/// </summary>
public class ModelImportSettings
{
    /// <summary>Scale factor to apply during import.</summary>
    public float Scale { get; set; } = 1.0f;

    /// <summary>Whether to import bones/skeleton.</summary>
    public bool ImportBones { get; set; } = true;

    /// <summary>Whether to import animations.</summary>
    public bool ImportAnimations { get; set; } = true;

    /// <summary>Whether to import materials.</summary>
    public bool ImportMaterials { get; set; } = true;

    /// <summary>Whether to generate colliders.</summary>
    public bool GenerateColliders { get; set; } = false;

    /// <summary>Whether to setup IK for humanoid avatars.</summary>
    public bool SetupIK { get; set; } = true;

    /// <summary>Whether to center the model.</summary>
    public bool Center { get; set; } = true;

    /// <summary>Whether to rescale to standard height.</summary>
    public bool Rescale { get; set; } = true;

    /// <summary>Target height when rescaling.</summary>
    public float TargetHeight { get; set; } = 1.7f;

    /// <summary>Whether to force T-pose for humanoids.</summary>
    public bool ForceTpose { get; set; } = false;

    /// <summary>Whether this is an avatar import.</summary>
    public bool IsAvatarImport { get; set; } = false;
}

/// <summary>
/// Result of a model import operation.
/// </summary>
public class ModelImportResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
    public Slot RootSlot { get; set; }
    public SkeletonBuilder Skeleton { get; set; }
    public List<SkinnedMeshRenderer> SkinnedMeshes { get; set; } = new();
    public string LocalUri { get; set; }
}

/// <summary>
/// Imports 3D models using Godot's GLTFDocument.
/// </summary>
public static class ModelImporter
{
    /// <summary>
    /// Supported model file extensions.
    /// </summary>
    public static readonly string[] SupportedExtensions = new[]
    {
        ".glb", ".gltf", ".vrm"
    };

    /// <summary>
    /// Check if a file is a supported model format.
    /// </summary>
    public static bool IsSupportedFormat(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        foreach (var ext in SupportedExtensions)
        {
            if (extension == ext)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Import a model file and create the slot hierarchy.
    /// This is the core API method called from Godot side.
    /// </summary>
    public static async Task<ModelImportResult> ImportModelAsync(
        string filePath,
        Slot targetSlot,
        ModelImportSettings settings = null,
        LocalDB localDB = null,
        IProgress<(float progress, string status)> progress = null)
    {
        settings ??= new ModelImportSettings();
        var result = new ModelImportResult();

        if (!File.Exists(filePath))
        {
            result.ErrorMessage = $"File not found: {filePath}";
            Logger.Error($"ModelImporter: {result.ErrorMessage}");
            return result;
        }

        if (!IsSupportedFormat(filePath))
        {
            result.ErrorMessage = $"Unsupported format: {Path.GetExtension(filePath)}";
            Logger.Error($"ModelImporter: {result.ErrorMessage}");
            return result;
        }

        try
        {
            progress?.Report((0.1f, "Reading model file..."));

            // Import to LocalDB if provided
            string localUri = null;
            if (localDB != null)
            {
                localUri = await localDB.ImportLocalAssetAsync(filePath, LocalDB.ImportLocation.Copy);
                result.LocalUri = localUri;
            }

            progress?.Report((0.2f, "Creating slot structure..."));

            // Create model root slot
            var modelName = Path.GetFileNameWithoutExtension(filePath);
            var modelSlot = targetSlot.AddSlot(modelName);
            result.RootSlot = modelSlot;

            // Store the file path and local URI on the slot for the hook to use
            var modelData = modelSlot.AttachComponent<ModelData>();
            modelData.SourcePath.Value = filePath;
            modelData.LocalUri.Value = localUri ?? "";
            modelData.ImportSettings = settings;

            // The actual GLTF loading happens in the Godot hook (ModelDataHook)
            // This ensures we're on the main thread when creating Godot nodes

            progress?.Report((0.5f, "Setting up model structure..."));

            // If this is an avatar import, mark it as such
            if (settings.IsAvatarImport)
            {
                modelData.IsAvatar.Value = true;
            }

            progress?.Report((1.0f, "Import complete!"));

            result.Success = true;
            Logger.Log($"ModelImporter: Successfully queued import for '{modelName}'");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            Logger.Error($"ModelImporter: Failed to import '{filePath}': {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Import a model as an avatar.
    /// Sets up IK, skeleton, and avatar-specific components.
    /// </summary>
    public static async Task<ModelImportResult> ImportAvatarAsync(
        string filePath,
        Slot targetSlot,
        LocalDB localDB = null,
        IProgress<(float progress, string status)> progress = null)
    {
        var settings = new ModelImportSettings
        {
            ImportBones = true,
            ImportAnimations = false,
            SetupIK = true,
            IsAvatarImport = true,
            Rescale = true,
            TargetHeight = 1.7f
        };

        return await ImportModelAsync(filePath, targetSlot, settings, localDB, progress);
    }
}

/// <summary>
/// Component that holds imported model data.
/// The Godot hook will use this to load the actual GLTF.
/// </summary>
[ComponentCategory("Assets")]
public class ModelData : ImplementableComponent
{
    /// <summary>Original source file path.</summary>
    public Sync<string> SourcePath { get; private set; }

    /// <summary>Local URI after import.</summary>
    public Sync<string> LocalUri { get; private set; }

    /// <summary>Whether this model is an avatar.</summary>
    public Sync<bool> IsAvatar { get; private set; }

    /// <summary>Whether the model has been loaded by the hook.</summary>
    public Sync<bool> IsLoaded { get; private set; }

    /// <summary>Import settings (not synced, set at import time).</summary>
    public ModelImportSettings ImportSettings { get; set; }

    public override void OnAwake()
    {
        base.OnAwake();
        SourcePath = new Sync<string>(this, "");
        LocalUri = new Sync<string>(this, "");
        IsAvatar = new Sync<bool>(this, false);
        IsLoaded = new Sync<bool>(this, false);

        // Subscribe to SourcePath changes to trigger hook update
        SourcePath.OnChanged += _ => RunApplyChanges();
    }
}
