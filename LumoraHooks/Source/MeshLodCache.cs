// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using Lumora.Core.Assets;
using Lumora.Core.Phos;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Continuous mesh LOD for imported geometry.
//
// The renderer can carry several index-reduced versions of a surface inside one mesh and pick between
// them per frame from the surface's screen-space error, so a distant prop draws fewer triangles and
// nothing ever disappears: the coarsest level holds forever, however far away it gets. That is the whole
// point of doing it this way rather than distance culling. Selection is driven by the viewport's LOD
// threshold in pixels, so the levels below are generated once and are correct at every threshold.
//
// Generating them is not cheap - 15ms for a 17k-triangle surface, 140ms for 160k - so the index buffers
// are cached on disk under a hash of the geometry and the second encounter with a mesh skips the
// simplifier entirely. -xlinka

public sealed class MeshSurfaceLods
{
    public float[] Sizes = Array.Empty<float>();
    public int[][] Indices = Array.Empty<int[]>();

    public int Count => Sizes.Length;

    public long IndexTotal
    {
        get
        {
            long total = 0;
            foreach (var level in Indices)
                total += level?.LongLength ?? 0;
            return total;
        }
    }

    // Keyed by screen-space error, which is the shape both the mesh and the importer expect.
    public global::Godot.Collections.Dictionary? ToGodot()
    {
        if (Count == 0)
            return null;
        var dict = new global::Godot.Collections.Dictionary();
        for (int i = 0; i < Sizes.Length; i++)
        {
            var indices = Indices[i];
            if (indices == null || indices.Length == 0)
                continue;
            dict[Variant.From(Sizes[i])] = Variant.From(indices);
        }
        return dict.Count == 0 ? null : dict;
    }
}

public sealed class MeshLodSet
{
    public MeshSurfaceLods[] Surfaces = Array.Empty<MeshSurfaceLods>();

    public int TotalLevels
    {
        get
        {
            int total = 0;
            foreach (var surface in Surfaces)
                total += surface?.Count ?? 0;
            return total;
        }
    }

    public MeshSurfaceLods? For(int surface) =>
        (surface >= 0 && surface < Surfaces.Length) ? Surfaces[surface] : null;
}

public static class MeshLodCache
{
    // Blobs and their sidecars are sealed and named by a keyed hash of the geometry key, with no extension.
    // Files from before that carry the plain key and these extensions; they still read, and the background
    // pass seals them.
    public const string Extension = ".lvlod";
    public const string NameScope = "meshlod";

    // The sidecar records which asset a blob came from and what the bake produced, sealed like the blob.
    public const string MetadataExtension = ".lvlodmeta";
    public const string MetadataScope = "meshlodmeta";

    // Under a couple of thousand triangles the simplifier has almost nothing to remove and the levels it
    // does emit save less than the extra index buffers cost to keep resident.
    public const int MinBakeTriangles = 2048;

    // The angles the scene importer uses. They are documented parameters of the simplifier, not knobs to
    // taste: inventing values here is how you get a mesh that visibly changes shape at the default
    // threshold, which is exactly what this must never do.
    public const float NormalMergeAngle = 60f;
    public const float NormalSplitAngle = 25f;

    private const uint Magic = 0x4C4D564C; // "LVML" little-endian
    // 2: surfaces carry only the vertices they use, so every level is numbered against that subset and a
    // level stored by version 1 indexes the whole mesh. -xlinka
    private const ushort ContainerVersion = 2;

    // A surface can hold a LOD chain roughly log2(indices) deep; anything past this is a corrupt blob.
    private const int MaxLevelsPerSurface = 64;

    // ELIGIBILITY

