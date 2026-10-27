// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Components.Import;
using Lumora.Core.Logging;
using Lumora.Core.Math;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop.Unity;

public sealed class UnityPackageReport : IDisposable
{
    public UnityPackageArchive Archive = null!;
    public List<UnityImportCandidate> Candidates = new();
    public List<string> Notes = new();
    public int Materials, Textures, Shaders, Models, AnimationClips, AnimatorControllers, Scripts;
    public int Animations => AnimationClips + AnimatorControllers;

    public void Dispose() => Archive?.Dispose();
}

public sealed class UnityImportResult
{
    public bool Succeeded;
    public string? FailureReason;
    public Slot? Root;
    public int SurfacesMatched, SurfacesUnmatched;
    public int TexturesImported;
    public int CubemapsImported;
    public bool VisemesMapped;
    public int Chains, ChainsSkipped, Colliders;
    public int Links, LinksSkipped;
    public int Toggles, Gestures, Constraints, Animations, AnimationControllers, Contacts;
    public int Sliders;
    // Model files placed inside the avatar besides its own: outfits, eye meshes, props.
    public int Models;
    // Meshes made for objects the project added: built-in shapes and mesh assets.
    public int AddedMeshes;
    // The components built from each Unity object's scripts (its bone chains and colliders), for clips that
    // switch those scripts on and off.
    internal readonly Dictionary<Slot, List<Component>> Scripts = new();
    public readonly Dictionary<UnityShaderFamily, int> MaterialsByFamily = new();
    public readonly List<string> Notes = new();
    public int MaterialsConverted => MaterialsByFamily.Values.Sum();
}

