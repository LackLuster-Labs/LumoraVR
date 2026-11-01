// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Fluids;

// An avatar's contact mesh: the surface fluid actually meets, over the whole body (head, hair, face, neck, torso,
// arms, hands, fingers, legs, feet), decimated to one budget, in rest pose with the same four bone weights the drawn
// avatar skins with. The GPU skins it every frame from the same slots the hook flush poses the drawn avatar from, so
// fluid collides with and sticks to the body as drawn, and goo on a foot never trails the foot.
//
// Prepare runs on the world thread and copies what it needs out of the scene (each mesh bone's slot, part and current
// skin matrix); Build reads only the immutable CPU meshes and those copies, so it runs on a worker.
//
// Outermost surface only. A body under clothes has two surfaces, and fluid on the inner one would sit 5-20 mm inside
// the cloth. So every candidate triangle casts a short ray along its outward normal, and a closed surface of another
// piece (another renderer, or another connected piece of the same mesh) that the ray hits covers it. Open surfaces
// never cover: hair cards are porous, so the scalp under them stays and catches what runs between the cards.
//
// Open or closed is per connected piece of a mesh, welded by position: a piece with many boundary edges (hair cards,
// lashes, a ribbon) is open, and its vertices carry FluidLayout.ContactTwoSided, so the skin collision holds fluid on
// the side it came from. Closed pieces (skin, shoes, clothes) are one sided with a thickness. Every triangle is wound
// so its cross product points the way its vertex normals do, outward, which is the side skin anchors sit on.
//
// The budget is the whole body's (est. 14k vertices), shared out by area and a weight per part that favours the head,
// face and hands, where drips read best; open pieces count half. Clustering keeps each mesh and each open/closed class
// apart, so hair never welds into the scalp under it.
//
// Layout the GPU reads (FluidPeopleGather keeps it in the frame block):
//   Vertex BoneN, low 16 bits: an index into the person's bone records, relative to their BoneStart. Records 0-15
//   are the part bones (FluidBodyParts order), so skin bones start at SkinBoneBase. Bone0 bits 16-23: the vertex's
//   FluidBodyParts index; bits 24-31: surface flags (ContactTwoSided).
//   Indices: three per triangle, relative to this mesh; the gather makes them absolute in the frame's contact arrays.
//   Deltas: VertexShape low 20 bits a vertex of this mesh, high 12 bits an index into ShapeRenderers here; the gather
//   rebases both into the frame's contact vertices and BlendWeights when it packs. -xlinka
public sealed class FluidContactMesh
{
    // m, how far a triangle looks out along its normal for something covering it (est.)
    public const float CoverDistance = 0.03f;
    // the layout's FLUID_SKIN_BONE_FIRST: a vertex's relative bones count the 16 part bones first
    public const int SkinBoneBase = (int)FluidLayout.SkinBoneFirst;
    public const int MaxSkinBones = 256;
    public const int MaxShapes = 64;
    public const uint BoneMask = 0xFFFF;
    public const int PartShift = FluidLayout.ContactPartShift;
    public const uint TwoSidedFlag = FluidLayout.ContactTwoSided;
    public const int VertexBits = 20;
    public const uint VertexMask = (1u << VertexBits) - 1;
    public const FluidBodyRegions DefaultRegions = FluidBodyRegions.Body;
    // vertices for the whole body (est.)
    public const int DefaultBudget = 14000;
    // a part that has a surface keeps at least this many
    public const int MinPartBudget = 120;
    // boundary edges over all edges past which a welded piece is open (est.)
    public const float OpenRatio = 0.12f;
    // m, positions closer than this weld when pieces are found
    public const float WeldDistance = 5e-5f;

    // One decimation group per body part.
    public const int GroupCount = FluidBodyParts.Count;

    // Vertices per area, relative: the head and face and the hands get the detail drips need (est.).
    private static readonly float[] PartWeights =
    {
        1.2f, 1.2f, 1.2f, 1.2f, // feet, toes
        1.0f, 1.0f, 0.8f, 0.8f, // lower legs, upper legs
        4.0f, 4.0f, 1.2f, 1.2f, // hands, lower arms
        1.0f, 1.0f, 1.0f, 3.0f, // upper arms, chest, head
    };
    private const float OpenWeight = 0.5f;

    private const float Epsilon = 1e-4f;
    private const int MaxPointsPerPart = 20000;

    // Sources: one per skinned renderer that could be followed.
    private readonly List<PhosMesh> _meshes = new();
    private readonly List<float4x4[]> _skins = new();
    private readonly List<int[]> _boneParts = new();
    private readonly List<FluidBodyRegions[]> _boneRegions = new();
    private readonly List<bool[]> _boneForced = new();
    private readonly List<Slot?[]> _boneSlots = new();
    private readonly List<SkinnedMeshRenderer?> _renderers = new();

    // Build scratch, per source.
    private readonly List<float3[]> _posed = new();
    private readonly List<float3[]> _posedNormals = new();
    private readonly List<sbyte[]> _vertexParts = new();
    private readonly List<bool[]> _vertexIn = new();
    private readonly List<float3[]?> _restNormals = new();
    // per source: each triangle's welded piece, each piece open or not, each vertex's piece
    private readonly List<int[]> _triPiece = new();
    private readonly List<int[]> _vertexPiece = new();
    private readonly List<bool[]> _pieceOpen = new();
    private List<float3>[]? _partPoints;

    public FluidBodyRegions Regions { get; set; } = DefaultRegions;

    public int VertexBudget { get; set; } = DefaultBudget;

    // Outputs.
    public readonly List<FluidContactVertex> Vertices = new();
    public readonly List<uint> Indices = new();
    public readonly List<byte> TriangleParts = new();
    public readonly List<FluidBlendDelta> Deltas = new();
    public readonly List<Slot?> SkinBones = new();
    public readonly List<float4x4> SkinBind = new();
    public readonly List<float4x4> SkinAtEquip = new();
    public readonly List<SkinnedMeshRenderer?> ShapeRenderers = new();
    public readonly List<int> ShapeIndices = new();
    // World positions at equip of every output vertex, for proxies, the debug view and GPU tests.
    public readonly List<float3> EquipPositions = new();
    public readonly int[] GroupVertices = new int[GroupCount];
    public readonly int[] GroupSourceVertices = new int[GroupCount];
    public readonly int[] GroupBudgets = new int[GroupCount];
    public readonly List<string> Notes = new();

