// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components.Gizmos;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Interaction;

// WHAT A SELECT PRESS POINTS AT
//
// The slot under the beam, whatever it is. The laser's hover only knows things it can act on, so a select that
// read the hover alone could not pick a floor, a wall or any prop nobody made grabbable: the ground of a fresh
// world was out of reach. In order:
//   - the hovered target, the exact slot the beam landed on (a handle the tool pulled forward ends it there);
//   - else the first solid surface the cast stopped on, the slot of that collider and not some container above it;
//   - a drawn mesh with no collider in front of either, tested against its triangles. One lying on the surface
//     behind it (a decal, the glow painted on a floor) is part of that surface: pointing there means the floor;
//   - in VR, an origin within a hand's width of the tip, nearer than anything on the beam. Lights, sounds, empty
//     groups and the inside of a volume have nothing to point at, so the tip pokes them instead.
// Refusals are not decided here; DevToolItem judges whatever this hands it. -xlinka
internal static class DevToolPick
{
    private const float SurfaceClearance = 0.02f;
    private const float SheetThickness = 0.01f;
    private const float SheetLift = 0.1f;
    private const float TipReach = 0.1f;
    // A bigger mesh is taken as its box, so a press never stalls on a dense import.
    private const int MaxTestedTriangles = 250_000;

    public static Slot? Pick(InteractionLaser? laser, bool tipPokes)
    {
        var world = laser?.World;
        if (laser == null || laser.IsDestroyed || world?.RootSlot == null || laser.YieldedToOtherSpace)
            return null;

        var origin = laser.CastOrigin;
        var direction = laser.CastDirection;
        if (direction.LengthSquared < 1e-8f)
            return null;
        direction = direction.Normalized;
        float scale = UserScale(laser);

        Slot? best = null;
        float bestDistance = MathF.Max(laser.MaxDistance.Value, 0.01f);
        float3 surfaceNormal = float3.Zero;

        if (laser.CurrentHitSlot is { IsDestroyed: false } hovered)
        {
            if (laser.CurrentHitPreferred)
                return hovered;
            best = hovered;
            bestDistance = (laser.CurrentHitPoint - origin).Length;
            if (ReferenceEquals(hovered, laser.CurrentColliderHitSlot))
                surfaceNormal = laser.CurrentColliderHitNormal;
        }
        else if (laser.CurrentColliderHitSlot is { IsDestroyed: false } surface)
        {
            best = surface;
            bestDistance = (laser.CurrentColliderHitPoint - origin).Length;
            surfaceNormal = laser.CurrentColliderHitNormal;
        }

        var search = new Search(origin, direction, bestDistance, tipPokes ? laser.Slot.GlobalPosition : (float3?)null, TipReach * scale);
        search.Walk(world.RootSlot, isRoot: true);

        if (search.Drawn != null && (best == null || !LiesOn(search, bestDistance, direction, surfaceNormal, scale)))
        {
            best = search.Drawn;
            bestDistance = search.DrawnDistance;
        }

        // The cast starts at the tip in VR, so the distances on the beam and the poke are measured from one point.
        if (search.Poked != null && search.PokedDistance < bestDistance)
            best = search.Poked;
        return best;
    }

    // Whether the drawn hit is part of the surface behind it rather than something in front of it. Within a
    // couple of centimetres it is the same surface. A flat sheet held up off a solid one (the spawn glow sits two
    // centimetres over the floor, a decal a hair off its wall) belongs to it up to a hand's width. A thing with
    // depth of its own in front of the surface is its own thing. Without a solid surface's normal (a panel, a
    // grab ball) only the distance along the beam can say it.
    private static bool LiesOn(Search search, float surfaceDistance, float3 direction, float3 normal, float scale)
    {
        float gap = surfaceDistance - search.DrawnDistance;
        if (normal.LengthSquared <= 1e-8f)
            return gap <= SurfaceClearance * scale;
        normal = normal.Normalized;
        gap *= MathF.Abs(float3.Dot(direction, normal));
        if (gap <= SurfaceClearance * scale)
            return true;
        return gap <= SheetLift * scale && search.Thickness(normal) <= SheetThickness * scale;
    }