// Brings one model out of a Unity package. The package's model file goes through the ordinary model
// importer, so meshes, skeleton, rig and avatar setup are exactly what a dropped model gets; what this
// adds is everything the Unity project knew about it: the materials its renderers were given (converted
// to ours), and which parts the placement switched off. Textures are copied into the local database
// before any of it touches the world, the same as a model's own textures. -xlinka
public static class UnityPackageImporter
{
    private static readonly HashSet<string> ReadableImages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".webp", ".tif", ".tiff", ".psd", ".gif", ".dds", ".exr", ".hdr",
    };

    public static UnityPackageReport Read(string packagePath, CancellationToken cancel = default)
    {
        var archive = UnityPackageArchive.Extract(packagePath, PathResolver.TempPath, cancel);
        var report = new UnityPackageReport { Archive = archive };
        report.Candidates = UnityCandidateFinder.Find(archive, report.Notes);
        report.Notes = report.Notes.Distinct().ToList();
        foreach (var asset in archive.ByGuid.Values)
        {
            switch (asset.Extension)
            {
                case ".mat": report.Materials++; break;
                case ".shader": report.Shaders++; break;
                case ".anim": report.AnimationClips++; break;
                case ".controller": case ".overridecontroller": report.AnimatorControllers++; break;
                case ".cs": report.Scripts++; break;
                case ".fbx": case ".obj": case ".dae": case ".blend": report.Models++; break;
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd": case ".tif": case ".tiff": case ".exr": case ".bmp":
                case ".gif": case ".dds": case ".hdr": case ".webp": report.Textures++; break;
            }
        }
        return report;
    }

    public static async Task<UnityImportResult> ImportAsync(UnityPackageReport report, UnityImportCandidate candidate, Slot target, bool asAvatar)
    {
        var result = new UnityImportResult();
        var archive = report.Archive;
        if (!candidate.IsPrepared)
            await Task.Run(() => candidate.Prepare(archive)).ConfigureAwait(false);
        result.Notes.AddRange(candidate.Notes);
        var model = candidate.Model;
        if (model.File == null || !File.Exists(model.File))
        {
            result.FailureReason = $"{model.Path} is not in the package.";
            return result;
        }
        if (!ModelImporter.IsSupportedFormat(model.File))
        {
            result.FailureReason = $"{Path.GetExtension(model.File)} models cannot be imported.";
            return result;
        }

        var meta = UnityModelMeta.Parse(model.Meta);
        var localDB = Engine.Current?.LocalDB;
        // The menu first: the materials its switches put on have to be converted with the rest.
        UnityVrcPlan? vrc = null;
        if (asAvatar && candidate.Descriptor != null)
        {
            try
            {
                var contactParameters = UnityAvatarBuilder.ContactParameters(candidate);
                vrc = await Task.Run(() => UnityVrcPlan.Build(archive, candidate.Descriptor, contactParameters, UnityFullController.Merges(archive, candidate))).ConfigureAwait(false);
                if (vrc != null)
                    candidate.SwappedMaterials.UnionWith(vrc.MaterialGuids);
            }
            catch (Exception ex)
            {
                result.Notes.Add("The avatar's menu could not be read: " + ex.Message);
            }
        }
        Uri? animatorUri = null;
        async Task<Uri> StoreAnimator(UnityAnimatorProgram animation)
        {
            var bytes = await Task.Run(animation.ToBytes).ConfigureAwait(false);
            if (localDB != null) return new Uri(await localDB.SaveAssetAsync(bytes, ".luanim").ConfigureAwait(false));
            string path = Path.Combine(PathResolver.TempPath, "animator-" + Guid.NewGuid().ToString("N") + ".luanim");
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            return new Uri(path);
        }
        if (vrc?.Program is { } program)
        {
            try
            {
                animatorUri = await StoreAnimator(program).ConfigureAwait(false);
            }
            catch (Exception ex) { result.Notes.Add("Animator could not be stored; using converted poses: " + ex.Message); }
        }
        if (vrc != null)
            foreach (var body in vrc.BodyPrograms)
            {
                if (body.Type != 0 || body.Program.Clips.Count == 0)
                {
                    result.Notes.Add(body.Program.Clips.Count == 0
                        ? $"{body.Name}: its motion clips are absent from this package; keeping procedural motion."
                        : $"{body.Name}: this playable layer type is not supported yet; keeping procedural motion.");
                    continue;
                }
                try { body.AssetUri = await StoreAnimator(body.Program).ConfigureAwait(false); }
                catch (Exception ex) { result.Notes.Add($"{body.Name}: could not store body animation: {ex.Message}"); }
            }
        var models = new List<(UnityPackageAsset Model, UnityModelMeta Meta)> { (model, meta) };
        foreach (var part in candidate.Parts)
            models.Add((part.Model, part.Meta));
        await UnityAvatarBuilder.StoreAudioAsync(archive, candidate, localDB).ConfigureAwait(false);
        var plan = await Task.Run(() => PlanMaterialsAsync(archive, candidate, models, localDB, result)).ConfigureAwait(false);
        var addedMeshes = await Task.Run(() => UnityAddedMeshes.BakeAsync(archive, candidate, localDB, result.Notes)).ConfigureAwait(false);
        var clips = new Dictionary<string, UnityAnimationClip?>(StringComparer.OrdinalIgnoreCase);
        UnityAnimationClip? ClipFor(string guid)
        {
            if (!clips.TryGetValue(guid, out var clip))
                clips[guid] = clip = UnityAnimationClip.Parse(archive.ReadText(archive.Find(guid)));
            return clip;
        }

        var built = new Dictionary<string, MaterialProvider>(StringComparer.OrdinalIgnoreCase);
        var matcher = new SurfaceMatcher(candidate.MaterialsById, candidate.MaterialsByName, candidate.ActiveById, candidate.EnabledById,
            candidate.ActiveByName, meta, plan, result, built);
        Slot? nodeRoot = null;
        var settings = new ModelImportSettings
        {
            IsAvatarImport = asAvatar,
            SetupIK = asAvatar,
            // A project's scene keeps its own colliders and its own idea of what moves; an avatar is picked up
            // whole.
            Grabbable = asAvatar,
            GenerateColliders = false,
            ImportAnimations = !asAvatar,
            ImportBones = true,
            Rescale = false,
            // The size the project showed it at: the file's own unit when "Convert Units" was on, times the
            // model's scale factor.
            UseFileUnits = meta.UseFileScale,
            Scale = meta.GlobalScale,
            // Not centred on its meshes: an avatar's root is its floor and the middle of its body, and what the
            // project hung off the model (colliders, contacts, a floor plane) is placed against that root. Mesh
            // hanging below the feet in the bind pose had lifted the whole model, floor plane and all, by as much.
            Center = false,
            HumanBones = asAvatar && meta.HumanBones.Count > 0 ? meta.HumanoidMap() : null,
            MaterialOverride = matcher.Resolve,
            AfterBuild = done =>
            {
                result.Root = done.RootSlot;
                nodeRoot = done.NodeRoot;
                matcher.ApplyStates(done.NodeRoot);
            },
        };

        try
        {
            var handler = ImportHandlers.Model;
            if (handler != null)
                await handler.ImportAsync(target, model.File, new ModelImportRequest { Settings = settings }).ConfigureAwait(false);
            else
            {
                var imported = await ModelImporter.ImportModelAsync(model.File, target, settings, localDB).ConfigureAwait(false);
                if (!imported.Success)
                    result.FailureReason = imported.ErrorMessage;
            }
            if (result.Root is { IsDestroyed: false } root && result.FailureReason == null)
                await AssembleAsync(candidate, root, nodeRoot, meta, plan, built, matcher, result, asAvatar, vrc, ClipFor, localDB, animatorUri, addedMeshes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.FailureReason = ex.Message;
            Logger.Warn($"UnityPackage: '{candidate.Name}' failed: {ex}");
        }
        if (result.Root == null && result.FailureReason == null)
            result.FailureReason = "The model did not import.";
        result.Succeeded = result.FailureReason == null;
        Logger.Log($"UnityPackage: '{candidate.Name}' from {Path.GetFileName(archive.SourcePath)}: {(result.Succeeded ? "imported" : "failed: " + result.FailureReason)}; "
            + $"{result.SurfacesMatched} surfaces given their Unity materials, {result.SurfacesUnmatched} left with the model's own; "
            + string.Join(", ", result.MaterialsByFamily.Select(f => $"{f.Value} {f.Key}")));
        return result;
    }

    // The body is in: every other model placed in the avatar comes in where its placement hangs, the
    // objects the project added are made, and then the avatar's own setup goes on over all of it. The
    // avatar's root scale goes on last, so every length worked out above is in the model's own units.
    private static async Task AssembleAsync(UnityImportCandidate candidate, Slot root, Slot? nodeRoot, UnityModelMeta meta, MaterialPlan plan,
        Dictionary<string, MaterialProvider> built, SurfaceMatcher matcher, UnityImportResult result, bool asAvatar, UnityVrcPlan? vrc,
        Func<string, UnityAnimationClip?> clipFor, LocalDB? localDB, Uri? animatorUri, Dictionary<UnityMeshRef, Uri> addedMeshes)
    {
        var world = root.World;
        UnityAvatarBuilder.Assembly assembly = null!;
        await OnWorld(world, () => assembly = UnityAvatarBuilder.Assemble(candidate, root, nodeRoot, meta)).ConfigureAwait(false);

        var noNames = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        var noSwitches = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var part in candidate.Parts.OrderBy(p => candidate.Parts.Count(other => !ReferenceEquals(other, p) && p.Root.IsUnder(other.Root))))
        {
            var file = part.Model.File;
            if (file == null || !File.Exists(file) || !ModelImporter.IsSupportedFormat(file))
            {
                result.Notes.Add($"{part.Root.Name}: {part.Model.Path} cannot be imported.");
                continue;
            }
            Slot? place = null;
            await OnWorld(world, () => place = UnityAvatarBuilder.PlaceFor(assembly, part)).ConfigureAwait(false);
            if (place == null)
            {
                result.Notes.Add($"{part.Root.Name}: nothing in the avatar to hang it from.");
                continue;
            }
            var partMeta = part.Meta;
            var partMatcher = new SurfaceMatcher(part.MaterialsById, noNames, part.ActiveById, part.EnabledById, noSwitches, partMeta, plan, result, built);
            var settings = new ModelImportSettings
            {
                IsAvatarImport = false,
                SetupIK = false,
                Grabbable = false,
                GenerateColliders = false,
                ImportAnimations = false,
                ImportBones = true,
                Rescale = false,
                Center = false,
                UseFileUnits = partMeta.UseFileScale,
                Scale = partMeta.GlobalScale,
                MaterialOverride = partMatcher.Resolve,
                AfterBuild = done =>
                {
                    partMatcher.ApplyStates(done.NodeRoot);
                    UnityAvatarBuilder.AddPart(assembly, part, done.RootSlot, done.NodeRoot);
                },
            };
            var imported = await ModelImporter.ImportModelAsync(file, place, settings, localDB).ConfigureAwait(false);
            if (imported.Success)
                result.Models++;
            else
                result.Notes.Add($"{part.Root.Name}: {imported.ErrorMessage}");
        }

        List<(SkinnedMeshRenderer Renderer, List<(string Name, string Shape, float Weight)> Shapes)>? weights = null;
        await OnWorld(world, () =>
        {
            result.AddedMeshes = UnityAddedMeshes.Build(candidate, assembly, addedMeshes, guid => matcher.Material(root, guid));
            UnityAvatarBuilder.Complete(assembly);
            UnityAvatarBuilder.Apply(candidate, root, nodeRoot, meta, result, asAvatar,
                guid => matcher.Material(root, guid), vrc, clipFor, localDB, animatorUri, assembly);
            var scale = candidate.RootScale;
            var size = new float3(MathF.Abs(scale.x), MathF.Abs(scale.y), MathF.Abs(scale.z));
            if (size.x > 1e-4f && size.y > 1e-4f && size.z > 1e-4f && float3.Distance(size, float3.One) > 1e-4f)
                root.LocalScale.Value *= size;
            weights = UnityAvatarBuilder.ShapeWeights(assembly);
        }).ConfigureAwait(false);

        // Shape weights wait for their meshes: a shape has no index until its mesh has loaded.
        if (weights is { Count: > 0 } pending)
        {
            root.StartTask(async () =>
            {
                var left = pending.ToList();
                for (int frame = 0; frame < 3600 && left.Count > 0; frame++)
                {
                    await WorldContext.NextUpdate();
                    left.RemoveAll(w => UnityAvatarBuilder.TryWriteShapeWeights(w.Renderer, w.Shapes));
                }
                if (left.Count > 0)
                    Logger.Warn($"UnityPackage: {left.Count} renderers never loaded; their saved shape weights were not set.");
            });
        }
    }

    private static Task OnWorld(World world, Action action)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.RunSynchronously(() =>
        {
            try
            {
                action();
                done.TrySetResult(true);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    // Everything a surface might ask for, parsed and with its textures already in the local database.
    private sealed class MaterialPlan
    {
        public readonly Dictionary<string, (UnityMaterialData Data, UnityShaderFamily Family, string? Shader)> Materials = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> GuidByName = new(StringComparer.Ordinal);
        public readonly Dictionary<string, UnityTextureRef> Textures = new(StringComparer.OrdinalIgnoreCase);
        // Cube textures, each redrawn as a panorama our cubemaps read.
        public readonly Dictionary<string, Uri> Cubemaps = new(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<MaterialPlan> PlanMaterialsAsync(UnityPackageArchive archive, UnityImportCandidate candidate,
        List<(UnityPackageAsset Model, UnityModelMeta Meta)> models, LocalDB? localDB, UnityImportResult result)
    {
        var plan = new MaterialPlan();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        wanted.UnionWith(candidate.MaterialGuids());
        foreach (var (_, meta) in models)
            wanted.UnionWith(meta.MaterialRemap.Values);

        // The project's own copies of the model's materials, for a surface nothing else names: Unity's
        // older import wrote them as "<name>.mat" beside the model or in a Materials folder next to it.
        foreach (var (model, _) in models)
        {
            string folder = Path.GetDirectoryName(model.Path)?.Replace('\\', '/') ?? "";
            foreach (var asset in archive.OfExtension(".mat"))
            {
                var dir = Path.GetDirectoryName(asset.Path)?.Replace('\\', '/') ?? "";
                if (dir == folder || dir == folder + "/Materials" || dir.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    wanted.Add(asset.Guid);
                    plan.GuidByName.TryAdd(asset.Name, asset.Guid);
                }
            }
        }

        var shaderNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var textureWants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var cubeWants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var guessed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in wanted)
        {
            var asset = archive.Find(guid);
            var data = UnityMaterialData.Parse(archive.ReadText(asset));
            if (data == null)
                continue;
            var shader = UnityMaterialBuilder.ShaderName(data, archive, shaderNames);
            var family = UnityMaterialBuilder.Identify(data, shader);
            bool missing = shader == null && UnityMaterialBuilder.ShaderMissing(data, archive);
            // Read off every material on the absent shader, not off this one's leftovers.
            if (missing && UnityMaterialBuilder.IdentifyMissing(data, archive) is { } shared)
                family = shared;
            plan.Materials[guid] = (data, family, shader);
            plan.GuidByName.TryAdd(data.Name, guid);
            // A family with its own marks is read right without its shader; the generic ones are guesses.
            if (family is UnityShaderFamily.Standard or UnityShaderFamily.StandardSpecular or UnityShaderFamily.Toon or UnityShaderFamily.Unknown
                && missing)
                guessed.Add(data.Name);
            foreach (var (tex, normal) in UnityMaterialBuilder.TexturesUsed(data, family))
                textureWants[tex] = textureWants.GetValueOrDefault(tex) || normal;
            cubeWants.UnionWith(UnityMaterialBuilder.CubemapsUsed(data, family));
        }
        if (guessed.Count > 0)
            result.Notes.Add("Shaders not in the package, these are drawn with a stand-in: " + string.Join(", ", guessed) + ".");

        foreach (var (guid, normalUse) in textureWants)
        {
            var asset = archive.Find(guid);
            if (asset?.File == null)
            {
                if (UnityToonRamps.Known(guid) && await UnityToonRamps.ImportAsync(guid, localDB).ConfigureAwait(false) is { } ramp)
                    plan.Textures[guid] = new UnityTextureRef(ramp, false, true, TextureWrapMode.Clamp, TextureWrapMode.Clamp, false);
                continue;
            }
            if (!ReadableImages.Contains(asset.Extension))
            {
                result.Notes.Add($"{Path.GetFileName(asset.Path)}: {asset.Extension} textures cannot be read yet, left off.");
                continue;
            }
            var settings = TextureSettings(asset.Meta);
            Uri? uri = null;
            try
            {
                // A GIF is its first frame on a material; our picture path would otherwise load all of it as a frame sheet.
                if (asset.Extension == ".gif")
                    uri = await FirstFrameAsync(asset, localDB).ConfigureAwait(false);
                if (uri == null && localDB != null)
                {
                    var local = await localDB.ImportLocalAssetAsync(asset.File, LocalDB.ImportLocation.Copy).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(local))
                        uri = new Uri(await TextureStorage.ForTransferAsync(localDB, local).ConfigureAwait(false));
                }
                uri ??= new Uri(asset.File);
            }
            catch (Exception ex)
            {
                result.Notes.Add($"{Path.GetFileName(asset.Path)}: {ex.Message}");
                continue;
            }
            plan.Textures[guid] = new UnityTextureRef(uri, settings.Normal || normalUse, settings.Linear, settings.WrapU, settings.WrapV, settings.Mipmaps);
            result.TexturesImported++;
        }

        foreach (var guid in cubeWants)
        {
            var asset = archive.Find(guid);
            if (asset?.File == null)
                continue;
            if (!ReadableImages.Contains(asset.Extension))
            {
                result.Notes.Add($"{Path.GetFileName(asset.Path)}: {asset.Extension} textures cannot be read yet, left off.");
                continue;
            }
            if (await UnityCubemapSource.ImportAsync(archive, asset, localDB, result.Notes).ConfigureAwait(false) is { } cube)
            {
                plan.Cubemaps[guid] = cube;
                result.CubemapsImported++;
            }
        }
        return plan;
    }

    private static async Task<Uri?> FirstFrameAsync(UnityPackageAsset asset, LocalDB? localDB)
    {
        if (asset.File == null || localDB == null)
            return null;
        var bytes = await File.ReadAllBytesAsync(asset.File).ConfigureAwait(false);
        var rgba = GifDecoder.DecodeFirstFrame(bytes, out int w, out int h);
        if (rgba == null || w <= 0 || h <= 0)
            return null;
        var local = await localDB.SaveAssetAsync(Lumora.Core.Photos.PhotoEncoder.Png(rgba, w, h), ".png").ConfigureAwait(false);
        return string.IsNullOrEmpty(local) ? null : new Uri(local);
    }

    private static (bool Normal, bool Linear, TextureWrapMode WrapU, TextureWrapMode WrapV, bool Mipmaps) TextureSettings(string? metaText)
    {
        if (string.IsNullOrEmpty(metaText))
            return (false, false, TextureWrapMode.Repeat, TextureWrapMode.Repeat, true);
        var importer = UnityYaml.ParseMapping(metaText)["TextureImporter"];
        if (importer == null)
            return (false, false, TextureWrapMode.Repeat, TextureWrapMode.Repeat, true);
        bool normal = importer["textureType"]?.Int() == 1;
        bool linear = importer["mipmaps"]?["sRGBTexture"]?.Int(1) == 0;
        bool mipmaps = importer["mipmaps"]?["enableMipMap"]?.Int(1) != 0;
        var settings = importer["textureSettings"];
        int wrapMode = settings?["wrapMode"]?.Int(-1) ?? -1;
        int wrapU = settings?["wrapU"]?.Int(wrapMode) ?? wrapMode;
        int wrapV = settings?["wrapV"]?.Int(wrapMode) ?? wrapMode;
        return (normal, linear, Wrap(wrapU), Wrap(wrapV), mipmaps);
    }

    private static TextureWrapMode Wrap(int unity) => unity switch
    {
        1 => TextureWrapMode.Clamp,
        2 or 3 => TextureWrapMode.Mirror,
        _ => TextureWrapMode.Repeat,
    };

    // Answers the model importer's "which material does this surface get", and afterwards switches off
    // what the placement had switched off. Runs on the world thread.
    private sealed class SurfaceMatcher
    {
        private readonly Dictionary<long, SortedDictionary<int, string>> _materialsById;
        private readonly Dictionary<string, List<string?>> _materialsByName;
        private readonly Dictionary<long, bool> _activeById;
        private readonly Dictionary<long, bool> _enabledById;
        private readonly Dictionary<string, bool> _activeByName;
        private readonly UnityModelMeta _meta;
        private readonly MaterialPlan _plan;
        private readonly UnityImportResult _result;
        // Shared by every model of one import, so a material the body and an outfit both wear is built once.
        private readonly Dictionary<string, MaterialProvider> _built;
        private Slot? _holder;

        public SurfaceMatcher(Dictionary<long, SortedDictionary<int, string>> materialsById, Dictionary<string, List<string?>> materialsByName,
            Dictionary<long, bool> activeById, Dictionary<long, bool> enabledById, Dictionary<string, bool> activeByName,
            UnityModelMeta meta, MaterialPlan plan, UnityImportResult result, Dictionary<string, MaterialProvider> built)
        {
            _materialsById = materialsById;
            _materialsByName = materialsByName;
            _activeById = activeById;
            _enabledById = enabledById;
            _activeByName = activeByName;
            _meta = meta;
            _plan = plan;
            _result = result;
            _built = built;
        }

        // A converted material by GUID, for what the avatar swaps in later. Built once, beside the rest.
        public MaterialProvider? Material(Slot model, string guid)
        {
            if (_built.TryGetValue(guid, out var material) && !material.IsDestroyed)
                return material;
            if (!_plan.Materials.TryGetValue(guid, out var entry))
                return null;
            _holder ??= model.AddSlot("Materials");
            material = UnityMaterialBuilder.Build(_holder, entry.Data, entry.Family, _plan.Textures, _plan.Cubemaps, entry.Shader);
            _built[guid] = material;
            _result.MaterialsByFamily[entry.Family] = _result.MaterialsByFamily.GetValueOrDefault(entry.Family) + 1;
            return material;
        }

        public MaterialProvider? Resolve(ModelMaterialRequest request)
        {
            string? guid = null;
            string how = "";
            var nodeRoot = NodeRootOf(request.Model, request.Node);
            var paths = UnityPaths(nodeRoot, request.Node).ToList();
            string path = paths.Count > 0 ? paths[0] : "";
            string name = request.Node.SlotName.Value ?? "";

            if (_materialsById.Count > 0)
            {
                foreach (var id in RendererIds(paths, name))
                {
                    if (_materialsById.TryGetValue(id, out var slots) && slots.TryGetValue(request.Index, out var g))
                    {
                        guid = g;
                        how = "placement";
                        break;
                    }
                }
            }
            if (guid == null && _materialsByName.TryGetValue(name, out var list) && request.Index < list.Count)
            {
                guid = list[request.Index];
                how = "renderer";
            }
            if (guid == null && _meta.MaterialRemap.TryGetValue(request.SourceName, out var remapped))
            {
                guid = remapped;
                how = "model settings";
            }
            if (guid == null && !string.IsNullOrEmpty(request.SourceName) && _plan.GuidByName.TryGetValue(request.SourceName, out guid))
                how = "same name";
            Logger.Debug($"UnityPackage: surface '{path}' #{request.Index} ('{request.SourceName}') -> {(guid == null ? "none" : how + " " + guid)}");

            if (guid == null || !_plan.Materials.TryGetValue(guid, out var entry))
            {
                _result.SurfacesUnmatched++;
                return null;
            }
            _result.SurfacesMatched++;
            if (_built.TryGetValue(guid, out var material) && !material.IsDestroyed)
                return material;

            _holder ??= request.Model.AddSlot("Materials");
            material = UnityMaterialBuilder.Build(_holder, entry.Data, entry.Family, _plan.Textures, _plan.Cubemaps, entry.Shader);
            _built[guid] = material;
            _result.MaterialsByFamily[entry.Family] = _result.MaterialsByFamily.GetValueOrDefault(entry.Family) + 1;
            return material;
        }

        public void ApplyStates(Slot? nodeRoot)
        {
            if (nodeRoot == null || _activeById.Count == 0 && _enabledById.Count == 0 && _activeByName.Count == 0)
                return;
            var stack = new Stack<Slot>();
            stack.Push(nodeRoot);
            while (stack.Count > 0)
            {
                var slot = stack.Pop();
                foreach (var child in slot.Children)
                    stack.Push(child);
                if (ReferenceEquals(slot, nodeRoot))
                    continue;
                string name = slot.SlotName.Value ?? "";
                var paths = UnityPaths(nodeRoot, slot).ToList();
                bool off = _activeByName.TryGetValue(name, out bool on) && !on;
                foreach (var path in paths)
                {
                    // A lone top node answers to the root's id, and the root is switched on the model slot.
                    if (path.Length == 0)
                        continue;
                    foreach (bool withRoot in new[] { true, false })
                    {
                        if (_activeById.TryGetValue(UnityModelMeta.GameObjectId(path, withRoot), out bool active) && !active)
                            off = true;
                    }
                }
                foreach (var id in RendererIds(paths, name))
                {
                    if (_enabledById.TryGetValue(id, out bool enabled) && !enabled)
                        off = true;
                }
                if (off && slot.ActiveSelf.Value)
                    slot.ActiveSelf.Value = false;
            }
        }

        private IEnumerable<long> RendererIds(IReadOnlyList<string> paths, string name)
        {
            foreach (var path in paths)
            {
                foreach (var type in new[] { "SkinnedMeshRenderer", "MeshRenderer" })
                {
                    yield return UnityModelMeta.ObjectId(type, path, withRootLevel: true);
                    yield return UnityModelMeta.ObjectId(type, path, withRootLevel: false);
                }
            }
            foreach (var (id, named) in _meta.NamedIds)
                if ((named.ClassId == 137 || named.ClassId == 23) && named.Name == name)
                    yield return id;
        }

    }

    // The paths Unity would know this slot by: names from below the model file's root node down to it,
    // and, when that root holds a single node, the same without it (Unity folds a lone top node into the
    // model's own root before naming anything). Assimp's pivot helper nodes are not in the project's
    // hierarchy and are left out. Nothing when the slot is not one of the file's nodes.
    internal static IEnumerable<string> UnityPaths(Slot? nodeRoot, Slot node)
    {
        if (nodeRoot == null || ReferenceEquals(node, nodeRoot))
            yield break;
        var names = new List<string>();
        var current = node;
        while (current != null && !ReferenceEquals(current, nodeRoot))
        {
            var name = current.SlotName.Value ?? "";
            if (!name.Contains("$AssimpFbx$", StringComparison.Ordinal))
                names.Add(name);
            current = current.Parent;
        }
        if (current == null || names.Count == 0)
            yield break;
        names.Reverse();
        yield return string.Join('/', names);
        if (nodeRoot.ChildCount == 1)
            yield return string.Join('/', names.Skip(1));
    }

    // The node root a slot of an import hangs under: the model slot's child on the way up.
    internal static Slot? NodeRootOf(Slot model, Slot node)
    {
        var current = node;
        while (current != null && current.Parent != null && !ReferenceEquals(current.Parent, model))
            current = current.Parent;
        return current != null && ReferenceEquals(current.Parent, model) ? current : null;
    }
}
