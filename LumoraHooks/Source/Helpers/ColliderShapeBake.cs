// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Godot.Helpers;

// One collider's shape as last handed to the physics server, and what it was made from. Primitives update in
// place; a mesh or a hull is cooked again only when its geometry, the scale baked into it, or the kind of body
// carrying it changes, since cooking a trimesh is not free. Both the collider hook and the rigid body hook keep
// one of these per shape, so a collider is the same shape whichever of them carries it. -xlinka
internal sealed class ColliderShapeBake
{
    public Shape3D? Shape { get; private set; }

    private float3[]? _meshSource;
    private int _meshIndexCount;
    // A cleared mesh hands its arrays back to the next build, so the same array is no proof of the same data:
    // the clear count is.
    private int _meshClearCount = -1;
    private Vector3 _scale;
    private MeshForm _meshForm;
    private int _hullVersion = -1;
    private int _hullCount = -1;
    // The last cook came out empty for the inputs above: every triangle without area, such as a part scaled to zero
    // to hide it. Kept as surely as a shape is, or the same nothing was cooked again on every move of the part.
    private bool _cookedEmpty;

    private List<float3>? _points;
    private List<float3>? _hull;
    private List<int>? _hullIndices;

    private enum MeshForm { None, Trimesh, Convex, Hulled }

    public void Clear()
    {
        Shape = null;
        _meshSource = null;
        _meshClearCount = -1;
        _cookedEmpty = false;
        _meshForm = MeshForm.None;
        _hullVersion = -1;
        _hullCount = -1;
    }

    // simulated: the shape rides a simulated body, which only carries convex shapes, so a mesh is hulled for
    // it and a lone triangle has nothing to give. Null when there is nothing to collide with yet (a mesh still
    // decoding, a hull under four points); the caller drops the shape rather than keep a stale one.
    public Shape3D? Build(Collider collider, Vector3 scale, bool simulated, out bool changed)
    {
        switch (collider)
        {
            case Lumora.Core.Components.MeshCollider mesh:
                return BuildMesh(mesh, scale, simulated, out changed);
            case ConvexHullCollider hull:
                return BuildHull(hull, scale, out changed);
            case TriangleCollider when simulated:
                changed = Shape != null;
                Clear();
                return null;
        }

        var primitive = ColliderShapeScaling.Update(collider, scale, Shape, out changed);
        Shape = primitive;
        return primitive;
    }

