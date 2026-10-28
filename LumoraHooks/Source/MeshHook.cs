// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Phos;
using Lumora.Core.Logging;
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lumora.Godot.Hooks;

// Godot hook for ProceduralMesh components: converts the PhosMesh to an ArrayMesh and uploads it.
// A procedural mesh is an asset and draws nothing itself. A MeshRenderer, a soft body or a particle
// system pointing at it is what puts it on screen. -xlinka
#nullable enable
[ImplementableHook(
    typeof(ProceduralMesh),
    typeof(Lumora.Core.Components.Meshes.BoxMesh),
    typeof(Lumora.Core.Components.Meshes.QuadMesh),
    typeof(Lumora.Core.Components.Meshes.CylinderMesh),
    typeof(Lumora.Core.Components.Meshes.SphereMesh),
    typeof(Lumora.Core.Components.TextRenderer))]
public class MeshHook : ComponentHook<ProceduralMesh>
{
    private ArrayMesh? godotMesh;
    private bool _loggedNoIndexWarning;
    private bool _loggedNoVertexWarning;
    private bool _loggedBadIndexError;
    private bool _loggedNaNVertexError;
    private bool _loggedSurfaceOverflow;

    // RenderingServer refuses surface 257 and up (MAX_MESH_SURFACES). Godot's own check is an ERR_FAIL
    // condition, so going past it prints one engine error PER SURFACE PER UPLOAD - a UI panel that rebuilds
    // on hover turns the log into a firehose and the extra surfaces are silently gone anyway. Stop at the
    // ceiling ourselves and say so once. -xlinka
    private const int MaxGodotMeshSurfaces = 256;

    // Reused surface-array scratch buffers. A deforming mesh (soft body) re-uploads every frame; allocating
    // fresh managed arrays each time churns the GC and causes frame hitches. Reuse and only grow. -xlinka
    private Vector3[]? _posBuf;
    private Vector3[]? _normBuf;
    private float[]? _tanBuf;
    private Color[]? _colBuf;
    private Vector2[]? _uvBuf;
    private Vector2[]? _uv1Buf;
    private float[]? _extraUV;
    private int[]? _idxBuf;

    // One surface's share of each channel, gathered from the buffers above. Grow-only too.
    private Vector3[]? _partPos;
    private Vector3[]? _partNorm;
    private float[]? _partTan;
    private Color[]? _partCol;
    private Vector2[]? _partUV0;
    private Vector2[]? _partUV1;
    private readonly SurfaceSubset _subset = new();

    // Which channels the upload in progress carries; set by ConvertChannels.
    private bool _hasNormals, _hasTangents, _hasColors, _hasUV0, _hasUV1;

    // The surface-array container handed to AddSurfaceFromArrays, reused across uploads. It is a native
    // container behind a finalizable wrapper, and a fresh one per submesh per upload was one more
    // finalizer-queue entry every frame for every deforming mesh. Cleared and re-sized per submesh so a
    // channel the previous upload carried cannot leak into one that does not. -xlinka
    private global::Godot.Collections.Array? _surfaceArrays;

    // Factory method for creating mesh hooks.
    public static IHook<ProceduralMesh> Constructor()
    {
        return new MeshHook();
    }

    public override void Initialize()
    {
        Lumora.Core.Logging.Logger.Debug($"MeshHook.Initialize: Starting for component on slot '{Owner?.Slot?.SlotName?.Value}'");

        godotMesh = new ArrayMesh();

        // Initial upload if mesh data is ready
        if (Owner!.PhosMesh != null && UploadMesh(Owner.PhosMesh, Owner.UploadHint))
            NotifyRenderer();
    }

    // The mesh as Godot holds it, for whatever draws it.
    public ArrayMesh? GodotMesh => godotMesh;

    // Tell the MeshRenderer on our slot to re-apply now that our ArrayMesh actually has surfaces - the
    // surface count going 0->N is invisible to its SyncRef change path, so it needs an explicit poke. -xlinka
    private void NotifyRenderer()
    {
        Owner.Slot?.GetComponent<Lumora.Core.Components.MeshRenderer>()?.FlagSurfacesDirty();
    }

