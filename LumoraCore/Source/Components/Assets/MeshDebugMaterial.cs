// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Shows a mesh's own data as colour, for finding what an import or a generator actually wrote: positions,
// vertex colours, normals, tangents and the tangents' handedness, bitangents, or any of the four UV sets, in the mesh's
// own space. The colour is the data times Scale plus Offset, clamped to 0..1, so a normal reads at Scale 0.5 and
// Offset 0.5 as the usual pastel map. Normalize shows a vector's direction alone. -xlinka
[ComponentCategory("Assets/Materials")]
public class MeshDebugMaterial : MaterialProvider
{
    public readonly Sync<MeshDebugData> Visualize;
    public readonly Sync<bool> Normalize;
    public readonly Sync<float> Scale;
    public readonly Sync<float3> Offset;
    // -1 = default
    public readonly Sync<int> RenderQueue;

    public override bool UseLoadingPlaceholder => false;

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public MeshDebugMaterial()
    {
        Visualize = new Sync<MeshDebugData>(this, MeshDebugData.Normals);
        Normalize = new Sync<bool>(this, false);
        Scale = new Sync<float>(this, 1f);
        Offset = new Sync<float3>(this, float3.Zero);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetCustomShader("res://Shaders/Mat_MeshDebug.gdshader");
        asset.SetFloat("RenderQueue", RenderQueue.Value);
        asset.SetInt("Visualize", (int)Visualize.Value);
        asset.SetBool("NormalizeData", Normalize.Value);
        asset.SetFloat("DataScale", Scale.Value);
        asset.SetFloat3("DataOffset", Offset.Value);
    }
}