    public int Sources { get; private set; }

    public int CandidateTriangles { get; private set; }

    public int CoveredTriangles { get; private set; }

    // Welded pieces found, and how many of them are open (hair cards and the like).
    public int Pieces { get; private set; }

    public int OpenPieces { get; private set; }

    public int TwoSidedTriangles { get; private set; }

    // Triangles whose winding was turned to face out.
    public int FlippedTriangles { get; private set; }

    // Renderers with no mesh asset (an old import with no bind poses): they give no skin triangles.
    public int Legacy { get; private set; }

    // Renderers whose mesh or skeleton had not loaded: the gather tries again later.
    public int Waiting { get; private set; }

    // Same renderers, same toggles, same settings: the same cut.
    public long Hash { get; private set; }

    public bool Built { get; private set; }

    public double BuildMs { get; private set; }

    public int TriangleCount => Indices.Count / 3;

    // No skin triangles at all: every anchor on this avatar is bone-local.
    public bool BoneLocalOnly => Built && Vertices.Count == 0;

    public static int GroupOf(int part) => part >= 0 && part < GroupCount ? part : FluidBodyParts.Chest;

    // A part's budget in the last build, 0 when it had no surface.
    public int BudgetOf(int group) => group >= 0 && group < GroupCount ? GroupBudgets[group] : 0;

    public static int PartOfVertex(in FluidContactVertex v) => (int)((v.Bone0 >> PartShift) & 0xFF);

    public static bool IsTwoSided(in FluidContactVertex v) => (v.Bone0 & TwoSidedFlag) != 0;

    // World thread. Copies out each followable skinned renderer under the avatar: its mesh, every mesh bone's slot,
    // part and skin matrix as it stands now. Renderers that cannot be followed are counted, never thrown on.
    public static FluidContactMesh Prepare(Slot avatar, HumanoidRig? rig, AvatarFluidContact? contact)
    {
        var result = new FluidContactMesh();
        if (contact != null && !contact.IsDestroyed)
        {
            result.Regions = contact.Regions.Value;
            if (contact.VertexBudget.Value > 0)
                result.VertexBudget = contact.VertexBudget.Value;
        }
        var rigMap = FluidBodyParts.RigMap(rig);
        var extras = new HashSet<Slot>();
        if (contact != null && !contact.IsDestroyed)
        {
            foreach (var extra in contact.ExtraBones)
            {
                if (extra != null && !extra.IsDestroyed)
                    extras.Add(extra);
            }
        }

        var renderers = Renderers(avatar, contact);
        foreach (var renderer in renderers)
        {
            if (!IsDrawn(renderer))
                continue;
            var pose = SkinnedPose.TryCreate(renderer, out string? problem);
            if (pose == null)
            {
                if (renderer.MeshAsset.Target == null)
                    result.Legacy++;
                else
                    result.Waiting++;
                result.Notes.Add($"{renderer.Slot.SlotName.Value}: {problem}");
                continue;
            }
            if (pose.IsRigid)
                continue;
            var mesh = pose.Mesh;
            int bones = mesh.BoneCount;
            var skins = new float4x4[bones];
            var parts = new int[bones];
            var regions = new FluidBodyRegions[bones];
            var forced = new bool[bones];
            var slots = new Slot?[bones];
            for (int i = 0; i < bones; i++)
            {
                var slot = pose.BoneSlot(i);
                slots[i] = slot;
                var world = slot != null && !slot.IsDestroyed ? slot.LocalToWorld : float4x4.Identity;
                skins[i] = world * mesh.GetBoneBindPose(i);
                parts[i] = FluidBodyParts.PartOfBone(rigMap, slot, avatar, out regions[i]);
                forced[i] = IsUnder(slot, extras);
                if (forced[i] && parts[i] < 0)
                {
                    parts[i] = FluidBodyParts.Chest;
                    regions[i] = FluidBodyRegions.Torso;
                }
            }
            result.AddSource(mesh, skins, parts, regions, forced, slots, renderer);
        }
        result.Hash = RendererHash(avatar, contact);
        return result;
    }

    // Which renderers a cut reads and which of them are on, plus the contact settings: when this changes, the cut runs
    // again. Cheap enough for once a second per person near fluid.
    public static long RendererHash(Slot avatar, AvatarFluidContact? contact)
    {
        long hash = 17;
        if (contact != null && !contact.IsDestroyed)
            hash = hash * 31 + (long)contact.Regions.Value + 1000L * contact.ExtraBones.Count + 7919L * contact.VertexBudget.Value;
        foreach (var renderer in Renderers(avatar, contact))
            hash = hash * 31 + (IsDrawn(renderer) ? (long)renderer.ReferenceID.RawValue : 7L);
        return hash;
    }

    private static List<SkinnedMeshRenderer> Renderers(Slot avatar, AvatarFluidContact? contact)
    {
        var renderers = new List<SkinnedMeshRenderer>();
        if (contact != null && !contact.IsDestroyed && contact.Renderers.Count > 0)
        {
            foreach (var r in contact.Renderers)
            {
                if (r != null && !r.IsDestroyed)
                    renderers.Add(r);
            }
        }
        else if (avatar != null && !avatar.IsDestroyed)
        {
            avatar.GetComponentsInChildren(renderers);
        }
        return renderers;
    }

    private static bool IsDrawn(SkinnedMeshRenderer renderer)
        => !renderer.IsDestroyed && renderer.Enabled.Value && renderer.Slot != null && renderer.Slot.IsActive;

    // A source given straight from data: a test's synthetic avatars, and Prepare above.
    public void AddSource(PhosMesh mesh, float4x4[] skins, int[] boneParts, FluidBodyRegions[] boneRegions, bool[]? boneForced,
        Slot?[]? boneSlots, SkinnedMeshRenderer? renderer)
    {
        int bones = mesh.BoneCount;
        if (skins.Length < bones || boneParts.Length < bones || boneRegions.Length < bones)
            throw new ArgumentException("bone arrays shorter than the mesh's bone list");
        _meshes.Add(mesh);
        Sources++;
        _skins.Add(skins);
        _boneParts.Add(boneParts);
        _boneRegions.Add(boneRegions);
        _boneForced.Add(boneForced ?? new bool[bones]);
        _boneSlots.Add(boneSlots ?? new Slot?[bones]);
        _renderers.Add(renderer);
    }

