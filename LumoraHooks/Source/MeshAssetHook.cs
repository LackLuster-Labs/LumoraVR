// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Phos;

namespace Lumora.Godot.Hooks;

// Godot implementation of mesh asset hook.
// Creates and manages Godot ArrayMesh resources.
//
// The attribute is what makes this exist at all. AssetHookRegistry.RegisterFromAssembly only picks up
// hooks that carry it, and without it GetHookType(typeof(MeshDataAsset)) returns null, InstantiateHook
// returns null, and every MeshDataAsset in the world ends up with a null Hook. Nothing errors: the
// renderer asks for the ArrayMesh, gets nothing, logs "not ready yet" at Debug and quietly draws whatever
// it had. That is why a MeshProvider mesh never appeared - not a load failure, the asset decoded fine and
// reported FullyLoaded, there was simply no hook to build the ArrayMesh. Every other asset hook in this
// folder carries it; this one was the only one that did not. -xlinka
[ImplementableHook(typeof(MeshDataAsset))]
public class MeshAssetHook : AssetHook, IMeshAssetHook
{
    private ArrayMesh _godotMesh = null!;
    private bool _loggedSurfaceOverflow;
    private volatile bool _unloaded;
    private long _uploadSerial;
    private long _appliedSerial;

    // Held instead of uploaded, see UploadMesh. Materialized by the first reader, if one ever comes.
    //
    // The asset load thread writes this while the main thread reads it through IsValid and the GodotMesh
    // getter, so the field is volatile and every mutation goes through _deferGate. It also has to stay set
    // until the upload it produced has really applied: an apply reached from off the main thread lands a
    // frame later through CallDeferred, so clearing on the way in leaves a window where IsValid answers
    // true over a null GodotMesh, which is the one state the deferral exists to prevent. -xlinka
    private volatile PhosMesh? _deferredMesh;
    private readonly object _deferGate = new();

    // Guarded by _deferGate. One reader builds; the rest fall through to the mesh that is already there.
    private bool _materializing;

    // RenderingServer refuses surface 257 and up (MAX_MESH_SURFACES), one engine error per rejected surface.
    // Stop at the ceiling and say it once instead. -xlinka
    private const int MaxGodotMeshSurfaces = 256;

    // Get the Godot ArrayMesh.
    public ArrayMesh GodotMesh
    {
        get
        {
            MaterializeDeferred();
            return _godotMesh;
        }
    }

    // Which submesh each surface was built from; see SubmeshForSurface. Main thread only.
    private int[]? _surfaceSubmeshes;

    // An empty submesh makes no surface, so surface and submesh numbers part ways at the first one, and a
    // renderer's materials are numbered by submesh. Asked per surface when materials are put on. -xlinka
    public int SubmeshForSurface(int surface) =>
        _surfaceSubmeshes != null && surface < _surfaceSubmeshes.Length ? _surfaceSubmeshes[surface] : surface;

    // Whether the mesh is valid and usable. A deferred mesh counts: the reader that asks is the one that
    // makes it real, and every caller checks this before touching GodotMesh.
    public bool IsValid => _godotMesh != null || _deferredMesh != null;

    private sealed class MeshUpload
    {
        public long Serial;
        public PhosMesh? Source;
        public List<global::Godot.Collections.Array> Surfaces = new();
        public int Dropped;
        public int SubmeshCount;
        public List<int> SurfaceSubmeshes = new();
        // Surfaces built from a points submesh (PointQuadSurface), which carry a custom channel.
        public HashSet<int> PointSurfaces = new();
        public MeshLodSet? Lods;
    }

