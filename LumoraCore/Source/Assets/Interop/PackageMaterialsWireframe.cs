// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;

namespace Lumora.Core.Assets.Interop;

// WIREFRAMES. The package draws a wireframe's lines from triangles its shader builds on the fly; ours reads each
// triangle's corners from the vertex colours, which a mesh only carries once it is unwelded and baked
// (PhosBarycentrics). So every imported mesh a wireframe is worn on is loaded baked. The bake replaces the
// mesh's own vertex colours, so a mesh another material reads colours from keeps them: the renderers that wear
// only wires move to a baked copy of the same file, and one wearing both keeps its colours and is reported.
// Skinned meshes are left as they are, since the bake does not carry bones. -xlinka
public static partial class PackageMemberMapper
{
    internal static void FinishWireframesMats(IEnumerable<IWorldElement> built)
    {
        var wearers = new Dictionary<Component, List<MeshRenderer>>(ReferenceEqualityComparer.Instance);
        foreach (var element in built)
        {
            if (element is SkinnedMeshRenderer skinned && !skinned.IsDestroyed
                && (WearsWiresMats(skinned.Materials) || skinned.Material.Target is WireframeMaterial { UseVertexBarycentric.Value: true }))
                Logging.Logger.Warn($"Package import: a wireframe on the skinned mesh at '{skinned.Slot?.Name}' draws its fill only; skinned meshes are not unwelded here.");
            if (element is not MeshRenderer renderer || renderer.IsDestroyed || renderer.Mesh.Target is not Component mesh)
                continue;
            if (!wearers.TryGetValue(mesh, out var list))
                wearers[mesh] = list = new List<MeshRenderer>();
            list.Add(renderer);
        }

        foreach (var (mesh, renderers) in wearers)
        {
            if (!renderers.Exists(r => WearsWiresMats(r.Materials)))
                continue;
            if (!renderers.Exists(r => ReadsVertexColoursMats(r.Materials)))
            {
                SetBakedMats(mesh);
                continue;
            }

            MeshProvider? copy = null;
            foreach (var renderer in renderers)
            {
                if (!WearsWiresMats(renderer.Materials))
                    continue;
                if (ReadsVertexColoursMats(renderer.Materials) || mesh is not MeshProvider file)
                {
                    Logging.Logger.Warn($"Package import: the wireframe on '{renderer.Slot?.Name}' shares its mesh with a material that reads vertex colours; it draws its fill only.");
                    continue;
                }
                if (copy == null)
                {
                    copy = file.Slot.AttachComponent<MeshProvider>();
                    copy.URL.Value = file.URL.Value;
                    copy.ImportScale.Value = file.ImportScale.Value;
                    copy.KeepReadable.Value = file.KeepReadable.Value;
                    copy.MeshIndex.Value = file.MeshIndex.Value;
                    copy.WireframeBarycentrics.Value = true;
                }
                renderer.Mesh.Target = copy;
            }
        }
    }

    private static void SetBakedMats(Component mesh)
    {
        switch (mesh)
        {
            case MeshProvider file:
                file.WireframeBarycentrics.Value = true;
                break;
            case ProceduralMesh procedural:
                procedural.WireframeBarycentrics.Value = true;
                break;
        }
    }

    private static bool WearsWiresMats(SyncAssetList<MaterialAsset> materials)
    {
        foreach (var slot in materials)
            if (slot.Target is WireframeMaterial { UseVertexBarycentric.Value: true })
                return true;
        return false;
    }

    // A material other than the wires that takes colour from the vertices: a vertex colour switch that is on,
    // or a vertex colour target other than none.
    private static bool ReadsVertexColoursMats(SyncAssetList<MaterialAsset> materials)
    {
        foreach (var slot in materials)
        {
            if (slot.Target is not Component material || material is WireframeMaterial)
                continue;
            foreach (var field in material.GetType().GetFields())
            {
                var value = field.GetValue(material);
                if (field.Name is "UseVertexColor" or "UseVertexColors" && value is Sync<bool> { Value: true })
                    return true;
                if (field.Name == "VertexColors" && value is Sync<VertexColorTarget> target && target.Value != VertexColorTarget.None)
                    return true;
            }
        }
        return false;
    }
}
