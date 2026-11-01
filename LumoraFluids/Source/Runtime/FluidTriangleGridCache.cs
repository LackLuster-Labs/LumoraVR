// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lumora.Core.Phos;

namespace Lumora.Fluids;

// Triangle grids for mesh colliders: the triangles in collider space and a uniform grid over their bounds, each cell
// listing the triangles that cross it, so the collide kernel tests a handful of triangles per particle instead of
// the mesh. Built once per mesh on a worker from the immutable CPU mesh and shared by every collider and world that
// uses it; the result never changes, so readers need no lock once a build has landed.
//
// A rebuilt procedural mesh is a new build: the key is the mesh object, checked against its vertex and triangle
// counts. A system holds for up to half a second while a grid it needs builds, then goes on without that collider
// and the inspector says so. Past a million triangles it warns; the grid still builds, it just costs memory. -xlinka
public sealed class FluidTriangleGridCache
{
    public const int WarnTriangles = 1_000_000;
    // Triangles a cell should list on average; the grid is sized from the mesh's count and bounds.
    public const int TrianglesPerCell = 8;
    public const int MaxCellsPerAxis = 64;

    public static readonly FluidTriangleGridCache Shared = new();

    private readonly ConditionalWeakTable<PhosMesh, Task<FluidTriangleGrid>> _builds = new();
    private readonly object _lock = new();

    // A built grid, or null while it builds (and for a mesh with nothing to collide with).
    public FluidTriangleGrid? Get(PhosMesh? mesh, out bool building)
    {
        building = false;
        if (mesh == null)
            return null;
        Task<FluidTriangleGrid> task;
        lock (_lock)
        {
            if (!_builds.TryGetValue(mesh, out task!) || (task.IsCompletedSuccessfully && task.Result.IsStale(mesh)))
            {
                var captured = mesh;
                task = Task.Run(() => FluidTriangleGrid.Build(captured));
                _builds.AddOrUpdate(mesh, task);
            }
        }
        if (!task.IsCompleted)
        {
            building = true;
            return null;
        }
        if (!task.IsCompletedSuccessfully)
            return null;
        return task.Result.TriangleCount > 0 ? task.Result : null;
    }
}
