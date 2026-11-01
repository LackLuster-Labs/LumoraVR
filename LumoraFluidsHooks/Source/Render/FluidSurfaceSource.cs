// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;

namespace Lumora.Fluids.Hooks;

// What the surface reads from a world's simulation for one frame: the cell-sorted particle half the frame ended
// on, the bonds, ids and id map the tubes walk, the labels the strand cap keys on, the spray particles, and the
// solver's counters for its live count. The solver hands this over after its frame end (FromSolver); a test
// builds it from synthetic buffers. Any RID left empty binds a stand-in, so a renderer without strands or spray
// still records, and a source without counters draws every slot it is given. -xlinka
public struct FluidSurfaceSource
{
    public Rid Particles;
    // particles dispatched over; dead slots are skipped
    public uint ParticleCount;
    public Rid Bonds;
    public uint BondSlots;
    // bonds stored per slot rather than per stable id
    public bool BondsBySlot;
    public Rid Ids;
    public Rid IdToSlot;
    public Rid Accumulators;
    public Rid SprayParticles;
    public uint SprayCount;
    // the solver's counter words; the live count caps the dispatch on the GPU
    public Rid Counters;
    // Optional current skin contacts, read only by surface reconstruction. -xlinka
    public Rid Anchors;
    public Rid Header;
    public Rid People;
    public Rid ContactIndices;
    public Rid SkinnedVertices;
    public Rid Frames;
    public bool HasSkinContacts => Anchors.IsValid && Header.IsValid && People.IsValid
        && ContactIndices.IsValid && SkinnedVertices.IsValid && Frames.IsValid && (Ids.IsValid || BondsBySlot);
    // the frame's per-origin hide words (FluidFrameBlock.HiddenOrigins); null hides nothing
    public IReadOnlyList<uint>? HiddenOrigins;
    // FluidLayout.None draws every system in the buffer
    public uint System;
    // the particles' frame to the renderer's local space
    public Transform3D ToLocal;

    // A world's solver as it stands after its frame end. The cell sort leaves the particles in the first half, with
    // bonds and accumulators kept by stable id and partners named by id, so the id map resolves them. Everything is
    // dispatched over the capacity and cut to the live count on the GPU, which is where that count lives.
    public static FluidSurfaceSource FromSolver(FluidGpuSolver solver)
    {
        var b = solver.Buffers;
        if (!b.Created)
            return default;
        return new FluidSurfaceSource
        {
            Particles = b[FluidGpuBuffers.Particles],
            ParticleCount = (uint)b.Capacity,
            Bonds = b[FluidGpuBuffers.Bonds],
            BondSlots = (uint)b.BondSlots,
            BondsBySlot = false,
            Ids = b[FluidGpuBuffers.Ids],
            IdToSlot = b[FluidGpuBuffers.IdToSlot],
            Accumulators = b[FluidGpuBuffers.Accumulators],
            Counters = b[FluidGpuBuffers.Counters],
            Anchors = b[FluidGpuBuffers.Anchors],
            Header = b[FluidGpuBuffers.Header],
            People = b[FluidGpuBuffers.People],
            ContactIndices = b[FluidGpuBuffers.ContactIndex],
            SkinnedVertices = b[FluidGpuBuffers.Skinned],
            Frames = b[FluidGpuBuffers.Frames],
            HiddenOrigins = solver.Block.HiddenOrigins,
            System = FluidLayout.None,
            ToLocal = Transform3D.Identity,
        };
    }
}