    // The posed points of one part at equip, for FluidProxyFit: the outermost surface where the part's region is
    // cut, every vertex of the part elsewhere. Valid between Build and ReleaseSources.
    public IReadOnlyList<float3> PartPoints(int part)
        => _partPoints != null && part >= 0 && part < FluidBodyParts.Count ? _partPoints[part] : Array.Empty<float3>();

    // The same points, closed surfaces only (skin, clothes, shoes: never hair cards), for the inner proxies.
    public IReadOnlyList<float3> ClosedPartPoints(int part)
        => _closedPoints != null && part >= 0 && part < FluidBodyParts.Count ? _closedPoints[part] : Array.Empty<float3>();

    private List<float3>[]? _closedPoints;

    // Worker safe: reads the sources and writes only this object.
    public void Build()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Vertices.Clear();
        Indices.Clear();
        TriangleParts.Clear();
        Deltas.Clear();
        SkinBones.Clear();
        SkinBind.Clear();
        SkinAtEquip.Clear();
        ShapeRenderers.Clear();
        ShapeIndices.Clear();
        EquipPositions.Clear();
        Array.Clear(GroupVertices);
        Array.Clear(GroupSourceVertices);
        Array.Clear(GroupBudgets);
        CandidateTriangles = 0;
        CoveredTriangles = 0;
        Pieces = 0;
        OpenPieces = 0;
        TwoSidedTriangles = 0;
        FlippedTriangles = 0;
        _posed.Clear();
        _posedNormals.Clear();
        _vertexParts.Clear();
        _vertexIn.Clear();
        _restNormals.Clear();
        _triPiece.Clear();
        _vertexPiece.Clear();
        _pieceOpen.Clear();
        _partPoints = new List<float3>[FluidBodyParts.Count];
        _closedPoints = new List<float3>[FluidBodyParts.Count];
        for (int p = 0; p < _partPoints.Length; p++)
        {
            _partPoints[p] = new List<float3>();
            _closedPoints[p] = new List<float3>();
        }

        for (int s = 0; s < _meshes.Count; s++)
        {
            Pose(s);
            FindPieces(s);
        }

        // Candidates: triangles with at least two of their corners in a switched-on region, wound to face out.
        var candidates = new List<long>();
        var candidateParts = new List<int>();
        var candidateNormals = new List<float3>();
        var candidateCentres = new List<float3>();
        var candidateFlip = new List<bool>();
        for (int s = 0; s < _meshes.Count; s++)
        {
            var mesh = _meshes[s];
            var posed = _posed[s];
            var normals = _posedNormals[s];
            var parts = _vertexParts[s];
            var inside = _vertexIn[s];
            ForEachTriangle(mesh, (t, a, b, c) =>
            {
                int count = (inside[a] ? 1 : 0) + (inside[b] ? 1 : 0) + (inside[c] ? 1 : 0);
                if (count < 2)
                    return;
                var pa = posed[a];
                var face = float3.Cross(posed[b] - pa, posed[c] - pa);
                float area2 = face.Length;
                if (!(area2 > 1e-12f))
                    return;
                face /= area2;
                bool flip = mesh.HasNormals ? float3.Dot(face, normals[a] + normals[b] + normals[c]) < 0f : false;
                if (flip)
                    face = -face;
                int part = Majority(inside[a] ? parts[a] : -1, inside[b] ? parts[b] : -1, inside[c] ? parts[c] : -1);
                if (part < 0)
                    return;
                candidates.Add(((long)s << 40) | (uint)t);
                candidateParts.Add(part);
                candidateNormals.Add(face);
                candidateCentres.Add((pa + posed[b] + posed[c]) / 3f);
                candidateFlip.Add(flip);
            });
        }
        CandidateTriangles = candidates.Count;

        var covered = Cover(candidates, candidateNormals, candidateCentres);

        // Points for the proxy fit: kept candidate corners for cut parts, every vertex for the rest. Area per part for
        // the budget, open pieces at their weight.
        var cutPart = new bool[FluidBodyParts.Count];
        var weightedArea = new float[FluidBodyParts.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            if (covered[i])
            {
                CoveredTriangles++;
                continue;
            }
            int s = (int)(candidates[i] >> 40);
            int t = (int)(candidates[i] & 0xFFFFFFFF);
            int part = candidateParts[i];
            cutPart[part] = true;
            Corners(_meshes[s], t, out int a, out int b, out int c);
            var posed = _posed[s];
            bool open = _pieceOpen[s][_triPiece[s][t]];
            float area = float3.Cross(posed[b] - posed[a], posed[c] - posed[a]).Length * 0.5f;
            weightedArea[part] += area * PartWeights[part] * (open ? OpenWeight : 1f);
            AddPoint(_partPoints, part, posed[a]);
            AddPoint(_partPoints, part, posed[b]);
            AddPoint(_partPoints, part, posed[c]);
            if (!open)
            {
                AddPoint(_closedPoints, part, posed[a]);
                AddPoint(_closedPoints, part, posed[b]);
                AddPoint(_closedPoints, part, posed[c]);
            }
        }
        for (int s = 0; s < _meshes.Count; s++)
        {
            var parts = _vertexParts[s];
            var posed = _posed[s];
            int n = _meshes[s].VertexCount;
            for (int v = 0; v < n; v++)
            {
                int part = parts[v];
                if (part >= 0 && !cutPart[part])
                {
                    AddPoint(_partPoints, part, posed[v]);
                    if (!_pieceOpen[s][_vertexPiece[s][v]])
                        AddPoint(_closedPoints, part, posed[v]);
                }
            }
        }

        // The whole body's budget, by weighted area.
        float total = 0f;
        foreach (float w in weightedArea)
            total += w;
        for (int p = 0; p < GroupCount; p++)
        {
            if (!(weightedArea[p] > 0f))
                continue;
            GroupBudgets[p] = System.Math.Max(MinPartBudget, (int)MathF.Round(VertexBudget * weightedArea[p] / MathF.Max(total, 1e-12f)));
        }

