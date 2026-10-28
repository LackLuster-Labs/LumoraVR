// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Assets;

// URL instances gather and decode their file in LoadSelf. Procedural ones come from InitializeDynamic
// and get their geometry through SetMeshData.
public class MeshDataAsset : ImplementableAsset<IMeshAssetHook>
{
    private PhosMesh _meshData = null!;
    private BoundingBox _bounds;
    private bool _keepReadable;

    public PhosMesh MeshData => _meshData;

    public BoundingBox Bounds => _bounds;

    public int VertexCount => _meshData?.VertexCount ?? 0;

    // Skinning surface - lets a skinned renderer rebind off the asset's own bone table + blendshape
    // names instead of carrying that data as per-element synced lists. Returns empties until the decoder
    // populates them (the bake/glTF-skinning producer is the next step). -xlinka

    public int BoneCount => _meshData?.BoneCount ?? 0;

    public string? GetBoneName(int index) =>
        (_meshData != null && index >= 0 && index < _meshData.BoneCount) ? _meshData.GetBoneName(index) : null;

    public Lumora.Core.Math.float4x4 GetBoneBindPose(int index) =>
        (_meshData != null && index >= 0 && index < _meshData.BoneCount) ? _meshData.GetBoneBindPose(index) : Lumora.Core.Math.float4x4.Identity;

    public int BlendShapeCount => _meshData?.BlendShapeCount ?? 0;

    public string? GetBlendShapeName(int index) =>
        (_meshData != null && index >= 0 && index < _meshData.BlendShapeCount) ? _meshData.BlendShapes[index].Name : null;

    public int BlendShapeIndex(string name) => _meshData?.BlendShapeIndex(name) ?? -1;

    public int TriangleCount
    {
        get
        {
            if (_meshData == null || _meshData.Submeshes.Count == 0) return 0;
            int total = 0;
            foreach (var submesh in _meshData.Submeshes)
            {
                total += submesh.IndexCount / 3;
            }
            return total;
        }
    }

    public bool KeepReadable
    {
        get => _keepReadable;
        set => _keepReadable = value;
    }

    // The formats that build a full Assimp scene. Everything else decodes as a buffer and runs free.
    private static readonly string[] SceneFormats =
    {
        ".fbx", ".dae", ".blend", ".3ds", ".ase", ".x", ".gltf", ".glb", ".vrm", ".lwo", ".lws", ".ms3d",
    };

    private static bool NeedsSourceParseGate(string ext)
    {
        foreach (var format in SceneFormats)
        {
            if (ext.Equals(format, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    protected override async Task LoadSelf()
    {
        var bytes = await AssetManager.RequestGather(AssetURL).ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0)
        {
            FailLoad($"No mesh data gathered for {AssetURL}");
            return;
        }

        var descriptor = TargetVariant as MeshVariantDescriptor ?? MeshVariantDescriptor.Default;
        string ext = SniffIfUnknown(ResolveExtension(AssetURL), bytes);

        // Two separate guarantees, and both are needed.
        //
        // The hop off-thread is unconditional: this line runs on whichever thread completed the gather,
        // and a source parse landing on the world loop is a multi-second frame.
        //
        // Gated on what a format COSTS, not on whether it was baked. An .obj or .stl is a buffer walk
        // like .lmesh is; only the scene formats build a whole Assimp scene graph with materials,
        // animations and morph attachments. Gating the cheap ones too would serialise a world full of
        // small props behind whichever FBX happened to take the semaphore first.
        //
        // The gate is only for source formats. Content saved against a source model URL still arrives
        // here once per mesh index, so one avatar is N whole-file parses of the same bytes, each one
        // keeping a full scene alive while it throws all but one mesh away. Run together they do not
        // finish sooner, they just stack their peaks until the machine pages. .lmesh is a baked single
        // mesh - buffer copy, no gate, or an ordinary scene load would queue behind one FBX. -xlinka
        PhosMesh? mesh;
        if (!NeedsSourceParseGate(ext))
        {
            mesh = await DecodeOffThread(() => MeshDecoder.Decode(bytes, ext, descriptor.MeshIndex)).ConfigureAwait(false);
        }
        else
        {
            await Lumora.Core.Assets.AssetManager.SourceParseGate.WaitAsync().ConfigureAwait(false);
            try
            {
                mesh = await DecodeOffThread(() => MeshDecoder.Decode(bytes, ext, descriptor.MeshIndex)).ConfigureAwait(false);
            }
            finally
            {
                Lumora.Core.Assets.AssetManager.SourceParseGate.Release();
            }
        }

        if (mesh == null)
        {
            FailLoad($"Failed to decode mesh {AssetURL}");
            return;
        }

        if (System.Math.Abs(descriptor.ImportScale - 1.0f) > 0.0001f)
        {
            MeshDecoder.ScaleMesh(mesh, descriptor.ImportScale);
        }

        if (descriptor.WireframeBarycentrics)
            PhosBarycentrics.Bake(mesh);

        _keepReadable = descriptor.KeepReadable;
        SetMeshData(mesh);
    }

    // local:// URIs are content-hashed and carry no extension in their path, so the decoder can't tell the
    // format from the URL alone. The record the asset is filed under keeps the format it came in as. Falls
    // back to the URL's own extension for file://, cdn://, etc. -xlinka
    private static string ResolveExtension(Uri url)
    {
        if (url.Scheme == "local")
        {
            var extension = Engine.Current?.LocalDB?.GetExtension(url.OriginalString);
            if (!string.IsNullOrEmpty(extension))
                return extension!;
        }
        return Path.GetExtension(url.IsFile ? url.LocalPath : url.AbsolutePath) ?? "";
    }

    // Last resort, not the mechanism. The format travels with the asset: a transfer declares it in its
    // start header and the receiving cache file keeps that extension, so ResolveExtension normally
    // answers. This only catches the blob a peer on an older build sends with no format at all, where
    // the alternative is a mesh nobody can decode. Both headers identify their container outright, so
    // there is no guessing: "LMSH" is our own bake and "glTF" is the binary glTF container. -xlinka
    private static string SniffIfUnknown(string ext, byte[] data)
    {
        if (!string.IsNullOrEmpty(ext) && !ext.Equals(".asset", StringComparison.OrdinalIgnoreCase))
            return ext;

        if (data.Length >= 4)
        {
            if (data[0] == 'L' && data[1] == 'M' && data[2] == 'S' && data[3] == 'H')
                return ".lmesh";
            if (data[0] == 'g' && data[1] == 'l' && data[2] == 'T' && data[3] == 'F')
                return ".glb";
        }

        return ext;
    }

    public void SetMeshData(PhosMesh mesh)
    {
        _meshData = mesh;
        _bounds = mesh?.CalculateBoundingBox() ?? new BoundingBox();
        Version++;

        // Upload to hook if available
        if (Hook != null && mesh != null)
        {
            Hook.UploadMesh(mesh);
        }
    }

    public void SetBounds(BoundingBox bounds)
    {
        _bounds = bounds;
    }

    public void RecalculateBounds()
    {
        if (_meshData != null)
        {
            _bounds = _meshData.CalculateBoundingBox();
        }
    }

    public static MeshDataAsset FromPhosMesh(PhosMesh mesh)
    {
        var asset = new MeshDataAsset();
        asset.InitializeDynamic();
        asset.SetMeshData(mesh);
        return asset;
    }

    public override void Unload()
    {
        if (!_keepReadable)
        {
            _meshData?.Clear();
        }
        _meshData = null!;
        _bounds = new BoundingBox();
        base.Unload();
    }
}
