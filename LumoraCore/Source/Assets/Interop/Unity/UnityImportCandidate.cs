// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lumora.Core.Math;

namespace Lumora.Core.Assets.Interop.Unity;

// One thing in a package that can be brought in: an avatar as a scene or prefab set it up, a prefab of
// parts, or a model file on its own, with the materials, switched-off parts and avatar settings it carries.
//
// Real avatars live in scenes. A scene places a model and changes it by hashed object id, hangs outfits and
// script prefabs off it (each of those placements with changes of its own), and adds its own objects and
// scripts; all of that is read through UnityPrefabGraph. An unpacked prefab writes the whole hierarchy out
// instead, renderers and all, and names its game objects after the model's nodes ("Hierarchy"). -xlinka
// A transform the project referred to, in terms that survive into our import: an object inside a placed
// model file (its hashed id, and which placement), a path below the model's root for an unpacked hierarchy,
// the avatar's own root when that is not a model, or an object the project added under one of those, with
// its own local pose in the project's (left-handed) space.
public sealed class UnityNodeRef
{
    public long ModelId;
    // The same node under the other id a nested placement can give it (the top bit is lost on the way in).
    public long ModelIdAlt;
    // The placed model the node is inside; null for the candidate's main model.
    public UnityModelPart? Part;
    public bool IsImportRoot;
    public string? Path;
    public UnityNodeRef? Parent;
    public string Name = "";
    public float3 Position;
    public floatQ Rotation = floatQ.Identity;
    public float3 Scale = float3.One;
    public bool Active = true;
}

// A script component the importer knows how to carry: what it is, where it sits, and its saved fields.
public sealed class UnityComponentRef
{
    public enum Kind { PhysBone, PhysBoneCollider, DynamicBone, DynamicBoneCollider, ArmatureLink, Toggle, Gestures, Constraint, Contact }

    public Kind Type;
    public long FileId;
    public UnityNodeRef Node = null!;
    public UnityDocument Doc = null!;
    // VRCFury features keep their settings in a reference block, not on the document itself.
    public UnityNode? Data;
    // The VRCFury feature's name, or for a constraint which kind it is.
    public string Feature = "";
    // Switched off in the project; built switched off, for a menu or clip to switch on.
    public bool Enabled = true;
}

// A mesh drawn by an object the project made rather than by a model file: a built-in shape or a mesh asset.
public sealed class UnityMeshRef
{
    public UnityNodeRef Node = null!;
    public string? MeshGuid;
    public long MeshFileId;
    public readonly List<string?> Materials = new();
    public bool Enabled = true;
}

public sealed class UnityImportCandidate
{
    public enum Shape { Instance, Hierarchy }

    // Where the list puts it: the scenes a creator ships to upload from, the older and work-in-progress
    // scenes kept alongside, prefabs, and the bare model files.
    public enum Listing { Scene, OlderScene, Prefab, ModelFile }

    public string Name = "";
    public string SourcePath = "";
    public Shape Kind;
    public Listing Section;
    public UnityPackageAsset Model = null!;
    public bool IsAvatar;
    public float3? ViewPosition;
    // The descriptor object's own scale. Its viewpoint is measured with it applied; the import leaves it off.
    public float ViewRootScale = 1f;
    public int Renderers;
    // The avatar root's own scale in the project; the import puts it on the model once everything is built.
    public float3 RootScale = float3.One;
    // The avatar's root is an object of the project's own with the model below it, not the model itself.
    public bool AuthoredRoot;
    // The main model's own name in the project.
    public string ModelName = "";

    // Instance: renderer id inside the model -> material slot -> material GUID.
    public readonly Dictionary<long, SortedDictionary<int, string>> MaterialsById = new();
    // Hierarchy: renderer game object name -> material GUIDs by slot.
    public readonly Dictionary<string, List<string?>> MaterialsByName = new(StringComparer.Ordinal);
    // Instance: game object or renderer id -> switched on.
    public readonly Dictionary<long, bool> ActiveById = new();
    public readonly Dictionary<long, bool> EnabledById = new();
    // Instance: transform id -> the pose fields the placement changed ("m_LocalScale.x" -> 1).
    public readonly Dictionary<long, Dictionary<string, float>> TransformOverrides = new();
    // Instance: game object id -> the name the placement gave it; renderer id -> shape index -> weight 0..100.
    public readonly Dictionary<long, string> NamesById = new();
    public readonly Dictionary<long, SortedDictionary<int, float>> ShapeWeights = new();
    // Hierarchy: game object name -> switched on; renderer's game object name -> shape index -> weight 0..100.
    public readonly Dictionary<string, bool> ActiveByName = new(StringComparer.Ordinal);
    public readonly Dictionary<string, SortedDictionary<int, float>> ShapeWeightsByName = new(StringComparer.Ordinal);
    // The other model files placed inside it (outfits, eyes, props), each with its own overrides.
    public readonly List<UnityModelPart> Parts = new();
    // Objects the project added, made whether or not anything points at them, parents first.
    public readonly List<UnityNodeRef> AddedObjects = new();
    // Meshes on objects the project made: built-in shapes and mesh assets.
    public readonly List<UnityMeshRef> Meshes = new();

