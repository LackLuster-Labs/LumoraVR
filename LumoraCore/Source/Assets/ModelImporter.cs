// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lumora.Core.Assets.Interop.Unity;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Import;
using Lumora.Core.Input;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

public class ModelImportSettings
{
    public float Scale { get; set; } = 1.0f;

    public bool ImportBones { get; set; } = true;

    public bool ImportAnimations { get; set; } = true;

    public bool ImportMaterials { get; set; } = true;

    // On unless asked otherwise: an imported thing is something to point at and pick up.
    public bool GenerateColliders { get; set; } = true;

    // The import lands as one object to pick up: a grab on the slot it imports into, scalable or not, and
    // an object root so tools and photos take it whole.
    public bool Grabbable { get; set; } = true;
    public bool Scalable { get; set; } = true;

    // Whether those colliders stop a walking body. See the attach site for why this is separate.
    public bool SolidToPeople { get; set; }

    public bool SetupIK { get; set; } = true;

    public bool Center { get; set; } = true;

    public bool Rescale { get; set; } = true;

    public float TargetHeight { get; set; } = 1.7f;

    public bool ForceTpose { get; set; } = false;

    public bool IsAvatarImport { get; set; } = false;

    // Bring the model in at the size its file declares (an FBX's unit factor, times Scale), the way the
    // tool it came from showed it, instead of reading file units as metres. Applied before the rig is
    // measured, so an avatar's height comes out in metres too. Ignored when Rescale is on.
    public bool UseFileUnits { get; set; }

    // The caller already scaled the target by a unit it assumed (Scale). When the file states its own unit, that
    // unit wins and the model lands at its real size whatever was assumed; a file that states none keeps Scale.
    // Ignored when Rescale is on.
    public bool PreferFileUnits { get; set; }

    // --- Options driven by the import dialog (ModelImportRequest). Only those the
    // importer can actually honor in its own scope are wired through; geometry-level
    // toggles (normals/tangents/flat-shaded) would have to change the shared decode
    // post-process and are NOT applied here (see ImportModelPhosAsync). - xlinka

    public ModelMaterialType Material { get; set; } = ModelMaterialType.Lit;

    public bool MakeDualSided { get; set; } = false;

    public bool ImportAlbedoColor { get; set; } = true;

    public bool ImportEmissive { get; set; } = true;

    public bool ForceNoMipMaps { get; set; } = false;

    // -1 = no limit. Not enforced yet, no downscaler.
    public int MaxTextureSize { get; set; } = -1;

    // A source that carries its own material definitions answers here. The importer asks once per
    // surface, on the world thread, and only builds its own material when the answer is null. -xlinka
    public Func<ModelMaterialRequest, MaterialProvider?>? MaterialOverride { get; set; }

    // Runs on the world thread once the model is built and placed, before the import reports done.
    public Action<ModelImportResult>? AfterBuild { get; set; }

    // The creator's own humanoid mapping, rig node -> bone name, when the source ships one.
    // Null lets a loose model read its Unity .meta sidecar; an explicit map takes precedence.
    public IReadOnlyDictionary<BodyNode, string>? HumanBones { get; set; }

    // --- The dialog's advanced answers. Every default is the import as it was before they existed.

    // A fitted primitive or convex hull per static part. Needs GenerateColliders. -xlinka
    public bool FitColliders { get; set; }

    public bool ImportLights { get; set; }

    public bool ImportCameras { get; set; }

    // Only the assets, each on a slot of its own: meshes, materials, textures and clips, with nothing
    // drawing them, no bones, no colliders and no grab. For swapping one into something already built.
    public bool AssetsOnly { get; set; }

    // A material the file gives no texture for takes the image files beside the model named after it.
    public bool MatchTexturesByName { get; set; }

    // Open the avatar studio over the model once it is in, its markers already on the rig.
    public bool OpenAvatarStudio { get; set; }

    public ModelAssetPlacement AssetPlacement { get; set; } = ModelAssetPlacement.WithParts;

    // Identical meshes, materials and textures become one asset that every user of them shares.
    public bool ShareAssets { get; set; }

    // Material kind per material, by the file's name for it. Anything not named takes Material.
    public IReadOnlyDictionary<string, ModelMaterialType>? MaterialTypes { get; set; }

    // The look comes from the mesh's own colours: a material whose meshes carry them becomes the vertex colour form
    // of its kind, and the colours come in as linear light. Off keeps them exactly as the file wrote them, unused.
    public bool VertexColors { get; set; }

    public ModelMaterialType MaterialTypeFor(string? sourceName)
        => sourceName != null && MaterialTypes != null && MaterialTypes.TryGetValue(sourceName, out var type) ? type : Material;
}

// One surface asking for its material: the model file's own name for that material, the model's root
// slot, the node the surface belongs to, the slot its renderer sits on, and its index among that node's
// surfaces in order.
public readonly record struct ModelMaterialRequest(string SourceName, Slot Model, Slot Node, Slot Renderer, int Index);

public class ModelImportResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = null!;
    public Slot RootSlot { get; set; } = null!;
    public SkeletonBuilder Skeleton { get; set; } = null!;
    public List<SkinnedMeshRenderer> SkinnedMeshes { get; set; } = new();
    public string LocalUri { get; set; } = null!;

    // In source order. Empty when the model has none.
    public List<AnimationProvider> AnimationProviders { get; set; } = new();

    public Animator Animator { get; set; } = null!;

    // Metres per file unit as the file declares it; 1 when it declares nothing.
    public float FileUnitScale { get; set; } = 1f;

    // The slot of the model file's own root node. Everything the file defines hangs under it; the model
    // slot also carries what the import adds beside it (references, animations, materials).
    public Slot? NodeRoot { get; set; }
}

public static partial class ModelImporter
{
    public static readonly string[] SupportedExtensions = new[]
    {
        ".glb",
        ".gltf",
        ".vrm",
        ".fbx",
        ".obj",
        ".dae",
        ".3ds",
        ".stl",
        ".ply",
        ".x",
        ".ase"
    };

    // A blend shape is stripped when every delta on it is smaller than this FRACTION OF THE MESH'S OWN
    // BOUNDING BOX DIAGONAL, not smaller than a fixed distance. Nothing in a mesh file states its units, so
    // a fixed 0.001 means two different things: 1 mm on a metre-authored avatar, which is large enough to
    // delete a real blink, and 0.01 mm on the same avatar authored in centimetres, which strips nothing at
    // all. Against the diagonal both land near 20 micrometres on a human-sized model - far above the float
    // noise an exporter writes, far below any motion a face can show - and a model of any scale gets the
    // same decision. -xlinka
    private const float BlendShapeStripDiagonalFraction = 1e-5f;

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

    public static async Task<ModelImportResult> ImportModelAsync(
        string filePath,
        Slot targetSlot,
        ModelImportSettings? settings = null,
        LocalDB? localDB = null,
        IProgress<ImportProgress>? progress = null)
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

