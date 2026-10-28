// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Numerics;
using Lumora.Core.Components.Import;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

public static partial class ModelImporter
{
    // VERTEX COLOURS, for models whose look is in their vertices: low poly, scans, photogrammetry.
    //
    // Every material reads a vertex colour as linear light. glTF stores it that way by its spec; every other format
    // here (PLY, OBJ, FBX, Collada and the rest) stores the colour the modelling tool showed, which is sRGB, so those
    // are decoded on the way in. Left as written, a mid grey of 128 reads as 0.5 of linear light instead of 0.22 and
    // every colour on the model comes out washed pale. glTF's three-component colour also reaches us with an alpha of
    // zero: a mesh whose every vertex is fully see-through is a colour with no alpha written, never a model meant to
    // vanish, so it is made opaque. Done on the parse, before any mesh is converted, so a part baked alone and a part
    // merged with its neighbours get the same colours. -xlinka
    private static void PrepareVertexColors(Assimp.Scene? scene, string hint)
    {
        if (scene == null || !scene.HasMeshes)
            return;
        bool linear = hint is ".glb" or ".gltf";
        foreach (var mesh in scene.Meshes)
        {
            if (mesh == null || !mesh.HasVertexColors(0) || mesh.VertexColorChannels[0] is not { Count: > 0 } colors)
                continue;
            bool noAlpha = true;
            for (int i = 0; i < colors.Count && noAlpha; i++)
                noAlpha = colors[i].W == 0f;
            if (linear && !noAlpha)
                continue;
            for (int i = 0; i < colors.Count; i++)
            {
                var c = colors[i];
                if (!linear)
                    c = new Vector4(ColorSpace.GammaToLinear(c.X), ColorSpace.GammaToLinear(c.Y), ColorSpace.GammaToLinear(c.Z), c.W);
                if (noAlpha)
                    c.W = 1f;
                colors[i] = c;
            }
        }
    }

    // Which materials draw a mesh that has colours. A material no coloured mesh uses keeps its kind, so a file with
    // no colours at all imports exactly as a plain import would.
    private static void MarkVertexColored(Assimp.Scene scene, Dictionary<int, ResolvedMaterial?> materials, string filePath)
    {
        int marked = 0;
        foreach (var mesh in scene.Meshes)
        {
            if (mesh == null || !mesh.HasVertexColors(0) || !materials.TryGetValue(mesh.MaterialIndex, out var rm) || rm == null || rm.VertexColored)
                continue;
            rm.VertexColored = true;
            marked++;
        }
        if (marked == 0)
            Logger.Log($"ModelImporter: '{System.IO.Path.GetFileName(filePath)}' has no vertex colours; its materials stay as they are");
    }

    // The vertex colour form of a kind, out of the full PBS build, the way ConvertMaterial converts. PBS Metallic
    // becomes PBS_VertexColor, the material made for this. That one draws front faces only and has no specular
    // workflow, so a dual sided import, the Dual Sided kind and the Specular kind take PBS_TwoSided with its colours on
    // the albedo, which keeps both. Toon turns on its own vertex colour option (Unlit does at its build). Triplanar and
    // Wireframe have no vertex colour form: null, and they build as they always have. -xlinka
    private static MaterialProvider? WithVertexColors(PBS_Metallic pbs, ModelMaterialType type)
    {
        switch (type)
        {
            case ModelMaterialType.Lit when pbs.Culling.Value == Culling.Back:
                return Replace(pbs, pbs.Slot.AttachComponent<PBS_VertexColor>());
            case ModelMaterialType.Lit:
            case ModelMaterialType.DualSided:
            {
                var twoSided = pbs.Slot.AttachComponent<PBS_TwoSided>();
                Replace(pbs, twoSided);
                twoSided.VertexColors.Value = VertexColorTarget.Albedo;
                twoSided.Culling.Value = Culling.None;
                return twoSided;
            }
            case ModelMaterialType.Specular:
            {
                var specular = ConvertMaterial(pbs, type);
                var twoSided = specular.Slot.AttachComponent<PBS_TwoSided>();
                Replace(specular, twoSided);
                twoSided.Workflow.Value = PBSWorkflow.Specular;
                twoSided.VertexColors.Value = VertexColorTarget.Albedo;
                return twoSided;
            }
            case ModelMaterialType.Toon:
            case ModelMaterialType.ToonOutline:
            {
                var toon = ConvertMaterial(pbs, type);
                if (toon is XiexeToonMaterial xiexe)
                    xiexe.UseVertexColors.Value = true;
                return toon;
            }
            default:
                return null;
        }
    }

    private static MaterialProvider Replace(MaterialProvider from, MaterialProvider to)
    {
        MaterialConversion.CopyProperties(from, to);
        from.Destroy();
        return to;
    }
}