        // Decimate and emit, part by part.
        var skinIndex = new Dictionary<long, int>();
        var outIndex = new Dictionary<long, int>();
        for (int group = 0; group < GroupCount; group++)
        {
            var triangles = new List<int>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!covered[i] && GroupOf(candidateParts[i]) == group)
                    triangles.Add(i);
            }
            if (triangles.Count == 0)
                continue;
            EmitGroup(group, triangles, candidates, candidateParts, candidateFlip, skinIndex, outIndex);
        }

        BuildDeltas(outIndex);
        Built = true;
        BuildMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // Drops the build scratch and the references to the source meshes once the fit has used them.
    public void ReleaseSources()
    {
        _posed.Clear();
        _posedNormals.Clear();
        _vertexParts.Clear();
        _vertexIn.Clear();
        _restNormals.Clear();
        _triPiece.Clear();
        _vertexPiece.Clear();
        _pieceOpen.Clear();
        _partPoints = null;
        _closedPoints = null;
        _meshes.Clear();
        _skins.Clear();
        _boneParts.Clear();
        _boneRegions.Clear();
        _boneForced.Clear();
        _boneSlots.Clear();
        _renderers.Clear();
    }

    // GPU tests read this. Little endian: "FLCM", the version (1; 2 with the proxies; 3 with the inner proxies
    // and the build's numbers), then vertex, triangle, delta, skin bone and shape counts (int32 each); the vertices as
    // their raw 48 B records (bones relative to the person, the part in Bone0 bits 16-23, flags in 24-31); three uint32
    // indices per triangle; one byte part per triangle; the deltas as raw 16 B records; per skin bone its matrix at
    // equip, then its bind matrix, as 16 floats column by column; and each vertex's world position at equip as 3
    // floats. Version 2 goes on with the proxies: their count, each as its raw 48 B record (Bone the index into the
    // frames), the frame count and each frame relative to its part bone, then per part a present byte and its bone's
    // matrix at equip. Version 3 then adds the inner proxies (count, raw records, same frames), per part its budget and
    // kept vertices (int32 each), then the regions, candidates, covered, pieces, open pieces and two-sided triangles
    // (int32 each).
    public void Export(string path, FluidProxyFit? fit = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write(0x4D434C46u);
        w.Write(fit != null ? 3 : 1);
        w.Write(Vertices.Count);
        w.Write(TriangleCount);
        w.Write(Deltas.Count);
        w.Write(SkinBones.Count);
        w.Write(ShapeRenderers.Count);
        w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(Vertices)));
        foreach (uint i in Indices)
            w.Write(i);
        foreach (byte p in TriangleParts)
            w.Write(p);
        w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(Deltas)));
        for (int i = 0; i < SkinBones.Count; i++)
        {
            WriteMatrix(w, SkinAtEquip[i]);
            WriteMatrix(w, SkinBind[i]);
        }
        foreach (var p in EquipPositions)
        {
            w.Write(p.x);
            w.Write(p.y);
            w.Write(p.z);
        }
        if (fit == null)
            return;
        w.Write(fit.Proxies.Count);
        w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(fit.Proxies)));
        w.Write(fit.Frames.Count);
        foreach (var f in fit.Frames)
            WriteMatrix(w, f);
        for (int p = 0; p < FluidBodyParts.Count; p++)
        {
            w.Write((byte)(fit.PartPresent[p] ? 1 : 0));
            WriteMatrix(w, fit.PartWorld[p]);
        }
        w.Write(fit.Inner.Count);
        w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(fit.Inner)));
        for (int p = 0; p < FluidBodyParts.Count; p++)
        {
            w.Write(GroupBudgets[p]);
            w.Write(GroupVertices[p]);
        }
        w.Write((int)Regions);
        w.Write(CandidateTriangles);
        w.Write(CoveredTriangles);
        w.Write(Pieces);
        w.Write(OpenPieces);
        w.Write(TwoSidedTriangles);
    }

    public static uint PackNormal(float3 n)
    {
        float l = MathF.Abs(n.x) + MathF.Abs(n.y) + MathF.Abs(n.z);
        if (!(l > 1e-12f))
            return PackNormal(float3.Up);
        float x = n.x / l, y = n.y / l;
        if (n.z < 0f)
        {
            float ox = (1f - MathF.Abs(y)) * (x >= 0f ? 1f : -1f);
            float oy = (1f - MathF.Abs(x)) * (y >= 0f ? 1f : -1f);
            x = ox;
            y = oy;
        }
        uint ux = (uint)System.Math.Clamp((int)MathF.Round((x * 0.5f + 0.5f) * 65535f), 0, 65535);
        uint uy = (uint)System.Math.Clamp((int)MathF.Round((y * 0.5f + 0.5f) * 65535f), 0, 65535);
        return ux | (uy << 16);
    }

    public static float3 UnpackNormal(uint packed)
    {
        float x = (packed & 0xFFFF) / 65535f * 2f - 1f;
        float y = (packed >> 16) / 65535f * 2f - 1f;
        float z = 1f - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0f)
        {
            float ox = (1f - MathF.Abs(y)) * (x >= 0f ? 1f : -1f);
            float oy = (1f - MathF.Abs(x)) * (y >= 0f ? 1f : -1f);
            x = ox;
            y = oy;
        }
        var v = new float3(x, y, z);
        float len = v.Length;
        return len > 1e-6f ? v / len : float3.Up;
    }

    private static void AddPoint(List<float3>[] lists, int part, in float3 p)
    {
        var list = lists[part];
        if (list.Count < MaxPointsPerPart)
            list.Add(p);
    }

    // Every vertex posed by its blended skin matrix, its part by the weight its bones carry per part, and whether it
    // sits in a switched-on region.
    private void Pose(int s)
    {
        var mesh = _meshes[s];
        var skins = _skins[s];
        var boneParts = _boneParts[s];
        var boneRegions = _boneRegions[s];
        var boneForced = _boneForced[s];
        int n = mesh.VertexCount;
        var posed = new float3[n];
        var normals = new float3[n];
        var parts = new sbyte[n];
        var inside = new bool[n];
        var positions = mesh.RawPositions;
        var meshNormals = mesh.HasNormals ? mesh.RawNormals : null;
        var bindings = mesh.HasBoneBindings ? mesh.RawBoneBindings : null;
        Span<float> perPart = stackalloc float[FluidBodyParts.Count];
        for (int v = 0; v < n; v++)
        {
            var p = positions[v];
            var nrm = meshNormals != null ? meshNormals[v] : float3.Up;
            if (bindings == null)
            {
                posed[v] = p;
                normals[v] = nrm;
                parts[v] = -1;
                continue;
            }
            var b = bindings[v];
            float total = 0f;
            float4 c0 = default, c1 = default, c2 = default, c3 = default;
            perPart.Clear();
            int strongest = -1;
            float strongestWeight = 0f;
            for (int k = 0; k < 4; k++)
            {
                int index = (int)b.boneIndices[k];
                float weight = b.boneWeights[k];
                if (weight <= 0f || index < 0 || index >= skins.Length)
                    continue;
                ref var m = ref skins[index];
                c0 += m.c0 * weight;
                c1 += m.c1 * weight;
                c2 += m.c2 * weight;
                c3 += m.c3 * weight;
                total += weight;
                int part = boneParts[index];
                if (part >= 0 && part < FluidBodyParts.Count)
                    perPart[part] += weight;
                if (weight > strongestWeight)
                {
                    strongestWeight = weight;
                    strongest = index;
                }
            }
            if (total > 1e-6f)
            {
                float inv = 1f / total;
                c0 *= inv;
                c1 *= inv;
                c2 *= inv;
                c3 *= inv;
                posed[v] = new float3(c0.x * p.x + c1.x * p.y + c2.x * p.z + c3.x,
                    c0.y * p.x + c1.y * p.y + c2.y * p.z + c3.y,
                    c0.z * p.x + c1.z * p.y + c2.z * p.z + c3.z);
                var tn = new float3(c0.x * nrm.x + c1.x * nrm.y + c2.x * nrm.z,
                    c0.y * nrm.x + c1.y * nrm.y + c2.y * nrm.z,
                    c0.z * nrm.x + c1.z * nrm.y + c2.z * nrm.z);
                float len = tn.Length;
                normals[v] = len > 1e-12f ? tn / len : float3.Up;
            }
            else
            {
                posed[v] = p;
                normals[v] = nrm;
            }

            int best = -1;
            float bestWeight = 0f;
            for (int q = 0; q < perPart.Length; q++)
            {
                if (perPart[q] > bestWeight)
                {
                    bestWeight = perPart[q];
                    best = q;
                }
            }
            parts[v] = (sbyte)best;
            if (best >= 0 && strongest >= 0)
            {
                bool forced = boneForced[strongest] && boneParts[strongest] == best;
                var region = boneParts[strongest] == best ? boneRegions[strongest] : FluidBodyParts.RegionOf(best);
                inside[v] = forced || (region & Regions) != 0;
            }
        }
        _posed.Add(posed);
        _posedNormals.Add(normals);
        _vertexParts.Add(parts);
        _vertexIn.Add(inside);
    }

    // The source's welded pieces (vertices closer than WeldDistance are one, UV and normal seams included), and which
    // of them are open: more than OpenRatio of their edges have one triangle only.
    private void FindPieces(int s)
    {
        var mesh = _meshes[s];
        var positions = mesh.RawPositions;
        int n = mesh.VertexCount;
        var welded = new int[n];
        var weld = new Dictionary<(int, int, int), int>(n);
        float inv = 1f / WeldDistance;
        for (int v = 0; v < n; v++)
        {
            var p = positions[v];
            var key = ((int)MathF.Round(p.x * inv), (int)MathF.Round(p.y * inv), (int)MathF.Round(p.z * inv));
            if (!weld.TryGetValue(key, out int id))
            {
                id = weld.Count;
                weld[key] = id;
            }
            welded[v] = id;
        }
        var parent = new int[weld.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        void Union(int x, int y)
        {
            x = Find(x);
            y = Find(y);
            if (x != y)
                parent[System.Math.Max(x, y)] = System.Math.Min(x, y);
        }
        var edges = new Dictionary<long, int>();
        void Edge(int x, int y)
        {
            long key = x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x;
            edges.TryGetValue(key, out int c);
            edges[key] = c + 1;
        }
        int triangles = 0;
        ForEachTriangle(mesh, (t, a, b, c) =>
        {
            triangles = System.Math.Max(triangles, t + 1);
            int wa = welded[a], wb = welded[b], wc = welded[c];
            Union(wa, wb);
            Union(wb, wc);
            Edge(wa, wb);
            Edge(wb, wc);
            Edge(wc, wa);
        });
        var compact = new Dictionary<int, int>();
        int PieceOf(int weldedId)
        {
            int root = Find(weldedId);
            if (!compact.TryGetValue(root, out int piece))
            {
                piece = compact.Count;
                compact[root] = piece;
            }
            return piece;
        }
        var vertexPiece = new int[n];
        for (int v = 0; v < n; v++)
            vertexPiece[v] = PieceOf(welded[v]);
        var edgeCount = new List<int>();
        var boundary = new List<int>();
        foreach (var entry in edges)
        {
            int piece = PieceOf((int)(entry.Key >> 32));
            while (edgeCount.Count <= piece)
            {
                edgeCount.Add(0);
                boundary.Add(0);
            }
            edgeCount[piece]++;
            if (entry.Value == 1)
                boundary[piece]++;
        }
        var open = new bool[compact.Count];
        for (int p = 0; p < open.Length; p++)
        {
            if (p < edgeCount.Count && edgeCount[p] > 0)
                open[p] = boundary[p] > OpenRatio * edgeCount[p];
            if (open[p])
                OpenPieces++;
        }
        Pieces += open.Length;
        var triPiece = new int[triangles];
        ForEachTriangle(mesh, (t, a, b, c) => triPiece[t] = vertexPiece[a]);
        _triPiece.Add(triPiece);
        _vertexPiece.Add(vertexPiece);
        _pieceOpen.Add(open);
    }

    // The outermost cut: a short ray from each candidate's centre along its normal against the closed pieces near it
    // other than its own, through a hash grid of cells one ray long.
    private bool[] Cover(List<long> candidates, List<float3> normals, List<float3> centres)
    {
        var covered = new bool[candidates.Count];
        if (candidates.Count == 0)
            return covered;
        float cell = CoverDistance;
        var wanted = new HashSet<long>();
        var lo = new float3(float.MaxValue);
        var hi = new float3(float.MinValue);
        for (int i = 0; i < candidates.Count; i++)
        {
            var a = centres[i];
            var b = a + normals[i] * CoverDistance;
            var mn = Min(a, b);
            var mx = Max(a, b);
            lo = Min(lo, mn);
            hi = Max(hi, mx);
            ForCells(mn, mx, cell, key => wanted.Add(key));
        }

        var grid = new Dictionary<long, List<long>>();
        for (int s = 0; s < _meshes.Count; s++)
        {
            var posed = _posed[s];
            var triPiece = _triPiece[s];
            var open = _pieceOpen[s];
            ForEachTriangle(_meshes[s], (t, a, b, c) =>
            {
                if (open[triPiece[t]])
                    return;
                var mn = Min(Min(posed[a], posed[b]), posed[c]);
                var mx = Max(Max(posed[a], posed[b]), posed[c]);
                if (mx.x < lo.x || mx.y < lo.y || mx.z < lo.z || mn.x > hi.x || mn.y > hi.y || mn.z > hi.z)
                    return;
                long id = ((long)s << 40) | (uint)t;
                ForCells(mn, mx, cell, key =>
                {
                    if (!wanted.Contains(key))
                        return;
                    if (!grid.TryGetValue(key, out var list))
                        grid[key] = list = new List<long>();
                    list.Add(id);
                });
            });
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            int source = (int)(candidates[i] >> 40);
            int sourcePiece = _triPiece[source][(int)(candidates[i] & 0xFFFFFFFF)];
            var origin = centres[i] + normals[i] * Epsilon;
            var dir = normals[i];
            var end = origin + dir * CoverDistance;
            bool hit = false;
            ForCells(Min(origin, end), Max(origin, end), cell, key =>
            {
                if (hit || !grid.TryGetValue(key, out var list))
                    return;
                foreach (long id in list)
                {
                    int s = (int)(id >> 40);
                    int t = (int)(id & 0xFFFFFFFF);
                    if (s == source && _triPiece[s][t] == sourcePiece)
                        continue;
                    Corners(_meshes[s], t, out int a, out int b, out int c);
                    var posed = _posed[s];
                    if (RayHits(origin, dir, CoverDistance, posed[a], posed[b], posed[c]))
                    {
                        hit = true;
                        return;
                    }
                }
            });
            covered[i] = hit;
        }
        return covered;
    }

    // Vertex clustering on a grid until the part fits its budget. Each cluster keeps a real vertex (the one nearest
    // its centre), so skin weights stay exact; a cluster never spans two meshes or an open and a closed piece; triangles
    // that fold to a line or repeat are dropped.
    private void EmitGroup(int group, List<int> triangles, List<long> candidates, List<int> candidateParts, List<bool> candidateFlip,
        Dictionary<long, int> skinIndex, Dictionary<long, int> outIndex)
    {
        var local = new Dictionary<long, int>();
        var keys = new List<long>();
        var points = new List<float3>();
        var domains = new List<int>();
        float area = 0f;
        foreach (int i in triangles)
        {
            int s = (int)(candidates[i] >> 40);
            Corners(_meshes[s], (int)(candidates[i] & 0xFFFFFFFF), out int a, out int b, out int c);
            var posed = _posed[s];
            area += float3.Cross(posed[b] - posed[a], posed[c] - posed[a]).Length * 0.5f;
            Touch(s, a);
            Touch(s, b);
            Touch(s, c);
        }
        GroupSourceVertices[group] = keys.Count;
        int budget = GroupBudgets[group] > 0 ? GroupBudgets[group] : MinPartBudget;
        GroupBudgets[group] = budget;
        var representative = new int[keys.Count];
        for (int k = 0; k < representative.Length; k++)
            representative[k] = k;
        if (keys.Count > budget)
        {
            float h = MathF.Max(1e-4f, MathF.Sqrt(area / MathF.Max(1, budget)));
            for (int attempt = 0; attempt < 64; attempt++)
            {
                if (Cluster(points, domains, h, representative) <= budget)
                    break;
                h *= 1.15f;
            }
        }

        var seen = new HashSet<(int, int, int)>();
        foreach (int i in triangles)
        {
            int s = (int)(candidates[i] >> 40);
            int t = (int)(candidates[i] & 0xFFFFFFFF);
            Corners(_meshes[s], t, out int a, out int b, out int c);
            int ra = representative[local[Key(s, a)]];
            int rb = representative[local[Key(s, b)]];
            int rc = representative[local[Key(s, c)]];
            if (ra == rb || rb == rc || ra == rc)
                continue;
            var sorted = Sort(ra, rb, rc);
            if (!seen.Add(sorted))
                continue;
            bool open = _pieceOpen[s][_triPiece[s][t]];
            int part = candidateParts[i];
            int oa = Emit(keys[ra], points[ra], part, open, skinIndex, outIndex);
            int ob = Emit(keys[rb], points[rb], part, open, skinIndex, outIndex);
            int oc = Emit(keys[rc], points[rc], part, open, skinIndex, outIndex);
            if (candidateFlip[i])
            {
                (ob, oc) = (oc, ob);
                FlippedTriangles++;
            }
            Indices.Add((uint)oa);
            Indices.Add((uint)ob);
            Indices.Add((uint)oc);
            TriangleParts.Add((byte)part);
            if (open)
                TwoSidedTriangles++;
        }

        void Touch(int s, int v)
        {
            long key = Key(s, v);
            if (local.ContainsKey(key))
                return;
            local[key] = keys.Count;
            keys.Add(key);
            points.Add(_posed[s][v]);
            domains.Add(s * 2 + (_pieceOpen[s][_vertexPiece[s][v]] ? 1 : 0));
        }
    }

    private static int Cluster(List<float3> points, List<int> domains, float h, int[] representative)
    {
        float inv = 1f / h;
        var cells = new Dictionary<(long, int), int>();
        var sums = new List<float3>();
        var counts = new List<int>();
        var cellOf = new int[points.Count];
        for (int k = 0; k < points.Count; k++)
        {
            var key = (CellKey(points[k], inv), domains[k]);
            if (!cells.TryGetValue(key, out int c))
            {
                c = sums.Count;
                cells[key] = c;
                sums.Add(float3.Zero);
                counts.Add(0);
            }
            sums[c] += points[k];
            counts[c]++;
            cellOf[k] = c;
        }
        var best = new int[sums.Count];
        var bestDistance = new float[sums.Count];
        for (int c = 0; c < best.Length; c++)
        {
            best[c] = -1;
            bestDistance[c] = float.MaxValue;
        }
        for (int k = 0; k < points.Count; k++)
        {
            int c = cellOf[k];
            float d = float3.DistanceSquared(points[k], sums[c] / counts[c]);
            if (d < bestDistance[c])
            {
                bestDistance[c] = d;
                best[c] = k;
            }
        }
        for (int k = 0; k < points.Count; k++)
            representative[k] = best[cellOf[k]];
        return sums.Count;
    }

    private int Emit(long key, in float3 equip, int part, bool open, Dictionary<long, int> skinIndex, Dictionary<long, int> outIndex)
    {
        if (outIndex.TryGetValue(key, out int existing))
            return existing;
        int s = (int)(key >> 32);
        int v = (int)(key & 0xFFFFFFFF);
        var mesh = _meshes[s];
        var rest = mesh.RawPositions[v];
        var normal = mesh.HasNormals ? mesh.RawNormals[v] : RestNormals(s)[v];
        var b = mesh.RawBoneBindings[v];
        Span<int> bone = stackalloc int[4];
        Span<float> weight = stackalloc float[4];
        bone[0] = (int)b.boneIndices.x;
        bone[1] = (int)b.boneIndices.y;
        bone[2] = (int)b.boneIndices.z;
        bone[3] = (int)b.boneIndices.w;
        weight[0] = b.boneWeights.x;
        weight[1] = b.boneWeights.y;
        weight[2] = b.boneWeights.z;
        weight[3] = b.boneWeights.w;
        float total = 0f;
        int first = -1;
        for (int k = 0; k < 4; k++)
        {
            if (!(weight[k] > 0f) || bone[k] < 0 || bone[k] >= mesh.BoneCount)
            {
                weight[k] = 0f;
                bone[k] = -1;
                continue;
            }
            long skinKey = ((long)s << 32) | (uint)bone[k];
            if (!skinIndex.TryGetValue(skinKey, out int index))
            {
                if (SkinBones.Count >= MaxSkinBones)
                {
                    weight[k] = 0f;
                    bone[k] = -1;
                    continue;
                }
                index = SkinBones.Count;
                skinIndex[skinKey] = index;
                SkinBones.Add(_boneSlots[s][bone[k]]);
                SkinBind.Add(mesh.GetBoneBindPose(bone[k]));
                SkinAtEquip.Add(_skins[s][bone[k]]);
            }
            bone[k] = SkinBoneBase + index;
            total += weight[k];
            if (first < 0)
                first = bone[k];
        }
        if (first < 0)
        {
            // Every influence was dropped: ride the first skin bone at full weight rather than lose the vertex.
            first = SkinBoneBase;
            bone[0] = first;
            weight[0] = 1f;
            total = 1f;
            if (SkinBones.Count == 0)
            {
                SkinBones.Add(null);
                SkinBind.Add(float4x4.Identity);
                SkinAtEquip.Add(float4x4.Identity);
            }
        }
        for (int k = 0; k < 4; k++)
        {
            weight[k] /= total;
            if (bone[k] < 0)
                bone[k] = first;
        }
        var record = new FluidContactVertex
        {
            Position = rest,
            Normal = PackNormal(normal),
            Weights = new float4(weight[0], weight[1], weight[2], weight[3]),
            Bone0 = ((uint)bone[0] & BoneMask) | ((uint)(part & 0xFF) << PartShift) | (open ? TwoSidedFlag : 0u),
            Bone1 = (uint)bone[1] & BoneMask,
            Bone2 = (uint)bone[2] & BoneMask,
            Bone3 = (uint)bone[3] & BoneMask,
        };
        int index2 = Vertices.Count;
        Vertices.Add(record);
        EquipPositions.Add(equip);
        outIndex[key] = index2;
        GroupVertices[GroupOf(part)]++;
        return index2;
    }

    // Sparse deltas at the kept vertices, for the shapes that move them most.
    private void BuildDeltas(Dictionary<long, int> outIndex)
    {
        var magnitude = new Dictionary<long, float>();
        foreach (var entry in outIndex)
        {
            int s = (int)(entry.Key >> 32);
            int v = (int)(entry.Key & 0xFFFFFFFF);
            var shapes = _meshes[s].BlendShapes;
            for (int i = 0; i < shapes.Count; i++)
            {
                var frames = shapes[i].Frames;
                if (frames.Length == 0 || v >= frames[0].positions.Length)
                    continue;
                float m = frames[0].positions[v].Length;
                if (m > 1e-6f)
                {
                    long shapeKey = ((long)s << 32) | (uint)i;
                    magnitude.TryGetValue(shapeKey, out float sum);
                    magnitude[shapeKey] = sum + m;
                }
            }
        }
        if (magnitude.Count == 0)
            return;
        var order = new List<KeyValuePair<long, float>>(magnitude);
        order.Sort((x, y) => y.Value.CompareTo(x.Value));
        var shapeIndex = new Dictionary<long, int>();
        for (int k = 0; k < order.Count && k < MaxShapes; k++)
        {
            long shapeKey = order[k].Key;
            shapeIndex[shapeKey] = ShapeRenderers.Count;
            ShapeRenderers.Add(_renderers[(int)(shapeKey >> 32)]);
            ShapeIndices.Add((int)(shapeKey & 0xFFFFFFFF));
        }
        // By vertex, as FluidSkin's binary search wants them.
        var sorted = new List<KeyValuePair<long, int>>(outIndex);
        sorted.Sort((x, y) => x.Value.CompareTo(y.Value));
        foreach (var entry in sorted)
        {
            int s = (int)(entry.Key >> 32);
            int v = (int)(entry.Key & 0xFFFFFFFF);
            var shapes = _meshes[s].BlendShapes;
            for (int i = 0; i < shapes.Count; i++)
            {
                if (!shapeIndex.TryGetValue(((long)s << 32) | (uint)i, out int local))
                    continue;
                var frames = shapes[i].Frames;
                if (frames.Length == 0 || v >= frames[0].positions.Length)
                    continue;
                var d = frames[0].positions[v];
                if (d.LengthSquared <= 1e-12f)
                    continue;
                Deltas.Add(new FluidBlendDelta { Delta = d, VertexShape = ((uint)entry.Value & VertexMask) | ((uint)local << VertexBits) });
            }
        }
    }

    // Face normals summed at each vertex, for a mesh that came without normals; once per source.
    private float3[] RestNormals(int s)
    {
        while (_restNormals.Count <= s)
            _restNormals.Add(null);
        if (_restNormals[s] is { } done)
            return done;
        var mesh = _meshes[s];
        var positions = mesh.RawPositions;
        var sums = new float3[mesh.VertexCount];
        ForEachTriangle(mesh, (t, a, b, c) =>
        {
            var face = float3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            sums[a] += face;
            sums[b] += face;
            sums[c] += face;
        });
        for (int v = 0; v < sums.Length; v++)
        {
            float len = sums[v].Length;
            sums[v] = len > 1e-12f ? sums[v] / len : float3.Up;
        }
        _restNormals[s] = sums;
        return sums;
    }

    private static void ForEachTriangle(PhosMesh mesh, Action<int, int, int, int> visit)
    {
        int offset = 0;
        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Topology != PhosTopology.Triangles)
                continue;
            var raw = sub.RawIndices;
            int n = sub.Count;
            int count = mesh.VertexCount;
            for (int t = 0; t < n; t++)
            {
                int a = raw[t * 3], b = raw[t * 3 + 1], c = raw[t * 3 + 2];
                if ((uint)a < (uint)count && (uint)b < (uint)count && (uint)c < (uint)count)
                    visit(offset + t, a, b, c);
            }
            offset += n;
        }
    }

    private static void Corners(PhosMesh mesh, int triangle, out int a, out int b, out int c)
    {
        int offset = 0;
        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Topology != PhosTopology.Triangles)
                continue;
            if (triangle < offset + sub.Count)
            {
                int t = triangle - offset;
                var raw = sub.RawIndices;
                a = raw[t * 3];
                b = raw[t * 3 + 1];
                c = raw[t * 3 + 2];
                return;
            }
            offset += sub.Count;
        }
        a = b = c = 0;
    }

    // Two sided: a lining modelled facing inward still covers.
    private static bool RayHits(in float3 origin, in float3 dir, float length, in float3 a, in float3 b, in float3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = float3.Cross(dir, e2);
        float det = float3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f)
            return false;
        float inv = 1f / det;
        var s = origin - a;
        float u = float3.Dot(s, p) * inv;
        if (u < 0f || u > 1f)
            return false;
        var q = float3.Cross(s, e1);
        float v = float3.Dot(dir, q) * inv;
        if (v < 0f || u + v > 1f)
            return false;
        float t = float3.Dot(e2, q) * inv;
        return t > 0f && t <= length;
    }

    private static void ForCells(in float3 mn, in float3 mx, float cell, Action<long> visit)
    {
        float inv = 1f / cell;
        int x0 = (int)MathF.Floor(mn.x * inv), x1 = (int)MathF.Floor(mx.x * inv);
        int y0 = (int)MathF.Floor(mn.y * inv), y1 = (int)MathF.Floor(mx.y * inv);
        int z0 = (int)MathF.Floor(mn.z * inv), z1 = (int)MathF.Floor(mx.z * inv);
        // A huge triangle (a floor plane skinned to the hips) would walk thousands of cells; it covers nothing a body
        // has, so it is clamped to the first few.
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1) > 4096)
        {
            x1 = System.Math.Min(x1, x0 + 15);
            y1 = System.Math.Min(y1, y0 + 15);
            z1 = System.Math.Min(z1, z0 + 15);
        }
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                    visit(Pack(x, y, z));
    }

    private static long CellKey(in float3 p, float inv)
        => Pack((int)MathF.Floor(p.x * inv), (int)MathF.Floor(p.y * inv), (int)MathF.Floor(p.z * inv));

    private static long Pack(int x, int y, int z)
        => (((long)x & 0x1FFFFF) << 42) | (((long)y & 0x1FFFFF) << 21) | ((long)z & 0x1FFFFF);

    private static long Key(int s, int v) => ((long)s << 32) | (uint)v;

    private static (int, int, int) Sort(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private static int Majority(int a, int b, int c)
    {
        if (a >= 0 && (a == b || a == c))
            return a;
        if (b >= 0 && b == c)
            return b;
        return a >= 0 ? a : b >= 0 ? b : c;
    }

    private static bool IsUnder(Slot? slot, HashSet<Slot> roots)
    {
        if (roots.Count == 0)
            return false;
        int depth = 0;
        for (var walk = slot; walk != null && depth < 256; walk = walk.Parent, depth++)
        {
            if (roots.Contains(walk))
                return true;
        }
        return false;
    }

    private static float3 Min(in float3 a, in float3 b) => new(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));

    private static float3 Max(in float3 a, in float3 b) => new(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));

    private static void WriteMatrix(BinaryWriter w, in float4x4 m)
    {
        w.Write(m.c0.x); w.Write(m.c0.y); w.Write(m.c0.z); w.Write(m.c0.w);
        w.Write(m.c1.x); w.Write(m.c1.y); w.Write(m.c1.z); w.Write(m.c1.w);
        w.Write(m.c2.x); w.Write(m.c2.y); w.Write(m.c2.z); w.Write(m.c2.w);
        w.Write(m.c3.x); w.Write(m.c3.y); w.Write(m.c3.z); w.Write(m.c3.w);
    }
}
