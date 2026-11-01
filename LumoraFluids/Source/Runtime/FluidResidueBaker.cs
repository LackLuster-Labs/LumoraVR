// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The wearer's coat: bakes, merges, prints, drips, washes, wipes and cleans the local user's spots, and publishes them
// on their own FluidPerson. Only the wearer's machine runs it; every other machine just reads the spots.
//
// The baker keeps the exact amounts; FluidPerson carries them quantized to 0.05 mL with their birth second, and every
// machine works drying out of that. Anything that changes a spot rebases it: its birth becomes now and its amount what
// it holds now, so drying never runs twice over the same span.
//
// Off the person goes through the release ring and never makes goo out of nothing: a release only ever carries what
// left a spot. Onto the person is a Pickup, gathered per ledger cell to at most 4 a second, and the GPU particles the
// bake took leave the simulation the same frame (PendingRemoves).
//
// Water never anchors, so it never bakes; it leaves the skin wet instead. The skin collision reports where fluid ran
// over the wearer (FluidWetSample, a few cm patch each), and a substance that does not stick leaves a thin film there
// (WetFilmMl a patch, est.), topped up to that film and never past it, drying on the substance's DryTime. No particle
// leaves the simulation for it: the film is what a stream leaves behind, not the stream. -xlinka
public sealed class FluidResidueBaker
{
    // m, a bake within this many spacings of a spot on the same part joins it
    public const float MergeSpacings = 2f;
    // mL below which a spot is gone
    public const float GoneMl = 0.002f;
    // mL, the smallest footprint worth a release: what a published spot can show
    public const float MinPrintMl = FluidSpot.QuantumMl;
    // share of a sole spot one step leaves on the ground, per unit of surface stickiness (est.)
    public const float PrintShare = 0.3f;
    // s between drip entries from the whole coat
    public const double DripGap = 0.1;
    // mL, the most one drip carries (est.)
    public const float DripMl = 0.5f;
    // m2 a spot spreads over before it drips (a 1.5 cm disc, est.)
    public const float SpotArea = 7.07e-4f;
    // wetness e-folds this many times faster than the coat dries (est.): a water coat looks dry in about 4 min
    public const float WetnessRate = 8f;
    public const double PublishGap = 0.5;
    public const double CrowdPublishGap = 1.0;
    public const double PickupGap = 0.25;
    public const double CleanSeconds = 1.0;
    // mL of film one wet patch holds when soaked (a 3.5 cm patch at about 80 um, est.)
    public const float WetFilmMl = 0.1f;
    // particle frames on a patch before it counts as soaked, and the fewest that count at all
    public const int WetSoakContacts = 8;
    public const int WetMinContacts = 2;
    // m, a patch this close to a film spot of the same substance tops that spot up
    public const float WetMerge = 0.035f;
    // a substance stickier than this coats through anchors, not as a film
    public const float WetMaxStickiness = 0.05f;
    // mL under which a top-up only refreshes the wetness
    public const float WetMinTopUp = 0.005f;

    private static readonly ConditionalWeakTable<World, FluidResidueBaker> ByWorld = new();

    private readonly World _world;
    private readonly bool[] _used = new bool[FluidPerson.SpotCount];
    private readonly float3[] _local = new float3[FluidPerson.SpotCount];
    private readonly float3[] _normal = new float3[FluidPerson.SpotCount];
    private readonly float[] _ml = new float[FluidPerson.SpotCount];
    private readonly double[] _born = new double[FluidPerson.SpotCount];
    // When the spot first formed: cure counts from here, through every rebase.
    private readonly double[] _firstFormed = new double[FluidPerson.SpotCount];
    private readonly float[] _wet = new float[FluidPerson.SpotCount];
    private readonly int[] _part = new int[FluidPerson.SpotCount];
    private readonly uint[] _tint = new uint[FluidPerson.SpotCount];
    private readonly int[] _touched = new int[FluidPerson.SpotCount];
    private readonly int[] _substance = new int[FluidPerson.SpotCount];
    private readonly double[] _nextDrip = new double[FluidPerson.SpotCount];
    private readonly RefID[] _systems = new RefID[FluidPerson.TouchedCount];
    private readonly Dictionary<(int touched, int substance, uint cell, int part), float> _pickups = new();
    private double _nextPublish;
    private double _nextPickup;
    private double _lastDrip = double.NegativeInfinity;
    private double _cleanUntil = double.NegativeInfinity;
    private bool _dirty;
    private int _seed;
    private HumanoidRig? _rig;
    private UserRoot? _rigRoot;
    private double _nextRig;

