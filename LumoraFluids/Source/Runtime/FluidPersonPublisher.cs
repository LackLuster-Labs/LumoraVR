// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The wearer's side of FluidPerson: keeps it on the local user's own root (again after a respawn), mirrors the stick
// mode from the setting, and publishes what the summary says about this body at the plan's rates: Touching and Limbs
// at 4 Hz on change (a touch shorter than a publish is latched for one), Gpu at most once a second.
//
// The summary's contact and limb records for the local person's slot also feed this machine's own footing, haptics
// and sounds, two frames after the GPU saw them; the arrays below are what they read. A view-only wearer gets the same
// from the keeper's coat proposals instead (ApplyProposal). -xlinka
public sealed class FluidPersonPublisher
{
    public const double PublishGap = 0.25;
    public const double GpuGap = 1.0;
    public const float StuckBit = 0.2f;
    public const double ProposalTimeout = 2.5;

    private readonly World _world;
    private readonly FluidResidueBaker _baker;
    private readonly uint[] _gpuSnaps = new uint[FluidBodyParts.Limbs.Length];
    private readonly bool[] _gpuSnapsSeen = new bool[FluidBodyParts.Limbs.Length];
    private readonly int[] _snapsPerLimb = new int[FluidBodyParts.Limbs.Length];
    private UserRoot? _root;
    private int _latched;
    private double _nextPublish;
    private double _nextGpu;
    private double _nextModeRead;
    private bool _limbsChanged;
    private readonly double[] _proposalLimbAt = new double[FluidBodyParts.Limbs.Length];
    private double _proposalContactAt = double.NegativeInfinity;
    private bool _proposals;
    private int _localSlot = -1;
    public int SummaryVersion { get; private set; }
    public double LastSummaryTime { get; private set; } = double.NegativeInfinity;

    public FluidPersonPublisher(World world, FluidResidueBaker baker)
    {
        _world = world;
        _baker = baker;
        Array.Fill(Substance, -1);
        Array.Fill(_proposalLimbAt, double.NegativeInfinity);
        AvatarIK.Footstep += OnFootstep;
    }

    // The world is going: stop hearing footsteps.
    public void Dispose() => AvatarIK.Footstep -= OnFootstep;

    public int Footsteps { get; private set; }

    public FluidPerson? Person { get; private set; }

    // From the last summary for the local person, by FluidBodyParts index: m of depth, m/s of relative speed, and the
    // dominant substance's frame index (-1 where nothing touches).
    public readonly float[] Depth = new float[FluidBodyParts.Count];
    public readonly float[] Speed = new float[FluidBodyParts.Count];
    public readonly int[] Substance = new int[FluidBodyParts.Count];

    // Per limb (FluidBodyParts.Limbs order).
    public readonly float[] Stuck = new float[FluidBodyParts.Limbs.Length];
    public readonly int[] Strands = new int[FluidBodyParts.Limbs.Length];
    public readonly float[] LastSnapLength = new float[FluidBodyParts.Limbs.Length];

    public int TouchBits { get; private set; }

    public int SnapTotal { get; private set; }

    public int Republished { get; private set; }

    // A strand let go of a limb: the limb, and how long it was in m. Fires on the wearer's machine.
    public event Action<int, float>? Snapped;

    // Called every update; one reference compare unless the local user root changed. A new root (a respawn rebuilt it)
    // gets its FluidPerson and the Self-ring actions back, and the coat goes up again from the baker's copy.
    public FluidPerson? EnsureAttached()
    {
        var root = _world.LocalUser?.Root;
        if (root == _root && Person != null && !Person.IsDestroyed)
            return Person;
        bool respawn = _root != null && root != _root;
        _root = root;
        Person = FluidPerson.Ensure(_world);
        if (Person != null)
        {
            Mirror(Person, force: true);
            if (respawn || _baker.Spots > 0)
            {
                _baker.Republish(Person);
                Republished++;
            }
            _limbsChanged = true;
        }
        return Person;
    }

    // The summary's records for the local person's slot.
    public void TakeSummary(in FluidSummaryView view, int localSlot)
    {
        Array.Clear(Depth);
        Array.Clear(Speed);
        Array.Fill(Substance, -1);
        TouchBits = 0;
        SummaryVersion++;
        LastSummaryTime = _world.Time.TotalTime;
        _proposals = false;
        if (_localSlot != localSlot)
        {
            Array.Clear(_gpuSnapsSeen);
            _localSlot = localSlot;
        }
        for (int l = 0; l < Stuck.Length; l++)
            ClearLimb(l);
        if (localSlot < 0)
            return;
        foreach (var c in view.Contacts)
        {
            if (c.Person != (uint)localSlot)
                continue;
            int part = (int)(c.Part & 0xFF);
            if (part >= FluidBodyParts.Count)
                continue;
            TouchBits |= FluidBodyParts.Bit(part);
            if (c.Depth >= Depth[part])
            {
                Depth[part] = c.Depth;
                Substance[part] = (int)((c.Part >> 8) & 0xFF);
            }
            Speed[part] = MathF.Max(Speed[part], c.Speed);
        }
        _latched |= TouchBits;
        foreach (var limb in view.Limbs)
        {
            if (limb.Person != (uint)localSlot || limb.Limb >= (uint)Stuck.Length)
                continue;
            TakeLimb((int)limb.Limb, limb.Stuck, (int)limb.Strands, limb.Snaps, limb.LastSnapLength);
        }
    }

