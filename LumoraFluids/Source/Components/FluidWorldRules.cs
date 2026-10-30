// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;

namespace Lumora.Fluids;

// A world's fluid rules, on the world root. The defaults apply when it is missing.
[ComponentCategory("Physics/Fluids")]
[SingleInstancePerSlot]
public sealed class FluidWorldRules : Component
{
    public readonly Sync<FluidPourAccess> WhoMayPour = new(FluidPourAccess.Spawners);
    // L/s
    public readonly Sync<float> MaxPourRate = new(2f);
    // L; 0 = unbounded
    public readonly Sync<float> PersonShare = new(20f);
    public readonly Sync<bool> ShareCountsScripts = new(false);
    // L; 0 = unbounded
    public readonly Sync<float> WorldLitres = new(0f);
    public readonly Sync<int> LedgerBytesPerSecond = new(4096);
    // mL per region
    public readonly Sync<float> CoatLimit = new(200f);
    // mL
    public readonly Sync<float> WetCap = new(5f);
    public readonly Sync<bool> RemoveOnLeave = new(true);
    public readonly Sync<bool> StickToPeople = new(true);
    public readonly Sync<bool> SlowPeople = new(true);
    public readonly Sync<bool> AllowScooping = new(true);
    public readonly Sync<FluidKeeperAccess> KeeperAccess = new(FluidKeeperAccess.Anyone);
}