    public FluidResidueBaker(World world)
    {
        _world = world;
        ByWorld.AddOrUpdate(world, this);
    }

    // Session seconds, from the world unless something else drives time (a test steps minutes in a few frames).
    public Func<double>? Clock { get; set; }

    private double Time => Clock?.Invoke() ?? _world.SessionSeconds;

    public static FluidResidueBaker? Find(World? world) => world != null && ByWorld.TryGetValue(world, out var baker) ? baker : null;

    // Set by the space: a frame substance index (as bake candidates carry it) to its component and system.
    public Func<int, FluidSubstance?>? FrameSubstance { get; set; }

    public FluidStickMode Mode { get; set; } = FluidStickMode.Sticky;

    // mL per part; FluidWorldRules.CoatLimit
    public float CoatLimitMl { get; set; } = 200f;

    // mL per part in Wet mode; FluidWorldRules.WetCap
    public float WetCapMl { get; set; } = 5f;

    // People coated in this world: past 8 the publish rate halves.
    public int CoatedPeople { get; set; }

    // Particle ids the bake took: the space sends them as RemoveParticles commands this frame.
    public readonly List<uint> PendingRemoves = new();

    // Every release this baker made, newest last, for the inspector and tests. Trimmed to 256.
    public readonly List<FluidRelease> Released = new();

    public int Spots
    {
        get
        {
            int n = 0;
            foreach (bool u in _used)
                n += u ? 1 : 0;
            return n;
        }
    }

    public bool IsCleaning => Time < _cleanUntil;

    public static float DryTimeFor(float dryTime, FluidStickMode mode)
        => mode == FluidStickMode.Wet && dryTime > 0f ? dryTime * 0.1f : dryTime;

    // How wet a spot still looks: its wetness at birth, fading WetnessRate times faster than it dries. A coat that
    // never dries never stops looking wet.
    public static float WetnessAt(in FluidSpot spot, double sessionSeconds, float dryTime)
    {
        if (spot.IsEmpty)
            return 0f;
        if (!(dryTime > 0f))
            return spot.Wetness;
        return spot.Wetness * MathF.Exp(-(float)spot.AgeAt(sessionSeconds) * WetnessRate / dryTime);
    }

    public float MillilitresOf(int index) => index >= 0 && index < _used.Length && _used[index] ? Now(index) : 0f;

    public float WetnessOf(int index)
    {
        if (index < 0 || index >= _used.Length || !_used[index])
            return 0f;
        float dry = DryTime(index);
        double age = Time - _born[index];
        return dry > 0f ? _wet[index] * MathF.Exp(-(float)age * WetnessRate / dry) : _wet[index];
    }

    public int PartOf(int index) => index >= 0 && index < _part.Length && _used[index] ? _part[index] : -1;

    public float CoatMl()
    {
        float sum = 0f;
        for (int i = 0; i < _used.Length; i++)
        {
            if (_used[i])
                sum += Now(i);
        }
        return sum;
    }

    public float PartMl(int part)
    {
        float sum = 0f;
        for (int i = 0; i < _used.Length; i++)
        {
            if (_used[i] && _part[i] == part)
                sum += Now(i);
        }
        return sum;
    }

    // Anchored, slow particles on the local user's body become spots. Candidates owned by anyone else are left for
    // the keeper's coat proposals. Returns how many were baked; the rest stay in the simulation. Candidates come in
    // the part bone's space, as the kernels bake them.
    public int Bake(ReadOnlySpan<FluidBakeCandidate> candidates, int localSlot)
    {
        if (Mode == FluidStickMode.Off || localSlot < 0)
            return 0;
        int baked = 0;
        double now = Time;
        foreach (var c in candidates)
        {
            int person = (int)(c.Owner >> 16);
            int part = (int)((c.Owner >> 8) & 0xFF);
            int frameSubstance = (int)(c.Owner & 0xFF);
            if (person != localSlot || part >= FluidBodyParts.Count || !(c.Volume > 0f) || !float.IsFinite(c.Volume))
                continue;
            float ml = c.Volume * 1e6f;
            float limit = Mode == FluidStickMode.Wet ? WetCapMl : CoatLimitMl;
            if (PartMl(part) + ml > limit)
                continue;
            var substance = FrameSubstance?.Invoke(frameSubstance);
            if (!TryTouched(substance, out int touched, out int index))
                continue;
            float spacing = substance != null ? MathF.Max(0.004f, substance.Spacing.Value) : 0.01f;
            if (!AddToSpot(part, c.Local, ml, c.Tint, touched, index, spacing * MergeSpacings, now))
                continue;
            PendingRemoves.Add(c.Id);
            var key = (touched, index, PickupCell(substance, part, c), part);
            _pickups.TryGetValue(key, out float sum);
            _pickups[key] = sum + ml;
            baked++;
        }
        return baked;
    }

