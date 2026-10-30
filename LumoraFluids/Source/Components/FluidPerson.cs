// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A person's fluid state, on their own user root, written only by their own machine. Every member is session-only
// (a user root is never saved) and fixed in length; readers trust a FluidPerson only when its RefID was minted by
// the wearer and its arrays are the fixed lengths (FluidProvenance). Drying is computed on every machine from the
// spots' birth, never sent.
//
// Only the wearer's machine makes one, on its own root. Every array knows its length and sizes itself where it is
// minted, inside the creation, so the whole first state rides in the creation record and never grows after that.
// -xlinka
[ComponentCategory("Users/Avatar")]
public sealed class FluidPerson : UserRootComponent
{
    public const int SpotCount = 48;
    public const int HeldCount = 2;
    public const int PourCount = 4;
    public const int LimbCount = 4;
    public const int TouchedCount = 8;
    public const int ReleaseCount = 64;
    public const int RequestCount = 32;
    public const int ReportCount = 128;

    // Gpu: the tier in the low 4 bits, then what keeper election asks of a candidate.
    public const int GpuTierMask = 15;
    public const int GpuFocused = 16;
    public const int GpuHealthy = 32;
    public const int GpuRealTime = 64;

    [NonPersistent]
    public readonly WearerSync<FluidStickMode> Stick = new(FluidStickMode.Sticky);
    // FluidGpuTier, plus bits: focused on this world, budget healthy, drawing in real time
    [NonPersistent]
    public readonly WearerSync<int> Gpu = new(0);
    [NonPersistent]
    [HideInInspector]
    public readonly WearerArray<float4> Spots = new(SpotCount);
    [NonPersistent]
    [HideInInspector]
    public readonly WearerArray<int4> SpotMeta = new(SpotCount);
    [NonPersistent]
    [HideInInspector]
    public readonly WearerArray<int4> Held = new(HeldCount);
    // 2 hands x 2: a held tool's live pour
    [NonPersistent]
    [HideInInspector]
    public readonly WearerArray<int4> Pour = new(PourCount);
    [NonPersistent]
    [HideInInspector]
    public readonly WearerArray<float4> Limbs = new(LimbCount);
    // scalar copies of Limbs, so a generic On Value Changed can watch them
    [NonPersistent]
    public readonly WearerSync<int> SnapCount = new(0);
    [NonPersistent]
    public readonly WearerSync<int> StuckBits = new(0);
    // contact bits over FluidBodyParts, latched for one publish
    [NonPersistent]
    public readonly WearerSync<int> Touching = new(0);
    // x, y = packed RefID of the user
    [NonPersistent]
    public readonly WearerSync<int4> LastSplashedBy = new();
    [NonPersistent]
    [HideInInspector]
    public readonly WearerArray<int4> Touched = new(TouchedCount);
    [NonPersistent]
    [HideInInspector]
    public readonly WearerArray<int4> Releases = new(ReleaseCount);
    [NonPersistent]
    public readonly WearerSync<int> ReleaseSequence = new(0);
    [NonPersistent]
    [HideInInspector]
    public readonly WearerArray<int4> Requests = new(RequestCount);
    [NonPersistent]
    public readonly WearerSync<int> RequestSequence = new(0);
    // the keeper's report ring, 64 entries x 2
    [NonPersistent]
    [HideInInspector]
    public readonly WearerArray<int4> Report = new(ReportCount);
    [NonPersistent]
    public readonly WearerSync<int> ReportSequence = new(0);
    [NonPersistent]
    public readonly WearerSync<int> PourNumber = new(0);

    public User? Wearer => Slot?.ActiveUserRoot?.ActiveUser;

    public bool IsLocalWearer => Wearer is { } wearer && wearer == World?.LocalUser;

    // Minted by the wearer's own machine: the one check a forged copy on someone's root cannot pass.
    public bool MintedByWearer => Wearer is { } wearer && ReferenceID.GetUserByte() == wearer.AllocationID.Value;

    public bool HasFixedLengths => FluidProvenance.HasFixedLengths(this);

    public bool IsTrusted => FluidProvenance.IsTrusted(this);

