// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Phos;
using Lumora.Script;
using Lumora.Warden;

namespace Lumora.Core.Scripting;

// Merges the plain meshes under a slot into one, one part per material, so they draw in as few calls as
// they have materials. What each renderer shows is copied on the world thread as the bake starts; the merge
// and the save into this machine's asset store run on a worker; the result goes on a "Baked Meshes" slot
// made under the root, placed where the originals were, and every machine loads it from the address the
// store gave it the way it loads any imported mesh.
//
// Only what the script may already change: world content from a world script, the author's own things from
// a person's script, what a Local script made on its machine. Skinned meshes, meshes not loaded here and
// renderers the script may not switch off are left as they were. A world script's and a Local script's
// originals are switched off, not removed, so switching them back on undoes the bake; a person's script
// makes an unsaved copy that goes when they leave, so their originals stay on. Colliders, grabbing and
// everything else under the root are untouched. -xlinka
internal static class ScriptBake
{
    public const int WarnVertices = 1_000_000;

    private sealed class Piece
    {
        public float3[] Positions = Array.Empty<float3>();
        public float3[]? Normals;
        public float4[]? Tangents;
        public color[]? Colors;
        public float2[]? Uv0;
        public float2[]? Uv1;
        public float4x4 Matrix;
        public readonly List<(int[] Indices, IAssetProvider<MaterialAsset>? Material)> Parts = new();
    }

    private sealed record Result(string? Url, List<IAssetProvider<MaterialAsset>?> Materials, string Error);

    public static void Start(ScriptContext ctx, AwaitTicket<MeshesBaked> ticket, Slot? root, bool hidden)
    {
        if (!ScriptReads.Visible(ctx, root))
        {
            ticket.Answer("failed", new MeshesBaked(null, 0, 0, "there is no slot the script can see to bake"));
            return;
        }
        try
        {
            ScriptAttach.CheckPlace(ctx, root!);
        }
        catch (UnauthorizedAccessException refused)
        {
            // Refused here as when it lands: failed says so, and the refusal is still counted. -xlinka
            ticket.Answer("failed", new MeshesBaked(null, 0, 0, refused.Message));
            throw;
        }
        catch
        {
            ticket.Abandon();
            throw;
        }

        var pieces = new List<Piece>();
        var renderers = new List<MeshRenderer>();
        int left = 0;
        Gather(ctx, root!, root!, hidden, pieces, renderers, ref left);
        if (pieces.Count == 0)
        {
            ticket.Answer("failed", new MeshesBaked(null, 0, left, "nothing under the slot is a loaded mesh the script may bake"));
            return;
        }
        var store = Engine.Current?.LocalDB;
        if (store == null)
        {
            ticket.Answer("failed", new MeshesBaked(null, 0, left, "this machine has no asset store to keep the baked mesh in"));
            return;
        }
        long vertices = 0;
        foreach (var piece in pieces)
            vertices += piece.Positions.Length;
        if (vertices > WarnVertices)
            ctx.Warn(ScriptWarning.Bakes, $"one bake of {vertices:N0} vertices");

        var baking = Task.Run(() => Bake(pieces, store));
        ctx.World.StartCoroutine(AfterBake(ctx, ticket, root!, renderers, pieces.Count, left, baking));
    }

    private static void Gather(ScriptContext ctx, Slot root, Slot slot, bool hidden, List<Piece> pieces, List<MeshRenderer> renderers,
        ref int left)
    {
        if (slot.IsDestroyed || (!hidden && !slot.IsActive))
            return;
        foreach (var component in slot.Components)
        {
            if (component.IsDestroyed || (!hidden && !component.Enabled.Value))
                continue;
            if (component is SkinnedMeshRenderer)
            {
                left++;
                continue;
            }
            if (component is not MeshRenderer renderer)
                continue;
            var mesh = renderer.Mesh.Target switch
            {
                ProceduralMesh procedural => procedural.PhosMesh,
                IAssetProvider<MeshDataAsset> provider => provider.Asset?.MeshData,
                _ => null,
            };
            var piece = mesh != null && mesh.VertexCount > 0 && Switchable(ctx, renderer) ? Copy(mesh, renderer, root, slot) : null;
            if (piece == null)
            {
                left++;
                continue;
            }
            pieces.Add(piece);
            renderers.Add(renderer);
        }
        foreach (var child in slot.Children)
            Gather(ctx, root, child, hidden, pieces, renderers, ref left);
    }