    // Upload PhosMesh data to the Godot mesh.
    public void UploadMesh(PhosMesh mesh)
    {
        if (mesh == null || mesh.VertexCount == 0) return;

        // A skinned mesh is drawn by SkinnedMeshHook, which builds its OWN ArrayMesh out of the same
        // PhosMesh because it has to attach a Skin and the per-shape morph arrays that a surface built
        // here cannot carry. Nothing reads this one: the only two readers (the static renderer base and
        // the particle system) resolve a MeshProvider, and neither is on the skinned path. So every
        // imported avatar was converting and uploading its entire geometry to the GPU a second time, for
        // a resource with no reader - on top of an import that was already the heaviest moment in the
        // session. Hold the data instead and pay only if somebody actually asks: pointing a plain
        // MeshRenderer or a particle system at a skinned provider is legal and still works, one frame
        // later than it used to. -xlinka
        if (mesh.HasBoneBindings)
        {
            lock (_deferGate)
            {
                _deferredMesh = mesh;
                // New data supersedes whatever a reader was already building, so let the next reader claim it.
                _materializing = false;
            }
            return;
        }

        UploadNow(mesh, applyImmediately: false);
    }

    // Build the mesh a deferred upload was holding. Runs on the reader's thread, which is the main thread
    // for both readers, so the surfaces can go on without a round trip through CallDeferred and the caller
    // gets its mesh in the same frame it asked. -xlinka
    private void MaterializeDeferred()
    {
        if (_deferredMesh == null)
            return;

        PhosMesh? pending;
        lock (_deferGate)
        {
            pending = _deferredMesh;
            if (pending == null || _materializing)
                return;
            _materializing = true;
        }

        if (_unloaded)
            return;

        // Nothing renderable came out of it, so no apply is coming to clear the hold. Drop it here or
        // IsValid keeps promising a mesh that will never exist.
        if (!UploadNow(pending, applyImmediately: IsMainThread()))
            ClearDeferred(pending);
    }

    // Only the data that produced this upload gets released; a newer deferred mesh that arrived while the
    // upload was in flight has to survive to be materialized by the next reader.
    private void ClearDeferred(PhosMesh? source)
    {
        if (source == null) return;
        lock (_deferGate)
        {
            if (!ReferenceEquals(_deferredMesh, source)) return;
            _deferredMesh = null;
            _materializing = false;
        }
    }

    private bool UploadNow(PhosMesh mesh, bool applyImmediately)
    {
        var upload = BuildSurfaces(mesh);
        if (upload == null) return false;

        // Everything up to here is plain array conversion, so it stays on the caller's thread; only the
        // AddSurfaceFromArrays at the end touches the RenderingServer and has to be deferred to the main
        // thread. The LOD bake costs tens of milliseconds to seconds and must never land on the frame loop.
        // -xlinka
        if (!ShouldBakeLods(mesh))
        {
            if (applyImmediately)
                ApplyUpload(upload);
            else
                Commit(upload);
            return true;
        }

        // A chain already on disk is a file read and comes straight back on the load thread. A bake goes to the
        // variant workers, ahead of the texture ladders: an import's static meshes all missed together and baked
        // as wide as the thread pool would go, at the frame's own priority. The finished upload is committed from
        // the pool, so the mesh build after it never runs on a low-priority thread holding the renderer's locks.
        // -xlinka
        string label = DescribeSource();
        string? directory = MeshLodCache.GetDirectory();
        bool main = IsMainThread();
        string? key = main ? null : MeshLodCache.BuildKey(mesh);
        if (key != null && !string.IsNullOrEmpty(directory) && MeshLodCache.TryLoad(directory!, key, upload.Surfaces.Count, out var cached))
        {
            upload.Lods = cached;
            Commit(upload);
            return true;
        }

        var cancel = System.Threading.LazyInitializer.EnsureInitialized(ref _bakeCancel, () => new System.Threading.CancellationTokenSource());
        VariantWorkers.Run("LOD chain of " + label, _ =>
        {
            if (_unloaded)
                return;
            key ??= MeshLodCache.BuildKey(mesh);
            upload.Lods = main && !string.IsNullOrEmpty(directory) && MeshLodCache.TryLoad(directory!, key, upload.Surfaces.Count, out var hit)
                ? hit
                : MeshLodCache.Bake(directory, key, upload.Surfaces, label);
        }, urgent: true, cancel.Token).ContinueWith(done =>
        {
            // A bake that threw (the workers log it) still shows the mesh, just without levels.
            _ = done.Exception;
            if (done.IsCanceled)
                ReleaseSurfaces(upload);
            else
                Commit(upload);
        }, System.Threading.Tasks.TaskScheduler.Default);
        return true;
    }

