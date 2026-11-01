// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;

namespace Lumora.Fluids.Hooks;

// The view-only path (Android, and any machine that does not simulate): settled ledger cells as flattened blobs
// in one MultiMesh, and the spots on people in MultiMeshes of its own, all written on the CPU. Blobs change only
// when the ledger does (at most 2k instances); spots are written every frame from the bones, since a hand moving
// at 1.5 m/s would otherwise leave its goo behind. These MultiMeshes are never GPU-written, so a CPU setter on
// them is fine. Each blob is a flattened ellipsoid of the cell's volume, its short axis on the surface normal.
// Blobs sit in their renderer's local space and the blob instance carries the renderer's transform; spots are in
// world space, from the bones. These are raw instances in the shared scenario, so their owner hides them while
// its world is not the one on screen. -xlinka
public sealed class FluidViewOnlyBlobs : IDisposable
{
    private const int Floats = FluidResiduePass.InstanceFloats;

    private readonly float[] _blobs;
    private readonly float[] _spots;
    private readonly float[] _headSpots;
    private Rid _blobMesh;
    private Rid _blobMultimesh;
    private Rid _spotMultimesh;
    private Rid _headMultimesh;
    private Rid _blobInstance;
    private Rid _spotInstance;
    private Rid _headInstance;
    private int _blobCount;
    private int _spotVisible = -1;
    private Transform3D _blobTransform = Transform3D.Identity;
    private bool _visible = true;

    public FluidViewOnlyBlobs(int blobCapacity, int spotCapacity, Rid scenario, uint layers, uint headLayers)
    {
        BlobCapacity = Math.Max(blobCapacity, 1);
        SpotCapacity = Math.Max(spotCapacity, 1);
        _blobs = new float[BlobCapacity * Floats];
        _spots = new float[SpotCapacity * Floats];
        _headSpots = new float[SpotCapacity * Floats];
        _blobMesh = FluidResiduePass.BuildSpotMesh(false);
        _blobMultimesh = Make(_blobMesh, BlobCapacity);
        _spotMultimesh = Make(_blobMesh, SpotCapacity);
        _headMultimesh = Make(_blobMesh, SpotCapacity);
        _blobInstance = Instance(_blobMultimesh, scenario, layers);
        _spotInstance = Instance(_spotMultimesh, scenario, layers);
        _headInstance = Instance(_headMultimesh, scenario, headLayers);
    }

    public int BlobCapacity { get; }

    public int SpotCapacity { get; }

    public int BlobCount => _blobCount;

    public Rid BlobMultimesh => _blobMultimesh;

    public Rid SpotMultimesh => _spotMultimesh;

    public Rid HeadMultimesh => _headMultimesh;

    private static Rid Make(Rid mesh, int capacity)
    {
        var mm = RenderingServer.MultimeshCreate();
        RenderingServer.MultimeshSetMesh(mm, mesh);
        RenderingServer.MultimeshAllocateData(mm, capacity, RenderingServer.MultimeshTransformFormat.Transform3D, true, true, false);
        RenderingServer.MultimeshSetVisibleInstances(mm, 0);
        return mm;
    }

    private static Rid Instance(Rid multimesh, Rid scenario, uint layers)
    {
        var instance = RenderingServer.InstanceCreate2(multimesh, scenario);
        RenderingServer.InstanceSetLayerMask(instance, layers);
        RenderingServer.InstanceGeometrySetCastShadowsSetting(instance, RenderingServer.ShadowCastingSetting.Off);
        return instance;
    }

    public void SetMaterials(Rid blobMaterial, Rid spotMaterial)
    {
        RenderingServer.InstanceGeometrySetMaterialOverride(_blobInstance, blobMaterial);
        RenderingServer.InstanceGeometrySetMaterialOverride(_spotInstance, spotMaterial);
        RenderingServer.InstanceGeometrySetMaterialOverride(_headInstance, spotMaterial);
    }

    public void SetBounds(Aabb bounds)
    {
        RenderingServer.MultimeshSetCustomAabb(_blobMultimesh, bounds);
        RenderingServer.MultimeshSetCustomAabb(_spotMultimesh, bounds);
        RenderingServer.MultimeshSetCustomAabb(_headMultimesh, bounds);
    }

    public void SetTransform(Transform3D transform)
    {
        if (transform == _blobTransform)
            return;
        _blobTransform = transform;
        RenderingServer.InstanceSetTransform(_blobInstance, transform);
    }