    private static bool Switchable(ScriptContext ctx, MeshRenderer renderer)
    {
        try
        {
            ScriptWrites.Check(ctx, renderer.Enabled);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Piece? Copy(PhosMesh mesh, MeshRenderer renderer, Slot root, Slot slot)
    {
        int count = mesh.VertexCount;
        if (mesh.RawPositions.Length < count)
            return null;
        var piece = new Piece
        {
            Matrix = slot.GetLocalToSpaceMatrix(root),
            Positions = mesh.RawPositions.AsSpan(0, count).ToArray(),
            Normals = mesh.HasNormals && mesh.RawNormals.Length >= count ? mesh.RawNormals.AsSpan(0, count).ToArray() : null,
            Tangents = mesh.HasTangents && mesh.RawTangents.Length >= count ? mesh.RawTangents.AsSpan(0, count).ToArray() : null,
            Colors = mesh.HasColors && mesh.RawColors.Length >= count ? mesh.RawColors.AsSpan(0, count).ToArray() : null,
            Uv0 = mesh.HasUV0s && mesh.RawUV0s.Length >= count ? mesh.RawUV0s.AsSpan(0, count).ToArray() : null,
            Uv1 = mesh.HasUV1s && mesh.RawUV1s.Length >= count ? mesh.RawUV1s.AsSpan(0, count).ToArray() : null,
        };
        int materials = renderer.Materials.Count;
        for (int i = 0; i < mesh.Submeshes.Count; i++)
        {
            if (mesh.Submeshes[i] is not PhosTriangleSubmesh triangles || triangles.Count == 0 || triangles.RawIndices.Length < triangles.IndexCount)
                continue;
            // A surface past the end of the material list draws with the last one, as the renderer does.
            var material = materials == 0 ? null : renderer.Materials[System.Math.Min(i, materials - 1)];
            piece.Parts.Add((triangles.RawIndices.AsSpan(0, triangles.IndexCount).ToArray(), material));
        }
        return piece.Parts.Count > 0 ? piece : null;
    }

    private static async Task<Result> Bake(List<Piece> pieces, LocalDB store)
    {
        var materials = new List<IAssetProvider<MaterialAsset>?>();
        var mesh = Merge(pieces, materials);
        var url = await store.SaveMeshAsync(mesh).ConfigureAwait(false);
        return new Result(url, materials, url == null ? "the baked mesh could not be saved" : string.Empty);
    }

    // One vertex list in the root's space, one part per material. A mirrored piece has its faces turned back
    // round, and a face naming a vertex its piece does not have is dropped.
    private static PhosMesh Merge(List<Piece> pieces, List<IAssetProvider<MaterialAsset>?> materials)
    {
        bool normals = false, colors = false, uv0 = false, uv1 = false, tangents = true;
        int total = 0;
        foreach (var piece in pieces)
        {
            normals |= piece.Normals != null;
            colors |= piece.Colors != null;
            uv0 |= piece.Uv0 != null;
            uv1 |= piece.Uv1 != null;
            tangents &= piece.Tangents != null;
            total += piece.Positions.Length;
        }
        tangents &= normals;

        var mesh = new PhosMesh { HasNormals = normals, HasColors = colors, HasTangents = tangents };
        if (uv0)
            mesh.SetHasUV(0, true);
        if (uv1)
            mesh.SetHasUV(1, true);
        mesh.IncreaseVertexCount(total);

        var parts = new Dictionary<IAssetProvider<MaterialAsset>, PhosTriangleSubmesh>(ReferenceEqualityComparer.Instance);
        PhosTriangleSubmesh? unset = null;
        int start = 0;
        foreach (var piece in pieces)
        {
            var m = piece.Matrix;
            var linear = new float3x3(new float3(m.c0.x, m.c0.y, m.c0.z), new float3(m.c1.x, m.c1.y, m.c1.z), new float3(m.c2.x, m.c2.y, m.c2.z));
            float determinant = linear.Determinant;
            bool mirrored = determinant < 0f;
            var turnNormals = MathF.Abs(determinant) > 1e-12f ? linear.Inverse.Transposed : linear;
            int count = piece.Positions.Length;
            for (int v = 0; v < count; v++)
            {
                int at = start + v;
                mesh.RawPositions[at] = m.MultiplyPoint(piece.Positions[v]);
                if (normals)
                    mesh.RawNormals[at] = piece.Normals != null ? Unit(turnNormals * piece.Normals[v]) : float3.Up;
                if (tangents)
                {
                    var tangent = piece.Tangents![v];
                    var along = Unit(linear * new float3(tangent.x, tangent.y, tangent.z));
                    mesh.RawTangents[at] = new float4(along.x, along.y, along.z, mirrored ? -tangent.w : tangent.w);
                }
                if (colors)
                    mesh.RawColors[at] = piece.Colors != null ? piece.Colors[v] : color.White;
                if (uv0)
                    mesh.RawUV0s[at] = piece.Uv0 != null ? piece.Uv0[v] : float2.Zero;
                if (uv1)
                    mesh.RawUV1s[at] = piece.Uv1 != null ? piece.Uv1[v] : float2.Zero;
            }

            foreach (var (indices, material) in piece.Parts)
            {
                PhosTriangleSubmesh part;
                if (material == null)
                {
                    part = unset ??= Part(mesh, materials, null);
                }
                else if (!parts.TryGetValue(material, out part!))
                {
                    part = Part(mesh, materials, material);
                    parts.Add(material, part);
                }
                int triangles = indices.Length / 3;
                int first = part.Count;
                part.IncreaseCount(triangles);
                int written = 0;
                for (int t = 0; t < triangles; t++)
                {
                    int a = indices[t * 3], b = indices[t * 3 + 1], c = indices[t * 3 + 2];
                    if ((uint)a >= (uint)count || (uint)b >= (uint)count || (uint)c >= (uint)count)
                        continue;
                    if (mirrored)
                        (b, c) = (c, b);
                    part.SetTriangle(first + written++, start + a, start + b, start + c);
                }
                if (written < triangles)
                    part.RemoveFromEnd(triangles - written);
            }
            start += count;
        }
        return mesh;
    }

    private static PhosTriangleSubmesh Part(PhosMesh mesh, List<IAssetProvider<MaterialAsset>?> materials, IAssetProvider<MaterialAsset>? material)
    {
        var part = new PhosTriangleSubmesh(mesh);
        mesh.Submeshes.Add(part);
        materials.Add(material);
        return part;
    }

    private static float3 Unit(float3 value)
    {
        float length = value.Length;
        return length > 1e-12f && float.IsFinite(length) ? value / length : float3.Up;
    }

    private static IEnumerator AfterBake(ScriptContext ctx, AwaitTicket<MeshesBaked> ticket, Slot root, List<MeshRenderer> renderers,
        int meshes, int left, Task<Result> baking)
    {
        yield return baking;
        if (ticket.Cancelled.IsCancellationRequested)
        {
            ticket.Abandon();
            yield break;
        }
        if (baking.IsFaulted || baking.IsCanceled || baking.Result.Url == null)
        {
            string why = baking.IsFaulted ? "the bake could not run: " + baking.Exception?.GetBaseException().Message
                : baking.IsCanceled ? "the bake was cancelled" : baking.Result.Error;
            ticket.Answer("failed", new MeshesBaked(null, 0, left, why));
            yield break;
        }
        var world = ctx.World;
        if (world.IsDisposed || world.IsDestroyed || PermissionEngine.IsGateOpenOnThisThread || WorldConfigEdit.IsOpen
            || !ctx.Principal.IsStillAuthorIn(world))
        {
            ticket.Abandon();
            yield break;
        }
        Finish(ctx, ticket, root, renderers, meshes, left, baking.Result);
    }

    private static void Finish(ScriptContext ctx, AwaitTicket<MeshesBaked> ticket, Slot root, List<MeshRenderer> renderers, int meshes,
        int left, Result result)
    {
        using (ctx.World.DataModelPermissions?.EnterActor(ctx.Principal))
        {
            try
            {
                if (!ScriptReads.Visible(ctx, root))
                    throw new UnauthorizedAccessException("the slot is gone");
                ScriptAttach.CheckPlace(ctx, root);
                var address = new Uri(result.Url!);
                Slot baked;
                if (ctx.IsLocal)
                {
                    ctx.ReserveMade(1);
                    baked = root.AddLocalSlot("Baked Meshes");
                    ctx.Made(baked, 1);
                }
                else
                {
                    baked = ScriptWrites.MakeUnder(ctx, root, 1, () => root.AddSlot("Baked Meshes"));
                }
                using (ctx.IsLocal ? null : ScriptWrites.RangeOf(ctx.World, ctx.Principal))
                {
                    var provider = baked.AttachComponent<MeshProvider>();
                    provider.URL.Value = address;
                    var renderer = baked.AttachComponent<MeshRenderer>();
                    renderer.Mesh.Target = provider;
                    foreach (var material in result.Materials)
                        renderer.Materials.Add(material!);
                }
                if (ctx.Principal.IsWorldContent || ctx.IsLocal)
                {
                    foreach (var original in renderers)
                    {
                        if (original.IsDestroyed)
                            continue;
                        try
                        {
                            ScriptWrites.Write(ctx, original.Enabled, false);
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                }
                ticket.Answer("baked", new MeshesBaked(baked, meshes, left, string.Empty));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or UriFormatException)
            {
                ticket.Answer("failed", new MeshesBaked(null, 0, left, ex.Message));
            }
        }
    }
}