    // Avatar parts: the descriptor's viseme mesh and shape names, and every bone chain and collider.
    public UnityDocument? Descriptor;
    public UnityNodeRef? VisemeMesh;
    public readonly List<string> VisemeShapes = new();
    public readonly List<UnityComponentRef> Chains = new();
    public readonly Dictionary<long, UnityComponentRef> Colliders = new();
    public readonly List<UnityComponentRef> Links = new();
    public readonly List<UnityComponentRef> Toggles = new();
    public readonly List<UnityComponentRef> Gestures = new();
    public readonly List<UnityComponentRef> Constraints = new();
    // Contact receivers: a zone that sets a parameter while something touches it.
    public readonly List<UnityComponentRef> Contacts = new();
    // Prefab-only objects still matter to animation paths, even when they have no mesh or script.
    public readonly Dictionary<string, UnityNodeRef> NodesByPath = new(StringComparer.Ordinal);
    // Materials the toggles and gestures swap in, so they are converted along with the rest.
    public readonly HashSet<string> SwappedMaterials = new(StringComparer.OrdinalIgnoreCase);
    // VRCFury features with nothing to become here, by feature name.
    public readonly SortedDictionary<string, int> LeftBehind = new(StringComparer.Ordinal);
    // What reading the setup ran into: placements of files the package does not have, and the like.
    public readonly List<string> Notes = new();
    // Turns a transform reference from the project file into a node, for the fields inside the parts.
    public Func<long, UnityNodeRef?> ResolveTransform = _ => null;
    // The same for a game object reference.
    public Func<long, UnityNodeRef?> ResolveObject = _ => null;
    // And for a component reference (a renderer): the object it sits on.
    public Func<long, UnityNodeRef?> ResolveComponent = _ => null;

    // A scene or prefab avatar is found from a light read; its setup is read in full only when it is picked.
    internal UnityPackageAsset? SetupFile;
    internal long SetupRoot;
    private bool _prepared = true;

    public bool IsPrepared => _prepared;

    public string Label => (IsAvatar ? "Avatar: " : "Model: ") + Name;

    // The scene or prefab it comes from, by file name.
    public string SourceName => System.IO.Path.GetFileNameWithoutExtension(SourcePath);

    internal void AwaitPrepare(UnityPackageAsset file, long root)
    {
        SetupFile = file;
        SetupRoot = root;
        _prepared = false;
    }

    // Reads the whole setup of a scene or prefab avatar, once. Safe to call off the world thread.
    public void Prepare(UnityPackageArchive archive)
    {
        if (_prepared || SetupFile == null)
            return;
        lock (this)
        {
            if (_prepared)
                return;
            UnitySceneCandidates.Fill(this, new UnityDocumentCache(archive), SetupFile, SetupRoot);
            _prepared = true;
        }
    }

    public IEnumerable<string> MaterialGuids()
    {
        foreach (var slots in MaterialsById.Values)
            foreach (var guid in slots.Values)
                yield return guid;
        foreach (var list in MaterialsByName.Values)
            foreach (var guid in list)
                if (guid != null)
                    yield return guid;
        foreach (var part in Parts)
            foreach (var slots in part.MaterialsById.Values)
                foreach (var guid in slots.Values)
                    yield return guid;
        foreach (var guid in SwappedMaterials)
            yield return guid;
        foreach (var mesh in Meshes)
            foreach (var guid in mesh.Materials)
                if (guid != null)
                    yield return guid;
    }
}