    // Where fluid ran over the wearer since the last read: a non-sticking substance leaves its film, topped up to what
    // a patch holds. Returns the patches that changed the coat.
    public int Wet(IReadOnlyList<FluidWetSample> samples, int localSlot)
    {
        if (Mode == FluidStickMode.Off || localSlot < 0)
            return 0;
        int changed = 0;
        foreach (var w in samples)
        {
            if (w.Person != localSlot)
                continue;
            if (WetPatch(FrameSubstance?.Invoke(w.FrameSubstance), w.Part, w.Local, w.Contacts))
                changed++;
        }
        return changed;
    }

    // One patch of skin fluid ran over: the wearer's own read-back, or the keeper's proposal on a view-only machine
    // (FluidCoatRelay). True when it changed the coat.
    public bool WetPatch(FluidSubstance? substance, int part, float3 local, int contacts)
    {
        if (Mode == FluidStickMode.Off || part < 0 || part >= FluidBodyParts.Count || contacts < WetMinContacts)
            return false;
        if (!float.IsFinite(local.x + local.y + local.z))
            return false;
        if (substance != null && substance.Stickiness.Value > WetMaxStickiness)
            return false;
        if (!TryTouched(substance, out int touched, out int index))
            return false;
        double now = Time;
        float film = WetFilmMl * System.Math.Clamp(contacts / (float)WetSoakContacts, 0.25f, 1f);
        float limit = Mode == FluidStickMode.Wet ? WetCapMl : CoatLimitMl;
        int near = FilmNear(part, local, touched, index);
        float have = near >= 0 ? Now(near) : 0f;
        float add = MathF.Min(film - have, limit - PartMl(part));
        if (add < WetMinTopUp)
        {
            if (near >= 0 && _wet[near] < 0.99f)
            {
                Rebase(near, have, now);
                _wet[near] = 1f;
                return true;
            }
            return false;
        }
        if (near < 0 && Spots >= _used.Length)
            return false;
        uint tint = substance != null ? Rgba8(substance.Tint.Value) : 0xFFFFFFFFu;
        return AddToSpot(part, local, add, tint, touched, index, WetMerge, now);
    }

    // A bake the keeper's simulation made on this view-only wearer (FluidCoatRelay): the coat goes where it saw the
    // goo, under this wearer's own stick mode and limits, and a Pickup claims it from the cell, exactly as a bake from
    // this machine's own GPU would. The keeper removes the particles when it reads the Pickup. True when it took it.
    public bool ApplyCoat(FluidSubstance? substance, int part, float3 local, float ml, uint tint, uint cell)
    {
        if (Mode == FluidStickMode.Off || part < 0 || part >= FluidBodyParts.Count || !(ml > 0f) || !float.IsFinite(ml))
            return false;
        if (!float.IsFinite(local.x + local.y + local.z))
            return false;
        float limit = Mode == FluidStickMode.Wet ? WetCapMl : CoatLimitMl;
        if (PartMl(part) + ml > limit)
            return false;
        if (!TryTouched(substance, out int touched, out int index))
            return false;
        float spacing = substance != null ? MathF.Max(0.004f, substance.Spacing.Value) : 0.01f;
        if (!AddToSpot(part, local, ml, tint, touched, index, spacing * MergeSpacings, Time))
            return false;
        var key = (touched, index, cell, part);
        _pickups.TryGetValue(key, out float sum);
        _pickups[key] = sum + ml;
        return true;
    }

    // The ledger cell a bake's Pickup claims. The kernel's own cell field is its packed frame-space coordinates, not a
    // ledger index, so the cell is found from where the goo sits on the bone. A system this machine keeps already took
    // these particles out by id (PendingRemoves); its keeper side would take the Pickup's litres out of the cell again,
    // so that Pickup names no cell and only the coat moves. With no system (a test) the candidate's cell stands.
    private uint PickupCell(FluidSubstance? substance, int part, in FluidBakeCandidate c)
    {
        var system = substance?.Slot?.GetComponentInParents<FluidSystem>();
        if (system == null)
            return c.Cell;
        var bone = Bone(part);
        int index = bone != null && !ReferenceEquals(system.Keeper.Target, _world.LocalUser)
            ? FluidCoatPlacement.LedgerCellAt(system, bone.LocalPointToGlobal(c.Local))
            : FluidRelease.NoCell;
        return unchecked((uint)index);
    }