    // A view-only wearer: contact and limbs as the keeper's simulation of this body found them.
    public void ApplyProposal(int touchBits, ReadOnlySpan<FluidLimbRecord> limbs, bool contactsChanged = true)
    {
        double now = _world.Time.TotalTime;
        SummaryVersion++;
        LastSummaryTime = now;
        _proposals = true;
        Array.Clear(Depth);
        Array.Clear(Speed);
        Array.Fill(Substance, -1);
        if (contactsChanged)
        {
            TouchBits = touchBits;
            _latched |= touchBits;
            _proposalContactAt = now;
        }
        foreach (var limb in limbs)
        {
            if (limb.Limb >= (uint)Stuck.Length)
                continue;
            int l = (int)limb.Limb;
            _proposalLimbAt[l] = now;
            TakeLimb(l, limb.Stuck, (int)limb.Strands, limb.Snaps, limb.LastSnapLength);
        }
    }

    // Summaries are complete; proposals are deltas. Missing summary limbs clear immediately, while a stopped
    // keeper's proposal state expires after its keepalive window. Neither leaves a foot stuck forever. -xlinka
    private void ClearLimb(int limb)
    {
        if (Stuck[limb] != 0f || Strands[limb] != 0)
            _limbsChanged = true;
        Stuck[limb] = 0f;
        Strands[limb] = 0;
    }

    // Each update after the summary was taken: the coat, then the members at their rates.
    public void Update(IFluidGpu? gpu, bool focused, bool budgetHealthy)
    {
        double now = _world.Time.TotalTime;
        if (_proposals)
        {
            if (now - _proposalContactAt > ProposalTimeout)
                TouchBits = 0;
            for (int l = 0; l < Stuck.Length; l++)
                if (now - _proposalLimbAt[l] > ProposalTimeout)
                    ClearLimb(l);
        }
        if (now >= _nextModeRead)
        {
            _nextModeRead = now + PublishGap;
            _baker.Mode = ReadStickMode();
        }
        var person = EnsureAttached();
        _baker.Update(person);
        if (person == null || !person.IsLocalWearer || !person.HasFixedLengths)
            return;
        Mirror(person, force: false);

        if (now >= _nextGpu)
        {
            _nextGpu = now + GpuGap;
            int tier = gpu != null && gpu.Path == FluidGpuPath.Gpu ? (int)gpu.Tier : (int)FluidGpuTier.None;
            int bits = (tier & FluidPerson.GpuTierMask)
                | (focused ? FluidPerson.GpuFocused : 0)
                | (budgetHealthy ? FluidPerson.GpuHealthy : 0)
                | (gpu?.DrawingInRealTime == true ? FluidPerson.GpuRealTime : 0);
            if (person.Gpu.Value != bits)
                person.Gpu.Value = bits;
        }

        if (now < _nextPublish)
            return;
        _nextPublish = now + PublishGap;
        if (person.Touching.Value != _latched)
            person.Touching.Value = _latched;
        _latched = TouchBits;
        if (_limbsChanged)
        {
            _limbsChanged = false;
            int stuckBits = 0;
            for (int l = 0; l < Stuck.Length; l++)
            {
                var value = new float4(Stuck[l], Strands[l], SnapTotalOf(l), LastSnapLength[l]);
                if (person.Limbs[l] != value)
                    person.Limbs[l] = value;
                if (Stuck[l] > StuckBit)
                    stuckBits |= 1 << l;
            }
            if (person.StuckBits.Value != stuckBits)
                person.StuckBits.Value = stuckBits;
            if (person.SnapCount.Value != SnapTotal)
                person.SnapCount.Value = SnapTotal;
        }
    }

    // A planted foot of the local user's own avatar prints what its sole carries. Tracked feet send no footsteps; their
    // prints come later from sole contact.
    private void OnFootstep(AvatarIK ik, AvatarFootstep step)
    {
        if (ik == null || ik.IsDestroyed || ik.World != _world)
            return;
        var root = _world.LocalUser?.Root;
        if (root == null || ik.Slot?.ActiveUserRoot != root)
            return;
        Footsteps++;
        _baker.Footstep(step.Side == Chirality.Right ? FluidBodyParts.RightFoot : FluidBodyParts.LeftFoot, step.Position);
    }

    // The stick mode a user picks on their Self ring. It follows the account through FluidAccountSync, which stamps
    // the change so "newer wins" sees it; the first read attaches that sync.
    public static void SetStickMode(FluidStickMode mode) => FluidAccountSync.SetStickMode(mode);

    public static FluidStickMode ReadStickMode()
    {
        var mode = FluidAccountSync.StickMode;
        return mode >= FluidStickMode.Sticky && mode <= FluidStickMode.Off ? mode : FluidStickMode.Sticky;
    }

    private int SnapTotalOf(int limb) => _snapsPerLimb[limb];

    private void TakeLimb(int l, float stuck, int strands, uint snaps, float lastLength)
    {
        stuck = System.Math.Clamp(float.IsFinite(stuck) ? stuck : 0f, 0f, 1f);
        if (MathF.Abs(stuck - Stuck[l]) > 0.02f || strands != Strands[l])
            _limbsChanged = true;
        Stuck[l] = stuck;
        Strands[l] = strands;
        // The GPU's counter is per slot and starts over when a slot is handed out again; only steps up count.
        if (_gpuSnapsSeen[l] && snaps > _gpuSnaps[l])
        {
            int added = (int)System.Math.Min(snaps - _gpuSnaps[l], 64u);
            _snapsPerLimb[l] += added;
            SnapTotal += added;
            LastSnapLength[l] = lastLength;
            _limbsChanged = true;
            for (int i = 0; i < added; i++)
                Snapped?.Invoke(l, lastLength);
        }
        _gpuSnaps[l] = snaps;
        _gpuSnapsSeen[l] = true;
    }

    private void Mirror(FluidPerson person, bool force)
    {
        if (!person.IsLocalWearer)
            return;
        var mode = _baker.Mode;
        if (force || person.Stick.Value != mode)
            person.Stick.Value = mode;
    }
}