    public FluidGpuTier GpuTier => (FluidGpuTier)System.Math.Clamp(Gpu.Value & GpuTierMask, 0, (int)FluidGpuTier.Ultra);

    public bool IsViewOnly => GpuTier == FluidGpuTier.None;

    // The local user's own FluidPerson, attached when missing, with the Self-ring actions beside it. Null when there
    // is no local user root yet. Safe to call every frame: one registered-component scan.
    public static FluidPerson? Ensure(World world)
    {
        var user = world?.LocalUser;
        var root = user?.Root;
        if (root == null || root.IsDestroyed || root.Slot == null || root.Slot.IsDestroyed)
            return null;
        var person = FluidProvenance.Of(user);
        if (person == null)
        {
            person = root.Slot.AttachComponent<FluidPerson>();
            person.SizeArrays();
        }
        if (root.Slot.GetComponent<FluidContextActions>() == null)
            root.Slot.AttachComponent<FluidContextActions>();
        // The world's fluid space attaches the wearer through here, so the people pass starts with it.
        FluidPeopleLink.Attach(FluidSpace.Find(world));
        return person;
    }

    // The FluidPerson a reader trusts for this user (FluidProvenance): minted by them, fixed lengths, undriven, the
    // oldest if several pass.
    public static FluidPerson? FindFor(User? user) => FluidProvenance.Of(user);

    public FluidSpot ReadSpot(int index)
        => index >= 0 && index < Spots.Count && index < SpotMeta.Count ? FluidSpot.Read(Spots[index], SpotMeta[index]) : default;

    // Wearer only.
    public void WriteSpot(int index, in FluidSpot spot)
    {
        if (index < 0 || index >= Spots.Count || index >= SpotMeta.Count)
            return;
        spot.Write(out var s, out var m);
        if (Spots[index] != s)
            Spots[index] = s;
        if (SpotMeta[index] != m)
            SpotMeta[index] = m;
    }

    // Wearer only: one entry pair on a ring, then the sequence bumped in the same frame.
    public void WriteRelease(in FluidRelease release)
    {
        release.Write(out var a, out var b);
        WriteRing(Releases, ReleaseSequence, a, b);
    }

    public void WriteRequest(int4 a, int4 b) => WriteRing(Requests, RequestSequence, a, b);

    public void WriteReport(int4 a, int4 b) => WriteRing(Report, ReportSequence, a, b);

    // The entry pair a ring holds for a sequence number, for readers that keep their own last-seen sequence.
    public static bool TryReadRing(SyncArray<int4> ring, int sequence, out int4 a, out int4 b)
    {
        int entries = ring.Count / 2;
        if (entries <= 0)
        {
            a = b = default;
            return false;
        }
        int at = (int)((uint)sequence % (uint)entries) * 2;
        a = ring[at];
        b = ring[at + 1];
        return true;
    }

    public static int4 PackRef(RefID id)
    {
        ulong raw = id.RawValue;
        return new int4(unchecked((int)(uint)raw), unchecked((int)(uint)(raw >> 32)), 0, 0);
    }

    public static RefID UnpackRef(int4 packed) => new(((ulong)(uint)packed.y << 32) | (uint)packed.x);

    private static void WriteRing(SyncArray<int4> ring, Sync<int> sequence, int4 a, int4 b)
    {
        int entries = ring.Count / 2;
        if (entries <= 0)
            return;
        int seq = sequence.Value;
        int at = (int)((uint)seq % (uint)entries) * 2;
        ring[at] = a;
        ring[at + 1] = b;
        sequence.Value = seq + 1;
    }

    // Right after the attach, on the wearer's machine, in the same frame: the sizing appends ride in the creation's
    // own batch, which the host does not judge. Writes from inside a member's initialization are never sent.
    private void SizeArrays()
    {
        Spots.EnsureLength();
        SpotMeta.EnsureLength();
        Held.EnsureLength();
        Pour.EnsureLength();
        Limbs.EnsureLength();
        Touched.EnsureLength();
        Releases.EnsureLength();
        Requests.EnsureLength();
        Report.EnsureLength();
    }
}