    // Whether Build would cook a mesh into a new shape now, rather than keep the one it holds. Cooking is the
    // costly part of a collider arriving, so a caller can hold it back to a later frame. -xlinka
    public bool WouldCook(Collider collider, Vector3 scale, bool simulated)
    {
        if (collider is not Lumora.Core.Components.MeshCollider meshCollider)
            return false;
        PhosMesh? mesh = ConvexHullPointSource.GetMesh(meshCollider.Mesh.Target);
        if (mesh == null || mesh.VertexCount == 0)
            return false;
        var form = meshCollider.Convex.Value ? MeshForm.Convex : simulated ? MeshForm.Hulled : MeshForm.Trimesh;
        int totalIndexCount = 0;
        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh.Topology == PhosTopology.Triangles)
                totalIndexCount += submesh.IndexCount;
        }
        if ((Shape != null || _cookedEmpty) && ReferenceEquals(_meshSource, mesh.RawPositions) && _meshClearCount == mesh.ClearCount
            && _meshIndexCount == totalIndexCount && _scale == scale && _meshForm == form)
            return false;
        return !(KeyOf(meshCollider, scale, form) is { } key && Cooked(key) != null);
    }

    // A COOKED LOADED MESH IS SHARED.
    //
    // A mesh loaded from an address is the same geometry wherever its address and import options are the same,
    // and so is its cooked shape: a world's repeated props each cooked the same trimesh, and every collider
    // cooked its mesh all over again after an asset reload, 160 to 200 ms on the main thread for one big one.
    // Held weakly, so a shape goes with the last collider using it. A generated mesh changes in place and is
    // never shared. -xlinka
    private readonly record struct CookKey(string Url, object? Variant, Vector3 Scale, MeshForm Form, int Indices);

    private static readonly Dictionary<CookKey, System.WeakReference<Shape3D>> _cooked = new();

    private static CookKey? KeyOf(Lumora.Core.Components.MeshCollider collider, Vector3 scale, MeshForm form)
    {
        if (collider.Mesh.Target is not MeshProvider { Asset: { AssetURL: { } url, MeshData: { } data } asset })
            return null;
        // A plain web link can point at something else after a reload; everything else names fixed bytes.
        if ((url.Scheme == System.Uri.UriSchemeHttp || url.Scheme == System.Uri.UriSchemeHttps) && !url.AbsolutePath.Contains("/content/"))
            return null;
        int indices = 0;
        foreach (var submesh in data.Submeshes)
        {
            if (submesh.Topology == PhosTopology.Triangles)
                indices += submesh.IndexCount;
        }
        return new CookKey(url.OriginalString, asset.TargetVariant, scale, form, indices);
    }

    private static Shape3D? Cooked(CookKey key)
        => _cooked.TryGetValue(key, out var weak) && weak.TryGetTarget(out var shape) && GodotObject.IsInstanceValid(shape) ? shape : null;

    private static void KeepCooked(CookKey key, Shape3D shape)
    {
        if (_cooked.Count >= 512)
        {
            var dead = new List<CookKey>();
            foreach (var pair in _cooked)
            {
                if (!pair.Value.TryGetTarget(out var held) || !GodotObject.IsInstanceValid(held))
                    dead.Add(pair.Key);
            }
            foreach (var gone in dead)
                _cooked.Remove(gone);
        }
        _cooked[key] = new System.WeakReference<Shape3D>(shape);
    }

    private Shape3D? BuildMesh(Lumora.Core.Components.MeshCollider collider, Vector3 scale, bool simulated, out bool changed)
    {
        changed = false;
        PhosMesh? mesh = ConvexHullPointSource.GetMesh(collider.Mesh.Target);
        int vertexCount = mesh?.VertexCount ?? 0;
        if (mesh == null || vertexCount == 0)
        {
            // Not decoded yet: what is there stays until the data lands and the collider applies again.
            return Shape;
        }

        var form = collider.Convex.Value ? MeshForm.Convex : simulated ? MeshForm.Hulled : MeshForm.Trimesh;

        int totalIndexCount = 0;
        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh.Topology == PhosTopology.Triangles)
                totalIndexCount += submesh.IndexCount;
        }

        if ((Shape != null || _cookedEmpty) && ReferenceEquals(_meshSource, mesh.RawPositions) && _meshClearCount == mesh.ClearCount
            && _meshIndexCount == totalIndexCount && _scale == scale && _meshForm == form)
            return Shape;

        var positions = mesh.RawPositions;
        var key = KeyOf(collider, scale, form);
        Shape3D? built = key is { } known ? Cooked(known) : null;
        if (built == null)
        {
            built = form switch
            {
                MeshForm.Convex => ConvexOf(positions, vertexCount, scale),
                MeshForm.Hulled => HullOf(mesh, scale),
                _ => TrimeshOf(mesh, totalIndexCount, scale),
            };
            if (built != null && key is { } fresh)
                KeepCooked(fresh, built);
        }
        _meshSource = positions;
        _meshIndexCount = totalIndexCount;
        _meshClearCount = mesh.ClearCount;
        _scale = scale;
        _meshForm = form;
        _cookedEmpty = built == null;
        if (built == null)
            return Shape;

        Shape = built;
        changed = true;
        _hullVersion = -1;
        return Shape;
    }

    private static ConvexPolygonShape3D ConvexOf(float3[] positions, int count, Vector3 scale)
    {
        var points = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            var p = positions[i];
            points[i] = new Vector3(p.x * scale.X, p.y * scale.Y, p.z * scale.Z);
        }
        return new ConvexPolygonShape3D { Points = points };
    }

    // A trimesh only works on something that never simulates, so a mesh collider on a simulated body becomes
    // the hull of its vertices, solved here so every peer gets the same one. A flat or degenerate mesh has no
    // hull with volume; it gets a thin box over its bounds so the body still has something to land on.
    private Shape3D? HullOf(PhosMesh mesh, Vector3 scale)
    {
        _points ??= new List<float3>();
        _hull ??= new List<float3>();
        _hullIndices ??= new List<int>();
        _points.Clear();
        var positions = mesh.RawPositions;
        int count = mesh.VertexCount;
        int stride = count > ConvexHullPointSource.MaxSourcePoints
            ? (count + ConvexHullPointSource.MaxSourcePoints - 1) / ConvexHullPointSource.MaxSourcePoints
            : 1;
        for (int i = 0; i < count; i += stride)
            _points.Add(positions[i]);

        var result = ConvexHullSolver.Solve(_points, 0f, _hull, _hullIndices);
        if (result == ConvexHullSolver.Result.Volume && _hull.Count >= 4)
        {
            var points = new Vector3[_hull.Count];
            for (int i = 0; i < _hull.Count; i++)
            {
                var p = _hull[i];
                points[i] = new Vector3(p.x * scale.X, p.y * scale.Y, p.z * scale.Z);
            }
            return new ConvexPolygonShape3D { Points = points };
        }

        if (_points.Count == 0)
            return null;
        float3 min = _points[0], max = _points[0];
        foreach (var p in _points)
        {
            min = new float3(System.MathF.Min(min.x, p.x), System.MathF.Min(min.y, p.y), System.MathF.Min(min.z, p.z));
            max = new float3(System.MathF.Max(max.x, p.x), System.MathF.Max(max.y, p.y), System.MathF.Max(max.z, p.z));
        }
        var size = new Vector3((max.x - min.x) * scale.X, (max.y - min.y) * scale.Y, (max.z - min.z) * scale.Z).Abs();
        size = size.Max(Vector3.One * 0.002f);
        var centre = (min + max) * 0.5f;
        var shifted = new Vector3(centre.x * scale.X, centre.y * scale.Y, centre.z * scale.Z);
        // A box has no offset of its own, so the bounds centre goes into the points of a box-shaped hull.
        var half = size * 0.5f;
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = shifted + new Vector3(
                (i & 1) == 0 ? -half.X : half.X,
                (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z);
        }
        return new ConvexPolygonShape3D { Points = corners };
    }

    private const float WeldDistanceSquared = 1e-4f * 1e-4f;

    private static ConcavePolygonShape3D? TrimeshOf(PhosMesh mesh, int totalIndexCount, Vector3 scale)
    {
        if (totalIndexCount == 0)
            return null;

        var positions = mesh.RawPositions;
        int vertexCount = mesh.VertexCount;
        var faces = new Vector3[totalIndexCount];
        int write = 0;
        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh.Topology != PhosTopology.Triangles)
                continue;
            var indices = submesh.RawIndices;
            int count = submesh.IndexCount;
            for (int i = 0; i + 2 < count; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                // An index past the vertex buffer would corrupt the cook; drop the triangle.
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                    continue;
                var pa = new Vector3(positions[a].x * scale.X, positions[a].y * scale.Y, positions[a].z * scale.Z);
                var pb = new Vector3(positions[b].x * scale.X, positions[b].y * scale.Y, positions[b].z * scale.Z);
                var pc = new Vector3(positions[c].x * scale.X, positions[c].y * scale.Y, positions[c].z * scale.Z);
                // The solver welds corners closer than a tenth of a millimetre and drops the triangles that leave
                // without area; a mesh of nothing else, such as a part scaled to zero to hide it, is refused outright.
                if ((pb - pa).LengthSquared() < WeldDistanceSquared || (pc - pb).LengthSquared() < WeldDistanceSquared
                    || (pa - pc).LengthSquared() < WeldDistanceSquared)
                    continue;
                faces[write++] = pa;
                faces[write++] = pb;
                faces[write++] = pc;
            }
        }
        if (write == 0)
            return null;
        if (write != totalIndexCount)
            System.Array.Resize(ref faces, write);

        // Imported geometry has no winding guarantee, and a one-sided trimesh lets things through every face
        // that happens to point the wrong way.
        return new ConcavePolygonShape3D { Data = faces, BackfaceCollision = true };
    }

    // The hull is solved engine-side and cached there, so every peer collides against the same shape; this
    // side only scales the points into the body's space. Under four points there is no volume, and keeping the
    // previous shape would be worse than having none.
    private Shape3D? BuildHull(ConvexHullCollider collider, Vector3 scale, out bool changed)
    {
        changed = false;
        var hull = collider.GetHullPoints();
        if (hull.Count < 4)
        {
            if (Shape != null)
            {
                Clear();
                changed = true;
            }
            return null;
        }

        if (Shape != null && _hullVersion == collider.HullVersion && _hullCount == hull.Count && _scale == scale)
            return Shape;

        var points = new Vector3[hull.Count];
        for (int i = 0; i < hull.Count; i++)
        {
            var p = hull[i];
            points[i] = new Vector3(p.x * scale.X, p.y * scale.Y, p.z * scale.Z);
        }

        Shape = new ConvexPolygonShape3D { Points = points };
        changed = true;
        _hullVersion = collider.HullVersion;
        _hullCount = hull.Count;
        _scale = scale;
        _meshForm = MeshForm.None;
        _meshSource = null;
        return Shape;
    }
}
