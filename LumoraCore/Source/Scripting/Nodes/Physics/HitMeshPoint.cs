// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Phos;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Where on a slot's mesh a world point lies, such as a ray's hit: the triangle nearest the point,
// counted across the mesh's triangle parts in order, the part it belongs to (the material slot it is
// drawn with), and the texture coordinate there, blended from the triangle's corners. The mesh is the
// slot's mesh collider's, else its mesh renderer's, as the mesh is at rest; a skinned mesh bent by its
// bones is not what this reads. The search looks at every triangle, so it costs what the mesh is. found is
// false, and the rest zero, for a slot with no readable mesh; uv is zero on a mesh with no texture
// coordinates.
[Node("Physics/Queries/Hit Mesh Point", Capability.Physics, Description = "Finds the mesh triangle nearest a world position and returns its material part and texture coordinates.",
    Keywords = "uv at hit, triangle index, texture coordinate, hit uv")]
public sealed class HitMeshPoint : ReadNode
{
    [return: Port("found")]
    public static bool Run(ScriptContext ctx, Slot? slot, float3 point, out int triangle, out int submesh, out float2 uv)
    {
        triangle = 0;
        submesh = 0;
        uv = float2.Zero;
        if (!ScriptReads.Visible(ctx, slot) || !ScriptPhysics.IsFinite(point) || !TryMesh(slot!, out var mesh, out var offset))
            return false;

        var local = slot!.GlobalPointToLocal(point) - offset;
        var positions = mesh.RawPositions;
        int vertices = System.Math.Min(mesh.VertexCount, positions.Length);
        float best = float.MaxValue;
        int counted = 0;
        int bestTriangle = -1, bestPart = 0, a0 = 0, a1 = 0, a2 = 0;
        float3 bestWeights = default;
        for (int part = 0; part < mesh.Submeshes.Count; part++)
        {
            var sub = mesh.Submeshes[part];
            if (sub.Topology != PhosTopology.Triangles)
                continue;
            var indices = sub.RawIndices;
            int count = System.Math.Min(sub.IndexCount, indices.Length) / 3;
            for (int t = 0; t < count; t++, counted++)
            {
                int i0 = indices[t * 3], i1 = indices[t * 3 + 1], i2 = indices[t * 3 + 2];
                if ((uint)i0 >= (uint)vertices || (uint)i1 >= (uint)vertices || (uint)i2 >= (uint)vertices)
                    continue;
                var weights = Closest(local, positions[i0], positions[i1], positions[i2], out float distanceSquared);
                if (distanceSquared < best)
                {
                    best = distanceSquared;
                    bestTriangle = counted;
                    bestPart = part;
                    bestWeights = weights;
                    (a0, a1, a2) = (i0, i1, i2);
                }
            }
        }
        if (bestTriangle < 0)
            return false;

        triangle = bestTriangle;
        submesh = bestPart;
        var uvs = mesh.HasUV0s ? mesh.RawUV0s : Array.Empty<float2>();
        if (a0 < uvs.Length && a1 < uvs.Length && a2 < uvs.Length)
            uv = uvs[a0] * bestWeights.x + uvs[a1] * bestWeights.y + uvs[a2] * bestWeights.z;
        return true;
    }

    // The collider's mesh with the collider's offset, else the renderer's.
    private static bool TryMesh(Slot slot, out PhosMesh mesh, out float3 offset)
    {
        foreach (var collider in slot.GetComponents<MeshCollider>())
        {
            if (!collider.IsDestroyed && DataOf(collider.Mesh.Target) is { } data)
            {
                mesh = data;
                offset = collider.Offset.Value;
                return true;
            }
        }
        foreach (var renderer in slot.GetComponents<MeshRenderer>())
        {
            if (!renderer.IsDestroyed && DataOf(renderer.Mesh.Target) is { } data)
            {
                mesh = data;
                offset = float3.Zero;
                return true;
            }
        }
        mesh = null!;
        offset = float3.Zero;
        return false;
    }

    private static PhosMesh? DataOf(Component? source) => source switch
    {
        ProceduralMesh { IsDestroyed: false } procedural => procedural.PhosMesh,
        MeshProvider { IsDestroyed: false } provider => provider.Asset?.MeshData,
        _ => null,
    };

    // The point of triangle abc nearest p, as weights on a, b and c, and its squared distance from p.
    private static float3 Closest(float3 p, float3 a, float3 b, float3 c, out float distanceSquared)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = float3.Dot(ab, ap), d2 = float3.Dot(ac, ap);
        float3 weights;
        if (d1 <= 0f && d2 <= 0f)
        {
            weights = new float3(1f, 0f, 0f);
        }
        else
        {
            var bp = p - b;
            float d3 = float3.Dot(ab, bp), d4 = float3.Dot(ac, bp);
            var cp = p - c;
            float d5 = float3.Dot(ab, cp), d6 = float3.Dot(ac, cp);
            float vc = d1 * d4 - d3 * d2, vb = d5 * d2 - d1 * d6, va = d3 * d6 - d5 * d4;
            if (d3 >= 0f && d4 <= d3)
                weights = new float3(0f, 1f, 0f);
            else if (d6 >= 0f && d5 <= d6)
                weights = new float3(0f, 0f, 1f);
            else if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                weights = new float3(1f - v, v, 0f);
            }
            else if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                weights = new float3(1f - w, 0f, w);
            }
            else if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                weights = new float3(0f, 1f - w, w);
            }
            else
            {
                float denominator = va + vb + vc;
                if (!(MathF.Abs(denominator) > 1e-20f))
                {
                    weights = new float3(1f, 0f, 0f);
                }
                else
                {
                    float v = vb / denominator, w = vc / denominator;
                    weights = new float3(1f - v - w, v, w);
                }
            }
        }
        var nearest = a * weights.x + b * weights.y + c * weights.z;
        distanceSquared = (p - nearest).LengthSquared;
        return weights;
    }
}
