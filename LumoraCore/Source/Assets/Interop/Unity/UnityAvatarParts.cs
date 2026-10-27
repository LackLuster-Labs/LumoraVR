// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Components;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Assets.Interop.Unity;

// A scene avatar is rarely one model. Outfits, eye meshes and props are model files of their own placed
// under the body's bones or under objects the project added, each with changes of its own. The body comes
// in first; every other model is imported where its placement hangs, posed the way the placement poses it,
// and joins one index, so a script that points into an outfit finds the outfit's bone. Names the project
// gave the model's objects go on afterwards, since the index knows them by the file's own names, and so do
// the shape weights it saved, once the meshes they belong to have loaded. -xlinka
public static partial class UnityAvatarBuilder
{
    public sealed class Assembly
    {
        internal NodeIndex Nodes = null!;
        public float Unit = 1f;
        public UnityImportCandidate Candidate = null!;
        public Slot Model = null!;
        // Renderer slots by the model they belong to, under the name the file gave them.
        internal readonly List<(UnityModelPart? Part, UnityPackageAsset File, UnityModelMeta Meta, Dictionary<Slot, string> Names)> Models = new();
    }

    // The main model is in; index it and give it what the placement changed. World thread.
    public static Assembly Assemble(UnityImportCandidate candidate, Slot model, Slot? nodeRoot, UnityModelMeta meta)
    {
        float unit = MathF.Abs(model.LocalScale.Value.x);
        if (unit < 1e-6f)
            unit = 1f;
        var assembly = new Assembly
        {
            Nodes = new NodeIndex(nodeRoot, meta, 1f / unit, candidate.NodesByPath),
            Unit = unit,
            Candidate = candidate,
            Model = model,
        };
        assembly.Nodes.SetMain(model, unit, candidate.AuthoredRoot);
        if (candidate.AuthoredRoot && nodeRoot != null)
            nodeRoot.SlotName.Value = candidate.ModelName;
        ApplyPoseOverrides(candidate.TransformOverrides, assembly.Nodes.ById, unit);
        assembly.Models.Add((null, candidate.Model, meta, Names(nodeRoot)));
        Rename(candidate.NamesById, assembly.Nodes.ById);
        return assembly;
    }

    // Where another placed model goes: the slot its placement hangs from. World thread.
    public static Slot? PlaceFor(Assembly assembly, UnityModelPart part)
    {
        var parent = part.Root.Node?.Parent;
        return parent != null && assembly.Nodes.Resolve(parent) is { } slot ? assembly.Nodes.Inside(slot) : null;
    }

    // Another placed model is in, under the slot PlaceFor gave. World thread.
    public static void AddPart(Assembly assembly, UnityModelPart part, Slot model, Slot? nodeRoot)
    {
        float unit = MathF.Abs(model.LocalScale.Value.x);
        if (unit < 1e-6f)
            unit = 1f;
        var root = part.Root;
        model.SlotName.Value = string.IsNullOrEmpty(root.Name) ? model.SlotName.Value : root.Name;
        var scale = new float3(MathF.Abs(root.Scale.x), MathF.Abs(root.Scale.y), MathF.Abs(root.Scale.z));
        if (model.Parent is { } parent)
        {
            var ratio = scale * (unit / assembly.Nodes.UnitOf(parent));
            // A file with one top node has that node folded into the model's root in the project, so the
            // placement's pose is that node's, in place of the node's own.
            if (nodeRoot is { ChildCount: 1 } && nodeRoot.Children.First() is { } lone)
                assembly.Nodes.Pose(lone, parent, root.Position, root.Rotation, ratio);
            else
                assembly.Nodes.Pose(model, parent, root.Position, root.Rotation, ratio);
        }
        model.ActiveSelf.Value = root.Active;
        assembly.Nodes.AddPart(part, model, nodeRoot, unit);
        ApplyPoseOverrides(part.TransformOverrides, id => assembly.Nodes.PartById(part, id), unit);
        assembly.Models.Add((part, part.Model, part.Meta, Names(nodeRoot)));
        Rename(part.NamesById, id => assembly.Nodes.PartById(part, id));
    }

    // The objects the project added, made in order, so animation paths and switches find every one of them.
    public static void Complete(Assembly assembly)
    {
        int missed = 0;
        foreach (var node in assembly.Candidate.AddedObjects)
            if (assembly.Nodes.Resolve(node) == null)
                missed++;
        if (missed > 0)
            Logger.Debug($"UnityPackage: {missed} added objects found nowhere to hang");
        assembly.Nodes.SnapshotPaths();
    }