    // The film spot of this substance nearest a patch on the part, within WetMerge; -1 when there is none.
    private int FilmNear(int part, float3 local, int touched, int substance)
    {
        int best = -1;
        float bestDistance = WetMerge * WetMerge;
        for (int i = 0; i < _used.Length; i++)
        {
            if (!_used[i] || _part[i] != part || _touched[i] != touched || _substance[i] != substance)
                continue;
            float d = float3.DistanceSquared(_local[i], local);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    private static uint Rgba8(colorHDR c)
    {
        static uint B(float v) => (uint)System.Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return B(c.r) | (B(c.g) << 8) | (B(c.b) << 16) | (B(c.a) << 24);
    }

    // A footstep on the sole: every sole spot of that foot (and its toes) facing down leaves a share of itself on the
    // ground, so prints thin geometrically and fade over a handful of steps. Cured spots no longer transfer. Returns
    // the print in mL, 0 when it would be too faint to show.
    public float Footstep(int foot, float3 point, float surfaceStickiness = 1f)
    {
        if (Mode == FluidStickMode.Off || (foot != FluidBodyParts.LeftFoot && foot != FluidBodyParts.RightFoot))
            return 0f;
        int toes = foot == FluidBodyParts.LeftFoot ? FluidBodyParts.LeftToes : FluidBodyParts.RightToes;
        float share = System.Math.Clamp(PrintShare * surfaceStickiness, 0.2f, 0.4f);
        double now = Time;
        float print = 0f;
        for (int i = 0; i < _used.Length; i++)
        {
            if (_used[i] && (_part[i] == foot || _part[i] == toes) && FacesDown(i, 0.5f) && !Cured(i))
                print += Now(i) * share;
        }
        if (print < MinPrintMl)
            return 0f;
        for (int i = 0; i < _used.Length; i++)
        {
            if (!_used[i] || (_part[i] != foot && _part[i] != toes) || !FacesDown(i, 0.5f) || Cured(i))
                continue;
            float v = Now(i);
            Release(FluidReleaseKind.Footprint, point, float3.Zero, v * share, foot, i);
            Rebase(i, v - v * share, now);
        }
        return print;
    }

    // Water over a part: each spot there loses its share of Washability x the water, a fifth of that once cured.
    // Returns the mL washed off.
    public float Wash(int part, float waterMl)
    {
        if (!(waterMl > 0f))
            return 0f;
        double now = Time;
        float coat = PartMl(part);
        if (!(coat > 0f))
            return 0f;
        float washed = 0f;
        for (int i = 0; i < _used.Length; i++)
        {
            if (!_used[i] || _part[i] != part)
                continue;
            float v = Now(i);
            var s = SubstanceOf(i);
            float washability = s != null ? System.Math.Clamp(s.Washability.Value, 0f, 1f) : 1f;
            float take = MathF.Min(v, waterMl * (v / coat) * washability * (Cured(i) ? 0.2f : 1f));
            if (!(take > 0f))
                continue;
            washed += take;
            Release(FluidReleaseKind.Wash, World(i), float3.Zero, take, part, i);
            Rebase(i, v - take, now);
        }
        return washed;
    }

    // A hand sweeping over a coated part: a share of that part's coat moves onto the hand (and off it as a Wipe when the
    // hand cannot hold more). Returns the mL moved.
    public float Wipe(int part, int hand, float share)
    {
        if (part == hand || !(share > 0f))
            return 0f;
        double now = Time;
        float moved = 0f;
        for (int i = 0; i < _used.Length; i++)
        {
            if (!_used[i] || _part[i] != part || Cured(i))
                continue;
            float v = Now(i);
            float take = v * System.Math.Clamp(share, 0f, 1f);
            Rebase(i, v - take, now);
            moved += take;
            var palm = HandLocal(hand);
            if (!AddToSpot(hand, palm, take, _tint[i], _touched[i], _substance[i], 0.05f, now))
                Release(FluidReleaseKind.Wipe, World(i), float3.Zero, take, part, i);
        }
        return moved;
    }

    // Takes up to the asked mL off a hand's spots (a scoop's ooze, a fling), largest first. The caller releases it.
    public float TakeFromPart(int part, float ml, out int touched, out int substance, out uint tint)
    {
        touched = 0;
        substance = 0;
        tint = 0xFFFFFFFF;
        double now = Time;
        float taken = 0f;
        for (int pass = 0; pass < _used.Length && taken < ml; pass++)
        {
            int best = -1;
            float bestMl = 0f;
            for (int i = 0; i < _used.Length; i++)
            {
                if (_used[i] && _part[i] == part && Now(i) > bestMl)
                {
                    bestMl = Now(i);
                    best = i;
                }
            }
            if (best < 0)
                break;
            float take = MathF.Min(bestMl, ml - taken);
            touched = _touched[best];
            substance = _substance[best];
            tint = _tint[best];
            Rebase(best, bestMl - take, now);
            taken += take;
        }
        return taken;
    }

    // Coated parts with a spot within reach of a world point, as FluidBodyParts bits; one part left out.
    public int PartsNear(float3 point, float reach, int except)
    {
        int bits = 0;
        float r2 = reach * reach;
        for (int i = 0; i < _used.Length; i++)
        {
            if (!_used[i] || _part[i] == except || (bits & FluidBodyParts.Bit(_part[i])) != 0)
                continue;
            if (float3.DistanceSquared(World(i), point) <= r2)
                bits |= FluidBodyParts.Bit(_part[i]);
        }
        return bits;
    }

    // "Clean off": the whole coat falls away over a second.
    public void CleanOff() => _cleanUntil = Time + CleanSeconds;

    // Proposed spots for a view-only wearer: the keeper's simulation of this body baked them; this machine applies its
    // own mode and caps exactly as for its own GPU's candidates.
    public int ApplyProposal(ReadOnlySpan<FluidBakeCandidate> candidates, int localSlot) => Bake(candidates, localSlot);

    // Each update on the wearer's machine: cleaning, drips, spots that dried out, Pickup flushes and the publish.
    public void Update(FluidPerson? person)
    {
        double now = Time;
        RefreshRig(now);
        if (_cleanUntil > double.NegativeInfinity)
            Clean(now);
        Drip(now);
        for (int i = 0; i < _used.Length; i++)
        {
            if (_used[i] && Now(i) < GoneMl)
                Clear(i);
        }
        if (now >= _nextPickup && _pickups.Count > 0)
        {
            _nextPickup = now + PickupGap;
            FlushPickups();
        }
        if (person != null && _dirty && now >= _nextPublish)
        {
            _nextPublish = now + (CoatedPeople > 8 ? CrowdPublishGap : PublishGap);
            Publish(person, now);
        }
    }

    // After a respawn the new root has an empty FluidPerson: the local copy goes up again as it is.
    public void Republish(FluidPerson person)
    {
        _dirty = true;
        Publish(person, Time);
    }

    // The whole local copy written to the wearer's own FluidPerson.
    public void Publish(FluidPerson person, double now)
    {
        if (!person.IsLocalWearer || !person.HasFixedLengths)
            return;
        _dirty = false;
        PublishTouched(person);
        for (int i = 0; i < _used.Length; i++)
        {
            if (!_used[i])
            {
                person.WriteSpot(i, default);
                continue;
            }
            // Published from when it first formed, with the amount and wetness it would have had then, so every machine
            // dries it along the same curve and cures it at the same moment. A spot so old that its birth amount no
            // longer fits starts its clock again.
            float dry = DryTime(i);
            double age = now - _firstFormed[i];
            float back = dry > 0f ? MathF.Exp((float)age / dry) : 1f;
            if (!float.IsFinite(back) || back > 1e4f)
            {
                _firstFormed[i] = now;
                age = 0;
                back = 1f;
            }
            float wetBack = dry > 0f ? MathF.Exp((float)age * WetnessRate / dry) : 1f;
            float wet = float.IsFinite(wetBack) ? MathF.Min(1f, WetnessOf(i) * wetBack) : 1f;
            var spot = FluidSpot.Make(_local[i], Now(i) * back, FluidBodyParts.Nodes[_part[i]], _tint[i], _normal[i], 0.6f, wet,
                _touched[i], _substance[i], _firstFormed[i]);
            person.WriteSpot(i, spot);
        }
    }

    private void PublishTouched(FluidPerson person)
    {
        for (int t = 0; t < _systems.Length && t < person.Touched.Count; t++)
        {
            var packed = FluidPerson.PackRef(_systems[t]);
            if (person.Touched[t] != packed)
                person.Touched[t] = packed;
        }
    }

    private bool AddToSpot(int part, float3 local, float ml, uint tint, int touched, int substance, float mergeDistance, double now)
    {
        int merge = -1;
        float best = mergeDistance * mergeDistance;
        int free = -1;
        int sameNode = -1;
        float sameNodeDistance = float.MaxValue;
        for (int i = 0; i < _used.Length; i++)
        {
            if (!_used[i])
            {
                if (free < 0)
                    free = i;
                continue;
            }
            if (_part[i] != part)
                continue;
            float d = float3.DistanceSquared(_local[i], local);
            if (_touched[i] == touched && _substance[i] == substance && d <= best)
            {
                best = d;
                merge = i;
            }
            if (d < sameNodeDistance)
            {
                sameNodeDistance = d;
                sameNode = i;
            }
        }
        if (merge < 0 && free < 0)
            merge = sameNode;
        if (merge >= 0)
        {
            float v = Now(merge);
            float total = v + ml;
            _local[merge] = (_local[merge] * v + local * ml) / total;
            _tint[merge] = ml > v ? tint : _tint[merge];
            _wet[merge] = 1f;
            Rebase(merge, total, now);
            _normal[merge] = NormalAt(part, _local[merge]);
            return true;
        }
        if (free < 0)
            return false;
        _used[free] = true;
        _part[free] = part;
        _local[free] = local;
        _normal[free] = NormalAt(part, local);
        _tint[free] = tint;
        _touched[free] = touched;
        _substance[free] = substance;
        _wet[free] = 1f;
        _ml[free] = ml;
        _born[free] = now;
        _firstFormed[free] = now;
        // A drop takes its substance's period to gather before the first one falls.
        _nextDrip[free] = now + DripPeriod(SubstanceOf(free));
        _dirty = true;
        return true;
    }

    // Spots facing down past their substance's film thickness shed a drop at the substance's pace, one entry for the
    // whole coat every 100 ms at most. Cured spots hold.
    private void Drip(double now)
    {
        if (now - _lastDrip < DripGap)
            return;
        for (int i = 0; i < _used.Length; i++)
        {
            if (!_used[i] || now < _nextDrip[i] || Cured(i) || !FacesDown(i, 0.3f))
                continue;
            var s = SubstanceOf(i);
            float hold = HoldMl(s);
            float v = Now(i);
            if (v <= hold)
                continue;
            float drop = MathF.Min(v - hold, DripMl);
            Rebase(i, v - drop, now);
            _nextDrip[i] = now + DripPeriod(s);
            _lastDrip = now;
            Release(FluidReleaseKind.Drip, World(i), float3.Zero, drop, _part[i], i);
            return;
        }
    }

    // A film holds without running while it is thinner than the larger of its yield height and its capillary length.
    public static float HoldMl(FluidSubstance? s)
    {
        float rho = s != null ? MathF.Max(100f, s.Density.Value) : 1000f;
        float yield = s != null ? MathF.Max(0f, s.YieldStress.Value) : 0f;
        float gamma = s != null ? MathF.Max(0f, s.SurfaceTension.Value) : 0.072f;
        float h = MathF.Max(yield / (rho * 9.81f), MathF.Sqrt(gamma / (rho * 9.81f)));
        return SpotArea * h * 1e6f;
    }

    // Seconds between drops: thin liquids drip about once a second, the thickest every 6 s (est.).
    public static double DripPeriod(FluidSubstance? s)
    {
        float mu = s != null ? MathF.Max(1e-4f, s.Viscosity.Value) : 0.001f;
        return System.Math.Clamp(1.0 + 2.0 * System.Math.Log10(1.0 + mu / 0.01), 1.0, 6.0);
    }

    private void Clean(double now)
    {
        if (now >= _cleanUntil)
        {
            for (int i = 0; i < _used.Length; i++)
            {
                if (_used[i])
                {
                    Release(FluidReleaseKind.Drop, World(i), float3.Zero, Now(i), _part[i], i);
                    Clear(i);
                }
            }
            _cleanUntil = double.NegativeInfinity;
            return;
        }
        // One spot a frame while it lasts, largest first, so the ring never sees a burst.
        int best = -1;
        float bestMl = 0f;
        for (int i = 0; i < _used.Length; i++)
        {
            if (_used[i] && Now(i) > bestMl)
            {
                bestMl = Now(i);
                best = i;
            }
        }
        if (best >= 0)
        {
            Release(FluidReleaseKind.Drop, World(best), float3.Zero, bestMl, _part[best], best);
            Clear(best);
        }
    }

    private void FlushPickups()
    {
        foreach (var entry in _pickups)
        {
            var (touched, substance, cell, part) = entry.Key;
            var bone = Bone(part);
            var at = bone != null ? bone.GlobalPosition : float3.Zero;
            Write(new FluidRelease(FluidReleaseKind.Pickup, at, float3.Zero, entry.Value * 1e-3f, FluidBodyParts.Nodes[part],
                substance, ++_seed, touched, unchecked((int)cell)));
        }
        _pickups.Clear();
    }

    // A release carries the substance as FluidRelease packs it: its index in the system, and the system as its Touched
    // index on this person.
    private void Release(FluidReleaseKind kind, float3 point, float3 velocity, float ml, int part, int spot)
    {
        if (!(ml > 0f))
            return;
        int touched = spot >= 0 ? _touched[spot] : 0;
        int substance = spot >= 0 ? _substance[spot] : 0;
        Write(new FluidRelease(kind, point, velocity, ml * 1e-3f, FluidBodyParts.Nodes[System.Math.Clamp(part, 0, FluidBodyParts.Count - 1)],
            substance, ++_seed, touched, FluidRelease.NoCell));
    }

    private void Write(in FluidRelease release)
    {
        if (Released.Count >= 256)
            Released.RemoveAt(0);
        Released.Add(release);
        var person = FluidPerson.Ensure(_world);
        if (person != null && person.IsLocalWearer && person.HasFixedLengths)
        {
            // The system a release names by its Touched index goes up with it, never after it: a Pickup read before
            // its Touched entry lands names no system and is lost.
            PublishTouched(person);
            person.WriteRelease(release);
        }
        _dirty = true;
    }

    // A hand query uses the same provenance as a coat; its Touched entry must precede the Pickup. -xlinka
    public bool PreparePickup(FluidSubstance substance, out int touched, out int index)
    {
        touched = 0;
        index = 0;
        if (substance == null || substance.IsDestroyed || substance.World != _world
            || substance.Slot?.GetComponentInParents<FluidSystem>() is not { IsDestroyed: false } system
            || system.Substances.IndexOf(substance) < 0)
            return false;
        var person = FluidPerson.Ensure(_world);
        if (person == null || !person.IsLocalWearer || !person.HasFixedLengths || !TryTouched(substance, out touched, out index))
            return false;
        PublishTouched(person);
        return true;
    }

    private bool TryTouched(FluidSubstance? substance, out int touched, out int index)
    {
        touched = 0;
        index = 0;
        if (substance == null || substance.IsDestroyed)
            return true;
        var system = substance.Slot?.GetComponentInParents<FluidSystem>();
        if (system == null)
            return true;
        index = System.Math.Max(0, system.Substances.IndexOf(substance));
        var id = system.ReferenceID;
        int free = -1;
        for (int t = 0; t < _systems.Length; t++)
        {
            if (_systems[t] == id)
            {
                touched = t;
                return true;
            }
            if (free < 0 && (_systems[t].IsNull || !SpotUses(t)))
                free = t;
        }
        if (free < 0)
            return false;
        _systems[free] = id;
        touched = free;
        _dirty = true;
        return true;
    }

    private bool SpotUses(int touched)
    {
        // Held fluid keeps its source even when no coat spot uses that Touched entry. Reusing it would send the
        // eventual hand release into another system. -xlinka
        var person = FluidPerson.FindFor(_world.LocalUser);
        if (person != null)
            foreach (var held in person.Held)
                if (held.x > 0 && (held.z & 255) == touched)
                    return true;
        for (int i = 0; i < _used.Length; i++)
        {
            if (_used[i] && _touched[i] == touched)
                return true;
        }
        return false;
    }

    private FluidSubstance? SubstanceOf(int i)
    {
        var id = _systems[_touched[i]];
        if (id.IsNull)
            return null;
        return _world.ReferenceController.GetObjectOrNull(id) is FluidSystem system && _substance[i] < system.Substances.Count
            ? system.Substances[_substance[i]]
            : null;
    }

    private float DryTime(int i)
    {
        var s = SubstanceOf(i);
        return DryTimeFor(s != null ? s.DryTime.Value : 900f, Mode);
    }

    private bool Cured(int i)
    {
        var s = SubstanceOf(i);
        if (s == null || !(s.CureTime.Value > 0f))
            return false;
        return Time - _firstFormed[i] >= s.CureTime.Value;
    }

    private float Now(int i)
    {
        float dry = DryTime(i);
        if (!(dry > 0f))
            return _ml[i];
        return _ml[i] * MathF.Exp(-(float)(Time - _born[i]) / dry);
    }

    private void Rebase(int i, float ml, double now)
    {
        _wet[i] = WetnessOf(i);
        _ml[i] = MathF.Max(0f, ml);
        _born[i] = now;
        _dirty = true;
        if (_ml[i] < GoneMl)
            Clear(i);
    }

    private void Clear(int i)
    {
        _used[i] = false;
        _ml[i] = 0f;
        _dirty = true;
    }

    private void RefreshRig(double now)
    {
        var root = _world.LocalUser?.Root;
        if (root == _rigRoot && now < _nextRig && (_rig == null || !_rig.IsDestroyed))
            return;
        _rigRoot = root;
        _nextRig = now + 1.0;
        _rig = null;
        if (root != null && !root.IsDestroyed)
            FluidPeopleGather.WornAvatar(root, out _rig);
    }

    private Slot? Bone(int part)
    {
        if (_rig == null || _rig.IsDestroyed)
            RefreshRig(double.PositiveInfinity);
        return FluidBodyParts.BoneOf(_rig, part);
    }

    private float3 World(int i)
    {
        var bone = Bone(_part[i]);
        return bone != null ? bone.LocalPointToGlobal(_local[i]) : _local[i];
    }

    private bool FacesDown(int i, float threshold)
    {
        var bone = Bone(_part[i]);
        if (bone == null)
            return false;
        var n = bone.LocalDirectionToGlobal(_normal[i]);
        float len = n.Length;
        return len > 1e-6f && n.y / len < -threshold;
    }

    // A spot's outward direction in bone space: away from the bone's own segment, which for a sole points down.
    private float3 NormalAt(int part, float3 local)
    {
        var bone = Bone(part);
        var axis = float3.Zero;
        if (bone != null)
        {
            var child = ChildPoint(part);
            if (child.HasValue)
                axis = bone.GlobalPointToLocal(child.Value);
        }
        float lengthSquared = axis.LengthSquared;
        float t = lengthSquared > 1e-12f ? System.Math.Clamp(float3.Dot(local, axis) / lengthSquared, 0f, 1f) : 0f;
        var outward = local - axis * t;
        float len = outward.Length;
        return len > 1e-6f ? outward / len : float3.Up;
    }

    private float3? ChildPoint(int part)
    {
        BodyNode child = part switch
        {
            FluidBodyParts.LeftFoot => BodyNode.LeftToes,
            FluidBodyParts.RightFoot => BodyNode.RightToes,
            FluidBodyParts.LeftLowerLeg => BodyNode.LeftFoot,
            FluidBodyParts.RightLowerLeg => BodyNode.RightFoot,
            FluidBodyParts.LeftUpperLeg => BodyNode.LeftLowerLeg,
            FluidBodyParts.RightUpperLeg => BodyNode.RightLowerLeg,
            FluidBodyParts.LeftLowerArm => BodyNode.LeftHand,
            FluidBodyParts.RightLowerArm => BodyNode.RightHand,
            FluidBodyParts.LeftUpperArm => BodyNode.LeftLowerArm,
            FluidBodyParts.RightUpperArm => BodyNode.RightLowerArm,
            FluidBodyParts.LeftHand => BodyNode.LeftMiddleFinger_Proximal,
            FluidBodyParts.RightHand => BodyNode.RightMiddleFinger_Proximal,
            FluidBodyParts.Chest => BodyNode.Neck,
            _ => BodyNode.NONE,
        };
        var slot = child != BodyNode.NONE ? _rig?.TryGetBone(child) : null;
        return slot != null && !slot.IsDestroyed ? slot.GlobalPosition : null;
    }

    // Where coat wiped onto a hand sits: the palm, a little past the wrist along the hand.
    private float3 HandLocal(int hand)
    {
        var bone = Bone(hand);
        var child = ChildPoint(hand);
        if (bone == null || !child.HasValue)
            return float3.Zero;
        return bone.GlobalPointToLocal(child.Value) * 0.5f;
    }
}