        // EVERY supported format goes through the one Phos importer - a single Assimp pipeline (glb/gltf/vrm/fbx/
        // obj/dae/...) that decodes off-thread, builds the mesh on a worker thread, and points a SkinnedMeshRenderer
        // at a content-hashed MeshAsset instead of giant synced inline vertex lists. The old ModelData/ModelDataHook
        // path that set inline lists synchronously on the main thread (the import-freeze cause) has been removed. -xlinka
        return await ImportModelPhosAsync(filePath, targetSlot, settings, localDB, progress);
    }

    public static async Task<ModelImportResult> ImportAvatarAsync(
        string filePath,
        Slot targetSlot,
        LocalDB? localDB = null,
        IProgress<ImportProgress>? progress = null)
    {
        var settings = new ModelImportSettings
        {
            ImportBones = true,
            ImportAnimations = false,
            SetupIK = true,
            IsAvatarImport = true,
            Rescale = true,
            TargetHeight = 1.7f,
            GenerateColliders = true,
        };

        return await ImportModelAsync(filePath, targetSlot, settings, localDB, progress);
    }

    // The single Assimp + Phos import path (one importer for everything): parse the file ONCE with Assimp,
    // bake each node's meshes to .lmesh files in the local DB (a node's parts merged into one mesh with a
    // submesh per material), build a Slot per node with its local transform, build a SkeletonBuilder from the
    // meshes' bone names (bones are just named Slots), and per baked mesh attach a MeshProvider pointed at
    // that file + a renderer, skinned ones bound to the skeleton by name.
    // Assimp/glTF/Godot are all right-handed Y-up, so we import straight: no axis negate, no winding flip.
    //
    // PARSE ONCE IS THE WHOLE POINT. Providers used to carry the SOURCE model URL with a MeshIndex, so every
    // one of them re-parsed the entire file through MeshDecoder and discarded every mesh but its own - and
    // did it concurrently, ungated, on every peer that ever loaded the model. A nine-mesh avatar therefore
    // cost ten whole-file parses on the importing machine alone: measured at 3.4 s and ~3.9 GB apiece, which
    // is how a 10.6 MB file committed 33 GB and starved the world thread. Baking here pays the parse once,
    // and what replicates afterwards is a compressed per-mesh file nobody has to decode a model to read. -xlinka
    private static async Task<ModelImportResult> ImportModelPhosAsync(
        string filePath, Slot targetSlot, ModelImportSettings settings, LocalDB? localDB,
        IProgress<ImportProgress>? progress)
    {
        var result = new ModelImportResult();
        bool ownsSourceGate = false;
        try
        {
            // Honesty: MaxTextureSize has no downscaler yet, so it's surfaced rather than silently ignored. (Geometry
            // toggles like normals/tangents/flat-shading are decode-driven via the shared GetAssimpPostProcessSteps,
            // which the per-mesh MeshDecoder reuses verbatim to keep MeshIndex aligned, so they aren't carried here.)
            if (settings.MaxTextureSize > 0)
                Logger.Warn($"ModelImporter: 'Max Texture Size' ({settings.MaxTextureSize}) is not enforced - no texture downscaler yet; importing at source resolution.");

            progress?.Report(new ImportProgress(ImportStage.Fetching, 0.05f));
            var humanBones = settings.HumanBones;
            if (settings.SetupIK && humanBones == null && File.Exists(filePath + ".meta"))
                humanBones = await Task.Run(() => ReadHumanoidSidecar(filePath + ".meta")).ConfigureAwait(false);
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string hint = ext == ".vrm" ? ".glb" : ext; // VRM is GLB; Assimp needs a known hint
            string modelDir = Path.GetDirectoryName(filePath) ?? ""; // for resolving external texture files

            // SAME post-process steps as the per-mesh decode, or the mesh indices wouldn't line up.
            // Force the parse onto a worker thread: it's seconds of synchronous work, and if an upstream await
            // (file read / local-DB import) ever completes synchronously the continuation would otherwise run it
            // INLINE on the main thread and freeze rendering for the whole parse. Task.Run makes off-main deterministic.
            progress?.Report(new ImportProgress(ImportStage.Decoding, 0.12f));

            // The importer takes the SAME gate the asset path takes, because a source parse is a source
            // parse whoever starts it. Without this, dropping five models at once starts five imports on
            // five tasks and each one parses concurrently - which is the exact shape of the bug the
            // per-mesh baking below exists to end, just spelled with files instead of meshes. One heavy
            // scene at a time, through baking: releasing after parsing lets the next native scene
            // overlap this scene's blend shapes and converted meshes.
            await Assets.AssetManager.SourceParseGate.WaitAsync().ConfigureAwait(false);
            ownsSourceGate = true;
            Assimp.Scene scene = await System.Threading.Tasks.Task.Run(() =>
            {
                // Read inside the gate, so queued imports do not retain whole-file buffers.
                var bytes = File.ReadAllBytes(filePath);
                if (ext == ".fbx") bytes = FbxImportLayers.Prepare(bytes);
                // The model from these bytes, its companions (an OBJ's .mtl, a .gltf's buffers) from its own folder
                // and nowhere else. See ModelFileAccess.
                using var access = new ModelFileAccess(bytes, Path.GetFileNameWithoutExtension(filePath) + hint, modelDir);
                using var actx = new Assimp.AssimpContext();
                actx.SetIOSystem(access);
                var parsed = actx.ImportFile(access.MainName, MeshDecoder.GetAssimpPostProcessSteps(perMesh: true, hint));
                if (settings.VertexColors)
                    PrepareVertexColors(parsed, hint);
                return parsed;
            }).ConfigureAwait(false);

            if (scene == null || !scene.HasMeshes || scene.MeshCount == 0)
            {
                result.ErrorMessage = "Assimp produced no meshes";
                Logger.Error($"ModelImporter(Phos): {result.ErrorMessage} for '{filePath}'");
                return result;
            }

            // EVERYTHING THE LATER STAGES NEED OUT OF THE PARSE, TAKEN NOW, AS PLAIN DATA.
            //
            // The scene is the largest resident thing in an import, and it used to live until after the
            // skeleton chunk - across texture IO, animation extraction and the whole hierarchy walk -
            // because those stages read it directly. They need almost nothing: the hierarchy is names and
            // local transforms, the skeleton is a list of bone names, and a renderer needs its mesh's
            // material index and whether it skins. Copying that here costs kilobytes and lets the parse
            // be dropped the instant the last mesh is converted. Holding an Assimp node instead would
            // hold its parent chain, and through that the scene, which is the same trap. -xlinka
            int meshCount = scene.MeshCount;
            var parts = MeshGrouping.Capture(scene);
            var boneNames = CollectBoneNames(scene);
            var nodeTree = CaptureNode(scene.RootNode);
            var extras = settings.AssetsOnly ? null : SceneExtras.Capture(scene, settings, hint);
            float? declaredUnit = FileUnitScale(scene);
            result.FileUnitScale = declaredUnit ?? 1f;
            float baseScale = settings.UseFileUnits && !settings.Rescale ? result.FileUnitScale * settings.Scale : 1f;
            // The dialog has already scaled the target by the unit it was told (Scale). A file that says its own
            // unit lands at that size instead: the two would otherwise multiply, and "Meters" on a centimetre FBX,
            // which is what the common modelling tools write, made the model a hundred times too big. -xlinka
            if (settings.PreferFileUnits && !settings.Rescale && declaredUnit is float unit && settings.Scale > 0f)
                baseScale = unit / settings.Scale;

            // ONE RENDERER PER NODE AND KIND, NOT PER SCENE MESH. A source object with several materials
            // reaches us as one mesh per material on the same node, and a renderer each turned one object
            // into a stack of "MeshN" slots. A node's parts go back together by how they deform (bones, blend
            // shapes alone, neither); a part on its own still bakes exactly as it always has. -xlinka
            var units = new List<MeshGrouping.Unit>();
            var noTriangles = new SortedSet<int>();
            GroupNodes(nodeTree, parts, units, new Dictionary<string, int>(StringComparer.Ordinal), noTriangles,
                mi => MeshGrouping.ExactBlendBytes(scene.Meshes[mi]));
            if (noTriangles.Count > 0)
                Logger.Log($"ModelImporter: left out {noTriangles.Count} mesh(es) of '{Path.GetFileName(filePath)}' with no triangles: {string.Join(", ", noTriangles)}");

            // OFF-THREAD: resolve every used material's textures into local:// URIs up front. Texture
            // saves hit the local DB (file IO) and must NOT run on the world thread - so do them here, off-thread.
            // The world-thread build below only ATTACHES components from these resolved results (no IO, no await).
            // Ahead of the bake because this reads the scene's materials and embedded texture blobs, and the
            // bake is what releases the scene.
            var resolvedMaterials = new Dictionary<int, ResolvedMaterial?>();
            var materialNames = new Dictionary<int, string>();
            // Counted before the loop so the readout can say "textures 3 of 11" instead of a bare
            // spinner - the slot count is what the user is actually waiting on here, and a model with
            // eleven 4K maps is exactly the import that feels like a hang without it. -xlinka
            int textureCount = CountTextureSlots(scene);
            int texturesDone = 0;
            var namedTextures = settings.MatchTexturesByName ? new TexturesByName(modelDir) : null;
            progress?.Report(new ImportProgress(ImportStage.Textures, 0.14f, 0, textureCount));
            foreach (var amesh in scene.Meshes)
            {
                if (amesh == null || resolvedMaterials.ContainsKey(amesh.MaterialIndex)) continue;
                if (amesh.MaterialIndex >= 0 && amesh.MaterialIndex < scene.MaterialCount)
                    materialNames[amesh.MaterialIndex] = scene.Materials[amesh.MaterialIndex]?.Name ?? "";
                var resolved = await ResolveMaterialAsync(scene, amesh.MaterialIndex, modelDir, localDB).ConfigureAwait(false);
                if (namedTextures != null && resolved != null)
                    await namedTextures.FillAsync(resolved, materialNames.GetValueOrDefault(amesh.MaterialIndex), localDB).ConfigureAwait(false);
                resolvedMaterials[amesh.MaterialIndex] = resolved;
                texturesDone += CountResolvedTextures(resolved);
                textureCount = System.Math.Max(textureCount, texturesDone);
                progress?.Report(new ImportProgress(ImportStage.Textures,
                    0.14f + 0.16f * (textureCount > 0 ? System.Math.Min(1f, (float)texturesDone / textureCount) : 1f),
                    texturesDone, textureCount));
            }
            if (settings.VertexColors)
                MarkVertexColored(scene, resolvedMaterials, filePath);

            // OFF-THREAD, animation clips: extraction is pure CPU over the already-parsed scene and
            // the serialize + local-DB write is file IO, so both belong out here rather than on the world
            // thread. Clip bytes land in the content-addressed DB exactly like meshes and textures, which is
            // what makes a clip replicate to joiners by hash instead of every peer re-deriving it from the
            // model file. With no local DB there is nowhere to put the bytes and therefore no URL a provider
            // could load, so animation import is skipped rather than attaching a provider that resolves to
            // nothing. Ahead of the bake because morph channels are named off the meshes. -xlinka
            var animations = new List<(Animation.AnimationClip clip, Uri uri)>();
            if (settings.ImportAnimations && scene.HasAnimations)
            {
                progress?.Report(new ImportProgress(ImportStage.Animations, 0.32f));
                var extracted = AnimationExtractor.Extract(scene);
                if (extracted.Count > 0 && localDB == null)
                {
                    Logger.Warn($"ModelImporter: '{Path.GetFileNameWithoutExtension(filePath)}' has {extracted.Count} animation(s) but no local DB is available - skipping animation import.");
                }
                else
                {
                    foreach (var clip in extracted)
                    {
                        var localUri = await localDB!.SaveAssetAsync(clip.ToBytes(), ".lanim").ConfigureAwait(false);
                        if (string.IsNullOrEmpty(localUri))
                        {
                            Logger.Warn($"ModelImporter: failed to store animation clip '{clip.Name}'");
                            continue;
                        }
                        animations.Add((clip, new Uri(localUri)));
                    }
                }
            }

            // BAKE, off-thread, one unit at a time: convert it out of the scene we already have, drop the
            // blend shapes whose deltas are all noise, and write it to the local DB as its own .lmesh. The
            // provider below then loads a file that IS that mesh, so no peer ever parses the model again - not
            // on join, not on reload, not once per mesh.
            //
            // One at a time and Clear()ed on the way out on purpose: the converted mesh is the largest
            // single allocation in the import (an avatar's dense blend shape deltas run to hundreds of
            // megabytes), and holding nine of them at once would just move the blowup rather than fix it. -xlinka
            var unitUris = new Uri?[units.Count];
            var unitSubmeshParts = new int[]?[units.Count];
            var partUris = new Dictionary<int, Uri>();
            var partConverted = new bool[meshCount];
            var meshUsable = new bool[meshCount];
            // Kept for the rescale/center bounds at the end, so the Assimp scene can be released the moment
            // the last mesh is converted instead of being pinned until the last chunk. One list per baked
            // mesh, because each is measured where its own renderer draws it. -xlinka
            var unitPoints = new List<float3>?[units.Count];
            var partPoints = new List<float3>?[meshCount];
            int strippedShapes = 0;
            var nodeRest = MeshDecoder.BuildNodeRestMap(scene);

            // The parse's copy of a mesh is dead once the last unit that reads it is converted, and it is the
            // heavy half of the scene: the incident avatar carried 756 attachments over 58k vertices, which is
            // 173 MB of dense frames sitting in the parse alongside the converted copy. Letting go per unit
            // means peak is one converted unit plus the meshes not reached yet; a mesh no unit reads goes
            // now. -xlinka
            var lastUse = new int[meshCount];
            Array.Fill(lastUse, -1);
            for (int u = 0; u < units.Count; u++)
            {
                foreach (int mi in units[u].Parts)
                    lastUse[mi] = u;
            }
            for (int mi = 0; mi < meshCount; mi++)
            {
                if (lastUse[mi] < 0)
                    scene.Meshes[mi] = null!;
            }

            progress?.Report(new ImportProgress(ImportStage.Decoding, 0.34f, 0, units.Count));
            for (int u = 0; u < units.Count; u++)
            {
                var unitParts = units[u].Parts;
                if (unitParts.Length > 1 && localDB != null)
                {
                    var points = new List<float3>();
                    var group = await BakeGroupAsync(scene, unitParts, hint, nodeRest, localDB, points, filePath).ConfigureAwait(false);
                    strippedShapes += group.Stripped;
                    unitUris[u] = group.Uri;
                    unitSubmeshParts[u] = group.SubmeshParts;
                    if (group.Uri != null)
                        unitPoints[u] = points;
                }

                // A lone part, or a unit that could not become one mesh: each part baked on its own, the way
                // every mesh used to be. -xlinka
                if (unitUris[u] == null)
                {
                    foreach (int mi in unitParts)
                    {
                        if (partConverted[mi])
                            continue;
                        partConverted[mi] = true;
                        var points = new List<float3>();
                        var part = await BakePartAsync(scene, mi, hint, nodeRest, localDB, points, filePath).ConfigureAwait(false);
                        partPoints[mi] = points;
                        strippedShapes += part.Stripped;
                        meshUsable[mi] = part.Usable;
                        if (part.Uri != null)
                            partUris[mi] = part.Uri;
                    }
                }

                foreach (int mi in unitParts)
                {
                    if (lastUse[mi] == u)
                        scene.Meshes[mi] = null!;
                }

                progress?.Report(new ImportProgress(ImportStage.Decoding,
                    0.34f + 0.26f * (float)(u + 1) / units.Count, u + 1, units.Count));
            }
            if (strippedShapes > 0)
                Logger.Debug($"ModelImporter: dropped {strippedShapes} blend shape(s) with no measurable deltas from '{Path.GetFileNameWithoutExtension(filePath)}'.");
            if (localDB == null)
                Logger.Warn("ModelImporter: no local DB - meshes cannot be baked, so each provider will re-parse the source model on every peer.");

            // LAST READER OF THE SCENE. Everything past this point runs off the plain arrays taken above -
            // per-mesh material index and kind, bone names, the node tree, the baked URIs and the
            // positions for the bounds - so the parse goes now rather than living across the hierarchy
            // walk, the skeleton chunk and every per-mesh renderer chunk after them. -xlinka
            scene = null!;
            Assets.AssetManager.SourceParseGate.Release();
            ownsSourceGate = false;

            // The source model gets a local DB record, but only the FALLBACK case gets a COPY of it.
            //
            // Every mesh that baked has its own .lmesh and nothing reads the model file again, so copying it
            // into the cache was a second full copy of a file nobody opens: 10.6 MB for the incident avatar,
            // hundreds for a scanned environment, on every import. Original registers the same hash and URI
            // against the file where it already sits, which keeps the dedup and the provenance and writes
            // nothing. When a mesh DID fall back, its provider carries that URI for the life of the content
            // on every peer, so then we own the bytes and copy them. -xlinka
            if (localDB != null)
            {
                bool anyFallback = false;
                for (int mi = 0; mi < meshCount; mi++)
                {
                    if (meshUsable[mi] && !partUris.ContainsKey(mi))
                    {
                        anyFallback = true;
                        break;
                    }
                }
                result.LocalUri = await localDB.ImportLocalAssetAsync(filePath,
                    anyFallback ? LocalDB.ImportLocation.Copy : LocalDB.ImportLocation.Original).ConfigureAwait(false);
            }

            // WORLD THREAD, CHUNKED: EVERY data-model + Godot scene-tree write happens from here on, on
            // the world thread under the engine's Implementer lock - the only place they're legal. We hop on per
            // logical chunk (and per mesh node) via OnWorldAsync so each lands on its own frame and the world keeps
            // rendering between chunks (load-in-pieces). The heavy decode + texture IO already ran off-thread above. -xlinka
            World world = targetSlot.World;

            // Chunk 1: node hierarchy (one Slot per captured node, with its local TRS).
            progress?.Report(new ImportProgress(ImportStage.Hierarchy, 0.64f));
            Slot modelSlot = null!;
            var nameToSlot = new Dictionary<string, Slot>();
            var meshNodeSlots = new List<(int[] units, Slot slot)>();
            AssetHome home = null!;
            await OnWorldAsync(world, () =>
            {
                modelSlot = targetSlot.AddSlot(Path.GetFileNameWithoutExtension(filePath));
                result.RootSlot = modelSlot;
                home = new AssetHome(settings, modelSlot);
                if (settings.AssetsOnly)
                    return;
                if (settings.Grabbable)
                {
                    var grab = targetSlot.GetComponent<Grabbable>() ?? targetSlot.AttachComponent<Grabbable>();
                    grab.Scalable.Value = settings.Scalable;
                    if (targetSlot.GetComponent<ObjectRoot>() == null)
                        targetSlot.AttachComponent<ObjectRoot>();
                }
                if (baseScale != 1f)
                    modelSlot.LocalScale.Value = new float3(baseScale, baseScale, baseScale);
                result.NodeRoot = BuildNodeSlots(nodeTree, modelSlot, nameToSlot, meshNodeSlots);
            });

            // The sourceUri is only reachable when nothing could be baked: the source model, decoded per mesh
            // by index, which is the old shape and its old cost.
            var sourceUri = !string.IsNullOrEmpty(result.LocalUri) ? new Uri(result.LocalUri) : new Uri(filePath);

            if (settings.AssetsOnly)
            {
                var bake = new BakedMeshes(units, parts, unitUris, unitSubmeshParts, partUris, meshUsable, sourceUri);
                await BuildAssetsOnlyAsync(world, home, nodeTree, bake, resolvedMaterials, materialNames, animations, settings, result, progress).ConfigureAwait(false);
                result.Success = true;
                Logger.Debug($"ModelImporter: '{Path.GetFileNameWithoutExtension(filePath)}' as assets only - {home.Describe()}");
                return result;
            }

            // Chunk 2: skeleton (union of mesh bone names) + avatar rig/IK if it classifies as a biped.
            progress?.Report(new ImportProgress(ImportStage.Skeleton, 0.68f));
            SkeletonBuilder? skelBuilder = null;
            await OnWorldAsync(world, () => skelBuilder = BuildSkeletonAndRig(boneNames, modelSlot, nameToSlot, settings, result, humanBones));

            // Chunk 3..N: renderers - one mesh NODE per frame so each renderer's GPU build lands on its own
            // frame and the world renders between them (load-in-pieces). A provider points at its OWN baked
            // .lmesh and carries no MeshIndex: the file is that mesh, so loading it costs a read and a decode
            // rather than a model parse. MeshIndex stays live on MeshProvider for content saved before the bake -
            // this path just has no reason to set it any more. -xlinka
            progress?.Report(new ImportProgress(ImportStage.Meshes, 0.70f, 0, meshNodeSlots.Count));
            int totalMeshNodes = meshNodeSlots.Count;
            int meshNodeDone = 0;
            int renderers = 0;
            var measured = new List<(List<float3> Points, Slot Slot)>();
            var unitFits = new ShapeFit?[units.Count];
            var partFits = new ShapeFit?[meshCount];
            bool fitShapes = settings.GenerateColliders && settings.FitColliders;
            foreach (var (nodeUnits, slot) in meshNodeSlots)
            {
                var unitIndices = nodeUnits;
                var capturedSlot = slot;
                await OnWorldAsync(world, () =>
                {
                    // A merged unit is one renderer, a unit baked part by part is one per part. A node left
                    // with a single renderer carries it itself, as a node with a single mesh always has. -xlinka
                    var entries = new List<(int Unit, int Part)>();
                    foreach (int u in unitIndices)
                    {
                        if (unitUris[u] != null)
                        {
                            entries.Add((u, -1));
                            continue;
                        }
                        foreach (int mi in units[u].Parts)
                        {
                            if (meshUsable[mi])
                                entries.Add((u, mi));
                        }
                    }

                    int nodeSurface = 0;
                    foreach (var (u, mi) in entries)
                    {
                        var unit = units[u];
                        Slot meshSlot = entries.Count == 1
                            ? capturedSlot
                            : capturedSlot.AddSlot($"Mesh{(mi >= 0 ? mi : unit.Parts[0])}");
                        if (mi >= 0)
                        {
                            bool baked = partUris.TryGetValue(mi, out var partUri);
                            if (fitShapes && partFits[mi] == null && partPoints[mi] is { Count: > 0 } fitPoints)
                                partFits[mi] = ShapeFit.Of(fitPoints);
                            AttachRenderer(meshSlot, unit.Kind, baked ? partUri! : sourceUri, baked ? -1 : mi,
                                new[] { parts[mi].MaterialIndex }, resolvedMaterials, settings, skelBuilder, result,
                                new SurfaceContext(modelSlot, capturedSlot, nodeSurface, materialNames), home, partFits[mi]);
                            nodeSurface++;
                            if (partPoints[mi] is { } points)
                                measured.Add((points, meshSlot));
                        }
                        else
                        {
                            var submeshParts = unitSubmeshParts[u] ?? unit.Parts;
                            var materialIndices = new int[submeshParts.Length];
                            for (int k = 0; k < submeshParts.Length; k++)
                                materialIndices[k] = parts[submeshParts[k]].MaterialIndex;
                            if (fitShapes && unitFits[u] == null && unitPoints[u] is { Count: > 0 } fitPoints)
                                unitFits[u] = ShapeFit.Of(fitPoints);
                            AttachRenderer(meshSlot, unit.Kind, unitUris[u]!, -1,
                                materialIndices, resolvedMaterials, settings, skelBuilder, result,
                                new SurfaceContext(modelSlot, capturedSlot, nodeSurface, materialNames), home, unitFits[u]);
                            nodeSurface += materialIndices.Length;
                            if (unitPoints[u] is { } points)
                                measured.Add((points, meshSlot));
                        }
                        renderers++;
                    }
                });
                meshNodeDone++;
                progress?.Report(new ImportProgress(ImportStage.Meshes,
                    0.70f + 0.25f * (totalMeshNodes > 0 ? (float)meshNodeDone / totalMeshNodes : 1f),
                    meshNodeDone, totalMeshNodes));
            }

            // Animation chunk: one provider per clip under an "Animations" holder, and - when the model has a
            // skeleton to drive - an Animator on the model root pre-bound to the first clip. Binding runs
            // against the clip object we already hold instead of waiting on the provider's asset to gather
            // back out of the local DB, so the animator is wired the moment the model appears. The bindings
            // are track-index based, so they stay valid once the identical clip arrives through the provider.
            if (animations.Count > 0)
            {
                progress?.Report(new ImportProgress(ImportStage.Animations, 0.96f));
                await OnWorldAsync(world, () =>
                {
                    var holder = modelSlot.AddSlot("Animations");
                    foreach (var (clip, uri) in animations)
                    {
                        var clipSlot = holder.AddSlot(string.IsNullOrEmpty(clip.Name) ? "Clip" : clip.Name);
                        var provider = clipSlot.AttachComponent<AnimationProvider>();
                        provider.URL.Value = uri;
                        result.AnimationProviders.Add(provider);
                    }

                    if (skelBuilder != null && result.AnimationProviders.Count > 0)
                    {
                        var animator = modelSlot.AttachComponent<Animator>();
                        animator.Clip.Target = result.AnimationProviders[0];
                        animator.Playback.LoopMode = PlaybackLoopMode.Loop;
                        // Left paused on purpose: import should not start something moving on its own. The
                        // bindings are live, so pressing play in the inspector is all it takes.
                        int bound = animator.AutoBind(modelSlot, animations[0].clip);
                        result.Animator = animator;
                        Logger.Debug($"ModelImporter: animator bound {bound}/{animations[0].clip.TrackCount} tracks of '{animations[0].clip.Name}'");
                    }
                });
            }

            // Final chunk: rescale to target height + center.
            if (settings.Rescale || settings.Center)
            {
                progress?.Report(new ImportProgress(ImportStage.Finishing, 0.98f));
                await OnWorldAsync(world, () => ApplyModelTransform(measured, modelSlot, settings, baseScale));
            }

            // After the rescale, because a light's range and a lens's clip planes are distances in the world,
            // not in the file, and the model's final size is only known now.
            if (extras is { IsEmpty: false })
                await OnWorldAsync(world, () => extras.Build(modelSlot, nameToSlot, result.FileUnitScale));

            if (settings.AfterBuild != null)
                await OnWorldAsync(world, () => settings.AfterBuild(result));

            if (settings.OpenAvatarStudio)
                await OnWorldAsync(world, () => OpenStudioFor(modelSlot));

            progress?.Report(new ImportProgress(ImportStage.Complete, 1f));
            result.Success = true;
            int bakedMeshes = partUris.Count;
            foreach (var unitUri in unitUris)
            {
                if (unitUri != null)
                    bakedMeshes++;
            }
            Logger.Debug($"ModelImporter(Phos/Assimp): '{Path.GetFileNameWithoutExtension(filePath)}' - {meshCount} meshes as {renderers} renderer(s) ({bakedMeshes} baked), {result.SkinnedMeshes.Count} skinned; {home.Describe()}");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            // The whole stack, not just the message: a failure inside a world-thread chunk surfaces here with
            // no other trace, and "Value cannot be null" alone names nothing.
            Logger.Error($"ModelImporter(Phos): failed '{filePath}': {ex}");
        }
        finally
        {
            if (ownsSourceGate)
                Assets.AssetManager.SourceParseGate.Release();
        }
        return result;
    }

    // Marshal a chunk of data-model / Godot work onto the WORLD thread and await it. The action runs inside the
    // next ProcessSynchronousActions (the synchronous-actions step of World.Update) - on the engine's render thread, under the
    // Implementer lock - which is the ONLY place these writes are legal: the data model rejects modifications from
    // a "non-locking thread" and Godot rejects scene-tree edits (AddChild) off the main thread. That's exactly the
    // crash we hit when the build ran on the parse's worker thread.
    // The TCS resumes the caller ASYNCHRONOUSLY (off-thread), so the NEXT awaited chunk is queued from off-thread
    // and lands on the FOLLOWING frame instead of draining inline this frame - that's what spreads the build across
    // frames so the world keeps rendering between chunks (load-in-pieces). -xlinka
    private static System.Threading.Tasks.Task OnWorldAsync(World world, Action action)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        if (world == null)
        {
            // No world to marshal onto (shouldn't happen for a live import) - run inline so we never hang.
            try { action(); tcs.TrySetResult(true); } catch (Exception e) { tcs.TrySetException(e); }
            return tcs.Task;
        }
        world.RunSynchronously(() =>
        {
            try { action(); tcs.TrySetResult(true); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }

    // The union of the meshes' bone names, in first-seen order. Taken while the parse is alive so the
    // skeleton chunk can run off names alone. -xlinka
    private static List<string> CollectBoneNames(Assimp.Scene scene)
    {
        var boneNames = new List<string>();
        var boneSeen = new HashSet<string>();
        foreach (var amesh in scene.Meshes)
            if (amesh != null && amesh.HasBones)
                foreach (var b in amesh.Bones)
                    if (b != null && !string.IsNullOrEmpty(b.Name) && boneSeen.Add(b.Name))
                        boneNames.Add(b.Name);
        return boneNames;
    }

    // Which blend shapes went, by the name they had before the strip renumbered the list. Capped because
    // a face mesh drops hundreds and a log line nobody can read is not a log line.
    private static string DescribeStripped(List<string> namesBeforeStrip, List<int> removedIndices)
    {
        const int Shown = 12;
        var sb = new System.Text.StringBuilder();
        int shown = System.Math.Min(removedIndices.Count, Shown);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(", ");
            int idx = removedIndices[i];
            sb.Append(idx >= 0 && idx < namesBeforeStrip.Count ? namesBeforeStrip[idx] : $"#{idx}");
        }
        if (removedIndices.Count > shown)
            sb.Append($" (+{removedIndices.Count - shown} more)");
        return sb.ToString();
    }

    // One scene mesh converted and baked exactly as a lone mesh always has been, so its file and hash stay what
    // they were. Usable with a null uri means it converted but could not be stored. -xlinka
    private static async Task<(Uri? Uri, bool Usable, int Stripped)> BakePartAsync(Assimp.Scene scene, int mi,
        string hint, Dictionary<string, System.Numerics.Matrix4x4> nodeRest, LocalDB? localDB,
        List<float3>? positions, string filePath)
    {
        var phos = await System.Threading.Tasks.Task.Run(
            () => MeshDecoder.ConvertScene(scene, mi, hint, nodeRest)).ConfigureAwait(false);
        if (phos == null || phos.VertexCount == 0)
        {
            Logger.Warn($"ModelImporter: mesh {mi} of '{Path.GetFileName(filePath)}' converted to nothing; skipping.");
            return (null, false, 0);
        }

        int stripped = StripFlatShapes(phos, positions, $"mesh {mi}");
        Uri? uri = null;
        if (localDB != null)
        {
            var meshLocal = await SaveMeshOrNullAsync(localDB, phos, $"mesh {mi}", filePath).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(meshLocal))
                uri = new Uri(meshLocal);
            else
                Logger.Warn($"ModelImporter: failed to store mesh {mi} of '{Path.GetFileName(filePath)}'; it will load from the source model instead.");
        }

        // Deterministic release: the next conversion would otherwise allocate on top of this one.
        phos.Clear();
        return (uri, true, stripped);
    }

    // A unit's parts as one mesh with a submesh each. A null uri hands the parts back to be baked one by one.
    // -xlinka
    private static async Task<(Uri? Uri, int[]? SubmeshParts, int Stripped)> BakeGroupAsync(
        Assimp.Scene scene, int[] unitParts, string hint, Dictionary<string, System.Numerics.Matrix4x4> nodeRest,
        LocalDB localDB, List<float3> positions, string filePath)
    {
        string what = $"meshes {string.Join(", ", unitParts)}";
        Phos.PhosMesh phos;
        int[] submeshParts;
        try
        {
            (phos, submeshParts) = await System.Threading.Tasks.Task.Run(() =>
            {
                var merged = MeshDecoder.ConvertMeshGroup(scene, unitParts, hint, nodeRest, out var map);
                return (merged, map);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ModelImporter: {what} of '{Path.GetFileName(filePath)}' could not be merged ({ex.Message}); baking them one by one.");
            return (null, null, 0);
        }

        if (phos == null || phos.VertexCount == 0 || submeshParts.Length == 0)
        {
            Logger.Warn($"ModelImporter: {what} of '{Path.GetFileName(filePath)}' merged to nothing; baking them one by one.");
            return (null, null, 0);
        }

        int stripped = StripFlatShapes(phos, positions, what);
        var meshLocal = await SaveMeshOrNullAsync(localDB, phos, what, filePath).ConfigureAwait(false);
        phos.Clear();
        if (string.IsNullOrEmpty(meshLocal))
        {
            Logger.Warn($"ModelImporter: {what} of '{Path.GetFileName(filePath)}' could not be stored as one mesh; baking them one by one.");
            return (null, null, 0);
        }
        return (new Uri(meshLocal), submeshParts, stripped);
    }

    // A bake the local DB refuses costs that mesh its file and nothing else. -xlinka
    private static async Task<string?> SaveMeshOrNullAsync(LocalDB localDB, Phos.PhosMesh phos, string what, string filePath)
    {
        try
        {
            return await localDB.SaveMeshAsync(phos).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ModelImporter: {what} of '{Path.GetFileName(filePath)}' could not be baked: {ex.Message}");
            return null;
        }
    }

    // Bounds first: the same walk that collects positions for the rescale/center pass (when positions is
    // given) sizes the blend shape epsilon, so neither costs a second pass over the vertices. Returns how many
    // flat shapes went. -xlinka
    private static int StripFlatShapes(Phos.PhosMesh phos, List<float3>? positions, string what)
    {
        var raw = phos.RawPositions;
        int vc = phos.VertexCount;
        if (positions != null)
            positions.Capacity = System.Math.Max(positions.Capacity, positions.Count + vc);
        float3 lo = raw[0], hi = raw[0];
        for (int v = 0; v < vc; v++)
        {
            var p = raw[v];
            positions?.Add(p);
            if (p.x < lo.x) lo.x = p.x;
            if (p.y < lo.y) lo.y = p.y;
            if (p.z < lo.z) lo.z = p.z;
            if (p.x > hi.x) hi.x = p.x;
            if (p.y > hi.y) hi.y = p.y;
            if (p.z > hi.z) hi.z = p.z;
        }

        if (phos.BlendShapeCount == 0)
            return 0;

        float diagonal = (hi - lo).Length;
        float epsilon = diagonal > 0f && float.IsFinite(diagonal)
            ? diagonal * BlendShapeStripDiagonalFraction
            : Phos.PhosMesh.BlendShapeDeltaEpsilon;

        var shapeNames = new List<string>(phos.BlendShapes.Count);
        for (int s = 0; s < phos.BlendShapes.Count; s++)
            shapeNames.Add(phos.BlendShapes[s].Name);
        var strippedIndices = new List<int>();
        int dropped = phos.StripEmptyBlendShapes(strippedIndices, epsilon);
        if (dropped > 0)
            Logger.Debug($"ModelImporter: {what} dropped {dropped} flat blend shape(s) under {epsilon:G3} ({diagonal:G4} bounds diagonal): {DescribeStripped(shapeNames, strippedIndices)}");
        return dropped;
    }

    private static IReadOnlyDictionary<BodyNode, string>? ReadHumanoidSidecar(string path)
    {
        try
        {
            // Match the package reader's limit for metadata. An optional sidecar must not prevent the
            // model itself from importing if it is unreadable or contains no usable humanoid table.
            using var stream = File.OpenRead(path);
            if (stream.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Humanoid metadata exceeds the 4 MB limit.");
            using var reader = new StreamReader(stream);
            var map = UnityModelMeta.Parse(reader.ReadToEnd()).HumanoidMap();
            return map.Count > 0 ? map : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"ModelImporter: could not read humanoid metadata from '{Path.GetFileName(path)}': {ex.Message}");
            return null;
        }
    }

    // Build the skeleton (union of the meshes' bone names) and, when it classifies as a biped, the avatar rig +
    // full-body IK. ALL data-model writes - call it on the world thread (from inside OnWorldAsync). Returns the
    // SkeletonBuilder, or null when the model has no bones. -xlinka
    private static SkeletonBuilder? BuildSkeletonAndRig(List<string> boneNames, Slot modelSlot,
        Dictionary<string, Slot> nameToSlot, ModelImportSettings settings, ModelImportResult result,
        IReadOnlyDictionary<BodyNode, string>? humanBones)
    {
        SkeletonBuilder? skelBuilder = null;
        if (boneNames.Count > 0)
        {
            skelBuilder = modelSlot.AttachComponent<SkeletonBuilder>();
            var boneSlots = new HashSet<Slot>();
            foreach (var bn in boneNames)
            {
                if (!nameToSlot.TryGetValue(bn, out var bslot)) continue;
                var p = bslot.LocalPosition.Value;
                var rq = bslot.LocalRotation.Value;
                var sc = bslot.LocalScale.Value;
                var rest = float4x4.Translate(p) * float4x4.Rotate(rq) * float4x4.Scale(sc);
                skelBuilder.AddBone(bn, bslot, rest);
                boneSlots.Add(bslot);
            }
            Slot? rootBoneSlot = FindTopmostBoneSlot(boneSlots, modelSlot);
            if (rootBoneSlot != null) skelBuilder.RootBone.Target = rootBoneSlot;
            skelBuilder.IsBuilt.Value = true;
            result.Skeleton = skelBuilder;
        }

        // Avatar rig + full-body IK (mirrors AvatarStudio.RunCreate): classify the skeleton into a biped rig and,
        // if it really is a biped, attach AvatarForm + AvatarIK + auto-placed reference points so the avatar is
        // wearable/drivable. Tracking auto-wires later on equip (AvatarEquipManager.EquipAvatar). Content-gated, not
        // extension-gated: we classify ANY skinned model and only attach the avatar bits when it's actually a biped.
        if (settings.SetupIK && skelBuilder != null && skelBuilder.IsBuilt.Value)
        {
            var rig = modelSlot.GetComponent<HumanoidRig>() ?? modelSlot.AttachComponent<HumanoidRig>();
            if (!(humanBones is { Count: > 0 } && rig.PopulateFromHumanMap(skelBuilder, humanBones, nameToSlot)))
                rig.PopulateFromSkeleton(skelBuilder);
            // Normalize to a T-pose before IK captures the rest pose, when the import asked for it (an A-pose model
            // would otherwise IK-bind crooked). Opt-in - ForceTpose defaults off.
            if (settings.ForceTpose)
                rig.MakeTPose();

            // Point the avatar root frame at the body's front before references are baked. Equip resets the
            // root to identity, so a +Z-authored model would otherwise wear backward and walk tail-first. The
            // alignment is world-invariant (frame only), so nothing visibly turns at import. -xlinka
            AvatarCalibration.AlignAvatarFacing(modelSlot, rig);

            rig.GuessForwardFlipped();

            // A four-legged rig takes a different solver entirely. Decided here, before the biped
            // branch, because the two must never both be attached. Returns null for anything that is
            // not a quadruped and the biped path below runs exactly as it always has. -xlinka
            var quadrupedIk = Components.Avatar.QuadrupedIK.TryAttachFor(modelSlot, skelBuilder, rig);
            if (quadrupedIk != null)
            {
                if (modelSlot.GetComponent<AvatarForm>() == null)
                    modelSlot.AttachComponent<AvatarForm>();
                AvatarCalibration.EnsureHeadViewOverride(modelSlot, rig);
            }
            else if (rig.IsHumanoid)
            {
                if (modelSlot.GetComponent<AvatarForm>() == null)
                    modelSlot.AttachComponent<AvatarForm>();
                // Same default the package path applies: the wearer never sees their own head from inside.
                AvatarCalibration.EnsureHeadViewOverride(modelSlot, rig);
                var avatarIk = modelSlot.GetComponent<AvatarIK>() ?? modelSlot.AttachComponent<AvatarIK>();
                avatarIk.Skeleton.Target = skelBuilder;
                avatarIk.Rig.Target = rig;
                bool hasFeet = rig.TryGetBone(BodyNode.LeftFoot) != null && rig.TryGetBone(BodyNode.RightFoot) != null;
                bool hasPelvis = rig.TryGetBone(BodyNode.Hips) != null;
                AvatarCalibration.AutoPlaceReferences(modelSlot, rig, hasFeet, hasPelvis);

                // Tell IK how tall the avatar actually is so it scales to the real body. With rescale on (the
                // default) the model ends up exactly TargetHeight, so use that; otherwise measure head-to-foot from
                // the raw rig. Clamped + sanity-gated so a weird rig can't poison the solver. -xlinka
                if (settings.Rescale)
                {
                    avatarIk.AvatarHeight.Value = settings.TargetHeight;
                }
                else
                {
                    var headBone = rig.TryGetBone(BodyNode.Head);
                    if (headBone != null)
                    {
                        float top = headBone.GlobalPosition.y;
                        float bottom;
                        var lf = rig.TryGetBone(BodyNode.LeftFoot);
                        var rf = rig.TryGetBone(BodyNode.RightFoot);
                        if (lf != null && rf != null)
                            bottom = System.Math.Min(lf.GlobalPosition.y, rf.GlobalPosition.y);
                        else if (hasPelvis)
                        {
                            var h = rig.TryGetBone(BodyNode.Hips);
                            bottom = h.GlobalPosition.y - System.Math.Abs(top - h.GlobalPosition.y);
                        }
                        else bottom = top - 1.7f;
                        float measured = (top - bottom) * 1.08f; // head crown + soles past the bones
                        if (measured > 0.3f && measured < 3.0f)
                            avatarIk.AvatarHeight.Value = measured;
                    }
                }

                // Body colliders (head sphere + per-limb capsules) so a worn avatar has grab/interaction hitboxes -
                // the IK foot raycast already excludes the user's own colliders expecting these.
                avatarIk.GenerateBodyColliders();

                // No skeleton overlay on import: an imported model is something you look at and drag the
                // studio markers onto, and a cage of balls and shafts over it is in the way of both. -xlinka
                Logger.Debug($"ModelImporter(Phos): rigged biped avatar ({rig.Bones.Count} bones)");
            }
            else
            {
                Logger.Log("ModelImporter(Phos): skeleton is not a biped - skipping IK rig.");
            }
        }
        // A rig nothing above fitted a body to still gets something to point at and pick up: a capsule per
        // bone. Its static meshes get their own colliders when they are built.
        if (skelBuilder != null && (settings.GenerateColliders || settings.IsAvatarImport)
            && modelSlot.GetComponentInChildren<Collider>() == null)
        {
            var bones = new List<Slot>();
            foreach (var bone in skelBuilder.BoneSlots)
            {
                if (bone != null)
                    bones.Add(bone);
            }
            int made = BoneColliders.Build(bones);
            Logger.Debug($"ModelImporter(Phos): {made} bone colliders");
        }
        return skelBuilder;
    }

    private static Slot? FindTopmostBoneSlot(HashSet<Slot> boneSlots, Slot modelSlot)
    {
        Slot? best = null;
        int bestDepth = int.MaxValue;
        foreach (var bone in boneSlots)
        {
            if (bone == null || bone.IsDestroyed)
                continue;

            bool hasBoneAncestor = false;
            int depth = 0;
            for (var p = bone.Parent; p != null && p != modelSlot; p = p.Parent)
            {
                depth++;
                if (boneSlots.Contains(p))
                {
                    hasBoneAncestor = true;
                    break;
                }
            }

            if (!hasBoneAncestor && depth < bestDepth)
            {
                best = bone;
                bestDepth = depth;
            }
        }
        return best;
    }

    // Rescale to target height + center the model (otherwise it imports at authored scale/origin - a cm-authored
    // model would be 100x too big and spawn off-origin). Bounds come from the positions of the meshes the import
    // already built, which are the same authored vertices the parse handed over - taking them here instead of
    // walking the Assimp scene is what lets the scene be released before this runs. Data-model writes - call on
    // the world thread (from inside OnWorldAsync). -xlinka
    //
    // EACH MESH IS MEASURED WHERE ITS RENDERER DRAWS IT: its raw vertices through its renderer slot's transform
    // relative to the model. A skinned mesh draws there at rest too, since every bone's rest times its bind is
    // that mesh node's own transform. All meshes used to go through ONE shared transform, the armature's, and
    // a mesh whose node carries a scale of its own measured at the wrong size: a fur shell on a node scaled
    // 21.86 next to an armature at 100 read several times taller than it draws, and the dog wearing it
    // imported a few centimetres long. -xlinka
    // Metres per unit, as the file states it, or null when it states none. An FBX carries its unit factor in
    // centimetres and Assimp hands it through as scene metadata with the geometry left in file units.
    private static float? FileUnitScale(Assimp.Scene scene)
    {
        try
        {
            if (scene.Metadata != null && scene.Metadata.TryGetValue("UnitScaleFactor", out var entry) && entry.Data != null)
            {
                double factor = Convert.ToDouble(entry.Data, System.Globalization.CultureInfo.InvariantCulture);
                if (factor > 0 && double.IsFinite(factor))
                    return (float)(factor * 0.01);
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException)
        {
        }
        return null;
    }

    private static void ApplyModelTransform(IReadOnlyList<(List<float3> Points, Slot Slot)> measured, Slot modelSlot, ModelImportSettings settings, float baseScale)
    {
        var toModel = float4x4.TRS(modelSlot.GlobalPosition, modelSlot.GlobalRotation, modelSlot.GlobalScale).Inverse;

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        bool any = false;
        foreach (var (points, slot) in measured)
        {
            if (slot == null || slot.IsDestroyed)
                continue;
            var place = toModel * float4x4.TRS(slot.GlobalPosition, slot.GlobalRotation, slot.GlobalScale);
            for (int i = 0; i < points.Count; i++)
            {
                var p = place.MultiplyPoint(points[i]);
                if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
                if (p.z < minZ) minZ = p.z; if (p.z > maxZ) maxZ = p.z;
                any = true;
            }
        }
        if (!any) return;
        float height = maxY - minY;
        float scale = (settings.Rescale && height > 0.001f) ? settings.TargetHeight / height : baseScale;
        modelSlot.LocalScale.Value = new float3(scale, scale, scale);
        if (settings.Center)
        {
            float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
            modelSlot.LocalPosition.Value = new float3(-cx * scale, -minY * scale, -cz * scale);
        }
    }

    // The parse's node tree as plain data: name, decomposed local TRS, the mesh indices hanging off it and the
    // units they were grouped into.
    // Nothing here refers back to the parse, which is the point - an Assimp.Node keeps its parent chain, and
    // through that the whole scene, alive for as long as anything holds it. -xlinka
    private sealed class NodeData
    {
        public string Name = string.Empty;
        public bool HasTransform;
        public float3 Position;
        public floatQ Rotation = floatQ.Identity;
        public float3 Scale = float3.One;
        public int[]? MeshIndices;
        public int[]? Units;
        public List<NodeData>? Children;
    }

    private static NodeData? CaptureNode(Assimp.Node node)
    {
        if (node == null) return null;

        var data = new NodeData { Name = node.Name ?? string.Empty };
        // Transpose: AssimpNetter stores the row-major aiMatrix4x4 into System.Numerics by field name, so the
        // translation is in M14/M24/M34 - but Decompose reads M41/M42/M43 and would return (0,0,0) for every node,
        // collapsing the whole skeleton rest to the origin. Transposing puts translation where Decompose expects it.
        // This MUST match the transpose in MeshDecoder.ToFloat4x4 or the rest and the bind poses won't cancel. -xlinka
        if (System.Numerics.Matrix4x4.Decompose(System.Numerics.Matrix4x4.Transpose(node.Transform), out var scale, out var rot, out var trans))
        {
            data.HasTransform = true;
            data.Position = new float3(trans.X, trans.Y, trans.Z);
            data.Rotation = new floatQ(rot.X, rot.Y, rot.Z, rot.W);
            data.Scale = new float3(scale.X, scale.Y, scale.Z);
        }
        if (node.HasMeshes) data.MeshIndices = node.MeshIndices.ToArray();

        if (node.ChildCount > 0)
        {
            data.Children = new List<NodeData>(node.ChildCount);
            foreach (var child in node.Children)
            {
                var captured = CaptureNode(child);
                if (captured != null) data.Children.Add(captured);
            }
        }
        return data;
    }

    // One Slot per captured node, local TRS from the node's transform, parented to the caller's slot. Records a
    // name -> Slot map (first wins on name collision) so mesh bone names resolve to their bone Slot. -xlinka
    private static Slot? BuildNodeSlots(NodeData? node, Slot parentSlot,
        Dictionary<string, Slot> nameToSlot, List<(int[], Slot)> meshNodeSlots)
    {
        if (node == null) return null;

        var slot = parentSlot.AddSlot(string.IsNullOrEmpty(node.Name) ? "Node" : node.Name);
        if (node.HasTransform)
        {
            slot.LocalPosition.Value = node.Position;
            slot.LocalRotation.Value = node.Rotation;
            slot.LocalScale.Value = node.Scale;
        }
        if (!string.IsNullOrEmpty(node.Name) && !nameToSlot.ContainsKey(node.Name))
            nameToSlot[node.Name] = slot;

        if (node.Units != null) meshNodeSlots.Add((node.Units, slot));

        if (node.Children != null)
        {
            for (int i = 0; i < node.Children.Count; i++)
                BuildNodeSlots(node.Children[i], slot, nameToSlot, meshNodeSlots);
        }
        return slot;
    }

    // Every node's meshes as units, shared between nodes that name the same parts so instanced geometry still
    // bakes once. -xlinka
    private static void GroupNodes(NodeData? node, MeshGrouping.Part[] parts, List<MeshGrouping.Unit> units,
        Dictionary<string, int> unitByKey, ISet<int> noTriangles, Func<int, long> exactBlend)
    {
        if (node == null) return;

        if (node.MeshIndices != null)
        {
            var nodeUnits = MeshGrouping.ForNode(node.MeshIndices, parts, exactBlend, noTriangles);
            if (nodeUnits.Count > 0)
            {
                node.Units = new int[nodeUnits.Count];
                for (int i = 0; i < nodeUnits.Count; i++)
                {
                    string key = nodeUnits[i].Key;
                    if (!unitByKey.TryGetValue(key, out int index))
                    {
                        index = units.Count;
                        units.Add(nodeUnits[i]);
                        unitByKey[key] = index;
                    }
                    node.Units[i] = index;
                }
            }
        }

        if (node.Children != null)
        {
            for (int i = 0; i < node.Children.Count; i++)
                GroupNodes(node.Children[i], parts, units, unitByKey, noTriangles, exactBlend);
        }
    }

    // A material's textures resolved to loadable local:// URIs plus its base colors. Produced OFF-thread
    // (ResolveMaterialAsync does the local-DB IO), consumed on the world thread (BuildMaterialSync attaches the
    // components). Splitting it this way keeps the file IO off the render thread. -xlinka
    private sealed class ResolvedMaterial
    {
        public colorHDR AlbedoColor = new colorHDR(1f, 1f, 1f, 1f);
        public bool HasAlbedoColor;
        public colorHDR EmissiveColor = new colorHDR(0f, 0f, 0f, 1f);
        public bool HasEmissiveColor;
        public Uri? AlbedoUri;
        public Uri? NormalUri;
        public Uri? EmissiveUri;
        public Uri? MetallicUri;
        public Uri? OcclusionUri;
        // A mesh with colours of its own draws with this material, and the import shows them.
        public bool VertexColored;
    }

    // How many texture slots this scene's USED materials actually name. Counts the same slots
    // ResolveMaterialAsync goes looking for, so the N-of-M the user reads matches the work being done.
    private static int CountTextureSlots(Assimp.Scene scene)
    {
        var seen = new HashSet<int>();
        int total = 0;
        foreach (var amesh in scene.Meshes)
        {
            if (amesh == null || !seen.Add(amesh.MaterialIndex)) continue;
            if (amesh.MaterialIndex < 0 || amesh.MaterialIndex >= scene.MaterialCount) continue;
            var amat = scene.Materials[amesh.MaterialIndex];
            if (amat == null) continue;
            if (amat.HasTextureDiffuse || HasTexture(amat, Assimp.TextureType.BaseColor)) total++;
            if (HasTexture(amat, Assimp.TextureType.Normals)) total++;
            if (HasTexture(amat, Assimp.TextureType.Emissive)) total++;
            if (HasTexture(amat, Assimp.TextureType.Metalness) || HasTexture(amat, Assimp.TextureType.Roughness)) total++;
            if (HasTexture(amat, Assimp.TextureType.AmbientOcclusion) || HasTexture(amat, Assimp.TextureType.Lightmap)) total++;
        }
        return total;
    }

    private static bool HasTexture(Assimp.Material amat, Assimp.TextureType type)
        => amat.GetMaterialTextureCount(type) > 0;

    private static int CountResolvedTextures(ResolvedMaterial? rm)
    {
        if (rm == null) return 0;
        int n = 0;
        if (rm.AlbedoUri != null) n++;
        if (rm.NormalUri != null) n++;
        if (rm.EmissiveUri != null) n++;
        if (rm.MetallicUri != null) n++;
        if (rm.OcclusionUri != null) n++;
        return n;
    }

    // Resolve an Assimp material's colors + texture URIs (OFF-thread). Textures are recorded into the
    // content-addressed local DB so they replicate to joiners (embedded bytes saved by hash; external files
    // imported). Returns null on an invalid index. -xlinka
    private static async Task<ResolvedMaterial?> ResolveMaterialAsync(Assimp.Scene scene, int materialIndex, string modelDir, LocalDB? localDB)
    {
        if (materialIndex < 0 || materialIndex >= scene.MaterialCount)
            return null;
        var amat = scene.Materials[materialIndex];
        var rm = new ResolvedMaterial();

        if (amat.HasColorDiffuse)
        {
            var c = amat.ColorDiffuse; // System.Numerics.Vector4 (rgba)
            rm.AlbedoColor = new colorHDR(c.X, c.Y, c.Z, c.W);
            rm.HasAlbedoColor = true;
        }
        if (amat.HasColorEmissive)
        {
            var e = amat.ColorEmissive;
            rm.EmissiveColor = new colorHDR(e.X, e.Y, e.Z, e.W);
            rm.HasEmissiveColor = true;
        }

        rm.AlbedoUri = await ResolveTextureUriAsync(scene, amat, Assimp.TextureType.BaseColor, modelDir, localDB).ConfigureAwait(false)
                    ?? await ResolveTextureUriAsync(scene, amat, Assimp.TextureType.Diffuse, modelDir, localDB).ConfigureAwait(false);
        rm.NormalUri = await ResolveTextureUriAsync(scene, amat, Assimp.TextureType.Normals, modelDir, localDB).ConfigureAwait(false);
        rm.EmissiveUri = await ResolveTextureUriAsync(scene, amat, Assimp.TextureType.Emissive, modelDir, localDB).ConfigureAwait(false);
        // Metallic/roughness map. glTF packs this combined (B=metallic, G=roughness); Assimp surfaces it as the
        // Metalness slot (and DiffuseRoughness for the FBX-style split). We assign the texture straight through -
        // repacking to a metallic-in-RGB + smoothness-in-alpha layout would need an image encoder we don't ship,
        // so this is the texture without the channel remap. Linear, no mips ideally (handled by the texture hook). -xlinka
        rm.MetallicUri = await ResolveTextureUriAsync(scene, amat, Assimp.TextureType.Metalness, modelDir, localDB).ConfigureAwait(false)
                      ?? await ResolveTextureUriAsync(scene, amat, Assimp.TextureType.Roughness, modelDir, localDB).ConfigureAwait(false);
        rm.OcclusionUri = await ResolveTextureUriAsync(scene, amat, Assimp.TextureType.AmbientOcclusion, modelDir, localDB).ConfigureAwait(false)
                       ?? await ResolveTextureUriAsync(scene, amat, Assimp.TextureType.Lightmap, modelDir, localDB).ConfigureAwait(false);
        return rm;
    }

    // Attach a material + its texture providers from a pre-resolved material (WORLD thread - call from inside
    // OnWorldAsync). No IO, no awaits - just data-model writes. The dialog options drive material type (Unlit, PBS,
    // or a kind converted from the PBS build), double-sidedness, albedo/emissive gating, texture mipmaps, and the
    // vertex colour form of the kind when its meshes carry colours (WithVertexColors). It is built ON matSlot, which the caller picked: the part's own "Material" child, or a slot in the import's
    // Assets. type is this material's kind, the dialog's choice or its per-material answer. -xlinka
    private static MaterialProvider BuildMaterialSync(Slot matSlot, ResolvedMaterial rm, ModelImportSettings settings,
        ModelMaterialType type, AssetHome home)
    {
        // Diagnostic: which maps did this material actually resolve? A stylized model with ONLY albedo should NOT
        // get metallic/occlusion maps - if it does (Assimp handing back a spurious slot), they drive bogus
        // roughness/AO and show as soft specular/occlusion blobs on the surface. -xlinka
        Logger.Debug($"BuildMaterialSync[maps] on '{matSlot.Parent?.SlotName.Value}': type={type} albedo={rm.AlbedoUri} normal={rm.NormalUri != null} emissive={rm.EmissiveUri != null} metallic={rm.MetallicUri != null} occlusion={rm.OcclusionUri != null}");

        var cull = settings.MakeDualSided ? Culling.None : Culling.Back;
        bool noMips = settings.ForceNoMipMaps;

        // Unlit is opt-in. It carries only albedo (tint + base texture); normal/emissive/metallic/
        // occlusion maps have no slot on it, so we deliberately drop them for this material type. -xlinka
        if (type == ModelMaterialType.Unlit)
        {
            var unlit = matSlot.AttachComponent<UnlitMaterial>();
            unlit.Culling.Value = cull;
            if (rm.VertexColored)
                unlit.UseVertexColor.Value = true;
            if (settings.ImportAlbedoColor && rm.HasAlbedoColor)
                unlit.TintColor.Value = rm.AlbedoColor;
            if (rm.AlbedoUri != null)
            {
                var tex = home.Texture(matSlot, rm.AlbedoUri, isNormal: false, noMips);
                unlit.Texture.Target = tex;
                var ac = unlit.TintColor.Value;
                if (ac.r <= 0.001f && ac.g <= 0.001f && ac.b <= 0.001f)
                    unlit.TintColor.Value = new colorHDR(1f, 1f, 1f, ac.a <= 0.001f ? 1f : ac.a);
            }
            return unlit;
        }

        var pbs = matSlot.AttachComponent<PBS_Metallic>();
        pbs.Culling.Value = cull;

        if (settings.ImportAlbedoColor && rm.HasAlbedoColor) pbs.AlbedoColor.Value = rm.AlbedoColor;
        if (settings.ImportEmissive && rm.HasEmissiveColor) pbs.EmissiveColor.Value = rm.EmissiveColor;

        if (rm.AlbedoUri != null)
        {
            pbs.AlbedoTexture.Target = home.Texture(matSlot, rm.AlbedoUri, isNormal: false, noMips);
            // A base-color FACTOR of ~0 alongside a base-color TEXTURE is legal in glTF, but the shader multiplies
            // texture * factor -> a fully black mesh. Force the tint to white so the texture actually shows. -xlinka
            var ac = pbs.AlbedoColor.Value;
            if (ac.r <= 0.001f && ac.g <= 0.001f && ac.b <= 0.001f)
                pbs.AlbedoColor.Value = new colorHDR(1f, 1f, 1f, ac.a <= 0.001f ? 1f : ac.a);
        }

        if (rm.NormalUri != null)
            pbs.NormalMap.Target = home.Texture(matSlot, rm.NormalUri, isNormal: true, noMips);

        if (settings.ImportEmissive && rm.EmissiveUri != null)
        {
            pbs.EmissiveMap.Target = home.Texture(matSlot, rm.EmissiveUri, isNormal: false, noMips);
            // Same trap as albedo: an emissive map with a ~0 emissive factor multiplies to nothing. Force white. -xlinka
            var ec = pbs.EmissiveColor.Value;
            if (ec.r <= 0.001f && ec.g <= 0.001f && ec.b <= 0.001f)
                pbs.EmissiveColor.Value = new colorHDR(1f, 1f, 1f, 1f);
        }

        if (rm.MetallicUri != null)
            pbs.MetallicMap.Target = home.Texture(matSlot, rm.MetallicUri, isNormal: false, noMips);

        if (rm.OcclusionUri != null)
            pbs.OcclusionMap.Target = home.Texture(matSlot, rm.OcclusionUri, isNormal: false, noMips);

        if (rm.VertexColored && WithVertexColors(pbs, type) is { } colored)
            return colored;
        return ConvertMaterial(pbs, type);
    }

    // The kinds past PBS Metallic and Unlit: the full PBS build hands its colour, textures, normal and emission
    // to the chosen kind wherever that kind has a place for them, the way the material tool converts.
    private const float ImportOutlineMetres = 0.002f;

    private static MaterialProvider ConvertMaterial(PBS_Metallic pbs, ModelMaterialType type)
    {
        var slot = pbs.Slot;
        MaterialProvider? target = type switch
        {
            ModelMaterialType.Specular => slot.AttachComponent<PBS_Specular>(),
            ModelMaterialType.DualSided => slot.AttachComponent<PBS_MetallicDualSided>(),
            ModelMaterialType.Triplanar => slot.AttachComponent<PBS_Triplanar>(),
            ModelMaterialType.Toon or ModelMaterialType.ToonOutline => slot.AttachComponent<XiexeToonMaterial>(),
            ModelMaterialType.Wireframe => slot.AttachComponent<WireframeMaterial>(),
            _ => null,
        };
        if (target == null)
            return pbs;

        MaterialConversion.CopyProperties(pbs, target);
        if (type == ModelMaterialType.ToonOutline && target is XiexeToonMaterial toon)
        {
            toon.OutlineWidth.Value = ImportOutlineMetres;
            toon.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 1f);
        }
        pbs.Destroy();
        return target;
    }

    // One renderer over one mesh. materialIndices holds the scene material of each submesh in order: a single
    // entry takes the single Material exactly as a lone mesh always has, several fill the per-surface list with
    // one material built per distinct scene material. meshIndex >= 0 only when the provider has to decode the
    // source model itself. -xlinka
    // Where a renderer's surfaces sit in their model, for a material override to look them up by.
    private readonly record struct SurfaceContext(Slot Model, Slot Node, int FirstIndex, Dictionary<int, string> MaterialNames);

    private static void AttachRenderer(Slot meshSlot, MeshGrouping.Kind kind, Uri meshUrl, int meshIndex,
        int[] materialIndices, Dictionary<int, ResolvedMaterial?> resolvedMaterials, ModelImportSettings settings,
        SkeletonBuilder? skelBuilder, ModelImportResult result, SurfaceContext surfaces, AssetHome home, ShapeFit? fit)
    {
        var provider = home.Mesh(meshSlot, meshUrl, meshIndex, meshSlot.SlotName.Value);

        home.BeginPart();
        var materials = new MaterialProvider?[materialIndices.Length];
        for (int k = 0; k < materialIndices.Length; k++)
        {
            if (settings.MaterialOverride != null)
            {
                surfaces.MaterialNames.TryGetValue(materialIndices[k], out var sourceName);
                var supplied = settings.MaterialOverride(new ModelMaterialRequest(
                    sourceName ?? "", surfaces.Model, surfaces.Node, meshSlot, surfaces.FirstIndex + k));
                if (supplied != null)
                {
                    materials[k] = supplied;
                    continue;
                }
            }
            resolvedMaterials.TryGetValue(materialIndices[k], out var rmat);
            surfaces.MaterialNames.TryGetValue(materialIndices[k], out var materialName);
            materials[k] = home.Material(meshSlot, materialIndices[k], rmat, materialName);
        }
        var firstMaterial = materials.Length > 0 ? materials[0] : null;

        if (kind != MeshGrouping.Kind.Plain)
        {
            var smr = meshSlot.AttachComponent<SkinnedMeshRenderer>();
            smr.MeshAsset.Target = provider;
            if (skelBuilder != null) smr.Skeleton.Target = skelBuilder;
            if (firstMaterial != null) smr.Material.Target = firstMaterial;
            if (materials.Length > 1)
            {
                foreach (var material in materials)
                    smr.Materials.Add(material!);
            }
            result.SkinnedMeshes.Add(smr);
            return;
        }

        // Static mesh: a plain MeshRenderer off the Phos asset.
        var mr = meshSlot.AttachComponent<MeshRenderer>();
        mr.Mesh.Target = provider;
        if (materials.Length > 1)
        {
            foreach (var material in materials)
                mr.Materials.Add(material!);
        }
        else if (firstMaterial != null)
        {
            mr.Material.Target = firstMaterial;
        }

        // Optional collision geometry off the same mesh provider (skinned meshes are skipped:
        // their geometry deforms, so a static collider wouldn't track it).
        //
        // NOT something you walk into. An imported model is a thing somebody put in
        // the room - a chair, a statue, a prop - and every one of them used to be a
        // wall in its own shape, so importing furniture built a maze. The collider
        // is still here and still solid to lasers, grabs, touches and falling
        // physics objects; it just does not stop a person. Tick CharacterCollider on
        // it to make an imported mesh part of the floor plan. -xlinka
        if (settings.GenerateColliders)
        {
            if (settings.FitColliders && fit != null)
            {
                fit.Attach(meshSlot, provider, settings.SolidToPeople);
                return;
            }
            var collider = meshSlot.AttachComponent<MeshCollider>();
            collider.Mesh.Target = provider;
            collider.CharacterCollider.Value = settings.SolidToPeople;
        }
    }

    // Resolve a material texture slot to a loadable URI. Embedded textures (path "*N") are saved into the local DB
    // by content hash; external paths resolve relative to the model dir and are imported into the local DB too.
    // Both yield a local:// URI (replicates to joiners; the loader resolves the extension back through LocalDB).
    // Falls back to a file:// URI when there's no local DB. -xlinka
    private static async Task<Uri?> ResolveTextureUriAsync(Assimp.Scene scene, Assimp.Material amat, Assimp.TextureType type, string modelDir, LocalDB? localDB)
    {
        if (amat.GetMaterialTextureCount(type) == 0)
            return null;
        if (!amat.GetMaterialTexture(type, 0, out var slot) || string.IsNullOrEmpty(slot.FilePath))
            return null;
        string path = slot.FilePath;

        var embedded = scene.GetEmbeddedTexture(path);
        if (embedded != null)
        {
            try
            {
                byte[] bytes;
                string fmt;
                if (embedded.HasCompressedData && embedded.CompressedData is { Length: > 0 })
                {
                    // Already a real image file (png/jpg/...) - save the bytes verbatim.
                    bytes = embedded.CompressedData;
                    fmt = string.IsNullOrEmpty(embedded.CompressedFormatHint) ? "png" : embedded.CompressedFormatHint;
                }
                else if (embedded.HasNonCompressedData && embedded.NonCompressedData is { Length: > 0 } && embedded.Width > 0)
                {
                    // Raw ARGB texels - re-encode to an uncompressed 32-bit BMP. BMP is just a header + raw rows,
                    // so this needs no image encoder library and the texture loader reads it natively. Skip this
                    // and raw embedded textures get silently dropped, leaving the mesh untextured. -xlinka
                    bytes = EncodeTexelsToBmp(embedded);
                    fmt = "bmp";
                    Logger.Debug($"ModelImporter: re-encoded uncompressed embedded texture '{path}' ({embedded.Width}x{embedded.Height}) to BMP.");
                }
                else
                {
                    Logger.Warn($"ModelImporter: embedded texture '{path}' has no usable data (compressed={embedded.HasCompressedData}, raw={embedded.HasNonCompressedData}); skipping.");
                    return null;
                }

                if (localDB != null)
                {
                    var uri = await localDB.SaveAssetAsync(bytes, fmt).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(uri)) return new Uri(await TextureStorage.ForTransferAsync(localDB, uri).ConfigureAwait(false));
                }
                // No local DB - fall back to a temp file so the texture at least loads on this machine. Sealed
                // under a name that says nothing, like every cache file; the gather opens it by its header.
                string dir = Lumora.Core.Persistence.PathResolver.Resolve("tmp", "textures");
                Directory.CreateDirectory(dir);
                Persistence.LocalEncryption.WireSealedStorage();
                string tmp = Path.Combine(dir, Lumora.Nexus.Storage.SealedKeys.NameFor("tmptex", $"{(uint)path.GetHashCode():x8}.{fmt}"));
                if (!File.Exists(tmp))
                    Lumora.Nexus.Storage.SealedFile.WriteAllBytes(tmp, bytes);
                return new Uri(tmp);
            }
            catch (Exception ex)
            {
                Logger.Warn($"ModelImporter: failed to extract embedded texture '{path}': {ex.Message}");
                return null;
            }
        }

        string? full = ResolveExternalTexturePath(path, modelDir);
        if (full == null)
        {
            Logger.Warn($"ModelImporter: texture '{path}' not found beside the model; surface will be untextured.");
            return null;
        }
        if (localDB != null)
        {
            var uri = await localDB.ImportLocalAssetAsync(full, LocalDB.ImportLocation.Copy).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(uri)) return new Uri(await TextureStorage.ForTransferAsync(localDB, uri).ConfigureAwait(false));
        }
        return new Uri(full);
    }

    // Find a texture the model REFERENCES but does not carry.
    //
    // Exporters write whatever path the authoring machine had, and that is very often an absolute path
    // into somebody else's home directory. Taking a rooted path at face value therefore throws away
    // every texture of every model exported that way, even when the user has the texture folder sitting
    // right next to the file - which is how a fully-textured model arrives as a grey checkerboard.
    //
    // So the reference is treated as a HINT and the filename is what matters: try the path as given,
    // then the same relative shape under the model's folder, then the bare filename, then the handful
    // of folder names exporters actually use, then one bounded search. -xlinka
    private static readonly string[] TextureSubfolders =
    {
        "Textures", "textures", "Texture", "tex", "Materials", "materials", "maps", "images",
    };

    private static string? ResolveExternalTexturePath(string path, string modelDir)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
            return null;

        // As written, for the case where the model really did ship beside its textures - but only while that
        // stays inside the model's folder. An absolute path, or one climbing out with "..", is read for its name
        // alone, below: a texture is copied into the world for everyone in it, so a model must not be able to name
        // any picture on this disk and have it shared. -xlinka
        string normalized = path.Replace('\\', '/');
        bool staysInside = !Path.IsPathRooted(path) && !normalized.Contains(':')
            && Array.IndexOf(normalized.Split('/'), "..") < 0;
        if (staysInside)
        {
            string asGiven = Path.Combine(modelDir, path);
            if (File.Exists(asGiven))
                return asGiven;
        }

        // The tail of an absolute author path is usually still the right SHAPE - "Textures/foo.png" -
        // so try progressively shorter tails against the model's own folder.
        var parts = Array.FindAll(normalized.Split('/', StringSplitOptions.RemoveEmptyEntries),
            p => p != "." && p != ".." && !p.Contains(':'));
        for (int start = parts.Length - 1; start >= 0 && parts.Length - start <= 3; start--)
        {
            string tail = string.Join(Path.DirectorySeparatorChar, parts, start, parts.Length - start);
            string candidate = Path.Combine(modelDir, tail);
            if (File.Exists(candidate))
                return candidate;
        }

        if (parts.Length == 0)
            return null;
        string file = parts[^1];
        foreach (var folder in TextureSubfolders)
        {
            string candidate = Path.Combine(modelDir, folder, file);
            if (File.Exists(candidate))
                return candidate;
        }

        // A DEPTH-LIMITED look, never a recursive one.
        //
        // This used to end in Directory.EnumerateFiles(modelDir, file, AllDirectories) with a cap on
        // RESULTS, which caps nothing: the enumerator still walks every directory below the model before
        // it can yield anything. A model sitting at a drive root therefore searched the whole drive, and
        // measured 34 seconds per missing texture on a cold cache - six misses on one avatar. Two levels
        // covers every real layout (beside the model, or one folder down) and cannot run away. -xlinka
        string? found = FindAtDepth(modelDir, file, 2);
        if (found != null)
            return found;

        // One level up too, for the "Model/" and "Textures/" siblings layout that exporters produce: only into the
        // parent's texture folders, by name, never through the parent's whole tree.
        var parent = Directory.GetParent(modelDir)?.FullName;
        if (parent == null)
            return null;
        foreach (var folder in TextureSubfolders)
        {
            var sibling = Path.Combine(parent, folder);
            if (Directory.Exists(sibling) && FindAtDepth(sibling, file, 1) is { } hit)
                return hit;
        }
        return null;
    }

    private static string? FindAtDepth(string root, string file, int depth)
    {
        try
        {
            string direct = Path.Combine(root, file);
            if (File.Exists(direct))
                return direct;

            if (depth <= 0)
                return null;

            int scanned = 0;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                // Bounded on DIRECTORIES, which is the thing that actually costs.
                if (++scanned > 32)
                    break;
                var hit = FindAtDepth(dir, file, depth - 1);
                if (hit != null)
                    return hit;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"ModelImporter: texture search under '{root}' failed: {ex.Message}");
        }

        return null;
    }

    // Encode Assimp raw ARGB texels into an uncompressed 32-bit BMP (BGRA pixels, bottom-up rows). No encoder
    // library needed - a 32bpp BMP is a 54-byte header + raw pixel rows, 4-byte aligned so no row padding.
    // Used for embedded textures that arrive as raw pixels instead of a packed image file. -xlinka
    private static byte[] EncodeTexelsToBmp(Assimp.EmbeddedTexture tex)
    {
        int w = tex.Width;
        int h = tex.Height;
        var texels = tex.NonCompressedData;
        const int headerSize = 54;             // 14 (file) + 40 (info)
        int pixelBytes = checked(w * h * 4);
        int fileSize = headerSize + pixelBytes;

        var buf = new byte[fileSize];
        // BITMAPFILEHEADER
        buf[0] = (byte)'B'; buf[1] = (byte)'M';
        WriteInt32LE(buf, 2, fileSize);
        WriteInt32LE(buf, 10, headerSize);     // pixel data offset
        // BITMAPINFOHEADER
        WriteInt32LE(buf, 14, 40);             // header size
        WriteInt32LE(buf, 18, w);
        WriteInt32LE(buf, 22, h);              // positive => bottom-up rows
        buf[26] = 1; buf[27] = 0;              // planes = 1
        buf[28] = 32; buf[29] = 0;             // 32 bpp
        // compression BI_RGB (0), sizes/resolutions left zero are valid for BI_RGB

        int dst = headerSize;
        // BMP rows are bottom-up: write the last source row first.
        for (int y = h - 1; y >= 0; y--)
        {
            int rowStart = y * w;
            for (int x = 0; x < w; x++)
            {
                var t = texels[rowStart + x]; // Texel exposes R/G/B/A bytes
                buf[dst++] = t.B;
                buf[dst++] = t.G;
                buf[dst++] = t.R;
                buf[dst++] = t.A;
            }
        }
        return buf;
    }

    private static void WriteInt32LE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