    private static Dictionary<Slot, string> Names(Slot? nodeRoot)
    {
        var names = new Dictionary<Slot, string>();
        if (nodeRoot == null)
            return names;
        var stack = new Stack<Slot>();
        stack.Push(nodeRoot);
        while (stack.Count > 0)
        {
            var slot = stack.Pop();
            names[slot] = slot.SlotName.Value ?? "";
            foreach (var child in slot.Children)
                stack.Push(child);
        }
        return names;
    }

    private static void Rename(Dictionary<long, string> names, Func<long, Slot?> byId)
    {
        foreach (var (id, name) in names)
        {
            if (UnityModelPart.IsRootId(id) || string.IsNullOrEmpty(name))
                continue;
            if (byId(id) is { } slot && slot.SlotName.Value != name)
                slot.SlotName.Value = name;
        }
    }

    // ---- shape weights ----

    // The weights each renderer starts at: the file's own saved ones when the project took them, then the
    // ones the placement set, by the file's numbering. Shapes left at zero need nothing.
    public static List<(SkinnedMeshRenderer Renderer, List<(string Name, string Shape, float Weight)> Shapes)> ShapeWeights(Assembly assembly)
    {
        var result = new List<(SkinnedMeshRenderer, List<(string, string, float)>)>();
        foreach (var (part, file, meta, names) in assembly.Models)
        {
            var saved = part?.ShapeWeights ?? assembly.Candidate.ShapeWeights;
            var byName = part == null ? assembly.Candidate.ShapeWeightsByName : null;
            if (saved.Count == 0 && byName is not { Count: > 0 } && !meta.ShapeDefaults)
                continue;
            if (file.File == null)
                continue;
            var order = UnityFbxShapes.Read(file.File);
            if (order.Count == 0)
                continue;
            Func<long, Slot?> byId = part == null ? assembly.Nodes.ById : id => assembly.Nodes.PartById(part, id);

            var byRenderer = new Dictionary<Slot, Dictionary<int, float>>();
            foreach (var (id, weights) in saved)
            {
                if (byId(id) is not { } slot)
                    continue;
                if (!byRenderer.TryGetValue(slot, out var map))
                    byRenderer[slot] = map = new Dictionary<int, float>();
                foreach (var (index, weight) in weights)
                    map[index] = weight;
            }
            if (byName != null)
                foreach (var (name, weights) in byName)
                {
                    if (assembly.Nodes.ByName(name) is not { } slot || byRenderer.ContainsKey(slot))
                        continue;
                    byRenderer[slot] = new Dictionary<int, float>(weights);
                }
            if (meta.ShapeDefaults)
                foreach (var slot in names.Keys)
                    if (names.TryGetValue(slot, out var n) && order.ContainsKey(n))
                        byRenderer.TryAdd(slot, new Dictionary<int, float>());

            foreach (var (slot, overrides) in byRenderer)
            {
                if (!names.TryGetValue(slot, out var fileName) || !order.TryGetValue(fileName, out var shapes))
                    continue;
                var renderer = slot.GetComponent<SkinnedMeshRenderer>() ?? slot.GetComponentInChildren<SkinnedMeshRenderer>();
                if (renderer == null)
                    continue;
                var wanted = new List<(string, string, float)>();
                for (int i = 0; i < shapes.Count; i++)
                {
                    float weight = meta.ShapeDefaults ? shapes[i].Weight : 0f;
                    if (overrides.TryGetValue(i, out var set))
                        weight = set;
                    if (MathF.Abs(weight) > 1e-4f)
                        wanted.Add((shapes[i].Name, shapes[i].ShapeName, System.Math.Clamp(weight / 100f, 0f, 1f)));
                }
                if (wanted.Count > 0)
                    result.Add((renderer, wanted));
            }
        }
        return result;
    }

    // Writes the weights once a renderer's mesh has loaded and its shapes have indices. World thread.
    public static bool TryWriteShapeWeights(SkinnedMeshRenderer renderer, List<(string Name, string Shape, float Weight)> shapes)
    {
        if (renderer.IsDestroyed)
            return true;
        var asset = renderer.MeshAsset.Asset;
        if (asset?.MeshData == null)
            return false;
        foreach (var (name, shape, weight) in shapes)
        {
            int index = asset.BlendShapeIndex(name);
            if (index < 0)
                index = asset.BlendShapeIndex(shape);
            if (index < 0)
                index = asset.BlendShapeIndex(name + "." + shape);
            if (index < 0)
                continue;
            while (renderer.BlendShapeWeights.Count <= index)
                renderer.BlendShapeWeights.Add(0f);
            renderer.SetBlendShapeWeight(index, weight);
        }
        return true;
    }
}