    private static float UserScale(InteractionLaser laser)
    {
        var scale = laser.Slot?.ActiveUserRoot?.Slot?.GlobalScale ?? float3.One;
        float s = (MathF.Abs(scale.x) + MathF.Abs(scale.y) + MathF.Abs(scale.z)) / 3f;
        return s > 1e-4f ? s : 1f;
    }

    // One walk of the tree for both questions. Your own body is left out (the beam passes it, and your beam and
    // cursor hang there too), and so is gizmo chrome, which the laser already offers on its own terms.
    private sealed class Search
    {
        private readonly float3 _origin;
        private readonly float3 _direction;
        private readonly float3? _tip;
        private readonly float _reach;

        public Slot? Drawn;
        public float DrawnDistance;
        private BoundingBox _drawnBox;
        public Slot? Poked;
        public float PokedDistance;
        private int _pokedComponents;

        public Search(float3 origin, float3 direction, float maxDistance, float3? tip, float reach)
        {
            _origin = origin;
            _direction = direction;
            _tip = tip;
            _reach = reach;
            DrawnDistance = maxDistance;
            PokedDistance = float.PositiveInfinity;
        }

        public void Walk(Slot slot, bool isRoot)
        {
            if (slot.IsDestroyed || !slot.ActiveSelf.Value)
                return;
            // A local slot straight under the world root is this machine's own overlay (a notice, the viewfinder,
            // the script drawings) and stands for nothing; the beam goes through it to what is behind.
            if (slot.IsLocalElement && slot.Parent is { IsRootSlot: true })
                return;

            var components = slot.Components;
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] is SlotGizmo or ComponentGizmo || components[i] is UserRoot { IsLocalUserRoot: true })
                    return;
            }

            if (!isRoot)
            {
                // A local renderer (the mesh a world text draws with, a panel's chunk) is hit like any other and
                // stands for the replicated slot above it. Its origin is plumbing, so the tip does not poke it.
                for (int i = 0; i < components.Count; i++)
                {
                    if (components[i] is MeshRenderer { IsDestroyed: false } renderer && renderer.Enabled.Value)
                        TestRenderer(slot, renderer);
                }
                if (_tip is { } tip && !slot.IsLocalElement)
                    TestPoke(slot, tip, components.Count);
            }

            var children = slot.Children;
            for (int i = 0; i < children.Count; i++)
                Walk(children[i], isRoot: false);
            // A replicated slot files its local children apart from the rest.
            var localChildren = slot.LocalChildren;
            for (int i = 0; i < localChildren.Count; i++)
                Walk(localChildren[i], isRoot: false);
        }

        private void TestPoke(Slot slot, float3 tip, int componentCount)
        {
            float distance = (slot.GlobalPosition - tip).Length;
            if (distance > _reach)
                return;
            // Two origins in one spot (a model and its first child, say): the one carrying more is the object.
            bool nearer = distance < PokedDistance - 1e-5f;
            bool tied = MathF.Abs(distance - PokedDistance) <= 1e-5f && componentCount > _pokedComponents;
            if (!nearer && !tied)
                return;
            Poked = slot;
            PokedDistance = distance;
            _pokedComponents = componentCount;
        }

        private void TestRenderer(Slot slot, MeshRenderer renderer)
        {
            if (!SlotBoundsHelper.TryGetRendererLocalBounds(renderer, out var box))
                return;

            // The ray in the slot's own space. The direction is the image of one world metre, so a parameter
            // along it is a distance in the world whatever the slot's scale.
            var localOrigin = slot.GlobalPointToLocal(_origin);
            var localDirection = slot.GlobalPointToLocal(_origin + _direction) - localOrigin;
            if (!RayBox(localOrigin, localDirection, box, out float enter, out bool inside) || enter >= DrawnDistance)
                return;

            var mesh = ConvexHullPointSource.GetMesh(renderer.Mesh.Target);
            float distance;
            if (mesh != null && mesh.VertexCount > 0 && TriangleCount(mesh) <= MaxTestedTriangles)
            {
                if (!RayMesh(mesh, localOrigin, localDirection, DrawnDistance, out distance))
                    return;
            }
            else
            {
                // A box with the ray starting inside it says nothing about where the surface is.
                if (inside)
                    return;
                distance = enter;
            }

            if (distance < DrawnDistance)
            {
                Drawn = slot;
                DrawnDistance = distance;
                _drawnBox = box;
            }
        }

        // How deep the drawn hit is along a world direction, from its box in its own slot's space.
        public float Thickness(float3 normal)
        {
            if (Drawn == null)
                return float.PositiveInfinity;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var corner = new float3(
                    (i & 1) == 0 ? _drawnBox.Min.x : _drawnBox.Max.x,
                    (i & 2) == 0 ? _drawnBox.Min.y : _drawnBox.Max.y,
                    (i & 4) == 0 ? _drawnBox.Min.z : _drawnBox.Max.z);
                float along = float3.Dot(Drawn.LocalPointToGlobal(corner), normal);
                min = MathF.Min(min, along);
                max = MathF.Max(max, along);
            }
            return max - min;
        }

        private static int TriangleCount(PhosMesh mesh)
        {
            int count = 0;
            foreach (var submesh in mesh.Submeshes)
            {
                if (submesh.Topology == PhosTopology.Triangles)
                    count += submesh.Count;
            }
            return count;
        }

        private static bool RayBox(in float3 origin, in float3 direction, in BoundingBox box, out float enter, out bool inside)
        {
            enter = 0f;
            inside = box.Contains(origin);
            float near = float.NegativeInfinity, far = float.PositiveInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = axis == 0 ? origin.x : axis == 1 ? origin.y : origin.z;
                float d = axis == 0 ? direction.x : axis == 1 ? direction.y : direction.z;
                float min = axis == 0 ? box.Min.x : axis == 1 ? box.Min.y : box.Min.z;
                float max = axis == 0 ? box.Max.x : axis == 1 ? box.Max.y : box.Max.z;
                if (MathF.Abs(d) < 1e-12f)
                {
                    if (o < min || o > max)
                        return false;
                    continue;
                }
                float t1 = (min - o) / d, t2 = (max - o) / d;
                if (t1 > t2)
                    (t1, t2) = (t2, t1);
                near = MathF.Max(near, t1);
                far = MathF.Min(far, t2);
                if (near > far)
                    return false;
            }
            if (far < 0f)
                return false;
            enter = MathF.Max(near, 0f);
            return true;
        }

        // Both faces count: a sheet seen from behind is still the sheet you pointed at.
        private static bool RayMesh(PhosMesh mesh, in float3 origin, in float3 direction, float maxDistance, out float distance)
        {
            distance = maxDistance;
            bool found = false;
            var positions = mesh.RawPositions;
            int vertexCount = mesh.VertexCount;
            foreach (var submesh in mesh.Submeshes)
            {
                if (submesh.Topology != PhosTopology.Triangles)
                    continue;
                var indices = submesh.RawIndices;
                int end = System.Math.Min(submesh.Count * 3, indices.Length);
                for (int i = 0; i + 2 < end; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                        continue;
                    if (RayTriangle(origin, direction, positions[a], positions[b], positions[c], out float t) && t < distance)
                    {
                        distance = t;
                        found = true;
                    }
                }
            }
            return found;
        }

        private static bool RayTriangle(in float3 origin, in float3 direction, in float3 a, in float3 b, in float3 c, out float t)
        {
            t = 0f;
            var edge1 = b - a;
            var edge2 = c - a;
            var p = float3.Cross(direction, edge2);
            float det = float3.Dot(edge1, p);
            if (MathF.Abs(det) < 1e-12f)
                return false;
            float inverse = 1f / det;
            var s = origin - a;
            float u = float3.Dot(s, p) * inverse;
            if (u < 0f || u > 1f)
                return false;
            var q = float3.Cross(s, edge1);
            float v = float3.Dot(direction, q) * inverse;
            if (v < 0f || u + v > 1f)
                return false;
            t = float3.Dot(edge2, q) * inverse;
            return t >= 0f;
        }
    }
}