    // Cancelled on unload, so a bake still waiting for a worker never starts for a mesh that is gone.
    private System.Threading.CancellationTokenSource? _bakeCancel;

    // A first build away from the main thread is put together where it is: packing the surfaces for the GPU was
    // the main thread's whole share of a mesh, and an import of a thousand meshes queued seconds of it behind the
    // frame. The skinned path has always built its meshes on a worker. The main thread only takes the finished
    // mesh, unless one already landed for this asset, in which case the surfaces go into that one so nothing
    // holding it is left on a mesh nobody updates. -xlinka
    private void Commit(MeshUpload upload)
    {
        if (_godotMesh == null && !IsMainThread())
        {
            var built = BuildFresh(upload);
            if (built != null)
            {
                MainThreadWork.Post("mesh adopt " + DescribeSource(), () => AdoptBuilt(upload, built));
                return;
            }
        }
        MainThreadWork.Post("mesh upload " + DescribeSource(), () => ApplyUpload(upload));
    }

    private ArrayMesh? BuildFresh(MeshUpload upload)
    {
        if (_unloaded)
            return null;
        try
        {
            var mesh = new ArrayMesh();
            AddSurfaces(mesh, upload);
            return mesh;
        }
        catch (System.Exception ex)
        {
            Lumora.Core.Logging.Logger.Warn($"MeshAssetHook: worker build failed for {DescribeSource()}, building on the main thread: {ex.Message}");
            return null;
        }
    }

    private void AdoptBuilt(MeshUpload upload, ArrayMesh built)
    {
        if (_unloaded || upload.Serial < _appliedSerial)
        {
            built.Dispose();
            ReleaseSurfaces(upload);
            return;
        }
        if (_godotMesh != null)
        {
            built.Dispose();
            ApplyUpload(upload);
            return;
        }
        _appliedSerial = upload.Serial;
        ClearDeferred(upload.Source);
        _godotMesh = built;
        _surfaceSubmeshes = upload.SurfaceSubmeshes.Count > 0 ? upload.SurfaceSubmeshes.ToArray() : null;
        ReleaseSurfaces(upload);
        ReportDropped(upload);
    }

    // The binding swaps a null argument for a fresh wrapper and holds nothing while the native call runs, so
    // optimised code can finalize it mid-add and the native side reads freed memory (Release-only crashes on
    // this worker). Shared empties and a kept-alive LOD dictionary close that. -xlinka
    private static readonly global::Godot.Collections.Array<global::Godot.Collections.Array> NoBlendShapes = new();
    private static readonly global::Godot.Collections.Dictionary NoLods = new();

