// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Logging;
using Lumora.Core.Persistence;

namespace Lumora.Core.Assets.Interop;

// A packaged avatar's voice-driven mouth: the analyser that listens to the wearer's voice, and the driver that
// names one shape of the face per viseme. Ours are the same two parts under our names; the driver's Source and
// StrengthMultiplier carry across by name, its shapes in FinishVisemes. -xlinka
public static partial class PackageTypeMap
{
    static partial void AliasesFace(Dictionary<string, string> aliases)
    {
        aliases["VisemeAnalyzer"] = "LipSyncAnalyzer";
        aliases["DirectVisemeDriver"] = "VisemeWeightDriver";
        // Face tracking onto named mouth shapes: ours keeps the same list, mesh by mesh (FinishExpressions).
        aliases["AvatarExpressionDriver"] = "MouthExpressionDriver";
        // The eye manager's timing and glances and the eye shape driver's lids, widen, squeeze and pupils are read
        // into our blink, gaze and eye-expression drivers on the avatar (FinishEyes).
        Carried.Add("EyeManager");
        Carried.Add("EyeLinearDriver");
    }
}

public static partial class PackageObjectImporter
{
    // The shape names a skinned renderer reads its weights by, from the mesh it was built with, as the mesh hook
    // writes them once the mesh lands. Until then every weight draws as zero, the creator's body sliders
    // included, and a face driver that looks for its shapes on an avatar worn straight after import finds none
    // and stops looking. The weights the package stored are kept and sized to the shapes, as the hook does.
    // -xlinka
    private static void FinishShapeNames(BuildContext context)
    {
        foreach (var element in context.ById.Values)
        {
            if (element is not SkinnedMeshRenderer renderer || renderer.IsDestroyed || renderer.BlendShapeNames.Count > 0
                || MeshUrlOf(renderer) is not { } url || !context.MeshShapeNames.TryGetValue(url, out var shapes))
                continue;
            foreach (var shape in shapes)
                renderer.BlendShapeNames.Add(shape);
            while (renderer.BlendShapeWeights.Count > shapes.Length)
                renderer.BlendShapeWeights.RemoveAt(renderer.BlendShapeWeights.Count - 1);
            while (renderer.BlendShapeWeights.Count < shapes.Length)
                renderer.BlendShapeWeights.Add(0f);
        }
    }

    // The package's viseme members in the order ours lists shape names: silence, PP to ou, then laughter.
    private static readonly string[] PackageVisemes =
        { "Silence", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou", "Laugh" };

    // A package's mouth names each viseme's shape by driving that entry of a renderer's weights, so it finds its
    // shapes by index, and a face authored with the bare viseme codes ("AA", "PP") carries no name ours looks for.
    // Ours names the shape: the entry's index read through the mesh's own shape list. A mouth can also drive the
    // value of a named-shape driver's entry instead, which passes it on to the shape of that name on its renderer;
    // that entry names the shape itself. One driver of ours takes one mesh, so drives spread over several
    // renderers get a driver each, beside the first. A viseme that names nothing stays empty and the mouth works
    // only the shapes the package named, as it does there. -xlinka
    private static void FinishVisemes(BuildContext context)
    {
        if (context.Visemes.Count == 0)
            return;
        var named = NamedShapeEntries(context);
        foreach (var (driver, data) in context.Visemes)
        {
            if (driver.IsDestroyed)
                continue;

            var byMesh = new List<(SkinnedMeshRenderer Mesh, string[] Names)>();
            for (int v = 0; v < PackageVisemes.Length; v++)
            {
                if (ReadId(data, PackageVisemes[v]) is not { } id || ShapeOf(context, named, id) is not var (renderer, shape))
                    continue;

                int at = byMesh.FindIndex(entry => ReferenceEquals(entry.Mesh, renderer));
                if (at < 0)
                {
                    byMesh.Add((renderer, new string[PackageVisemes.Length]));
                    at = byMesh.Count - 1;
                }
                byMesh[at].Names[v] = shape;
            }

            for (int m = 0; m < byMesh.Count; m++)
            {
                var (mesh, names) = byMesh[m];
                var target = driver;
                if (m > 0)
                {
                    target = driver.Slot.AttachComponent<VisemeWeightDriver>();
                    target.Source.Target = driver.Source.Target;
                    target.StrengthMultiplier.Value = driver.StrengthMultiplier.Value;
                }
                target.ShapeMesh.Target = mesh;
                target.ShapeNames.Clear();
                var landed = new List<string>();
                for (int v = 0; v < names.Length; v++)
                {
                    target.ShapeNames.Add(names[v] ?? string.Empty);
                    if (names[v] != null)
                        landed.Add($"{PackageVisemes[v]}={names[v]}");
                }
                target.ExactShapes.Value = true;
                Logger.Log($"Package import: visemes on '{mesh.Slot?.SlotName.Value}' named from the package, {landed.Count} shape(s): {string.Join(" ", landed)}");
            }
        }
    }

    // The renderer and shape a drive lands on: an entry of a renderer's weights read through its mesh, or the value
    // of a named-shape driver's entry.
    private static (SkinnedMeshRenderer Renderer, string Shape)? ShapeOf(BuildContext context,
        Dictionary<string, (string Renderer, string Shape)> named, string id)
    {
        if (context.Graphs.Members.TryGetValue(id, out var record))
        {
            if (record.Owner is SkinnedMeshRenderer renderer && !renderer.IsDestroyed && record.Member == "BlendShapeWeights"
                && record.Index >= 0 && MeshUrlOf(renderer) is { } url && context.MeshShapeNames.TryGetValue(url, out var shapes)
                && record.Index < shapes.Length)
                return (renderer, shapes[record.Index]);
            return null;
        }
        if (named.TryGetValue(id, out var entry) && entry.Shape.Length > 0
            && context.ById.TryGetValue(entry.Renderer, out var element) && element is SkinnedMeshRenderer target && !target.IsDestroyed)
            return (target, entry.Shape);
        return null;
    }

    // Each named-shape driver entry by the id of its value, with the renderer it drives and the shape it names.
    private static Dictionary<string, (string Renderer, string Shape)> NamedShapeEntries(BuildContext context)
    {
        var entries = new Dictionary<string, (string Renderer, string Shape)>(StringComparer.OrdinalIgnoreCase);
        foreach (var data in context.NamedShapeDrivers)
        {
            if (ReadId(data, "Renderer") is not { } renderer
                || (data.TryGetNode("BlendShapes") as DataTreeDictionary)?.TryGetNode("Data") is not DataTreeList list)
                continue;
            foreach (var node in list)
            {
                if (node is DataTreeDictionary entry
                    && (entry.TryGetNode("Value") as DataTreeDictionary)?.TryGetNode("ID") is DataTreeValue { Value: string valueId }
                    && (entry.TryGetNode("BlendShapeName") as DataTreeDictionary)?.TryGetNode("Data") is DataTreeValue { Value: string shape })
                    entries[valueId] = (renderer, shape);
            }
        }
        return entries;
    }
}
