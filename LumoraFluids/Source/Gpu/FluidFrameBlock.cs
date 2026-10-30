// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Lumora.Fluids;

// Everything one GPU frame of one world needs. The world thread packs it in late update (FluidFramePacker) and the
// device reads it at the next FramePreDraw, on the same thread under Safe, so it is never shared across threads.
// The lists keep their capacity: after the first busy frame packing allocates nothing, and the device uploads
// straight from CollectionsMarshal.AsSpan over them.
//
// Shapes (systems, substances, triangle grids, contact meshes, the hide table) go up only when their Changed flag
// is set and stay packed between frames; everything BeginFrame clears is rebuilt and uploaded every stepped frame.
// A background world that updates at 10 Hz keeps adding its Delta to PendingSeconds, and the device runs one
// sub-frame of at most 1/30 s per drawn frame out of it. -xlinka
public sealed class FluidFrameBlock
{
    public FluidFrameHeader Header;

    // Bumped by Submit. The device steps only on a sequence it has not stepped.
    public int Sequence;

    // World.Time.Delta not yet simulated, s.
    public double PendingSeconds;

    public bool Focused;

    // Every system asleep and no wake box this frame: the device records nothing for this world.
    public bool Asleep;

    // Shapes, kept between frames.
    public readonly List<FluidSystemRecord> Systems = new();
    public bool SystemsChanged;
    public readonly List<FluidSubstanceRecord> Substances = new();
    public bool SubstancesChanged;
    public readonly List<FluidTriangle> Triangles = new();
    public readonly List<FluidTriangleGridHeader> TriangleGrids = new();
    public readonly List<FluidGridCell> TriangleGridCells = new();
    public readonly List<uint> TriangleGridIndices = new();
    public bool TrianglesChanged;
    public readonly List<FluidContactVertex> ContactVertices = new();
    // Three per triangle, into ContactVertices.
    public readonly List<uint> ContactIndices = new();
    public readonly List<FluidBlendDelta> BlendDeltas = new();
    public bool ContactMeshesChanged;
    // Origin indices whose fluid this machine does not draw or let anchor to its user (blocked people).
    public readonly List<uint> HiddenOrigins = new();
    public bool HiddenOriginsChanged;

    // Rebuilt every frame.
    public readonly List<FluidFrameRecord> Frames = new();
    public readonly List<FluidGpuCommand> Commands = new();
    public readonly List<FluidColliderRecord> Colliders = new();
    // FluidForce on its systems; the header's Forces counts them, at most FluidLayout.MaxForces.
    public readonly List<FluidForceRecord> Forces = new();
    public readonly List<FluidPersonRecord> People = new();
    public readonly List<FluidBoneRecord> Bones = new();
    public readonly List<FluidProxyRecord> Proxies = new();
    // Current blendshape weights, indexed by the shape field of FluidBlendDelta.
    public readonly List<float> BlendWeights = new();
    public readonly List<FluidResidueBone> ResidueBones = new();
    public readonly List<FluidSpotRecord> Spots = new();
    public readonly List<FluidSoftSphere> SoftSpheres = new();
    public readonly List<FluidLedgerEntry> LedgerChanges = new();
    public readonly List<FluidQuerySphere> Queries = new();

    public void BeginFrame()
    {
        Frames.Clear();
        Commands.Clear();
        Colliders.Clear();
        Forces.Clear();
        People.Clear();
        Bones.Clear();
        Proxies.Clear();
        BlendWeights.Clear();
        ResidueBones.Clear();
        Spots.Clear();
        SoftSpheres.Clear();
        LedgerChanges.Clear();
        Queries.Clear();
        Asleep = false;
    }

    // After the device uploaded the shapes, so the next frame sends only what changed again.
    public void ClearChanged()
    {
        SystemsChanged = false;
        SubstancesChanged = false;
        TrianglesChanged = false;
        ContactMeshesChanged = false;
        HiddenOriginsChanged = false;
    }
}