    private static void AddSurfaces(ArrayMesh mesh, MeshUpload upload)
    {
        for (int i = 0; i < upload.Surfaces.Count; i++)
        {
            var lods = upload.Lods?.For(i)?.ToGodot();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, upload.Surfaces[i], NoBlendShapes, lods ?? NoLods,
                (upload.PointSurfaces.Contains(i) ? PointQuadSurface.Format : 0) | MeshUVChannels.FormatFor(upload.Surfaces[i]));
            System.GC.KeepAlive(lods);
            System.GC.KeepAlive(upload.Surfaces[i]);
        }
    }

    // LOD levels are attached when a surface is created and there is no API to add them afterwards, so a
    // "render now, swap in the levels later" split would have to clear and re-add every surface. Clearing
    // an ArrayMesh's surfaces makes every MeshInstance3D holding it drop its per-surface material
    // overrides, which is how you get a world that finishes loading and then turns default-grey. So the
    // levels are resolved before the one and only build. On a cache hit that costs a file read; on a miss
    // the mesh appears a few frames later than it would have, on a thread nobody is waiting on. -xlinka
    private bool ShouldBakeLods(PhosMesh mesh)
    {
        if (!MeshLodCache.IsBakeableAsset(asset))
            return false;
        if (!MeshLodCache.IsBakeableGeometry(mesh, out _, out _))
            return false;
        // The unindexed fallback surface below has no index buffer to reduce.
        return mesh.Submeshes.Count > 0;
    }

    private string DescribeSource() =>
        (asset as Asset)?.AssetURL?.ToString() ?? asset?.GetType().Name ?? "mesh";

    private static bool IsMainThread() => OS.GetThreadCallerId() == OS.GetMainThreadId();

    // Converts the mesh to Godot surface arrays. No RenderingServer contact, so it runs wherever the
    // caller is.
    private MeshUpload? BuildSurfaces(PhosMesh mesh)
    {
        var upload = new MeshUpload
        {
            Serial = System.Threading.Interlocked.Increment(ref _uploadSerial),
            Source = mesh,
            SubmeshCount = mesh.Submeshes.Count,
        };

        var subset = new SurfaceSubset(mesh.VertexCount);
        for (int index = 0; index < mesh.Submeshes.Count; index++)
        {
            var submesh = mesh.Submeshes[index];
            // Surfaces are drawn as triangles; a points submesh fed in as triangles drew noise or was refused,
            // so points go as one quad of corners each, for billboard materials to open up.
            if (submesh == null || submesh.IndexCount == 0) continue;
            bool points = submesh.Topology == PhosTopology.Points;
            if (!points && submesh.Topology != PhosTopology.Triangles) continue;

            if (upload.Surfaces.Count >= MaxGodotMeshSurfaces)
            {
                upload.Dropped++;
                continue;
            }

            var arrays = points ? PointQuadSurface.Build(mesh, submesh) : BuildSurfaceArrays(mesh, submesh, subset);
            if (arrays != null)
            {
                if (points)
                    upload.PointSurfaces.Add(upload.Surfaces.Count);
                upload.Surfaces.Add(arrays);
                upload.SurfaceSubmeshes.Add(index);
            }
        }

        // If no submeshes but we have vertex data, create a single surface
        if (mesh.Submeshes.Count == 0 && mesh.VertexCount > 0)
        {
            if ((mesh.VertexCount % 3) != 0)
            {
                Lumora.Core.Logging.Logger.Warn($"MeshAssetHook.UploadMesh: Skipping surface - no indices and vertex count {mesh.VertexCount} is not a multiple of 3");
                return null;
            }

            var arrays = BuildSurfaceArraysNoIndices(mesh);
            if (arrays != null)
                upload.Surfaces.Add(arrays);
        }

        return upload;
    }

    private void ApplyUpload(MeshUpload upload)
    {
        if (_unloaded) { ReleaseSurfaces(upload); return; }

        // Two uploads can be in flight when one of them stopped to bake. Whichever mesh data is newest
        // wins regardless of which finished first.
        if (upload.Serial < _appliedSerial) { ReleaseSurfaces(upload); return; }
        _appliedSerial = upload.Serial;

        // Past every early return, so the surfaces below are certain to land this call. This is the first
        // moment the hold can go without opening a valid-but-null gap.
        ClearDeferred(upload.Source);

        if (_godotMesh == null)
        {
            _godotMesh = new ArrayMesh();
        }
        else
        {
            _godotMesh.ClearSurfaces();
        }

        AddSurfaces(_godotMesh, upload);
        _surfaceSubmeshes = upload.SurfaceSubmeshes.Count > 0 ? upload.SurfaceSubmeshes.ToArray() : null;

        // The surface owns its copy of the vertex data now; the staging arrays hold native storage that
        // otherwise waits for a finalizer. -xlinka
        ReleaseSurfaces(upload);
        ReportDropped(upload);
    }

    private void ReportDropped(MeshUpload upload)
    {
        if (upload.Dropped > 0)
        {
            if (!_loggedSurfaceOverflow)
            {
                _loggedSurfaceOverflow = true;
                Lumora.Core.Logging.Logger.Warn($"MeshAssetHook.UploadMesh: mesh has {upload.SubmeshCount} submeshes but Godot caps a mesh at {MaxGodotMeshSurfaces} surfaces - {upload.Dropped} surface(s) past the cap were dropped and will not render.");
            }
        }
        else
        {
            _loggedSurfaceOverflow = false;
        }
    }

    private static void ReleaseSurfaces(MeshUpload upload)
    {
        for (int i = 0; i < upload.Surfaces.Count; i++)
            upload.Surfaces[i].Dispose();
        upload.Surfaces.Clear();
    }

    // One submesh's surface, carrying only the vertices its own triangles use (SurfaceSubset). Every surface
    // used to convert and carry the whole mesh, so an object with six materials paid for its geometry six
    // times over, on the load thread and on the GPU. -xlinka
    private global::Godot.Collections.Array BuildSurfaceArrays(PhosMesh mesh, PhosSubmesh submesh, SurfaceSubset subset)
    {
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);

        bool part = subset.Take(submesh.RawIndices, submesh.IndexCount);
        System.Span<int> used = part ? subset.Vertices : System.Span<int>.Empty;
        int vertexCount = part ? used.Length : mesh.VertexCount;

        // Positions (required)
        var rawPositions = mesh.RawPositions;
        if (rawPositions != null && rawPositions.Length > 0)
        {
            var positions = new Vector3[vertexCount];
            for (int k = 0; k < vertexCount; k++)
            {
                var pos = rawPositions[part ? used[k] : k];
                positions[k] = new Vector3(pos.x, pos.y, pos.z);
            }
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
        }

        // Normals
        var rawNormals = mesh.RawNormals;
        if (rawNormals != null && rawNormals.Length > 0)
        {
            var normals = new Vector3[vertexCount];
            for (int k = 0; k < vertexCount; k++)
            {
                var normal = rawNormals[part ? used[k] : k];
                normals[k] = new Vector3(normal.x, normal.y, normal.z);
            }
            arrays[(int)Mesh.ArrayType.Normal] = normals;
        }

        // Tangents
        var rawTangents = mesh.RawTangents;
        if (rawTangents != null && rawTangents.Length > 0)
        {
            var tangents = new float[vertexCount * 4];
            for (int k = 0; k < vertexCount; k++)
            {
                var t = rawTangents[part ? used[k] : k];
                tangents[k * 4] = t.x;
                tangents[k * 4 + 1] = t.y;
                tangents[k * 4 + 2] = t.z;
                tangents[k * 4 + 3] = t.w;
            }
            arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        }

        // UV0
        var rawUV0 = mesh.RawUV0s;
        if (rawUV0 != null && rawUV0.Length > 0)
        {
            var uvs = new Vector2[vertexCount];
            for (int k = 0; k < vertexCount; k++)
            {
                var uv = rawUV0[part ? used[k] : k];
                uvs[k] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        }

        // UV1
        var rawUV1 = mesh.RawUV1s;
        if (rawUV1 != null && rawUV1.Length > 0)
        {
            var uvs = new Vector2[vertexCount];
            for (int k = 0; k < vertexCount; k++)
            {
                var uv = rawUV1[part ? used[k] : k];
                uvs[k] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV2] = uvs;
        }

        float[]? extraUV = null;
        MeshUVChannels.Set(arrays, mesh, ref extraUV, used);

        // Vertex colors
        var rawColors = mesh.RawColors;
        if (rawColors != null && rawColors.Length > 0)
        {
            var colors = new Color[vertexCount];
            for (int k = 0; k < vertexCount; k++)
            {
                var c = rawColors[part ? used[k] : k];
                colors[k] = new Color(c.r, c.g, c.b, c.a);
            }
            arrays[(int)Mesh.ArrayType.Color] = colors;
        }

        // Bone indices + weights (skinning). Godot wants 4 influences per vertex: a PackedInt32 bones array
        // and a PackedFloat32 weights array, both sized vertexCount*4. The skeleton + Skin (bind poses) that
        // make these meaningful are built on the renderer side from the asset's bone table. -xlinka
        if (mesh.HasBoneBindings)
        {
            var bindings = mesh.RawBoneBindings;
            if (bindings != null && bindings.Length >= mesh.VertexCount)
            {
                var bones = new int[vertexCount * 4];
                var weights = new float[vertexCount * 4];
                for (int k = 0; k < vertexCount; k++)
                {
                    var b = bindings[part ? used[k] : k];
                    bones[k * 4 + 0] = (int)b.boneIndices.x;
                    bones[k * 4 + 1] = (int)b.boneIndices.y;
                    bones[k * 4 + 2] = (int)b.boneIndices.z;
                    bones[k * 4 + 3] = (int)b.boneIndices.w;
                    weights[k * 4 + 0] = b.boneWeights.x;
                    weights[k * 4 + 1] = b.boneWeights.y;
                    weights[k * 4 + 2] = b.boneWeights.z;
                    weights[k * 4 + 3] = b.boneWeights.w;
                }
                arrays[(int)Mesh.ArrayType.Bones] = bones;
                arrays[(int)Mesh.ArrayType.Weights] = weights;
            }
        }

        // Indices from submesh
        var rawIndices = submesh.RawIndices;
        if (part)
        {
            arrays[(int)Mesh.ArrayType.Index] = Variant.CreateFrom(subset.Indices);
        }
        else if (rawIndices != null && rawIndices.Length > 0)
        {
            var indices = new int[submesh.IndexCount];
            System.Array.Copy(rawIndices, indices, submesh.IndexCount);
            arrays[(int)Mesh.ArrayType.Index] = indices;
        }

        return arrays;
    }

    private global::Godot.Collections.Array? BuildSurfaceArraysNoIndices(PhosMesh mesh)
    {
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);

        int vertexCount = mesh.VertexCount;

        // Positions (required)
        var rawPositions = mesh.RawPositions;
        if (rawPositions == null || rawPositions.Length == 0) return null;

        var positions = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            var pos = rawPositions[i];
            positions[i] = new Vector3(pos.x, pos.y, pos.z);
        }
        arrays[(int)Mesh.ArrayType.Vertex] = positions;

        // Normals
        var rawNormals = mesh.RawNormals;
        if (rawNormals != null && rawNormals.Length > 0)
        {
            var normals = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var normal = rawNormals[i];
                normals[i] = new Vector3(normal.x, normal.y, normal.z);
            }
            arrays[(int)Mesh.ArrayType.Normal] = normals;
        }

        // UV0
        var rawUV0 = mesh.RawUV0s;
        if (rawUV0 != null && rawUV0.Length > 0)
        {
            var uvs = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var uv = rawUV0[i];
                uvs[i] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        }

        var rawUV1 = mesh.RawUV1s;
        if (rawUV1.Length >= vertexCount)
        {
            var uvs = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                uvs[i] = new Vector2(rawUV1[i].x, rawUV1[i].y);
            arrays[(int)Mesh.ArrayType.TexUV2] = uvs;
        }
        float[]? extraUV = null;
        MeshUVChannels.Set(arrays, mesh, ref extraUV);
        return arrays;
    }

    // Unload and dispose the Godot mesh.
    public override void Unload()
    {
        // A bake that is still running will finish and post its apply; the flag is what stops it
        // resurrecting a mesh for an asset that is gone.
        _unloaded = true;
        System.Threading.Interlocked.Exchange(ref _bakeCancel, null)?.Cancel();
        lock (_deferGate)
        {
            _deferredMesh = null;
            _materializing = false;
        }
        if (_godotMesh != null)
        {
            _godotMesh.Dispose();
            _godotMesh = null!;
        }
    }
}
