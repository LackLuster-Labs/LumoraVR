// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Interaction;

// Where a material goes on what it is put on, for the material tool and anything else that puts one on: which
// slots are never painted, which renderer a hit means, which surface a point on it is, and each surface's
// reference set as one undo record. Materials are read and written through a renderer's Materials list BY INDEX:
// MeshRenderer.Material adds an empty slot-zero entry when the list is empty, so going through it would edit the
// thing it was only supposed to look at. -xlinka
public static class MaterialSurfaces
{
    public const int MaxParentWalk = 16;

    // Not an orb (the whole point of one is to keep its material; a shader graph's object is one too), not a tool,
    // not a protected subtree.
    public static bool Paintable(Slot slot)
        => slot.GetComponent<MaterialOrb>() == null && slot.GetComponent<ShaderGraphSource>() == null
            && !ImmutableComponent.IsProtected(slot) && slot.GetComponentInParents<ToolItem>() == null;

    // Up from the hit for the renderer that owns the surface. A hit lands on the child slot a mesh happens to live
    // on far more often than on the slot somebody authored the renderer on.
    public static Component? FindRenderer(Slot? hitSlot, Func<Slot, bool> paintable)
    {
        var current = hitSlot;
        for (int depth = 0; current != null && depth < MaxParentWalk; depth++, current = current.Parent)
        {
            if (current.IsDestroyed || !paintable(current))
                return null;
            var mesh = current.GetComponent<MeshRenderer>();
            if (mesh != null && !mesh.IsDestroyed)
                return mesh;
            var skinned = current.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null && !skinned.IsDestroyed)
                return skinned;
        }
        return null;
    }

    // Every renderer at and under an object a material could go on, by the rules a swap keeps to.
    public static List<Component> RenderersUnder(Slot scope, Func<Slot, bool> paintable)
    {
        var found = new List<Component>();
        Collect(scope, MaterialPaintPolicy.Allows(scope));
        return found;

        void Collect(Slot slot, bool allowed)
        {
            if (slot.IsDestroyed || slot.IsLocalElement || !paintable(slot) || slot.GetComponent<UserRoot>() != null)
                return;
            if (slot.GetComponent<MaterialPaintPolicy>() is { IsDestroyed: false } policy && policy.Enabled.Value)
                allowed = policy.AllowPaint.Value;
            if (allowed)
            {
                foreach (var component in slot.Components)
                {
                    if (component is MeshRenderer or SkinnedMeshRenderer && !component.IsDestroyed)
                        found.Add(component);
                }
            }
            foreach (var child in slot.Children)
                Collect(child, allowed);
        }
    }

    // The before and after are RefIDs, because a reference IS a RefID field, which is exactly what the shared
    // field record restores.
    public static IUndoBatch? AssignAt(AssetRef<MaterialAsset> element, IAssetProvider<MaterialAsset> material)
    {
        if (element == null || element.IsDestroyed)
            return null;
        var field = (IField)element;
        object? before = field.BoxedValue;
        element.Target = material;
        object? after = field.BoxedValue;
        if (Equals(before, after))
            return null;
        return new FieldEditUndoBatch(field, before, after, UndoLocale.ApplyMaterial);
    }

    // Puts the material on one surface, or on every surface for -1, handing each change to record.
    public static void Apply(Component renderer, int surface, IAssetProvider<MaterialAsset> material, Action<IUndoBatch> record)
    {
        void Add(IUndoBatch? batch)
        {
            if (batch != null)
                record(batch);
        }

        switch (renderer)
        {
            case MeshRenderer mesh:
                if (mesh.Materials.Count == 0)
                {
                    // A renderer with no surface entries draws nothing at all. Painting it gives it its first one,
                    // which is a list change, so undo takes the entry back out.
                    Add(ListElementUndoBatch.Add(mesh.Materials, out var added));
                    if (added is AssetRef<MaterialAsset> first)
                        Add(AssignAt(first, material));
                    return;
                }
                for (int i = 0; i < mesh.Materials.Count; i++)
                {
                    if (surface < 0 || i == surface)
                        Add(AssignAt(mesh.Materials.GetElement(i), material));
                }
                break;

            case SkinnedMeshRenderer skinned:
                // A skinned renderer has its single material and the per-surface list over it.
                if (surface < 0 || (surface == 0 && skinned.Materials.Count == 0))
                    Add(AssignAt(skinned.Material, material));
                for (int i = 0; i < skinned.Materials.Count; i++)
                {
                    if (surface < 0 || i == surface)
                        Add(AssignAt(skinned.Materials.GetElement(i), material));
                }
                break;
        }
    }

    public static int SurfaceCount(Component renderer) => renderer switch
    {
        MeshRenderer mesh => mesh.Materials.Count,
        SkinnedMeshRenderer skinned => System.Math.Max(1, skinned.Materials.Count),
        _ => 0,
    };

    public static IAssetProvider<MaterialAsset>? MaterialAt(Component renderer, int surface) => renderer switch
    {
        MeshRenderer mesh => surface >= 0 && surface < mesh.Materials.Count ? mesh.Materials.GetElement(surface).Target : null,
        SkinnedMeshRenderer skinned => skinned.MaterialFor(surface),
        _ => null,
    };

    // Which surface a point is on. A laser reports a collider and a point, never a triangle, so on a mesh collider it
    // is the submesh holding the triangle nearest the point. A renderer with one surface has that one, and anything
    // else gets every surface: a box or a sphere collider says nothing about where on a multi-material mesh it was
    // hit.
    public static int SurfaceUnder(Component renderer, Slot hitSlot, float3 point)
    {
        int count = SurfaceCount(renderer);
        if (count == 1)
            return 0;
        var collider = hitSlot.GetComponent<MeshCollider>();
        if (collider?.Mesh.Target is MeshProvider provider && provider.Asset?.MeshData is { } mesh)
        {
            int submesh = NearestSubmesh(mesh, collider.Slot.GlobalPointToLocal(point));
            if (submesh >= 0 && submesh < count)
                return submesh;
        }
        return -1;
    }

    private static int NearestSubmesh(PhosMesh mesh, float3 point)
    {
        var positions = mesh.RawPositions;
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int s = 0; s < mesh.Submeshes.Count; s++)
        {
            var submesh = mesh.Submeshes[s];
            if (submesh == null || submesh.Topology != PhosTopology.Triangles)
                continue;
            var indices = submesh.RawIndices;
            for (int i = 0; i + 2 < submesh.IndexCount; i += 3)
            {
                float distance = DistanceSquaredToTriangle(point, positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = s;
                }
            }
        }
        return best;
    }

    // The closest point on a triangle by its Voronoi regions, squared distance to it.
    private static float DistanceSquaredToTriangle(float3 p, float3 a, float3 b, float3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = float3.Dot(ab, ap), d2 = float3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f)
            return (p - a).LengthSquared;

        var bp = p - b;
        float d3 = float3.Dot(ab, bp), d4 = float3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3)
            return (p - b).LengthSquared;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            return (p - (a + ab * (d1 / (d1 - d3)))).LengthSquared;

        var cp = p - c;
        float d5 = float3.Dot(ab, cp), d6 = float3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6)
            return (p - c).LengthSquared;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            return (p - (a + ac * (d2 / (d2 - d6)))).LengthSquared;

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return (p - (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).LengthSquared;

        float denominator = 1f / (va + vb + vc);
        return (p - (a + ab * (vb * denominator) + ac * (vc * denominator))).LengthSquared;
    }

    // The object a hit belongs to: the nearest slot marked as an object root, else the top-level slot it sits under.
    // Slot.ObjectRoot answers the world root when nothing is marked, which would quietly turn one object into the
    // whole scene.
    public static Slot? ObjectOf(Slot hit)
    {
        for (var current = hit; current != null && !current.IsRootSlot; current = current.Parent)
        {
            if (current.GetComponent<ObjectRoot>() != null)
                return current;
        }
        var top = hit;
        while (top.Parent != null && !top.Parent.IsRootSlot)
            top = top.Parent;
        return top.IsRootSlot ? null : top;
    }

    // Every material reference under scope a swap may change: renderers, particles, UI, a sky, a switch, whatever
    // wears one. Skips orbs, shader graph objects, tools, protected subtrees, people, slots a paint policy closes,
    // local slots (a change there would stay on one machine) and the given slot. The scope's own ancestry is asked
    // once; below it each slot only looks at itself, so a scene swap stays one pass over the world.
    public static void CollectReferences(Slot scope, Slot? skip, List<AssetRef<MaterialAsset>> into)
    {
        if (!Paintable(scope) || (skip != null && (ReferenceEquals(scope, skip) || scope.IsDescendantOf(skip))))
            return;
        CollectReferences(scope, skip, MaterialPaintPolicy.Allows(scope), into);
    }

    private static void CollectReferences(Slot slot, Slot? skip, bool allowed, List<AssetRef<MaterialAsset>> into)
    {
        if (slot.IsDestroyed || slot.IsLocalElement || slot.GetComponent<UserRoot>() != null
            || slot.GetComponent<MaterialOrb>() != null || slot.GetComponent<ShaderGraphSource>() != null
            || slot.GetComponent<ToolItem>() != null || ImmutableComponent.IsProtected(slot)
            || (skip != null && ReferenceEquals(slot, skip)))
            return;
        if (slot.GetComponent<MaterialPaintPolicy>() is { IsDestroyed: false } policy && policy.Enabled.Value)
            allowed = policy.AllowPaint.Value;
        if (allowed)
        {
            foreach (var component in slot.Components)
            {
                for (int i = 0; i < component.SyncMemberCount; i++)
                    CollectReferences(component.GetSyncMember(i), into);
            }
        }
        foreach (var child in slot.Children)
            CollectReferences(child, skip, allowed, into);
    }

    private static void CollectReferences(ISyncMember? member, List<AssetRef<MaterialAsset>> into)
    {
        switch (member)
        {
            case AssetRef<MaterialAsset> reference:
                into.Add(reference);
                break;
            case ISyncList list:
                foreach (var element in list.Elements)
                    CollectReferences(element as ISyncMember, into);
                break;
            case ISyncDictionary dictionary:
                foreach (var value in dictionary.Values)
                    CollectReferences(value as ISyncMember, into);
                break;
            case ISyncObject syncObject:
                foreach (var child in syncObject.SyncMembers)
                    CollectReferences(child, into);
                break;
        }
    }
}
