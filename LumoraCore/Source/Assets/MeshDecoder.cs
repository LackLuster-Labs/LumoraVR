// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Assimp;
using Lumora.Core.Phos;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

// Decodes mesh file bytes (glTF/GLB, OBJ, and the Assimp-supported formats) into a PhosMesh.
// Engine-agnostic and runs off the main thread during asset load.
public static class MeshDecoder
{
    // Decode mesh bytes of the given file extension (e.g. ".glb"). meshIndex -1 decodes the whole file
    // concatenated; >= 0 decodes only that mesh (one Phos asset per mesh). Returns null on failure.
    //
    // This is the LEGACY route for content already saved against a source model URL. Fresh imports bake
    // one .lmesh per mesh at import time and never come back through here. -xlinka
    public static PhosMesh? Decode(byte[] fileData, string extension, int meshIndex = -1)
    {
        if (fileData == null || fileData.Length == 0)
        {
            LumoraLogger.Warn("MeshDecoder: Empty file data");
            return null;
        }

        string ext = extension?.ToLowerInvariant() ?? "";

        try
        {
            return ext switch
            {
                ".lmesh" => PhosMeshSerializer.Deserialize(fileData),
                // Everything non-native goes through Assimp now (one parser). VRM is GLB, so
                // hint Assimp with .glb. Native .obj keeps its lightweight reader. -xlinka
                ".glb" or ".gltf" => DecodeAssimp(fileData, ext, meshIndex),
                ".vrm" => DecodeAssimp(fileData, ".glb", meshIndex),
                ".obj" => DecodeObj(fileData),
                ".fbx" or ".dae" or ".3ds" or ".blend" or ".stl" or ".ply" or ".x" or ".ase" => DecodeAssimp(fileData, ext, meshIndex),
                _ => throw new NotSupportedException($"Unsupported mesh format: {ext}")
            };
        }
        catch (Exception ex)
        {
            // Full exception (type + message + stack), not just .Message - a bare "Object reference not set"
            // with no stack cost a debugging round-trip. -xlinka
            LumoraLogger.Error($"MeshDecoder: Failed to decode mesh ({ext}, index {meshIndex}) - {ex}");
            return null;
        }
    }