    public override void ApplyChanges()
    {
        // Upload changes if mesh is dirty
        if (Owner.IsDirty && Owner.PhosMesh != null)
        {
            bool uploaded = UploadMesh(Owner.PhosMesh, Owner.UploadHint);
            Owner.ClearDirty();

            // Our ArrayMesh just went from empty -> populated. SurfaceCount changing 0->N is invisible to
            // the renderer's SyncRef change path, so on a joiner (where the MeshRenderer hook may have
            // drained before us) it would otherwise hold our empty mesh forever. Re-drive the co-located
            // renderer so it re-reads the now-populated ArrayMesh and re-applies materials. -xlinka
            if (uploaded)
                NotifyRenderer();
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        // Clean up Godot resources
        if (!destroyingWorld)
        {
            if (ReleaseEntry() && godotMesh != null)
            {
                godotMesh.Dispose();
            }
        }
        else
        {
            ReleaseEntry();
        }
        MeshReplaced = null;
        SurfacesRebuilt = null;

        _surfaceArrays?.Dispose();
        _surfaceArrays = null;
        godotMesh = null;
        _loggedNoIndexWarning = false;
        _loggedNoVertexWarning = false;
        _loggedBadIndexError = false;
        _loggedNaNVertexError = false;
        _loggedSurfaceOverflow = false;
    }

    // Upload PhosMesh to Godot ArrayMesh.
    // Only uploads channels marked dirty in the upload hint.
    //
    // Surfaces stay one per drawing triangle submesh, numbered in submesh order with the empty ones left out:
    // that is how every generator numbers the materials it hands the renderer (a canvas chunk counts only
    // the submeshes that draw), so the surface list must never change shape here. -xlinka
    // False when the data is what the mesh already holds and nothing was sent.
    private bool UploadMesh(PhosMesh phosMesh, MeshUploadHint uploadHint)
    {
        if (godotMesh == null) return false;

        Span<byte> digest = stackalloc byte[DigestBytes];
        bool hashed = !uploadHint[MeshUploadHint.Flag.Deforming] && TryDigest(phosMesh, uploadHint, digest);
        var key = hashed ? new DigestKey(digest) : default;
        if (hashed && _entry != null && _entry.Key.Equals(key))
            return false;

        if (hashed && _sharedMeshes.TryGetValue(key, out var existing))
        {
            bool alone = ReleaseEntry();
            existing.Users++;
            _entry = existing;
            ReplaceMesh(existing.Mesh, alone);
            return true;
        }

        if (_entry != null)
        {
            if (_entry.Users > 1)
            {
                // Others draw this one; the new data goes into a mesh of our own.
                ReleaseEntry();
                ReplaceMesh(new ArrayMesh(), false);
            }
            else
            {
                // Ours alone: it changes in place, so its old digest stops describing it.
                _sharedMeshes.Remove(_entry.Key);
                _entry = null;
            }
        }

        godotMesh!.ClearSurfaces();
        _subset.Reset(phosMesh.VertexCount);
        bool converted = false;

        int dropped = 0;
        foreach (var submesh in phosMesh.Submeshes)
        {
            if (submesh is not PhosTriangleSubmesh triangleSubmesh)
            {
                continue;
            }

            if (godotMesh.GetSurfaceCount() >= MaxGodotMeshSurfaces)
            {
                dropped++;
                continue;
            }

            UploadTriangleSubmesh(phosMesh, triangleSubmesh, uploadHint, ref converted);
        }

        if (dropped > 0)
        {
            if (!_loggedSurfaceOverflow)
            {
                _loggedSurfaceOverflow = true;
                Lumora.Core.Logging.Logger.Warn($"MeshHook.UploadMesh: '{Owner.Slot?.SlotName?.Value}' has {phosMesh.Submeshes.Count} submeshes but Godot caps a mesh at {MaxGodotMeshSurfaces} surfaces - {dropped} surface(s) past the cap were dropped and will not render. Split the mesh, or batch its materials.");
            }
        }
        else
        {
            // Fits again, so let the next overflow speak up.
            _loggedSurfaceOverflow = false;
        }

        SurfacesRebuilt?.Invoke();

        if (hashed)
        {
            _entry = new SharedMesh { Mesh = godotMesh, Key = key, Users = 1 };
            _sharedMeshes[key] = _entry;
        }
        return true;
    }

    // Procedural meshes with the same data share one Godot mesh. A world of graph cards builds
    // hundreds that are byte-identical to another (every card's preview quad, its rounded panels, the
    // gallery's matching spheres), and each was its own upload and its own GPU buffers. A shared mesh
    // is never changed in place: a hook whose data moves on takes another, and whatever draws it hears
    // through MeshReplaced. Main thread only, like every hook. -xlinka
    private sealed class SharedMesh
    {
        public ArrayMesh Mesh = null!;
        public DigestKey Key;
        public int Users;
    }

    private readonly struct DigestKey : IEquatable<DigestKey>
    {
        private readonly ulong _a, _b, _c, _d;

        public DigestKey(ReadOnlySpan<byte> digest)
        {
            var words = MemoryMarshal.Cast<byte, ulong>(digest);
            _a = words[0];
            _b = words[1];
            _c = words[2];
            _d = words[3];
        }

        public bool Equals(DigestKey other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;
        public override bool Equals(object? obj) => obj is DigestKey other && Equals(other);
        public override int GetHashCode() => (int)_a;
    }

    private static readonly System.Collections.Generic.Dictionary<DigestKey, SharedMesh> _sharedMeshes = new();
    private SharedMesh? _entry;

    // Raised when the Godot mesh this hook hands out becomes a different object.
    public event Action? MeshReplaced;

    // Raised straight after this hook's own mesh was emptied and filled again in place. A mesh instance keeps a
    // material per surface and cuts that list down to the surfaces the mesh has each time one is added, so a
    // rebuild leaves every surface after the first with no material. Whatever draws the mesh puts its materials
    // back from here, before the frame is drawn: the queued apply NotifyRenderer asks for can land after it on a
    // busy frame, and a neon stroke's halo was drawn once in the engine's plain grey as it was let go. -xlinka
    public event Action? SurfacesRebuilt;

    // Lets go of the shared entry. True when nobody else draws the mesh this hook held.
    private bool ReleaseEntry()
    {
        var entry = _entry;
        if (entry == null)
            return true;
        _entry = null;
        if (--entry.Users > 0)
            return false;
        _sharedMeshes.Remove(entry.Key);
        return true;
    }

    // Hands out another mesh. The one before goes when nobody else draws it; whatever still shows it
    // keeps it alive on the Godot side until it picks up the replacement.
    private void ReplaceMesh(ArrayMesh mesh, bool disposeOld)
    {
        var old = godotMesh;
        godotMesh = mesh;
        MeshReplaced?.Invoke();
        if (disposeOld && old != null && !ReferenceEquals(old, mesh))
            old.Dispose();
    }

    // What the last upload carried, so the same data is not sent again. Every procedural mesh built
    // its data twice on the way in, once when it started and once when its fields landed, and both
    // went up whole: a world of cards uploaded each quad, band and preview twice over. A canvas chunk
    // rebuilt for a neighbour went up again unchanged. A mesh that really did change pays one hash of
    // its data on top of the upload it was making anyway; a big one skips the hash, since those
    // are the ones that change every time they upload. -xlinka
    private const int DigestBytes = 32;
    private const int DigestVertexLimit = 65536;

    [ThreadStatic] private static IncrementalHash? _digest;

    private static bool TryDigest(PhosMesh mesh, MeshUploadHint hint, Span<byte> digest)
    {
        int n = mesh.VertexCount;
        if (n > DigestVertexLimit)
            return false;

        bool normals = mesh.HasNormals && hint[MeshUploadHint.Flag.Normals];
        bool tangents = mesh.HasTangents && hint[MeshUploadHint.Flag.Tangents];
        bool colors = mesh.HasColors && hint[MeshUploadHint.Flag.Colors];
        bool uv0 = mesh.HasUV0s && hint[MeshUploadHint.Flag.UV0];
        bool uv1 = mesh.HasUV1s && hint[MeshUploadHint.Flag.UV1];
        bool uv2 = mesh.RawUV2s.Length > 0 && hint[MeshUploadHint.Flag.UV2];
        bool uv3 = mesh.RawUV3s.Length > 0 && hint[MeshUploadHint.Flag.UV3];

        var h = _digest ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<int> header = stackalloc int[3];
        header[0] = n;
        header[1] = (normals ? 1 : 0) | (tangents ? 2 : 0) | (colors ? 4 : 0) | (uv0 ? 8 : 0) | (uv1 ? 16 : 0)
            | (uv2 ? 32 : 0) | (uv3 ? 64 : 0);
        header[2] = mesh.Submeshes.Count;
        h.AppendData(MemoryMarshal.AsBytes(header));
        h.AppendData(MemoryMarshal.AsBytes(mesh.RawPositions.AsSpan(0, n)));
        if (normals)
            h.AppendData(MemoryMarshal.AsBytes(Prefix(mesh.RawNormals, n)));
        if (tangents)
            h.AppendData(MemoryMarshal.AsBytes(Prefix(mesh.RawTangents, n)));
        if (colors)
            h.AppendData(MemoryMarshal.AsBytes(Prefix(mesh.RawColors, n)));
        if (uv0)
            h.AppendData(MemoryMarshal.AsBytes(Prefix(mesh.RawUV0s, n)));
        if (uv1)
            h.AppendData(MemoryMarshal.AsBytes(Prefix(mesh.RawUV1s, n)));
        if (uv2)
            h.AppendData(MemoryMarshal.AsBytes(Prefix(mesh.RawUV2s, n)));
        if (uv3)
            h.AppendData(MemoryMarshal.AsBytes(Prefix(mesh.RawUV3s, n)));

        Span<int> count = stackalloc int[1];
        foreach (var submesh in mesh.Submeshes)
        {
            // Only triangle submeshes become surfaces; anything else still counts, so a list that
            // changes kind changes the digest too.
            if (submesh is not PhosTriangleSubmesh triangles)
            {
                count[0] = -1;
                h.AppendData(MemoryMarshal.AsBytes(count));
                continue;
            }
            count[0] = triangles.IndexCount;
            h.AppendData(MemoryMarshal.AsBytes(count));
            h.AppendData(MemoryMarshal.AsBytes(Prefix(triangles.RawIndices, triangles.IndexCount)));
        }
        return h.TryGetHashAndReset(digest, out _);
    }

    // The first n entries, or as many as there are: a short channel is written padded at upload.
    private static ReadOnlySpan<T> Prefix<T>(T[]? array, int n) where T : struct
        => array == null ? ReadOnlySpan<T>.Empty : array.AsSpan(0, System.Math.Min(n, array.Length));

    // Every channel the upload carries, converted ONCE per upload into the grow-only buffers above. It
    // used to be converted in full for every surface, so a canvas chunk with forty material batches
    // converted, marshalled and uploaded its whole vertex buffer forty times per rebuild. -xlinka
    private void ConvertChannels(PhosMesh phosMesh, MeshUploadHint uploadHint)
    {
        int vertexCount = phosMesh.VertexCount;

        var positions = SurfaceSubset.Scratch(ref _posBuf, vertexCount);
        var rawPositions = phosMesh.RawPositions;
        bool hadBadPosition = false;
        for (int i = 0; i < vertexCount; i++)
        {
            var pos = rawPositions[i];
            // Non-finite positions poison the surface AABB and can fault the GPU
            // (device lost). Zero them and log instead of submitting garbage. - xlinka
            if (!float.IsFinite(pos.x) || !float.IsFinite(pos.y) || !float.IsFinite(pos.z))
            {
                hadBadPosition = true;
                positions[i] = Vector3.Zero;
                continue;
            }
            positions[i] = new Vector3(pos.x, pos.y, pos.z);
        }
        if (hadBadPosition && !_loggedNaNVertexError)
        {
            _loggedNaNVertexError = true;
            Lumora.Core.Logging.Logger.Error($"MeshHook.UploadTriangleSubmesh: Non-finite vertex positions in mesh on slot '{Owner.Slot?.SlotName?.Value}' - zeroed before upload");
        }

        _hasNormals = phosMesh.HasNormals && uploadHint[MeshUploadHint.Flag.Normals];
        if (_hasNormals)
        {
            var normals = SurfaceSubset.Scratch(ref _normBuf, vertexCount);
            for (int i = 0; i < vertexCount; i++)
            {
                var n = phosMesh.RawNormals[i];
                normals[i] = new Vector3(n.x, n.y, n.z);
            }
        }

        _hasTangents = phosMesh.HasTangents && uploadHint[MeshUploadHint.Flag.Tangents];
        if (_hasTangents)
        {
            var tangents = SurfaceSubset.Scratch(ref _tanBuf, vertexCount * 4);
            for (int i = 0; i < vertexCount; i++)
            {
                var t = phosMesh.RawTangents[i];
                tangents[i * 4 + 0] = t.x;
                tangents[i * 4 + 1] = t.y;
                tangents[i * 4 + 2] = t.z;
                tangents[i * 4 + 3] = t.w;
            }
        }

        _hasColors = phosMesh.HasColors && uploadHint[MeshUploadHint.Flag.Colors];
        if (_hasColors)
        {
            var colors = SurfaceSubset.Scratch(ref _colBuf, vertexCount);
            var rawColors = phosMesh.RawColors;
            for (int i = 0; i < vertexCount; i++)
            {
                // Safety: use white if color array is undersized
                if (i < rawColors.Length)
                {
                    var c = rawColors[i];
                    colors[i] = new Color(c.r, c.g, c.b, c.a);
                }
                else
                {
                    colors[i] = Colors.White;
                }
            }
        }

        _hasUV0 = phosMesh.HasUV0s && uploadHint[MeshUploadHint.Flag.UV0];
        if (_hasUV0)
        {
            var uvs = SurfaceSubset.Scratch(ref _uvBuf, vertexCount);
            for (int i = 0; i < vertexCount; i++)
            {
                var uv = phosMesh.RawUV0s[i];
                uvs[i] = new Vector2(uv.x, uv.y);
            }
        }

        // UV1 rides to the GPU as TexUV2. Vertex-animated meshes (BubbleFieldMesh) keep their shape
        // parameters here and their instance index in UV0, so a shader can rebuild the surface from
        // two floats without a per-instance draw. Asset meshes already did this; procedural ones
        // dropped the channel on the floor. -xlinka
        _hasUV1 = phosMesh.HasUV1s && uploadHint[MeshUploadHint.Flag.UV1];
        if (_hasUV1)
        {
            var uvs = SurfaceSubset.Scratch(ref _uv1Buf, vertexCount);
            var raw = phosMesh.RawUV1s;
            for (int i = 0; i < vertexCount; i++)
            {
                var uv = raw[i];
                uvs[i] = new Vector2(uv.x, uv.y);
            }
        }
    }

    // Upload a triangle submesh to Godot, carrying only the vertices its triangles use (SurfaceSubset).
    private void UploadTriangleSubmesh(PhosMesh phosMesh, PhosTriangleSubmesh submesh, MeshUploadHint uploadHint, ref bool converted)
    {
        if (godotMesh == null) return;
        if (submesh.IndexCount <= 0) return;

        int vertexCount = phosMesh.VertexCount;
        if (vertexCount <= 0 || phosMesh.RawPositions == null)
        {
            if (!_loggedNoVertexWarning)
            {
                _loggedNoVertexWarning = true;
                Lumora.Core.Logging.Logger.Warn($"MeshHook.UploadTriangleSubmesh: Skipping surface - no vertex data");
            }
            return;
        }

        var rawIndices = submesh.RawIndices;
        bool hasIndices = rawIndices != null;
        if (hasIndices)
        {
            int maxIndex = -1;
            int minIndex = int.MaxValue;
            for (int i = 0; i < submesh.IndexCount; i++)
            {
                int index = rawIndices![i];
                if (index > maxIndex)
                {
                    maxIndex = index;
                }
                if (index < minIndex)
                {
                    minIndex = index;
                }
            }

            // An index past the vertex buffer is an out-of-bounds read at draw time -
            // that's a Vulkan device-lost, not a visual glitch. Negative indices wrap
            // to ~4 billion as unsigned on the GPU, same failure. Drop the surface and
            // log so the broken generator is findable. - xlinka
            if (maxIndex >= vertexCount || minIndex < 0)
            {
                if (!_loggedBadIndexError)
                {
                    _loggedBadIndexError = true;
                    Lumora.Core.Logging.Logger.Error($"MeshHook.UploadTriangleSubmesh: Index range [{minIndex}, {maxIndex}] invalid for vertex count {vertexCount} on slot '{Owner.Slot?.SlotName?.Value}' - surface skipped");
                }
                return;
            }
        }
        else if ((vertexCount % 3) != 0)
        {
            if (!_loggedNoIndexWarning)
            {
                _loggedNoIndexWarning = true;
                Lumora.Core.Logging.Logger.Warn($"MeshHook.UploadTriangleSubmesh: Skipping surface - no indices and vertex count {vertexCount} is not a multiple of 3");
            }
            return;
        }

        if (!converted)
        {
            ConvertChannels(phosMesh, uploadHint);
            converted = true;
        }

        var arrays = _surfaceArrays ??= new global::Godot.Collections.Array();
        arrays.Clear();
        arrays.Resize((int)Mesh.ArrayType.Max);

        bool part = hasIndices && _subset.Take(rawIndices, submesh.IndexCount);
        if (part)
        {
            var used = _subset.Vertices;
            int n = used.Length;

            var positions = SurfaceSubset.Scratch(ref _partPos, n);
            SurfaceSubset.Gather<Vector3>(_posBuf, used, positions);
            Put(arrays, Mesh.ArrayType.Vertex, Variant.CreateFrom(positions));
            if (_hasNormals)
            {
                var normals = SurfaceSubset.Scratch(ref _partNorm, n);
                SurfaceSubset.Gather<Vector3>(_normBuf, used, normals);
                Put(arrays, Mesh.ArrayType.Normal, Variant.CreateFrom(normals));
            }
            if (_hasTangents)
            {
                var tangents = SurfaceSubset.Scratch(ref _partTan, n * 4);
                SurfaceSubset.Gather4<float>(_tanBuf, used, tangents);
                Put(arrays, Mesh.ArrayType.Tangent, Variant.CreateFrom(tangents));
            }
            if (_hasColors)
            {
                var colors = SurfaceSubset.Scratch(ref _partCol, n);
                SurfaceSubset.Gather<Color>(_colBuf, used, colors);
                Put(arrays, Mesh.ArrayType.Color, Variant.CreateFrom(colors));
            }
            if (_hasUV0)
            {
                var uvs = SurfaceSubset.Scratch(ref _partUV0, n);
                SurfaceSubset.Gather<Vector2>(_uvBuf, used, uvs);
                Put(arrays, Mesh.ArrayType.TexUV, Variant.CreateFrom(uvs));
            }
            if (_hasUV1)
            {
                var uvs = SurfaceSubset.Scratch(ref _partUV1, n);
                SurfaceSubset.Gather<Vector2>(_uv1Buf, used, uvs);
                Put(arrays, Mesh.ArrayType.TexUV2, Variant.CreateFrom(uvs));
            }
            Put(arrays, Mesh.ArrayType.Index, Variant.CreateFrom(_subset.Indices));
        }
        else
        {
            Put(arrays, Mesh.ArrayType.Vertex, Variant.CreateFrom(_posBuf.AsSpan(0, vertexCount)));
            if (_hasNormals)
                Put(arrays, Mesh.ArrayType.Normal, Variant.CreateFrom(_normBuf.AsSpan(0, vertexCount)));
            if (_hasTangents)
                Put(arrays, Mesh.ArrayType.Tangent, Variant.CreateFrom(_tanBuf.AsSpan(0, vertexCount * 4)));
            if (_hasColors)
                Put(arrays, Mesh.ArrayType.Color, Variant.CreateFrom(_colBuf.AsSpan(0, vertexCount)));
            if (_hasUV0)
                Put(arrays, Mesh.ArrayType.TexUV, Variant.CreateFrom(_uvBuf.AsSpan(0, vertexCount)));
            if (_hasUV1)
                Put(arrays, Mesh.ArrayType.TexUV2, Variant.CreateFrom(_uv1Buf.AsSpan(0, vertexCount)));
            if (hasIndices)
            {
                var indices = SurfaceSubset.Scratch(ref _idxBuf, submesh.IndexCount);
                rawIndices!.AsSpan(0, submesh.IndexCount).CopyTo(indices);
                Put(arrays, Mesh.ArrayType.Index, Variant.CreateFrom(indices));
            }
        }

        var uvFormat = MeshUVChannels.Set(arrays, phosMesh, ref _extraUV, part ? _subset.Vertices : default,
            uploadHint[MeshUploadHint.Flag.UV2], uploadHint[MeshUploadHint.Flag.UV3]);
        godotMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, NoBlendShapes, NoLods, uvFormat);
        // Godot copied the data into the surface; the container lets go of its copy now rather than
        // holding every procedural mesh's vertices twice until the next upload.
        arrays.Clear();
    }

    // A channel goes in and the temporary that carried it goes back at once. Left to the finalizer, six
    // or seven of these a surface kept one thread busy freeing them under a lock the frame also takes.
    // The binding also made a fresh empty blend-shape array and LOD dictionary for every call that left
    // them out. -xlinka
    private static void Put(global::Godot.Collections.Array arrays, Mesh.ArrayType channel, Variant value)
    {
        arrays[(int)channel] = value;
        value.Dispose();
    }

    private static readonly global::Godot.Collections.Array<global::Godot.Collections.Array> NoBlendShapes = new();
    private static readonly global::Godot.Collections.Dictionary NoLods = new();
}

