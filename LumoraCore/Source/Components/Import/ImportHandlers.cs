// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lumora.Core.Components.Import;

// Settings bag passed from ModelImportDialog to a registered IModelImportHandler.
// Only carries fields the dialog actually drives today; the handler can fill in
// implementation-specific defaults for everything else. Grow this struct as the
// model pipeline gains features (PBS materials, animations, point clouds, etc.)
// - don't add fields here speculatively. - xlinka
public sealed class ModelImportRequest
{
    public float Scale = 1f;
    public bool AutoScale;
    public ModelMaterialType Material = ModelMaterialType.Lit;
    public bool Colliders = true;

    // Whether the generated colliders stop a walking body. Off, because an import is a prop far more
    // often than it is a floor, and a prop you cannot walk through is a wall in the shape of a chair.
    // On is for importing a building.
    public bool SolidToPeople;
    public bool Grabbable = true;
    public bool Scalable = true;
    public bool ImportAtOrigin;
    public bool CalculateNormals = true;
    public bool CalculateTangents = true;
    public bool ImportVertexColors;
    public bool ImportAlbedoColor = true;
    public bool ImportEmissive = true;
    public bool MakeDualSided;
    public bool MakeFlatShaded;
    public int MaxTextureSize = -1;
    public bool ForceNoMipMaps;

    // On by default: a model with no animation channels extracts nothing, so leaving this on costs
    // files without animations exactly nothing.
    public bool ImportAnimations = true;

    // The advanced answers. Every default here is the import as it was before they existed.
    public bool FitColliders;
    public bool ImportLights;
    public bool ImportCameras;
    public bool AssetsOnly;
    public bool MatchTexturesByName;
    public bool AvatarSetup;
    public ModelAssetPlacement AssetPlacement = ModelAssetPlacement.WithParts;
    public bool ShareAssets;

    // Material kind per material, by the name the file gives it; anything not named takes Material.
    public Dictionary<string, ModelMaterialType>? MaterialTypes;

    // A source that already knows how its model goes together (a Unity package carries the materials,
    // the avatar flag and what to switch off) hands over finished settings, and the handler imports with
    // exactly those, avatar or not, instead of mapping the fields above.
    public Lumora.Core.Assets.ModelImportSettings? Settings;

    // The scale the dialog puts on the slot a file lands in: the unit it was told, unless the import fits the model
    // to a height (Auto Scale, Humanoid, any .vrm), where that height is the whole answer. Scaling the slot as well
    // stood a "Humanoid" model 1.8 x 1.8 = 3.24 m tall. -xlinka
    public float TargetScaleFor(string path)
    {
        bool vrm = string.Equals(System.IO.Path.GetExtension(path), ".vrm", System.StringComparison.OrdinalIgnoreCase);
        return AutoScale || vrm ? 1f : Scale;
    }

    // The answers as the importer's settings. A .vrm keeps the avatar preset it always had (rigged, stood
    // at 1.7 m, no clips) and takes everything else on top, so the dialog drives an avatar file too instead
    // of being dropped on the floor for one. -xlinka
    public Lumora.Core.Assets.ModelImportSettings ToSettings(string path)
    {
        bool vrm = string.Equals(System.IO.Path.GetExtension(path), ".vrm", System.StringComparison.OrdinalIgnoreCase);
        return new Lumora.Core.Assets.ModelImportSettings
        {
            // AutoScale -> rescale to a standard height; otherwise honor the unit scale verbatim.
            Scale = Scale,
            PreferFileUnits = true,
            Rescale = vrm || AutoScale,
            Center = vrm || AutoScale,
            TargetHeight = vrm ? 1.7f : 1.8f,
            GenerateColliders = Colliders,
            FitColliders = FitColliders,
            SolidToPeople = SolidToPeople,
            Grabbable = Grabbable,
            Scalable = Scalable,
            Material = Material,
            MaterialTypes = MaterialTypes,
            VertexColors = ImportVertexColors,
            MakeDualSided = MakeDualSided,
            ImportAlbedoColor = ImportAlbedoColor,
            ImportEmissive = ImportEmissive,
            ForceNoMipMaps = ForceNoMipMaps,
            MaxTextureSize = MaxTextureSize,
            ImportAnimations = !vrm && ImportAnimations,
            IsAvatarImport = vrm,
            SetupIK = vrm || AvatarSetup,
            OpenAvatarStudio = AvatarSetup,
            ImportLights = ImportLights,
            ImportCameras = ImportCameras,
            AssetsOnly = AssetsOnly,
            MatchTexturesByName = MatchTexturesByName,
            AssetPlacement = AssetPlacement,
            ShareAssets = ShareAssets,
        };
    }
}

public interface IImageImportHandler
{
    Task ImportAsync(Slot slot, string path);
}

public interface IModelImportHandler
{
    Task ImportAsync(Slot slot, string path, ModelImportRequest request);
}

// source is a file path OR a URL - a video plays from either, so the handler is never handed a path
// that had to be downloaded first.
public interface IVideoImportHandler
{
    Task ImportAsync(Slot slot, string source, VideoImportOptions options);
}

public interface IRawFileImportHandler
{
    Task ImportAsync(Slot slot, string path);
}

// Reads the real OS clipboard and routes its contents (files / images / urls /
// text) through the import pipeline. The core has no clipboard access, so the
// platform layer (LumoraGodot) implements this and registers it; without it,
// a paste is a no-op (no fake placeholder). - xlinka
public interface IClipboardPasteHandler
{
    // Pull whatever's on the OS clipboard and import it into the focused world.
    void Paste();
}

// Registry for pluggable import pipelines. LumoraCore defines the dialog flow;
// platform/asset code (LumoraGodot) registers concrete handlers at engine startup.
// Dialogs call the registered handler in RunImport; if none is registered, the
// dialog spawns a labeled placeholder slot instead. - xlinka
public static class ImportHandlers
{
    public static IImageImportHandler? Image { get; set; }
    public static IModelImportHandler? Model { get; set; }
    public static IVideoImportHandler? Video { get; set; }
    public static IRawFileImportHandler? Raw { get; set; }

    // Platform clipboard bridge. Null until the platform layer registers it. - xlinka
    public static IClipboardPasteHandler? Clipboard { get; set; }
}