    public static void ScaleMesh(PhosMesh mesh, float scale)
    {
        var positions = mesh.RawPositions;
        if (positions != null && positions.Length > 0)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] *= scale;
            }
        }
    }

    // AssimpNetter copies the native ROW-MAJOR aiMatrix4x4 into System.Numerics.Matrix4x4 BY FIELD NAME, so the
    // translation ends up in M14/M24/M34 (4th column by name) and the basis is stored as ROWS - this is NOT the
    // System.Numerics row-vector layout (translation in M41..M43) that Matrix4x4.Decompose/.Translation assume.
    // Our float4x4 is column-major column-vector (c0..c2 = basis columns, c3 = translation), exactly what
    // float4x4.ToGodot() reads. So TRANSPOSE first: that moves the by-name translation into M41..M43 and the basis
    // into columns, and the straight field map below then lands translation in c3 with a column-vector basis.
    // WITHOUT the transpose every bind pose loses its translation (c3 = 0) and the skinned mesh explodes into
    // shards. ModelImporter.WalkAssimpNode's Decompose of node.Transform MUST transpose identically (same bug,
    // same matrix source) so the skeleton rest and the bind poses live in one space. -xlinka
    private static float4x4 ToFloat4x4(System.Numerics.Matrix4x4 m)
    {
        m = System.Numerics.Matrix4x4.Transpose(m);
        return FromTransposed(m);
    }

    // A matrix already in the transposed (translation in M41..M43) convention.
    private static float4x4 FromTransposed(System.Numerics.Matrix4x4 m) =>
        new float4x4(
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44);

    // An inverse bind that cannot be inverted is no inverse bind: every vertex it weighs collapses.
    // Some exporters, mobile and Quest conversions above all, drop the skin cluster's transform link
    // and hand back a matrix of zeros. -xlinka
    private static bool IsUsableBind(System.Numerics.Matrix4x4 m)
    {
        var basis = new System.Numerics.Matrix4x4(
            m.M11, m.M12, m.M13, 0f,
            m.M21, m.M22, m.M23, 0f,
            m.M31, m.M32, m.M33, 0f,
            0f, 0f, 0f, 1f);
        float det = basis.GetDeterminant();
        return System.Math.Abs(det) > 1e-6f && !float.IsNaN(det);
    }

    // Where every named node sits in the model's own space, transposed so translation reads where
    // Decompose and the rest of the engine expect it. The fallback inverse bind is built from this:
    // bind = inverse(global rest), which is the definition the skinning canary checks.
    private static Dictionary<string, System.Numerics.Matrix4x4> BuildGlobalNodeTransforms(Assimp.Node? root)
    {
        var map = new Dictionary<string, System.Numerics.Matrix4x4>(StringComparer.Ordinal);
        void Walk(Assimp.Node? node, System.Numerics.Matrix4x4 parent)
        {
            if (node == null)
                return;
            var local = System.Numerics.Matrix4x4.Transpose(node.Transform);
            var global = local * parent;
            if (!string.IsNullOrEmpty(node.Name))
                map[node.Name] = global;
            for (int i = 0; i < node.ChildCount; i++)
                Walk(node.Children[i], global);
        }
        Walk(root, System.Numerics.Matrix4x4.Identity);
        return map;
    }

    // Weights should sum to 1 but exporters drift; renormalize so the GPU skin doesn't shrink/expand verts.
    private static float4 NormalizeWeights(float4 w)
    {
        float sum = w.x + w.y + w.z + w.w;
        if (sum <= 1e-6f)
            return new float4(1f, 0f, 0f, 0f);
        float inv = 1f / sum;
        return new float4(w.x * inv, w.y * inv, w.z * inv, w.w * inv);
    }

    private static PhosMesh DecodeObj(byte[] fileData)
    {
        var text = System.Text.Encoding.UTF8.GetString(fileData);
        var lines = text.Split('\n');

        var positions = new List<float3>();
        var normals = new List<float3>();
        var uvs = new List<float2>();
        var faceIndices = new List<(int v, int vt, int vn)>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            switch (parts[0])
            {
                case "v" when parts.Length >= 4:
                    positions.Add(new float3(
                        float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)));
                    break;

                case "vn" when parts.Length >= 4:
                    normals.Add(new float3(
                        float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)));
                    break;

                case "vt" when parts.Length >= 3:
                    // OBJ counts V upward from the bottom left; this engine samples from the top left,
                    // so the axis is mirrored here. The Assimp formats get the same treatment through
                    // FlipUVs; this parser is hand written and has to do it itself. -xlinka
                    uvs.Add(new float2(
                        float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                        1f - float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
                    break;

                case "f" when parts.Length >= 4:
                    var faceVerts = new List<(int v, int vt, int vn)>();
                    for (int i = 1; i < parts.Length; i++)
                    {
                        faceVerts.Add(ParseObjFaceVertex(parts[i]));
                    }
                    // Wound the other way round, as every other format is on its way in: our front faces are
                    // clockwise.
                    for (int i = 1; i < faceVerts.Count - 1; i++)
                    {
                        faceIndices.Add(faceVerts[0]);
                        faceIndices.Add(faceVerts[i + 1]);
                        faceIndices.Add(faceVerts[i]);
                    }
                    break;
            }
        }

        var phosMesh = new PhosMesh();
        phosMesh.HasNormals = normals.Count > 0;
        phosMesh.HasUV0s = uvs.Count > 0;

        phosMesh.IncreaseVertexCount(faceIndices.Count);

        for (int i = 0; i < faceIndices.Count; i++)
        {
            var (v, vt, vn) = faceIndices[i];

            phosMesh.RawPositions[i] = v > 0 && v <= positions.Count ? positions[v - 1] : float3.Zero;

            if (phosMesh.HasNormals)
                phosMesh.RawNormals[i] = vn > 0 && vn <= normals.Count ? normals[vn - 1] : float3.Up;
        }

        if (phosMesh.HasUV0s)
        {
            phosMesh.SetHasUV(0, true);
            for (int i = 0; i < faceIndices.Count; i++)
            {
                var vt = faceIndices[i].vt;
                var uv = vt > 0 && vt <= uvs.Count ? uvs[vt - 1] : float2.Zero;
                phosMesh.SetUV(0, i, uv);
            }
        }

        var submesh = new PhosTriangleSubmesh(phosMesh);
        phosMesh.Submeshes.Add(submesh);

        for (int i = 0; i < faceIndices.Count; i += 3)
        {
            submesh.AddTriangle(i, i + 1, i + 2);
        }

        LumoraLogger.Debug($"MeshDecoder: Decoded OBJ with {faceIndices.Count} vertices, {faceIndices.Count / 3} triangles");
        return phosMesh;
    }

    private static PhosMesh DecodeAssimp(byte[] fileData, string extension, int meshIndex = -1)
    {
        if (extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase))
            fileData = FbxImportLayers.Prepare(fileData);
        // Only the bytes: a model decoded here has no folder to fetch companions from, and must not reach for any.
        using var access = new ModelFileAccess(fileData, "model" + (extension.Equals(".vrm", StringComparison.OrdinalIgnoreCase) ? ".glb" : extension), folder: null);
        using var context = new AssimpContext();
        context.SetIOSystem(access);

        // meshIndex >= 0 = a single mesh for a skinned/per-mesh import: DON'T pre-transform (that flattens the
        // node hierarchy + bakes world transforms into verts, which breaks skinning). meshIndex -1 = legacy
        // whole-file concatenation, keep pre-transform so a standalone static mesh still lands in place. -xlinka
        bool perMesh = meshIndex >= 0;
        var scene = context.ImportFile(access.MainName, GetAssimpPostProcessSteps(perMesh, extension));
        if (scene == null || !scene.HasMeshes || scene.MeshCount == 0)
            throw new InvalidOperationException($"Assimp could not decode mesh data for '{extension}'");

        return ConvertScene(scene, meshIndex, extension);
    }

    // Where every named node rests, for bones whose exporter gave no usable inverse bind. Exposed so an
    // importer that converts many meshes out of ONE parse walks the node tree once instead of per mesh.
    internal static Dictionary<string, System.Numerics.Matrix4x4> BuildNodeRestMap(Assimp.Scene scene)
        => BuildGlobalNodeTransforms(scene?.RootNode);

    // The per-mesh conversion, over a scene SOMEBODY ELSE PARSED.
    //
    // This exists so the model importer can parse a file once and bake all of its meshes, instead of every
    // MeshProvider re-parsing the whole source through the URL path. Re-parsing was the import blowup: a nine
    // mesh avatar meant ten concurrent whole-file parses, each keeping every mesh it then threw away.
    //
    // meshIndex >= 0 converts only that mesh; -1 concatenates the whole scene (the legacy whole-file shape).
    // label only names the source in log lines. -xlinka
    internal static PhosMesh ConvertScene(Assimp.Scene scene, int meshIndex, string? label = null,
        Dictionary<string, System.Numerics.Matrix4x4>? nodeRest = null)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        string extension = label ?? "";
        var phosMesh = new PhosMesh();
        phosMesh.HasNormals = true;
        phosMesh.HasUV0s = true;

        // Kept for bones whose exporter gave no usable inverse bind; empty walks cost nothing.
        var globalNodeRest = nodeRest ?? BuildGlobalNodeTransforms(scene.RootNode);
        int rebuiltBinds = 0;

        var allPositions = new List<float3>();
        var allNormals = new List<float3>();
        var allUVs = new List<float2>();
        // Extra UV channels (1-3). Avatars rarely use them but lightmap/detail meshes do - keep them so the data
        // round-trips all UV channels instead of dropping everything past UV0. -xlinka
        var allUVs1 = new List<float2>();
        var allUVs2 = new List<float2>();
        var allUVs3 = new List<float2>();
        bool hasUV1 = false, hasUV2 = false, hasUV3 = false;
        var allIndices = new List<int>();

        // Skinning accumulators (parallel to allPositions). Bone table is deduped by name across decoded meshes.
        var allBoneIndices = new List<float4>();
        var allBoneWeights = new List<float4>();
        var boneSlotsUsed = new List<int>();
        bool anySkin = false;
        var boneIndexByName = new Dictionary<string, int>();

        // Morph-target (blendshape) accumulators, kept parallel to allPositions. Assimp attachment vertices are
        // ABSOLUTE morphed positions, so the stored delta is attachment - base. -xlinka
        var morphNames = new List<string>();
        var morphPos = new List<List<float3>>();
        var morphNorm = new List<List<float3>>();
        var morphTang = new List<List<float3>>();
        // Per shape, not per mesh: one shape carrying normal deltas used to force a dense normal array
        // onto every other shape of the mesh, and a dense channel costs VertexCount * 12 bytes for a
        // shape that never touches it. On an avatar with hundreds of shapes that is the difference
        // between tens of megabytes and nothing. -xlinka
        var morphHasNorm = new List<bool>();
        var morphHasTang = new List<bool>();

        // Tangents (xyz + handedness sign in w, for normal maps) and vertex colors, parallel to allPositions.
        var allTangents = new List<float4>();
        var allColors = new List<color>();
        bool anyTangents = false;
        bool anyColors = false;

        for (int mi = 0; mi < scene.MeshCount; mi++)
        {
            if (meshIndex >= 0 && mi != meshIndex)
                continue;
            var mesh = scene.Meshes[mi];
            if (mesh == null || !mesh.HasVertices || mesh.VertexCount == 0)
                continue;

            int baseVert = allPositions.Count;
            int vcount = mesh.VertexCount;

            // Establish the global morph set from the first mesh that has attachments, back-filling zero deltas
            // for any vertices already added so the per-morph arrays stay aligned to allPositions. -xlinka
            var attachments = mesh.MeshAnimationAttachments;
            if (mesh.HasMeshAnimationAttachments && attachments.Count > 0 && morphNames.Count == 0)
            {
                int already = allPositions.Count;
                for (int a = 0; a < attachments.Count; a++)
                {
                    morphNames.Add(ShapeName(attachments[a], a));
                    var pl = new List<float3>();
                    var nl = new List<float3>();
                    var tl = new List<float3>();
                    for (int k = 0; k < already; k++) { pl.Add(float3.Zero); nl.Add(float3.Zero); tl.Add(float3.Zero); }
                    morphPos.Add(pl);
                    morphNorm.Add(nl);
                    morphTang.Add(tl);
                    morphHasNorm.Add(false);
                    morphHasTang.Add(false);
                }
            }

            for (int i = 0; i < vcount; i++)
            {
                var position = mesh.Vertices[i];
                allPositions.Add(new float3(position.X, position.Y, position.Z));

                if (mesh.HasNormals && i < mesh.Normals.Count)
                {
                    var normal = mesh.Normals[i];
                    allNormals.Add(new float3(normal.X, normal.Y, normal.Z));
                }
                else
                {
                    allNormals.Add(float3.Up);
                }

                if (mesh.TextureCoordinateChannelCount > 0 &&
                    mesh.TextureCoordinateChannels[0] != null &&
                    i < mesh.TextureCoordinateChannels[0].Count)
                {
                    var uv = mesh.TextureCoordinateChannels[0][i];
                    allUVs.Add(new float2(uv.X, uv.Y));
                }
                else
                {
                    allUVs.Add(float2.Zero);
                }

                // Extra UV channels (1-3), mirroring channel 0. Zero-filled when this mesh lacks the channel so
                // every channel array stays aligned to allPositions.
                ReadExtraUV(mesh, 1, i, allUVs1, ref hasUV1);
                ReadExtraUV(mesh, 2, i, allUVs2, ref hasUV2);
                ReadExtraUV(mesh, 3, i, allUVs3, ref hasUV3);

                // Tangent (xyz) + handedness sign (w = sign of dot(cross(N,T), B)) for correct normal mapping.
                if (mesh.HasTangentBasis && i < mesh.Tangents.Count && i < mesh.BiTangents.Count)
                {
                    var t = mesh.Tangents[i];
                    var b = mesh.BiTangents[i];
                    var n = (mesh.HasNormals && i < mesh.Normals.Count) ? mesh.Normals[i] : new System.Numerics.Vector3(0, 1, 0);
                    float cx = n.Y * t.Z - n.Z * t.Y, cy = n.Z * t.X - n.X * t.Z, cz = n.X * t.Y - n.Y * t.X;
                    float sign = (cx * b.X + cy * b.Y + cz * b.Z) < 0f ? -1f : 1f;
                    allTangents.Add(new float4(t.X, t.Y, t.Z, sign));
                    anyTangents = true;
                }
                else allTangents.Add(new float4(1f, 0f, 0f, 1f));

                if (mesh.HasVertexColors(0) && mesh.VertexColorChannels[0] != null && i < mesh.VertexColorChannels[0].Count)
                {
                    var vc = mesh.VertexColorChannels[0][i];
                    allColors.Add(new color(vc.X, vc.Y, vc.Z, vc.W));
                    anyColors = true;
                }
                else allColors.Add(color.White);

                allBoneIndices.Add(new float4(0f, 0f, 0f, 0f));
                allBoneWeights.Add(new float4(0f, 0f, 0f, 0f));
                boneSlotsUsed.Add(0);

                // Morph deltas for this vertex (delta = attachment absolute - base). Zero for a morph this mesh
                // doesn't carry, so the arrays stay aligned. -xlinka
                for (int m = 0; m < morphNames.Count; m++)
                {
                    var att = (mesh.HasMeshAnimationAttachments && m < attachments.Count) ? attachments[m] : null;
                    if (att != null && att.HasVertices && i < att.Vertices.Count)
                    {
                        var ap = att.Vertices[i];
                        morphPos[m].Add(new float3(ap.X - position.X, ap.Y - position.Y, ap.Z - position.Z));
                    }
                    else morphPos[m].Add(float3.Zero);

                    if (att != null && att.HasNormals && i < att.Normals.Count && mesh.HasNormals && i < mesh.Normals.Count)
                    {
                        var an = att.Normals[i];
                        var bn = mesh.Normals[i];
                        morphNorm[m].Add(new float3(an.X - bn.X, an.Y - bn.Y, an.Z - bn.Z));
                        morphHasNorm[m] = true;
                    }
                    else morphNorm[m].Add(float3.Zero);

                    // Tangent delta = normalized morph tangent - normalized base tangent (xyz). Keeps normal-mapped
                    // morphs from shifting the tangent basis. Base tangent is the float4 we just added for this vertex.
                    if (att != null && att.HasTangentBasis && i < att.Tangents.Count)
                    {
                        var at = att.Tangents[i];
                        var baseT = allTangents[allTangents.Count - 1];
                        var atn = Normalize3(at.X, at.Y, at.Z);
                        var btn = Normalize3(baseT.x, baseT.y, baseT.z);
                        morphTang[m].Add(new float3(atn.x - btn.x, atn.y - btn.y, atn.z - btn.z));
                        morphHasTang[m] = true;
                    }
                    else morphTang[m].Add(float3.Zero);
                }
            }

            // Bones -> the mesh bone table (name + inverse-bind = OffsetMatrix) + per-vertex (index, weight),
            // accumulated into up to 4 influences per vertex.
            if (mesh.HasBones)
            {
                anySkin = true;
                foreach (var bone in mesh.Bones)
                {
                    if (bone == null) continue;
                    string bname = string.IsNullOrEmpty(bone.Name) ? $"Bone{phosMesh.BoneCount}" : bone.Name;
                    if (!boneIndexByName.TryGetValue(bname, out int bIdx))
                    {
                        var pb = phosMesh.AddBone(bname);
                        var offset = System.Numerics.Matrix4x4.Transpose(bone.OffsetMatrix);
                        if (IsUsableBind(offset))
                        {
                            pb.BindPose = FromTransposed(offset);
                        }
                        else if (globalNodeRest.TryGetValue(bname, out var rest)
                            && System.Numerics.Matrix4x4.Invert(rest, out var derived))
                        {
                            // Rebuild it from where the joint actually rests, so rest * bind is the
                            // identity the skin expects instead of collapsing the vertices to a point.
                            pb.BindPose = FromTransposed(derived);
                            rebuiltBinds++;
                        }
                        else
                        {
                            // Nothing to derive from either. Identity at least leaves the vertices
                            // where they are rather than folding them into the origin.
                            pb.BindPose = float4x4.Identity;
                            rebuiltBinds++;
                        }
                        bIdx = phosMesh.BoneCount - 1;
                        boneIndexByName[bname] = bIdx;
                    }
                    if (!bone.HasVertexWeights) continue;
                    foreach (var vw in bone.VertexWeights)
                    {
                        int gv = baseVert + vw.VertexID;
                        if (gv < 0 || gv >= allBoneWeights.Count) continue;
                        int slot = boneSlotsUsed[gv];
                        if (slot < 4)
                        {
                            allBoneIndices[gv] = WithComponent(allBoneIndices[gv], slot, bIdx);
                            allBoneWeights[gv] = WithComponent(allBoneWeights[gv], slot, vw.Weight);
                            boneSlotsUsed[gv] = slot + 1;
                        }
                        else
                        {
                            // Already 4 influences: keep the strongest 4 - replace the lowest if this is bigger.
                            var w = allBoneWeights[gv];
                            int minSlot = 0; float minW = w.x;
                            if (w.y < minW) { minW = w.y; minSlot = 1; }
                            if (w.z < minW) { minW = w.z; minSlot = 2; }
                            if (w.w < minW) { minW = w.w; minSlot = 3; }
                            if (vw.Weight > minW)
                            {
                                allBoneIndices[gv] = WithComponent(allBoneIndices[gv], minSlot, bIdx);
                                allBoneWeights[gv] = WithComponent(allBoneWeights[gv], minSlot, vw.Weight);
                            }
                        }
                    }
                }
            }

            foreach (var face in mesh.Faces)
            {
                if (!face.HasIndices || face.IndexCount < 3)
                    continue;

                for (int i = 0; i + 2 < face.Indices.Count; i += 3)
                {
                    allIndices.Add(baseVert + face.Indices[i]);
                    allIndices.Add(baseVert + face.Indices[i + 2]);
                    allIndices.Add(baseVert + face.Indices[i + 1]);
                }
            }
        }

        if (allPositions.Count == 0)
        {
            LumoraLogger.Warn($"MeshDecoder: Assimp import produced no vertices for '{extension}'");
            return phosMesh;
        }

        // Flag tangents/colors BEFORE IncreaseVertexCount so it allocates those arrays.
        phosMesh.HasTangents = anyTangents && allTangents.Count == allPositions.Count;
        phosMesh.HasColors = anyColors && allColors.Count == allPositions.Count;
        phosMesh.IncreaseVertexCount(allPositions.Count);
        phosMesh.SetHasUV(0, true);

        for (int i = 0; i < allPositions.Count; i++)
        {
            phosMesh.RawPositions[i] = allPositions[i];
            phosMesh.RawNormals[i] = allNormals[i];
            phosMesh.SetUV(0, i, allUVs[i]);
        }

        WriteExtraUV(phosMesh, 1, hasUV1, allUVs1);
        WriteExtraUV(phosMesh, 2, hasUV2, allUVs2);
        WriteExtraUV(phosMesh, 3, hasUV3, allUVs3);

        if (phosMesh.HasTangents)
            for (int i = 0; i < allPositions.Count; i++)
                phosMesh.RawTangents[i] = allTangents[i];
        if (phosMesh.HasColors)
            for (int i = 0; i < allPositions.Count; i++)
                phosMesh.RawColors[i] = allColors[i];

        if (anySkin && allBoneWeights.Count == allPositions.Count)
        {
            phosMesh.EnsureBoneBindings();
            phosMesh.HasBoneBindings = true;
            var bindings = phosMesh.RawBoneBindings;
            for (int i = 0; i < allPositions.Count; i++)
                bindings[i] = new PhosBoneBinding(allBoneIndices[i], NormalizeWeights(allBoneWeights[i]));
        }

        // Morph targets -> PhosMesh blendshapes (position deltas, plus normal deltas when present so lighting
        // follows the expression). The render path (SkinnedMeshHook.ApplyMeshFromAsset) already consumes these.
        for (int m = 0; m < morphNames.Count; m++)
        {
            if (morphPos[m].Count != phosMesh.VertexCount)
                continue;
            var bs = phosMesh.GetBlendShape(morphNames[m]);
            bool withNormals = morphHasNorm[m] && morphNorm[m].Count == phosMesh.VertexCount;
            bool withTangents = morphHasTang[m] && morphTang[m].Count == phosMesh.VertexCount;

            var frame = bs.Frames[0];
            frame.Allocate(phosMesh.VertexCount, withNormals, withTangents);
            morphPos[m].CopyTo(frame.positions);
            if (withNormals)
                morphNorm[m].CopyTo(frame.normals);
            if (withTangents)
                morphTang[m].CopyTo(frame.tangents);
        }

        var submesh = new PhosTriangleSubmesh(phosMesh);
        phosMesh.Submeshes.Add(submesh);
        for (int i = 0; i + 2 < allIndices.Count; i += 3)
        {
            submesh.AddTriangle(allIndices[i], allIndices[i + 1], allIndices[i + 2]);
        }

        LumoraLogger.Debug($"MeshDecoder: Decoded {extension} (mesh {meshIndex}) - {allPositions.Count} verts, {allIndices.Count / 3} tris, {phosMesh.BoneCount} bones, skinned={phosMesh.HasBoneBindings}");
        // Say it once, at the level someone reading a bug report will see: a file that needed this is
        // a file whose exporter is at fault, and the skin only works because we rebuilt it. -xlinka
        if (rebuiltBinds > 0)
            LumoraLogger.Warn($"MeshDecoder: {extension} (mesh {meshIndex}) shipped {rebuiltBinds}/{phosMesh.BoneCount} bones with no usable inverse bind; rebuilt them from the rest pose.");
        return phosMesh;
    }

    // Triangles a mesh's faces make, counted the way the conversions read them: a face under three indices
    // adds none. -xlinka
    internal static int CountTriangles(Assimp.Mesh mesh)
    {
        if (mesh == null || !mesh.HasFaces)
            return 0;
        int triangles = 0;
        foreach (var face in mesh.Faces)
        {
            if (face != null && face.HasIndices && face.IndexCount >= 3)
                triangles += face.IndexCount / 3;
        }
        return triangles;
    }

    // A node's meshes as ONE mesh.
    //
    // A source object with several materials reaches us as one mesh per material on the same node. Here
    // they go back together: each part keeps its own vertex range and becomes its own submesh, so one
    // renderer gives every part its material. Bones are shared by name; a part whose bind for a name
    // disagrees with the one already held gets an entry of its own under that name, and the skin binds both
    // to the same joint. Blend shapes are shared by name, zero over every part that does not carry them.
    // Parts without triangles add nothing. Keep skinned and unskinned parts in separate calls: an
    // unweighted vertex in a skinned mesh rides bone 0.
    //
    // submeshParts[k] is the scene mesh submesh k came from. -xlinka
    internal static PhosMesh ConvertMeshGroup(Assimp.Scene scene, IReadOnlyList<int> meshIndices, string? label,
        Dictionary<string, System.Numerics.Matrix4x4>? nodeRest, out int[] submeshParts)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (meshIndices == null)
            throw new ArgumentNullException(nameof(meshIndices));

        string extension = label ?? "";
        var globalNodeRest = nodeRest ?? BuildGlobalNodeTransforms(scene.RootNode);

        var parts = new List<(int Index, Assimp.Mesh Mesh, int Base, int Triangles)>(meshIndices.Count);
        long total = 0;
        bool anyTangents = false, anyColors = false, anySkin = false;
        bool hasUV1 = false, hasUV2 = false, hasUV3 = false;
        var shapeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var shapeNames = new List<string>();
        var shapeHasNormals = new List<bool>();
        var shapeHasTangents = new List<bool>();

        foreach (int mi in meshIndices)
        {
            if (mi < 0 || mi >= scene.MeshCount)
                continue;
            var mesh = scene.Meshes[mi];
            if (mesh == null || !mesh.HasVertices || mesh.VertexCount == 0)
                continue;
            int triangles = CountTriangles(mesh);
            if (triangles == 0)
                continue;

            parts.Add((mi, mesh, (int)total, triangles));
            total += mesh.VertexCount;
            if (total > int.MaxValue)
                throw new InvalidOperationException($"MeshDecoder: merged meshes of {extension} pass {int.MaxValue} vertices.");

            anyTangents |= mesh.HasTangentBasis;
            anyColors |= ColorChannel(mesh) != null;
            anySkin |= mesh.HasBones;
            hasUV1 |= UVChannel(mesh, 1) != null;
            hasUV2 |= UVChannel(mesh, 2) != null;
            hasUV3 |= UVChannel(mesh, 3) != null;

            if (!mesh.HasMeshAnimationAttachments)
                continue;
            var attachments = mesh.MeshAnimationAttachments;
            for (int a = 0; a < attachments.Count; a++)
            {
                var att = attachments[a];
                if (att == null)
                    continue;
                string name = ShapeName(att, a);
                if (!shapeIndex.TryGetValue(name, out int s))
                {
                    s = shapeNames.Count;
                    shapeIndex[name] = s;
                    shapeNames.Add(name);
                    shapeHasNormals.Add(false);
                    shapeHasTangents.Add(false);
                }
                if (att.HasNormals && mesh.HasNormals)
                    shapeHasNormals[s] = true;
                if (att.HasTangentBasis)
                    shapeHasTangents[s] = true;
            }
        }

        submeshParts = new int[parts.Count];
        var phosMesh = new PhosMesh();
        if (parts.Count == 0)
        {
            LumoraLogger.Warn($"MeshDecoder: none of meshes {string.Join(", ", meshIndices)} of {extension} has a triangle");
            return phosMesh;
        }

        int vertexCount = (int)total;
        phosMesh.HasNormals = true;
        phosMesh.HasTangents = anyTangents;
        phosMesh.HasColors = anyColors;
        phosMesh.IncreaseVertexCount(vertexCount);
        phosMesh.SetHasUV(0, true);
        if (hasUV1) phosMesh.SetHasUV(1, true);
        if (hasUV2) phosMesh.SetHasUV(2, true);
        if (hasUV3) phosMesh.SetHasUV(3, true);

        var positions = phosMesh.RawPositions;
        var normals = phosMesh.RawNormals;
        var tangents = anyTangents ? phosMesh.RawTangents : null;
        var colors = anyColors ? phosMesh.RawColors : null;
        var uv0 = phosMesh.RawUV0s;
        var uv1 = hasUV1 ? phosMesh.RawUV1s : null;
        var uv2 = hasUV2 ? phosMesh.RawUV2s : null;
        var uv3 = hasUV3 ? phosMesh.RawUV3s : null;

        var boneIndices = anySkin ? new float4[vertexCount] : null;
        var boneWeights = anySkin ? new float4[vertexCount] : null;
        var boneSlots = anySkin ? new byte[vertexCount] : null;
        var bonesByName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        int rebuiltBinds = 0;

        int shapeCount = shapeNames.Count;
        var shapePositions = new float3[shapeCount][];
        var shapeNormals = new float3[]?[shapeCount];
        var shapeTangents = new float3[]?[shapeCount];
        for (int s = 0; s < shapeCount; s++)
        {
            shapePositions[s] = new float3[vertexCount];
            if (shapeHasNormals[s])
                shapeNormals[s] = new float3[vertexCount];
            if (shapeHasTangents[s])
                shapeTangents[s] = new float3[vertexCount];
        }

        int triangleTotal = 0;
        for (int p = 0; p < parts.Count; p++)
        {
            var (partIndex, mesh, baseVert, partTriangles) = parts[p];
            int vcount = mesh.VertexCount;
            var mv = mesh.Vertices;
            var mn = mesh.HasNormals ? mesh.Normals : null;
            var mt = mesh.HasTangentBasis ? mesh.Tangents : null;
            var mb = mesh.HasTangentBasis ? mesh.BiTangents : null;
            var mc = ColorChannel(mesh);
            var t0 = UVChannel(mesh, 0);
            var t1 = UVChannel(mesh, 1);
            var t2 = UVChannel(mesh, 2);
            var t3 = UVChannel(mesh, 3);

            for (int i = 0; i < vcount; i++)
            {
                int v = baseVert + i;
                var position = mv[i];
                positions[v] = new float3(position.X, position.Y, position.Z);

                bool hasNormal = mn != null && i < mn.Count;
                var n = hasNormal ? mn![i] : new System.Numerics.Vector3(0f, 1f, 0f);
                normals[v] = hasNormal ? new float3(n.X, n.Y, n.Z) : float3.Up;

                uv0[v] = UVAt(t0, i);
                if (uv1 != null) uv1[v] = UVAt(t1, i);
                if (uv2 != null) uv2[v] = UVAt(t2, i);
                if (uv3 != null) uv3[v] = UVAt(t3, i);

                if (tangents != null)
                {
                    if (mt != null && mb != null && i < mt.Count && i < mb.Count)
                    {
                        var t = mt[i];
                        var b = mb[i];
                        float cx = n.Y * t.Z - n.Z * t.Y, cy = n.Z * t.X - n.X * t.Z, cz = n.X * t.Y - n.Y * t.X;
                        float sign = (cx * b.X + cy * b.Y + cz * b.Z) < 0f ? -1f : 1f;
                        tangents[v] = new float4(t.X, t.Y, t.Z, sign);
                    }
                    else tangents[v] = new float4(1f, 0f, 0f, 1f);
                }

                if (colors != null)
                {
                    if (mc != null && i < mc.Count)
                    {
                        var c = mc[i];
                        colors[v] = new color(c.X, c.Y, c.Z, c.W);
                    }
                    else colors[v] = color.White;
                }
            }

            if (mesh.HasBones && boneIndices != null && boneWeights != null && boneSlots != null)
            {
                foreach (var bone in mesh.Bones)
                {
                    if (bone == null)
                        continue;
                    string bname = string.IsNullOrEmpty(bone.Name) ? $"Bone{phosMesh.BoneCount}" : bone.Name;
                    var bind = BindPoseFor(bone, bname, globalNodeRest, out bool rebuilt);
                    if (!bonesByName.TryGetValue(bname, out var entries))
                    {
                        entries = new List<int>(1);
                        bonesByName[bname] = entries;
                    }
                    int bIdx = -1;
                    foreach (int e in entries)
                    {
                        if (SameBind(phosMesh.GetBoneBindPose(e), bind))
                        {
                            bIdx = e;
                            break;
                        }
                    }
                    if (bIdx < 0)
                    {
                        phosMesh.AddBone(bname).BindPose = bind;
                        bIdx = phosMesh.BoneCount - 1;
                        entries.Add(bIdx);
                        if (rebuilt)
                            rebuiltBinds++;
                    }

                    if (!bone.HasVertexWeights)
                        continue;
                    foreach (var vw in bone.VertexWeights)
                    {
                        if (vw.VertexID < 0 || vw.VertexID >= vcount)
                            continue;
                        int gv = baseVert + vw.VertexID;
                        int slot = boneSlots[gv];
                        if (slot < 4)
                        {
                            boneIndices[gv] = WithComponent(boneIndices[gv], slot, bIdx);
                            boneWeights[gv] = WithComponent(boneWeights[gv], slot, vw.Weight);
                            boneSlots[gv] = (byte)(slot + 1);
                        }
                        else
                        {
                            var w = boneWeights[gv];
                            int minSlot = 0; float minW = w.x;
                            if (w.y < minW) { minW = w.y; minSlot = 1; }
                            if (w.z < minW) { minW = w.z; minSlot = 2; }
                            if (w.w < minW) { minW = w.w; minSlot = 3; }
                            if (vw.Weight > minW)
                            {
                                boneIndices[gv] = WithComponent(boneIndices[gv], minSlot, bIdx);
                                boneWeights[gv] = WithComponent(boneWeights[gv], minSlot, vw.Weight);
                            }
                        }
                    }
                }
            }

            if (mesh.HasMeshAnimationAttachments)
            {
                var attachments = mesh.MeshAnimationAttachments;
                for (int a = 0; a < attachments.Count; a++)
                {
                    var att = attachments[a];
                    if (att == null)
                        continue;
                    int s = shapeIndex[ShapeName(att, a)];

                    if (att.HasVertices)
                    {
                        var dp = shapePositions[s];
                        var av = att.Vertices;
                        int count = System.Math.Min(vcount, av.Count);
                        for (int i = 0; i < count; i++)
                        {
                            var ap = av[i];
                            var bp = mv[i];
                            dp[baseVert + i] = new float3(ap.X - bp.X, ap.Y - bp.Y, ap.Z - bp.Z);
                        }
                    }

                    if (shapeNormals[s] is { } dn && att.HasNormals && mn != null)
                    {
                        var an = att.Normals;
                        int count = System.Math.Min(vcount, System.Math.Min(an.Count, mn.Count));
                        for (int i = 0; i < count; i++)
                        {
                            var x = an[i];
                            var y = mn[i];
                            dn[baseVert + i] = new float3(x.X - y.X, x.Y - y.Y, x.Z - y.Z);
                        }
                    }

                    if (shapeTangents[s] is { } dt && att.HasTangentBasis)
                    {
                        var at = att.Tangents;
                        int count = System.Math.Min(vcount, at.Count);
                        for (int i = 0; i < count; i++)
                        {
                            var baseT = tangents != null ? tangents[baseVert + i] : new float4(1f, 0f, 0f, 1f);
                            var atn = Normalize3(at[i].X, at[i].Y, at[i].Z);
                            var btn = Normalize3(baseT.x, baseT.y, baseT.z);
                            dt[baseVert + i] = new float3(atn.x - btn.x, atn.y - btn.y, atn.z - btn.z);
                        }
                    }
                }
            }

            // Same winding as ConvertScene, offset into this part's range.
            var submesh = new PhosTriangleSubmesh(phosMesh);
            submesh.IncreaseCount(partTriangles);
            var indices = submesh.RawIndices;
            int write = 0;
            foreach (var face in mesh.Faces)
            {
                if (face == null || !face.HasIndices || face.IndexCount < 3)
                    continue;
                var fi = face.Indices;
                for (int i = 0; i + 2 < fi.Count; i += 3)
                {
                    indices[write++] = baseVert + fi[i];
                    indices[write++] = baseVert + fi[i + 2];
                    indices[write++] = baseVert + fi[i + 1];
                }
            }
            phosMesh.Submeshes.Add(submesh);
            submeshParts[p] = partIndex;
            triangleTotal += partTriangles;
        }

        if (anySkin && boneIndices != null && boneWeights != null)
        {
            phosMesh.EnsureBoneBindings();
            phosMesh.HasBoneBindings = true;
            var bindings = phosMesh.RawBoneBindings;
            for (int i = 0; i < vertexCount; i++)
                bindings[i] = new PhosBoneBinding(boneIndices[i], NormalizeWeights(boneWeights[i]));
        }

        for (int s = 0; s < shapeCount; s++)
        {
            var frame = phosMesh.AddBlendShape(shapeNames[s], 1).Frames[0];
            frame.positions = shapePositions[s];
            if (shapeNormals[s] is { } dn)
                frame.normals = dn;
            if (shapeTangents[s] is { } dt)
                frame.tangents = dt;
        }

        LumoraLogger.Debug($"MeshDecoder: Merged meshes {string.Join(", ", submeshParts)} of {extension} - {vertexCount} verts, {triangleTotal} tris, {phosMesh.BoneCount} bones, {shapeCount} shapes, skinned={phosMesh.HasBoneBindings}");
        if (rebuiltBinds > 0)
            LumoraLogger.Warn($"MeshDecoder: {extension} (meshes {string.Join(", ", submeshParts)}) shipped {rebuiltBinds}/{phosMesh.BoneCount} bones with no usable inverse bind; rebuilt them from the rest pose.");
        return phosMesh;
    }

    // The bind ConvertScene gives a bone: its own inverse bind when usable, else rebuilt from where the joint
    // rests, else identity. rebuilt is true when the source's could not be used. -xlinka
    private static float4x4 BindPoseFor(Assimp.Bone bone, string name,
        Dictionary<string, System.Numerics.Matrix4x4> nodeRest, out bool rebuilt)
    {
        var offset = System.Numerics.Matrix4x4.Transpose(bone.OffsetMatrix);
        if (IsUsableBind(offset))
        {
            rebuilt = false;
            return FromTransposed(offset);
        }
        rebuilt = true;
        if (nodeRest.TryGetValue(name, out var rest) && System.Numerics.Matrix4x4.Invert(rest, out var derived))
            return FromTransposed(derived);
        return float4x4.Identity;
    }

    // Parts cut from one object carry one bind bit for bit; the tolerance only absorbs an exporter that
    // recomputed it per part. -xlinka
    private static bool SameBind(float4x4 a, float4x4 b) =>
        Close(a.c0, b.c0) && Close(a.c1, b.c1) && Close(a.c2, b.c2) && Close(a.c3, b.c3);

    private static bool Close(float4 a, float4 b) =>
        Close(a.x, b.x) && Close(a.y, b.y) && Close(a.z, b.z) && Close(a.w, b.w);

    private static bool Close(float a, float b) =>
        System.Math.Abs(a - b) <= 1e-5f * System.Math.Max(1f, System.Math.Max(System.Math.Abs(a), System.Math.Abs(b)));

    private static List<System.Numerics.Vector3>? UVChannel(Assimp.Mesh mesh, int channel)
    {
        if (mesh.TextureCoordinateChannelCount <= channel)
            return null;
        var list = mesh.TextureCoordinateChannels[channel];
        return list != null && list.Count > 0 ? list : null;
    }

    private static float2 UVAt(List<System.Numerics.Vector3>? channel, int i) =>
        channel != null && i < channel.Count ? new float2(channel[i].X, channel[i].Y) : float2.Zero;

    private static List<System.Numerics.Vector4>? ColorChannel(Assimp.Mesh mesh)
    {
        if (!mesh.HasVertexColors(0))
            return null;
        var list = mesh.VertexColorChannels[0];
        return list != null && list.Count > 0 ? list : null;
    }

    // Return a copy of v with component i (0..3) set to val.
    private static float4 WithComponent(float4 v, int i, float val) => i switch
    {
        0 => new float4(val, v.y, v.z, v.w),
        1 => new float4(v.x, val, v.z, v.w),
        2 => new float4(v.x, v.y, val, v.w),
        _ => new float4(v.x, v.y, v.z, val),
    };

    // Append one vertex's UV for an extra channel (zero when the mesh lacks the channel), tracking whether the
    // channel carried any real data so we only flag/write it when present.
    private static void ReadExtraUV(Assimp.Mesh mesh, int channel, int i, List<float2> dst, ref bool has)
    {
        if (mesh.TextureCoordinateChannelCount > channel &&
            mesh.TextureCoordinateChannels[channel] != null &&
            i < mesh.TextureCoordinateChannels[channel].Count)
        {
            var uv = mesh.TextureCoordinateChannels[channel][i];
            dst.Add(new float2(uv.X, uv.Y));
            has = true;
        }
        else dst.Add(float2.Zero);
    }

    // Flag + fill an extra UV channel on the mesh when it carried data and the count lines up.
    private static void WriteExtraUV(PhosMesh mesh, int channel, bool has, List<float2> uvs)
    {
        if (!has || uvs.Count != mesh.VertexCount) return;
        mesh.SetHasUV(channel, true);
        for (int i = 0; i < uvs.Count; i++)
            mesh.SetUV(channel, i, uvs[i]);
    }

    // Normalize an (x,y,z); returns zero for a degenerate vector.
    private static float3 Normalize3(float x, float y, float z)
    {
        float len = (float)System.Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-8f) return float3.Zero;
        return new float3(x / len, y / len, z / len);
    }

    private static (int v, int vt, int vn) ParseObjFaceVertex(string vertex)
    {
        var parts = vertex.Split('/');
        int v = 0, vt = 0, vn = 0;

        if (parts.Length >= 1 && !string.IsNullOrEmpty(parts[0]))
            int.TryParse(parts[0], out v);
        if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]))
            int.TryParse(parts[1], out vt);
        if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]))
            int.TryParse(parts[2], out vn);

        return (v, vt, vn);
    }

    // Internal so the model importer parses with the EXACT same steps as the per-mesh decode - otherwise the
    // two Assimp passes could order/split meshes differently and the MeshIndex wouldn't line up. -xlinka
    // Which way up a format writes its texture coordinates. glTF and its relatives put the UV origin
    // at the TOP left, which is where this engine samples from; OBJ, FBX, Collada and the rest put it
    // at the BOTTOM left and need V mirrored on the way in. -xlinka
    internal static bool NeedsUVFlip(string? extension)
    {
        var ext = extension?.Trim().TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "gltf" or "glb" or "vrm" => false,
            null or "" => false,
            _ => true,
        };
    }

    // A shape's name as the file means it. FBX keeps a shape inside a channel, and when the two share a name
    // the reader joins them ("Smile.Smile"); every other tool, and every avatar setup that names its shapes,
    // calls that shape "Smile". -xlinka
    public static string ShapeName(Assimp.MeshAnimationAttachment? attachment, int index)
    {
        var name = attachment?.Name;
        if (string.IsNullOrEmpty(name))
            return $"Morph{index}";
        int half = name.Length / 2;
        if (name.Length % 2 == 1 && name[half] == '.' && string.CompareOrdinal(name, 0, name, half + 1, half) == 0)
            return name[..half];
        return name;
    }

    internal static PostProcessSteps GetAssimpPostProcessSteps(bool perMesh = false, string? extension = null)
    {
        var steps = PostProcessSteps.Triangulate
            | PostProcessSteps.JoinIdenticalVertices
            | PostProcessSteps.GenerateSmoothNormals
            // Generate a tangent basis when the source omits one (no-ops where tangents already exist). Without
            // this, a normal-mapped mesh shipping without tangents gets a constant (1,0,0,1) per vertex and its
            // normal mapping renders wrong. Needs UVs + normals, which the steps above provide. -xlinka
            | PostProcessSteps.CalculateTangentSpace
            | PostProcessSteps.SortByPrimitiveType
            | PostProcessSteps.ImproveCacheLocality
            | PostProcessSteps.FindInvalidData
            | PostProcessSteps.ValidateDataStructure;

        // V origin, per format, and this is the only V transform in the engine. Textures upload
        // unflipped (image row 0 -> V=0), so a source whose UVs count V upward has to be mirrored or
        // its atlas rows land on the wrong faces: that is what put cream paw markings in the middle
        // of an orange fur band. Always-on FlipUVs broke glTF the same way in the other direction,
        // which is why it was ripped out; the answer is neither always nor never, it is the format's
        // own convention. -xlinka
        if (NeedsUVFlip(extension))
            steps |= PostProcessSteps.FlipUVs;

        // Per-mesh (skinned) import keeps the node hierarchy (no PreTransformVertices) and caps influences to 4.
        // The legacy whole-file path pre-transforms so a standalone static mesh lands at its world position.
        if (perMesh)
            steps |= PostProcessSteps.LimitBoneWeights;
        else
            steps |= PostProcessSteps.PreTransformVertices;
        return steps;
    }
}
