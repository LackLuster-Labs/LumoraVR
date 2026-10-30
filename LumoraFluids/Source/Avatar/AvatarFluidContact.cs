// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Components;

namespace Lumora.Fluids;

// Optional, on an avatar: which regions take fluid contact (the whole body by default), how sticky, rough and wettable
// this avatar's skin is, and overrides for odd avatars (renderers to cut the contact mesh from, extra bones for
// non-humanoid patches, a vertex budget for the cut). -xlinka
[ComponentCategory("Users/Avatar")]
public sealed class AvatarFluidContact : Component
{
    public readonly Sync<FluidBodyRegions> Regions = new(FluidBodyRegions.Body);
    // x the substance's
    public readonly Sync<float> Stickiness = new(1f);
    // x the substance's friction, on the skin
    public readonly Sync<float> Friction = new(1f);
    // x the substance's adhesion, on the skin: how readily it wets
    public readonly Sync<float> Wetting = new(1f);
    // contact mesh vertices for the whole body, 0 = the default (est. 14k)
    public readonly Sync<int> VertexBudget = new(0);
    public readonly Sync<bool> FitToMesh = new(true);
    // m, how far the sole proxy sits inside the mesh
    public readonly Sync<float> SoleInset = new(0f);
    public readonly Sync<bool> ShowResidue = new(true);
    public readonly SyncRefList<Slot> ExtraBones = new();
    // empty = every skinned renderer active at equip, outermost surface only
    public readonly SyncRefList<SkinnedMeshRenderer> Renderers = new();
}