    public void SetLayers(uint layers)
    {
        RenderingServer.InstanceSetLayerMask(_blobInstance, layers);
        RenderingServer.InstanceSetLayerMask(_spotInstance, layers);
    }

    public void SetVisible(bool visible)
    {
        if (visible == _visible)
            return;
        _visible = visible;
        RenderingServer.InstanceSetVisible(_blobInstance, visible);
        RenderingServer.InstanceSetVisible(_spotInstance, visible);
        RenderingServer.InstanceSetVisible(_headInstance, visible);
    }

    public void BeginBlobs() => _blobCount = 0;

    // One settled cell: its centre, its volume in m3, how flat it lies (short axis over long) and the surface
    // normal it lies on.
    public bool AddBlob(Vector3 centre, float volume, float flatten, Vector3 normal, Color tint, float wetness)
    {
        if (_blobCount >= BlobCapacity || volume <= 0f)
            return false;
        flatten = Math.Clamp(flatten, 0.05f, 1f);
        float a = MathF.Cbrt(volume * 0.2387324f / flatten);
        var n = normal.LengthSquared() > 1e-8f ? normal.Normalized() : Vector3.Up;
        var t1 = Math.Abs(n.Y) < 0.99f ? Vector3.Up.Cross(n).Normalized() : Vector3.Right.Cross(n).Normalized();
        var t2 = n.Cross(t1);
        // right-handed (t1 x n = -t2), or the mirrored instance would wind its faces backwards
        var basis = new Basis(t1 * a, n * (a * flatten), -t2 * a);
        var span = _blobs.AsSpan(_blobCount * Floats, Floats);
        span[0] = basis.X.X; span[1] = basis.Y.X; span[2] = basis.Z.X; span[3] = centre.X;
        span[4] = basis.X.Y; span[5] = basis.Y.Y; span[6] = basis.Z.Y; span[7] = centre.Y;
        span[8] = basis.X.Z; span[9] = basis.Y.Z; span[10] = basis.Z.Z; span[11] = centre.Z;
        span[12] = tint.R; span[13] = tint.G; span[14] = tint.B; span[15] = tint.A;
        span[16] = wetness; span[17] = 0f; span[18] = 0f; span[19] = volume;
        _blobCount++;
        return true;
    }

    // Called when the ledger changed, never per frame.
    public void CommitBlobs()
    {
        _blobs.AsSpan(_blobCount * Floats).Clear();
        RenderingServer.MultimeshSetBuffer(_blobMultimesh, _blobs);
        RenderingServer.MultimeshSetVisibleInstances(_blobMultimesh, _blobCount);
    }

    // Every frame: each spot on its bone, the wearer's own head spots apart on the head layer.
    public void SetSpots(ReadOnlySpan<FluidSpotRecord> spots, ReadOnlySpan<Transform3D> bones)
    {
        int count = Math.Min(spots.Length, SpotCapacity);
        for (int i = 0; i < count; i++)
        {
            var spot = spots[i];
            var main = _spots.AsSpan(i * Floats, Floats);
            var head = _headSpots.AsSpan(i * Floats, Floats);
            var bone = spot.Bone < bones.Length ? bones[(int)spot.Bone] : Transform3D.Identity;
            if (FluidResiduePass.SpotInstance(bone, spot, main))
            {
                main.CopyTo(head);
                main.Clear();
            }
            else
            {
                head.Clear();
            }
        }
        RenderingServer.MultimeshSetBuffer(_spotMultimesh, _spots);
        RenderingServer.MultimeshSetBuffer(_headMultimesh, _headSpots);
        if (count != _spotVisible)
        {
            _spotVisible = count;
            RenderingServer.MultimeshSetVisibleInstances(_spotMultimesh, count);
            RenderingServer.MultimeshSetVisibleInstances(_headMultimesh, count);
        }
    }

    public void Dispose()
    {
        foreach (var rid in new[] { _blobInstance, _spotInstance, _headInstance, _blobMultimesh, _spotMultimesh, _headMultimesh, _blobMesh })
        {
            if (rid.IsValid)
                RenderingServer.FreeRid(rid);
        }
        _blobInstance = _spotInstance = _headInstance = _blobMultimesh = _spotMultimesh = _headMultimesh = _blobMesh = default;
    }
}
