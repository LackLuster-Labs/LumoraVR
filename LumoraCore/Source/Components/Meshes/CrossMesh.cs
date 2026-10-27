// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

// Three bars through one centre, one along each axis: a marker for a point in space.
[ComponentCategory("Assets/Procedural Meshes")]
public class CrossMesh : ProceduralMesh
{
    // End to end length of each bar.
    public readonly Sync<float> Size;

    // Bar thickness as a share of Size.
    public readonly Sync<float> BarRatio;

    private PhosBox? _x, _y, _z;
    private float _size, _bar;

    public CrossMesh()
    {
        Size = new Sync<float>(this, 0.1f);
        BarRatio = new Sync<float>(this, 0.2f);
    }

    public float BarThickness => Size.Value * BarRatio.Value;

    public override void OnAwake()
    {
        base.OnAwake();
        SubscribeToChanges(Size);
        SubscribeToChanges(BarRatio);
    }

    protected override void PrepareAssetUpdateData()
    {
        _size = Size.Value;
        _bar = Size.Value * BarRatio.Value;
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        uploadHint[MeshUploadHint.Flag.Geometry] = _x == null;
        if (_x == null)
        {
            var submesh = new PhosTriangleSubmesh(mesh);
            mesh.Submeshes.Add(submesh);
            _x = new PhosBox(submesh);
            _y = new PhosBox(submesh);
            _z = new PhosBox(submesh);
        }

        _x.Size = new float3(_size, _bar, _bar);
        _y!.Size = new float3(_bar, _size, _bar);
        _z!.Size = new float3(_bar, _bar, _size);
        _x.Update();
        _y.Update();
        _z.Update();
    }

    protected override void ClearMeshData()
    {
        _x = _y = _z = null;
    }
}