public static class UnityCandidateFinder
{
    private const int TransformClass = 4;
    private const int RectTransformClass = 224;
    private const int MeshRendererClass = 23;
    private const int MeshFilterClass = 33;
    private const int SkinnedMeshRendererClass = 137;
    private const int MonoBehaviourClass = 114;

    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fbx", ".obj", ".dae", ".gltf", ".glb", ".3ds", ".blend",
    };

    internal static bool IsModelFile(UnityPackageAsset asset) => ModelExtensions.Contains(asset.Extension);

    public static List<UnityImportCandidate> Find(UnityPackageArchive archive, List<string>? notes = null)
    {
        var found = new List<UnityImportCandidate>();
        var cache = new UnityDocumentCache(archive);
        foreach (var asset in archive.ByGuid.Values.OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (asset.File == null || (asset.Extension != ".unity" && asset.Extension != ".prefab"))
                continue;
            int before = found.Count;
            UnityPrefabGraph? graph = null;
            try
            {
                graph = UnityPrefabGraph.Build(cache, asset, full: false);
            }
            catch (Exception ex)
            {
                notes?.Add($"{asset.Path}: could not be read ({ex.Message})");
            }
            if (graph != null)
                UnitySceneCandidates.Find(graph, found);

            // An unpacked hierarchy draws its own renderers; read the file the old way for those.
            var covered = new HashSet<long>();
            for (int i = before; i < found.Count; i++)
                covered.Add(found[i].SetupRoot);
            var docs = cache.Documents(asset);
            if (docs != null)
                new FileReader(archive, asset, docs, found, notes, covered).Read();
        }

        // Every model file, as itself.
        foreach (var asset in archive.ByGuid.Values)
        {
            if (asset.File == null || !ModelExtensions.Contains(asset.Extension))
                continue;
            found.Add(new UnityImportCandidate
            {
                Name = asset.Name,
                SourcePath = asset.Path,
                Kind = UnityImportCandidate.Shape.Instance,
                Section = UnityImportCandidate.Listing.ModelFile,
                Model = asset,
            });
        }

        return found
            .OrderBy(c => c.Section)
            .ThenBy(c => UnitySceneCandidates.Rank(c.SourcePath))
            .ThenByDescending(c => UnitySceneCandidates.Version(c.SourcePath))
            .ThenBy(c => c.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(c => c.IsAvatar)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- what a project's scripts become ----

    // An avatar descriptor, by what it holds rather than which script it is: a viewpoint.
    internal static bool IsDescriptor(UnityDocument d) => d.ClassId == MonoBehaviourClass && (d["ViewPosition"] != null || d["viewPosition"] != null);

    internal static float3? Vector(UnityNode? node)
        => node == null ? null : new float3(node["x"]?.Float() ?? 0f, node["y"]?.Float() ?? 0f, node["z"]?.Float() ?? 0f);

    // The descriptor's lip sync: a viseme mesh and fifteen shape names in the usual order, both dialects.
    internal static void ReadVisemes(UnityImportCandidate candidate)
    {
        var descriptor = candidate.Descriptor;
        if (descriptor == null)
            return;
        long mesh = (descriptor["VisemeSkinnedMesh"] ?? descriptor["bodyMesh"])?.Ref.FileId ?? 0;
        var shapes = (descriptor["VisemeBlendShapes"] ?? descriptor["visemeBlendshapes"]) as UnitySeq;
        if (mesh == 0 || shapes == null || shapes.Items.Count == 0)
            return;
        candidate.VisemeMesh = candidate.ResolveComponent(mesh);
        if (candidate.VisemeMesh == null)
            return;
        foreach (var item in shapes.Items)
            candidate.VisemeShapes.Add(item.Text ?? "");
    }

    // One document on an object of the candidate's: a bone chain, collider, constraint, contact or VRCFury
    // feature becomes a part of the candidate; anything else is not ours to carry.
    internal static void Classify(UnityImportCandidate candidate, UnityDocument d, UnityNodeRef node)
    {
        if (d.Stripped)
            return;
        if (UnityAvatarBuilder.CollectAudio(candidate, d, node))
            return;
        if (ConstraintClasses.TryGetValue(d.ClassId, out var constraintKind))
        {
            candidate.Constraints.Add(new UnityComponentRef { Type = UnityComponentRef.Kind.Constraint, FileId = d.FileId, Node = node, Doc = d, Feature = constraintKind });
            return;
        }
        if (d.ClassId != MonoBehaviourClass)
            return;
        if (UnityBuildTools.Collect(candidate, d, node))
            return;
        if (VrcFuryFeature(d) is { } feature)
        {
            CollectFeature(candidate, d, feature.Name, feature.Data, node);
            return;
        }
        if (VrcConstraintKind(d) is { } vrcKind)
        {
            candidate.Constraints.Add(new UnityComponentRef { Type = UnityComponentRef.Kind.Constraint, FileId = d.FileId, Node = node, Doc = d, Feature = vrcKind });
            return;
        }
        if (d["receiverType"] != null && d["collisionTags"] != null && d["parameter"] != null)
        {
            if (!string.IsNullOrEmpty(d["parameter"]?.Text))
                candidate.Contacts.Add(new UnityComponentRef { Type = UnityComponentRef.Kind.Contact, FileId = d.FileId, Node = node, Doc = d,
                    Feature = d["parameter"]!.Text!, Enabled = (d["m_Enabled"]?.Int(1) ?? 1) != 0 });
            return;
        }
        UnityComponentRef.Kind kind;
        if (d["pull"] != null && d["spring"] != null && d["rootTransform"] != null)
            kind = UnityComponentRef.Kind.PhysBone;
        else if (d["shapeType"] != null && d["radius"] != null && d["rootTransform"] != null)
            kind = UnityComponentRef.Kind.PhysBoneCollider;
        else if (d["m_Root"] != null && d["m_Elasticity"] != null)
            kind = UnityComponentRef.Kind.DynamicBone;
        else if (d["m_Direction"] != null && d["m_Radius"] != null && d["m_Bound"] != null)
            kind = UnityComponentRef.Kind.DynamicBoneCollider;
        else
            return;
        var part = new UnityComponentRef { Type = kind, FileId = d.FileId, Node = node, Doc = d, Enabled = (d["m_Enabled"]?.Int(1) ?? 1) != 0 };
        if (kind is UnityComponentRef.Kind.PhysBone or UnityComponentRef.Kind.DynamicBone)
            candidate.Chains.Add(part);
        else
            candidate.Colliders[d.FileId] = part;
    }

    // Unity's own constraint classes, by class id.
    private static readonly Dictionary<int, string> ConstraintClasses = new()
    {
        [1773428102] = "Parent", [1818360608] = "Position", [1818360609] = "Rotation",
        [1818360610] = "Scale", [895512359] = "Aim", [1183024399] = "LookAt",
    };

    // The avatar constraint scripts share one set of fields; each kind adds its own.
    private static string? VrcConstraintKind(UnityDocument d)
    {
        if (d["GlobalWeight"] == null || d["Sources"] == null || d["IsActive"] == null)
            return null;
        if (d["AimAxis"] != null)
            return "VrcAim";
        if (d["Roll"] != null && d["UseUpTransform"] != null)
            return "VrcLookAt";
        if (d["ScaleAtRest"] != null)
            return "VrcScale";
        if (d["PositionOffset"] != null)
            return "VrcPosition";
        if (d["RotationOffset"] != null && d["PositionAtRest"] == null)
            return "VrcRotation";
        return "VrcParent";
    }

    // A VRCFury component: its feature's name and settings, found by the content's reference id.
    private static (string Name, UnityNode Data)? VrcFuryFeature(UnityDocument d)
    {
        var rid = d["content"]?["rid"]?.Text;
        if (rid == null || d["references"]?["RefIds"] is not UnitySeq refs)
            return null;
        foreach (var item in refs.Items)
        {
            if (item["rid"]?.Text != rid)
                continue;
            var type = item["type"];
            var name = type?["class"]?.Text;
            if (name == null || item["data"] is not { } data || type?["asm"]?.Text != "VRCFury")
                return null;
            return (name, data);
        }
        return null;
    }

    // Build-time tidying done by the package's own tools; nothing to carry and nothing lost.
    private static readonly HashSet<string> BuildOnlyFeatures = new(StringComparer.Ordinal)
    {
        "DirectTreeOptimizer", "BlendshapeOptimizer", "MmdCompatibility", "FixWriteDefaults", "AnimatorLayerControlOptimizer",
    };

    private static void CollectFeature(UnityImportCandidate candidate, UnityDocument d, string name, UnityNode data, UnityNodeRef node)
    {
        if ((d["m_Enabled"]?.Int(1) ?? 1) == 0)
            return;
        var part = new UnityComponentRef { FileId = d.FileId, Node = node, Doc = d, Data = data, Feature = name };
        switch (name)
        {
            case "ArmatureLink":
                part.Type = UnityComponentRef.Kind.ArmatureLink;
                candidate.Links.Add(part);
                return;
            case "Toggle":
            case "WorldConstraint":
                part.Type = UnityComponentRef.Kind.Toggle;
                candidate.Toggles.Add(part);
                NoteMaterials(candidate, d, data["state"]);
                return;
            case "GestureDriver":
                part.Type = UnityComponentRef.Kind.Gestures;
                candidate.Gestures.Add(part);
                if (data["gestures"] is UnitySeq gestures)
                    foreach (var gesture in gestures.Items)
                        NoteMaterials(candidate, d, gesture["state"]);
                return;
            // Ours blinks on its own; only a blink that changes something else stays behind.
            case "Blinking" when !(data["state"]?["actions"] is UnitySeq { Items.Count: > 0 }):
                return;
        }
        if (UnityBuildTools.Collect(candidate, part))
            return;
        if (!BuildOnlyFeatures.Contains(name))
            candidate.LeftBehind[name] = candidate.LeftBehind.GetValueOrDefault(name) + 1;
    }

    private static void NoteMaterials(UnityImportCandidate candidate, UnityDocument d, UnityNode? state)
    {
        var references = UnityVrcFury.References(d);
        foreach (var action in UnityVrcFury.AllActions(state, references))
        {
            if (action.Class == "MaterialAction" && UnityVrcFury.Guid(action.Data["mat"]) is { } guid)
                candidate.SwappedMaterials.Add(guid);
        }
    }

    // A game object tagged to stay in the editor: the build strips it and everything under it.
    internal static bool IsEditorOnly(UnityDocument gameObject) => gameObject["m_TagString"]?.Text == "EditorOnly";

    // A mesh filter and renderer on an object the project made: kept to be built where the object lands.
    internal static void CollectMesh(UnityImportCandidate candidate, IEnumerable<UnityDocument> components, UnityNodeRef node)
    {
        UnityDocument? filter = null, renderer = null;
        foreach (var d in components)
        {
            if (d.Stripped)
                continue;
            if (d.ClassId == MeshFilterClass)
                filter = d;
            else if (d.ClassId == MeshRendererClass)
                renderer = d;
        }
        var mesh = filter?["m_Mesh"]?.Ref ?? default;
        if (renderer == null || mesh.FileId == 0)
            return;
        var made = new UnityMeshRef { Node = node, MeshGuid = mesh.Guid, MeshFileId = mesh.FileId, Enabled = (renderer["m_Enabled"]?.Int(1) ?? 1) != 0 };
        if (renderer["m_Materials"] is UnitySeq materials)
            foreach (var m in materials.Items)
                made.Materials.Add(m.Ref.Guid);
        candidate.Meshes.Add(made);
    }

    internal static void MarkAvatar(UnityImportCandidate candidate, UnityDocument descriptor, float rootScale)
    {
        candidate.IsAvatar = true;
        candidate.Descriptor = descriptor;
        candidate.ViewPosition = Vector(descriptor["ViewPosition"] ?? descriptor["viewPosition"]);
        float scale = MathF.Abs(rootScale);
        candidate.ViewRootScale = scale > 1e-4f ? scale : 1f;
    }

    // An unpacked prefab: the whole hierarchy written out, renderers on game objects named after the model's
    // nodes. The model is the file most of the renderers draw from. Every other model file the hierarchy draws
    // from comes in as a part, rooted at the object its renderers and their root bones all hang under: an
    // outfit dragged in whole and unpacked keeps its own top object and armature there.
    private sealed class FileReader
    {
        private readonly UnityPackageArchive _archive;
        private readonly UnityPackageAsset _file;
        private readonly List<UnityDocument> _docs;
        private readonly List<UnityImportCandidate> _found;
        private readonly List<string>? _notes;
        private readonly Dictionary<long, UnityDocument> _byId = new();
        // Transform id -> its game object id; game object id -> its transform id.
        private readonly Dictionary<long, long> _transformObject = new();
        private readonly Dictionary<long, long> _objectTransform = new();
        // Game object id -> descriptor document on it.
        private readonly Dictionary<long, UnityDocument> _descriptors = new();
        // Game object id -> its other components.
        private readonly Dictionary<long, List<UnityDocument>> _components = new();
        // One node per transform, so everything that points at the same added object gets the same one.
        private readonly Dictionary<long, UnityNodeRef?> _nodes = new();
        // The transform each extra model file is rooted at.
        private readonly Dictionary<long, UnityModelPart> _partRoots = new();

        private readonly HashSet<long> _covered;

        public FileReader(UnityPackageArchive archive, UnityPackageAsset file, List<UnityDocument> docs,
            List<UnityImportCandidate> found, List<string>? notes, HashSet<long> covered)
        {
            _archive = archive;
            _file = file;
            _docs = docs;
            _found = found;
            _notes = notes;
            _covered = covered;
        }

        public void Read()
        {
            foreach (var d in _docs)
                _byId[d.FileId] = d;
            foreach (var d in _docs)
            {
                if (d.ClassId is TransformClass or RectTransformClass)
                {
                    long go = d["m_GameObject"]?.Ref.FileId ?? 0;
                    _transformObject[d.FileId] = go;
                    if (go != 0)
                        _objectTransform[go] = d.FileId;
                }
                else
                {
                    if (IsDescriptor(d))
                    {
                        long go = d["m_GameObject"]?.Ref.FileId ?? 0;
                        if (go != 0)
                            _descriptors[go] = d;
                    }
                    if (!d.Stripped && d.ClassId != 1 && d["m_GameObject"]?.Ref.FileId is long owner and not 0)
                    {
                        if (!_components.TryGetValue(owner, out var list))
                            _components[owner] = list = new List<UnityDocument>();
                        list.Add(d);
                    }
                }
            }

            foreach (var d in _docs)
            {
                if (d.ClassId != TransformClass && d.ClassId != RectTransformClass)
                    continue;
                if (d.Stripped || (d["m_Father"]?.Ref.FileId ?? 0) != 0)
                    continue;
                long go = d["m_GameObject"]?.Ref.FileId ?? 0;
                if (_byId.TryGetValue(go, out var root) && !root.Stripped && !_covered.Contains(go) && !IsEditorOnly(root))
                    ReadHierarchy(root, d);
            }
        }

        private UnityNodeRef LocalPose(UnityDocument t)
        {
            long go = t["m_GameObject"]?.Ref.FileId ?? 0;
            var p = t["m_LocalPosition"];
            var r = t["m_LocalRotation"];
            var s = t["m_LocalScale"];
            return new UnityNodeRef
            {
                Name = _byId.TryGetValue(go, out var obj) ? obj["m_Name"]?.Text ?? "" : "",
                Active = (obj?["m_IsActive"]?.Int(1) ?? 1) != 0,
                Position = p == null ? float3.Zero : new float3(p["x"]?.Float() ?? 0f, p["y"]?.Float() ?? 0f, p["z"]?.Float() ?? 0f),
                Rotation = r == null ? floatQ.Identity : new floatQ(r["x"]?.Float() ?? 0f, r["y"]?.Float() ?? 0f, r["z"]?.Float() ?? 0f, r["w"]?.Float(1f) ?? 1f),
                Scale = s == null ? float3.One : new float3(s["x"]?.Float(1f) ?? 1f, s["y"]?.Float(1f) ?? 1f, s["z"]?.Float(1f) ?? 1f),
            };
        }

        private UnityNodeRef? ComponentNode(UnityImportCandidate candidate, long id)
        {
            if (id == 0 || !_byId.TryGetValue(id, out var component))
                return null;
            if (component.Stripped)
                return new UnityNodeRef { ModelId = component["m_CorrespondingSourceObject"]?.Ref.FileId ?? 0 };
            long go = component["m_GameObject"]?.Ref.FileId ?? 0;
            return go != 0 ? ObjectNode(candidate, go) : null;
        }

        private UnityNodeRef? ObjectNode(UnityImportCandidate candidate, long go)
        {
            if (_byId.TryGetValue(go, out var owner) && owner.Stripped)
                return new UnityNodeRef { ModelId = owner["m_CorrespondingSourceObject"]?.Ref.FileId ?? 0 };
            return _objectTransform.TryGetValue(go, out long transform) ? candidate.ResolveTransform(transform) : null;
        }

        private long Father(long transform) => _byId.TryGetValue(transform, out var t) ? t["m_Father"]?.Ref.FileId ?? 0 : 0;

        private string NameOf(long transform)
        {
            long go = _transformObject.GetValueOrDefault(transform);
            return _byId.TryGetValue(go, out var obj) ? obj["m_Name"]?.Text ?? "" : "";
        }

        // The part a transform sits in, and its path below the part's root ("" for the root itself).
        private (UnityModelPart Part, string Path)? InPart(long transform)
        {
            var names = new List<string>();
            long t = transform;
            for (int guard = 0; t != 0 && guard < 512; guard++)
            {
                if (_partRoots.TryGetValue(t, out var part))
                {
                    names.Reverse();
                    return (part, string.Join('/', names));
                }
                names.Add(NameOf(t));
                t = Father(t);
            }
            return null;
        }

        // A path of names below the hierarchy's root, the way the model file's own nodes are named. Inside a
        // part it is that model's node, by the id its path below the part's root gives it.
        private UnityNodeRef? HierarchyNode(long transformId, long rootTransform)
        {
            if (_nodes.TryGetValue(transformId, out var known))
                return known;
            var names = new List<string>();
            long t = transformId;
            for (int guard = 0; t != 0 && t != rootTransform && guard < 512; guard++)
            {
                if (!_byId.TryGetValue(t, out var transform))
                    return null;
                long go = transform["m_GameObject"]?.Ref.FileId ?? 0;
                names.Add(_byId.TryGetValue(go, out var obj) ? obj["m_Name"]?.Text ?? "" : "");
                t = transform["m_Father"]?.Ref.FileId ?? 0;
            }
            if (t != rootTransform)
                return null;
            names.Reverse();
            if (InPart(transformId) is { } inside)
            {
                var partNode = inside.Path.Length == 0
                    ? inside.Part.Root.Node
                    : new UnityNodeRef
                    {
                        ModelId = UnityModelMeta.ObjectId("Transform", inside.Path, withRootLevel: true),
                        ModelIdAlt = UnityModelMeta.ObjectId("Transform", inside.Path, withRootLevel: false),
                        Part = inside.Part,
                        Name = names.Count > 0 ? names[^1] : "",
                    };
                _nodes[transformId] = partNode;
                return partNode;
            }
            var node = _byId.TryGetValue(transformId, out var self) ? LocalPose(self) : new UnityNodeRef();
            node.Path = string.Join('/', names);
            _nodes[transformId] = node;
            if (transformId != rootTransform && self != null)
                node.Parent = HierarchyNode(self["m_Father"]?.Ref.FileId ?? 0, rootTransform);
            return node;
        }

        private List<long> Ancestry(long transform)
        {
            var chain = new List<long>();
            for (long t = transform; t != 0 && chain.Count < 512; t = Father(t))
                chain.Add(t);
            chain.Reverse();
            return chain;
        }

        private bool IsUnder(long transform, long ancestor)
        {
            for (long t = transform; t != 0; t = Father(t))
                if (t == ancestor)
                    return true;
            return false;
        }

        // The deepest transform every one of these hangs under.
        private long CommonAncestor(List<long> transforms)
        {
            List<long>? common = null;
            foreach (var t in transforms)
            {
                var chain = Ancestry(t);
                if (common == null)
                {
                    common = chain;
                    continue;
                }
                int n = 0;
                while (n < common.Count && n < chain.Count && common[n] == chain[n])
                    n++;
                common.RemoveRange(n, common.Count - n);
            }
            return common is { Count: > 0 } ? common[^1] : 0;
        }

        private void ReadHierarchy(UnityDocument root, UnityDocument rootTransform)
        {
            var objects = new List<long>();
            var stack = new Stack<long>();
            stack.Push(rootTransform.FileId);
            while (stack.Count > 0)
            {
                long t = stack.Pop();
                if (!_byId.TryGetValue(t, out var transform))
                    continue;
                if (_transformObject.TryGetValue(t, out long go) && go != 0)
                {
                    // Stripped out of every build, and everything under it with it.
                    if (_byId.TryGetValue(go, out var obj) && IsEditorOnly(obj))
                        continue;
                    objects.Add(go);
                }
                if (transform["m_Children"] is UnitySeq children)
                    foreach (var child in children.Items)
                        stack.Push(child.Ref.FileId);
            }
            var inTree = new HashSet<long>(objects);

            var candidate = new UnityImportCandidate
            {
                Name = root["m_Name"]?.Text ?? _file.Name,
                SourcePath = _file.Path,
                Kind = UnityImportCandidate.Shape.Hierarchy,
                Section = UnitySceneCandidates.ListingOf(_file.Path),
            };
            var meshFiles = new Dictionary<long, string>();
            foreach (var d in _docs)
                if (d.ClassId == MeshFilterClass && d["m_Mesh"]?.Ref.Guid is { } guid)
                    meshFiles[d["m_GameObject"]?.Ref.FileId ?? 0] = guid;

            var renderers = new List<(UnityDocument Doc, long Go, string? Mesh)>();
            var modelVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in _docs)
            {
                if (d.ClassId != SkinnedMeshRendererClass && d.ClassId != MeshRendererClass)
                    continue;
                long go = d["m_GameObject"]?.Ref.FileId ?? 0;
                if (!inTree.Contains(go) || !_byId.ContainsKey(go))
                    continue;
                string? meshGuid = d.ClassId == SkinnedMeshRendererClass ? d["m_Mesh"]?.Ref.Guid : meshFiles.GetValueOrDefault(go);
                renderers.Add((d, go, meshGuid));
                if (meshGuid != null)
                    modelVotes[meshGuid] = modelVotes.GetValueOrDefault(meshGuid) + 1;
            }
            if (renderers.Count == 0)
                return;

            var model = modelVotes.OrderByDescending(v => v.Value)
                .Select(v => _archive.Find(v.Key))
                .FirstOrDefault(a => a?.File != null && ModelExtensions.Contains(a.Extension));
            if (model == null)
            {
                _notes?.Add($"{_file.Path}: '{candidate.Name}' draws meshes that are not in the package.");
                return;
            }
            candidate.Model = model;

            var leftOut = new List<string>();
            var parts = new List<(long Root, UnityModelPart Part)>();
            foreach (var (guid, _) in modelVotes.OrderByDescending(v => v.Value))
            {
                var file = _archive.Find(guid);
                if (file?.File == null || !ModelExtensions.Contains(file.Extension) || string.Equals(guid, model.Guid, StringComparison.OrdinalIgnoreCase))
                    continue;
                var tops = new List<long>();
                foreach (var r in renderers)
                {
                    if (!string.Equals(r.Mesh, guid, StringComparison.OrdinalIgnoreCase) || !_objectTransform.TryGetValue(r.Go, out long at))
                        continue;
                    tops.Add(at);
                    if (r.Doc.ClassId == SkinnedMeshRendererClass && r.Doc["m_RootBone"]?.Ref.FileId is long bone and not 0)
                        tops.Add(bone);
                }
                long partRoot = tops.Count > 0 ? CommonAncestor(tops) : 0;
                bool shared = partRoot == 0 || partRoot == rootTransform.FileId || !IsUnder(partRoot, rootTransform.FileId)
                    || _partRoots.Keys.Any(other => IsUnder(other, partRoot) || IsUnder(partRoot, other))
                    || renderers.Any(r => string.Equals(r.Mesh, model.Guid, StringComparison.OrdinalIgnoreCase)
                        && _objectTransform.TryGetValue(r.Go, out long body) && IsUnder(body, partRoot));
                if (shared || !_byId.TryGetValue(partRoot, out var rootDoc))
                {
                    leftOut.Add(file.Path);
                    continue;
                }
                var pose = LocalPose(rootDoc);
                var part = new UnityModelPart { Model = file };
                part.Root = new UnitySceneObject
                {
                    Name = pose.Name, Active = pose.Active, Position = pose.Position, Rotation = pose.Rotation, Scale = pose.Scale,
                    Part = part, IsPartRoot = true,
                };
                _partRoots[partRoot] = part;
                parts.Add((partRoot, part));
                candidate.Parts.Add(part);
            }
            if (leftOut.Count > 0)
                _notes?.Add($"{_file.Path}: '{candidate.Name}' also draws from {string.Join(", ", leftOut)} where the body's own objects are; that is left out.");

            // Renderers: the main model's by name, a part's by the ids its paths give, and the rest (built-in
            // shapes, mesh assets) made where they sit once the nodes are known.
            var madeMeshes = new List<long>();
            foreach (var (d, go, mesh) in renderers)
            {
                var owner = _byId[go];
                var name = owner["m_Name"]?.Text ?? "";
                var list = new List<string?>();
                if (d["m_Materials"] is UnitySeq materials)
                    foreach (var m in materials.Items)
                        list.Add(m.Ref.Guid);
                bool enabled = (d["m_Enabled"]?.Int(1) ?? 1) != 0;
                // Every weight the renderer was saved at, zeros too: they win over the file's own defaults.
                // Weapons hidden behind "off" shapes at 100 showed in every hand without them.
                SortedDictionary<int, float>? weights = null;
                if (d.ClassId == SkinnedMeshRendererClass && d["m_BlendShapeWeights"] is UnitySeq saved && saved.Items.Count > 0)
                {
                    weights = new SortedDictionary<int, float>();
                    for (int i = 0; i < saved.Items.Count; i++)
                        weights[i] = saved.Items[i].Float();
                }
                var file = mesh != null ? _archive.Find(mesh) : null;
                bool fromModel = file?.File != null && ModelExtensions.Contains(file.Extension);
                if (!fromModel && d.ClassId == MeshRendererClass)
                {
                    madeMeshes.Add(go);
                    continue;
                }
                if (_objectTransform.TryGetValue(go, out long at) && InPart(at) is { } inside)
                {
                    string type = d.ClassId == SkinnedMeshRendererClass ? "SkinnedMeshRenderer" : "MeshRenderer";
                    var slots = new SortedDictionary<int, string>();
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] is { } g)
                            slots[i] = g;
                    foreach (bool withRoot in new[] { true, false })
                    {
                        long id = UnityModelMeta.ObjectId(type, inside.Path, withRoot);
                        inside.Part.MaterialsById[id] = slots;
                        if (!enabled)
                            inside.Part.EnabledById[id] = false;
                        if (weights != null)
                            inside.Part.ShapeWeights[id] = weights;
                    }
                    continue;
                }
                candidate.MaterialsByName[name] = list;
                if (weights != null)
                    candidate.ShapeWeightsByName[name] = weights;
                candidate.Renderers++;
                if (!enabled)
                    candidate.ActiveByName[name] = false;
            }

            foreach (var go in objects)
            {
                if (!_byId.TryGetValue(go, out var obj))
                    continue;
                if ((obj["m_IsActive"]?.Int(1) ?? 1) == 0)
                {
                    if (_objectTransform.TryGetValue(go, out long at) && InPart(at) is { } inside)
                    {
                        if (inside.Path.Length > 0)
                            foreach (bool withRoot in new[] { true, false })
                                inside.Part.ActiveById[UnityModelMeta.GameObjectId(inside.Path, withRoot)] = false;
                    }
                    else
                    {
                        candidate.ActiveByName[obj["m_Name"]?.Text ?? ""] = false;
                    }
                }
                if (_descriptors.TryGetValue(go, out var descriptor) && !candidate.IsAvatar)
                {
                    float scale = _objectTransform.TryGetValue(go, out long t) && _byId.TryGetValue(t, out var transform) && !transform.Stripped
                        ? transform["m_LocalScale"]?["y"]?.Float(1f) ?? 1f
                        : 1f;
                    MarkAvatar(candidate, descriptor, scale);
                }
            }

            var rootTransformId = rootTransform.FileId;
            candidate.ResolveTransform = id => HierarchyNode(id, rootTransformId);
            candidate.ResolveObject = go => ObjectNode(candidate, go);
            candidate.ResolveComponent = id => ComponentNode(candidate, id);
            foreach (var (partRoot, part) in parts)
            {
                part.Root.Node = new UnityNodeRef
                {
                    ModelId = UnityModelPart.RootTransform, Part = part, Name = part.Root.Name, Active = part.Root.Active,
                    Position = part.Root.Position, Rotation = part.Root.Rotation, Scale = part.Root.Scale,
                };
                _nodes[partRoot] = part.Root.Node;
                part.Root.Node.Parent = HierarchyNode(Father(partRoot), rootTransformId);
            }
            foreach (var go in objects)
            {
                if (!_objectTransform.TryGetValue(go, out long transform) || HierarchyNode(transform, rootTransformId) is not { } node)
                    continue;
                candidate.NodesByPath.TryAdd(node.Part == null && node.Path != null ? node.Path : PathOf(transform, rootTransformId), node);
            }
            ReadVisemes(candidate);
            foreach (var d in _docs)
            {
                long go = d["m_GameObject"]?.Ref.FileId ?? 0;
                if (go == 0 || !inTree.Contains(go) || ObjectNode(candidate, go) is not { } node)
                    continue;
                Classify(candidate, d, node);
            }
            foreach (var go in madeMeshes)
                if (ObjectNode(candidate, go) is { } node && _components.TryGetValue(go, out var components))
                    CollectMesh(candidate, components, node);

            _found.Add(candidate);
        }

        private string PathOf(long transform, long rootTransform)
        {
            var names = new List<string>();
            for (long t = transform; t != 0 && t != rootTransform && names.Count < 512; t = Father(t))
                names.Add(NameOf(t));
            names.Reverse();
            return string.Join('/', names);
        }
    }
}