    // Skinned and blend-shaped meshes are left out. Both rewrite vertex positions after upload -
    // skinning on the GPU against a pose the LOD chain was not built from, blend shapes by rebuilding
    // the whole ArrayMesh on our side - and a level whose indices were chosen against the rest pose is
    // not obviously safe under either. generate_lods takes a skin pose array for exactly this reason;
    // wiring that up is the follow-up. -xlinka
    public static bool IsBakeableGeometry(PhosMesh? mesh, out int triangles, out string reason)
    {
        triangles = 0;
        reason = string.Empty;

        if (mesh == null || mesh.VertexCount == 0)
        {
            reason = "empty";
            return false;
        }

        if (mesh.HasBoneBindings)
        {
            reason = "skinned";
            return false;
        }

        if (mesh.BlendShapeCount > 0)
        {
            reason = "blend shapes";
            return false;
        }

        if (mesh.Submeshes.Count == 0)
        {
            reason = "no submeshes";
            return false;
        }

        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh == null || submesh.IndexCount == 0)
                continue;
            if (submesh.IndicesPerElement != 3)
            {
                reason = "non-triangle topology";
                return false;
            }
            triangles += submesh.IndexCount / 3;
        }

        if (triangles < MinBakeTriangles)
        {
            reason = $"{triangles} triangles";
            return false;
        }

        return true;
    }

    // A dynamic asset is regenerated by whatever owns it - a trail, a text run, a particle batch, a
    // canvas - so its geometry is different on the next change and a bake would be thrown away before
    // it was ever selected against.
    public static bool IsBakeableAsset(IAsset? asset) =>
        asset is Asset concrete && concrete.AssetType == AssetType.Static;

    // ADDRESSING

    // Hashes the geometry the simplifier actually reads. Positions and indices decide the collapses;
    // normals and UV0 are weighted into the error metric, so two meshes that differ only there get
    // different chains and must not share a blob.
    public static string BuildKey(PhosMesh mesh)
    {
        using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);

        Span<byte> header = stackalloc byte[16];
        BitConverter.TryWriteBytes(header[..4], ContainerVersion);
        BitConverter.TryWriteBytes(header.Slice(4, 4), mesh.VertexCount);
        BitConverter.TryWriteBytes(header.Slice(8, 4), mesh.Submeshes.Count);
        BitConverter.TryWriteBytes(header.Slice(12, 4), (int)(NormalMergeAngle * 1000f));
        hasher.AppendData(header);

        AppendSpan(hasher, mesh.RawPositions, mesh.VertexCount);
        AppendSpan(hasher, mesh.RawNormals, mesh.VertexCount);
        AppendSpan(hasher, mesh.RawUV0s, mesh.VertexCount);

        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh == null)
                continue;
            AppendSpan(hasher, submesh.RawIndices, submesh.IndexCount);
        }

        return Convert.ToHexString(hasher.GetHashAndReset(), 0, 16).ToLowerInvariant();
    }

    private static void AppendSpan<T>(System.Security.Cryptography.IncrementalHash hasher, T[]? values, int count)
        where T : unmanaged
    {
        if (values == null || values.Length == 0 || count <= 0)
            return;
        int usable = System.Math.Min(count, values.Length);
        hasher.AppendData(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(values, 0, usable)));
    }

    public static string? GetDirectory()
    {
        try
        {
            return Lumora.Core.Engine.Current?.LocalDB?.GetMeshCachePath();
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"MeshLodCache: no cache directory available: {ex.Message}");
            return null;
        }
    }

    public static string BlobPath(string directory, string key)
    {
        Lumora.Core.Persistence.LocalEncryption.WireSealedStorage();
        return Path.Combine(directory, Lumora.Nexus.Storage.SealedKeys.NameFor(NameScope, key));
    }

    public static void RegisterSealing()
    {
        Lumora.Core.Assets.CacheSealing.Add("mesh LOD", async (pace, ct) =>
        {
            var directory = GetDirectory();
            var blobs = await Lumora.Core.Assets.CacheSealing.SealFolderAsync(directory, Extension, NameScope, pace, ct).ConfigureAwait(false);
            var sidecars = await Lumora.Core.Assets.CacheSealing.SealFolderAsync(directory, MetadataExtension, MetadataScope, pace, ct).ConfigureAwait(false);
            return (blobs.Files + sidecars.Files, blobs.Bytes + sidecars.Bytes);
        });
    }

    // BAKE

    // Callers try TryLoad first. The simplifier's result is written back either way - including an empty one.
    // A mesh the simplifier refuses (seams on every edge, so no collapse is cheap enough) would otherwise
    // pay the full generation cost on every single load, forever, to learn the same nothing. Up to seconds for
    // a big mesh, so it runs on a variant worker.
    public static MeshLodSet? Bake(
        string? directory,
        string key,
        IReadOnlyList<global::Godot.Collections.Array> surfaces,
        string sourceLabel)
    {
        if (surfaces == null || surfaces.Count == 0)
            return null;

        var start = DateTime.UtcNow;
        var generated = Generate(surfaces);
        double bakeMs = (DateTime.UtcNow - start).TotalMilliseconds;

        if (generated == null)
            return null;

        if (!string.IsNullOrEmpty(directory))
            Store(directory!, key, generated, sourceLabel, bakeMs);

        LumoraLogger.Debug(
            $"MeshLodCache: baked {generated.TotalLevels} level(s) across {surfaces.Count} surface(s) in {bakeMs:0.#}ms for {sourceLabel}");
        return generated;
    }

    // GENERATION

    // The importer mesh is used purely as the simplifier's front door: the levels come back out as index
    // buffers and the real mesh is built separately from them, so nothing here touches the render server
    // and this is safe to run on the load thread.
    public static MeshLodSet? Generate(IReadOnlyList<global::Godot.Collections.Array> surfaces)
    {
        if (surfaces == null || surfaces.Count == 0)
            return null;

        try
        {
            using var importer = new ImporterMesh();
            for (int i = 0; i < surfaces.Count; i++)
                importer.AddSurface(Mesh.PrimitiveType.Triangles, surfaces[i], null, null, null, $"s{i}");

            importer.GenerateLods(NormalMergeAngle, NormalSplitAngle, new global::Godot.Collections.Array());

            var set = new MeshLodSet { Surfaces = new MeshSurfaceLods[surfaces.Count] };
            for (int i = 0; i < surfaces.Count; i++)
            {
                int levels = importer.GetSurfaceLodCount(i);
                var lods = new MeshSurfaceLods
                {
                    Sizes = new float[levels],
                    Indices = new int[levels][],
                };
                for (int level = 0; level < levels; level++)
                {
                    lods.Sizes[level] = importer.GetSurfaceLodSize(i, level);
                    lods.Indices[level] = importer.GetSurfaceLodIndices(i, level);
                }
                set.Surfaces[i] = lods;
            }
            return set;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"MeshLodCache: LOD generation failed, mesh stays at full detail: {ex.Message}");
            return null;
        }
    }

    // STORAGE

    public static bool TryLoad(string directory, string key, int surfaceCount, out MeshLodSet set)
    {
        set = null!;
        var path = BlobPath(directory, key);
        try
        {
            if (!File.Exists(path))
            {
                path = Path.Combine(directory, key + Extension);
                if (!File.Exists(path))
                    return false;
            }

            using var stream = Lumora.Nexus.Storage.SealedFile.OpenAny(path);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != ContainerVersion)
                return false;

            int storedSurfaces = reader.ReadUInt16();
            // A blob whose surface count no longer matches would silently hand level indices to the wrong
            // surface. The hash makes that near-impossible, so treat it as corruption and re-bake.
            if (storedSurfaces != surfaceCount)
                return false;

            var loaded = new MeshLodSet { Surfaces = new MeshSurfaceLods[storedSurfaces] };
            for (int i = 0; i < storedSurfaces; i++)
            {
                int levels = reader.ReadInt32();
                if (levels < 0 || levels > MaxLevelsPerSurface)
                    return false;

                var lods = new MeshSurfaceLods
                {
                    Sizes = new float[levels],
                    Indices = new int[levels][],
                };
                for (int level = 0; level < levels; level++)
                {
                    lods.Sizes[level] = reader.ReadSingle();
                    int count = reader.ReadInt32();
                    if (count < 0 || (long)count * 4 > stream.Length - stream.Position)
                        return false;
                    var bytes = reader.ReadBytes(count * 4);
                    if (bytes.Length != count * 4)
                        return false;
                    var indices = new int[count];
                    Buffer.BlockCopy(bytes, 0, indices, 0, bytes.Length);
                    lods.Indices[level] = indices;
                }
                loaded.Surfaces[i] = lods;
            }

            set = loaded;
            return true;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"MeshLodCache: unreadable LOD blob '{path}': {ex.Message}");
            return false;
        }
    }

    public static void Store(string directory, string key, MeshLodSet set, string sourceLabel, double bakeMs)
    {
        var path = BlobPath(directory, key);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(directory);
            SweepOnce(directory);

            // Write beside the target then move into place: a crash mid-write must not leave a truncated
            // blob that a later run would load as a LOD chain. The beside file is this write's own, since two
            // assets with the same geometry bake the same key at the same time.
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = Lumora.Nexus.Storage.SealedFile.Create(temp))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(ContainerVersion);
                writer.Write((ushort)set.Surfaces.Length);
                foreach (var surface in set.Surfaces)
                {
                    int levels = surface?.Count ?? 0;
                    writer.Write(levels);
                    for (int level = 0; level < levels; level++)
                    {
                        var indices = surface!.Indices[level] ?? Array.Empty<int>();
                        writer.Write(surface.Sizes[level]);
                        writer.Write(indices.Length);
                        writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<int>(indices)));
                    }
                }
            }
            File.Move(temp, path, true);
            temp = null;

            WriteSidecar(directory, key, set, sourceLabel, bakeMs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(path))
        {
            // The same geometry baked by another asset landed first and is being read: it is this same chain.
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"MeshLodCache: could not cache LOD blob '{path}': {ex.Message}");
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    private static int _swept;

    // What a killed run left half-written beside the blobs, once a run. Only old ones: another instance on this
    // folder may be mid-write.
    private static void SweepOnce(string directory)
    {
        if (Interlocked.Exchange(ref _swept, 1) != 0)
            return;
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var file in Directory.EnumerateFiles(directory, "*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void WriteSidecar(string directory, string key, MeshLodSet set, string sourceLabel, double bakeMs)
    {
        var path = Path.Combine(directory, Lumora.Nexus.Storage.SealedKeys.NameFor(MetadataScope, key));
        try
        {
            var text = new System.Text.StringBuilder();
            text.Append("source=").Append(sourceLabel).Append('\n');
            text.Append("baked=").Append(DateTime.UtcNow.ToString("O")).Append('\n');
            text.Append("bakeMs=").Append(bakeMs.ToString("0.#")).Append('\n');
            text.Append("container=").Append(ContainerVersion).Append('\n');
            text.Append("mergeAngle=").Append(NormalMergeAngle).Append('\n');
            text.Append("splitAngle=").Append(NormalSplitAngle).Append('\n');
            for (int i = 0; i < set.Surfaces.Length; i++)
            {
                var surface = set.Surfaces[i];
                text.Append("surface").Append(i).Append("=levels:").Append(surface?.Count ?? 0)
                    .Append(" indices:").Append(surface?.IndexTotal ?? 0).Append('\n');
            }
            Lumora.Nexus.Storage.SealedFile.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        }
        catch (Exception ex)
        {
            LumoraLogger.Debug($"MeshLodCache: sidecar not written for '{key}': {ex.Message}");
        }
    }
}
