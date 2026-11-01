// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Coats for people whose machine never simulates (Android, a machine with no compute). Plan 9.3: the keeper says what
// its own simulation of that person found, and only the wearer acts on it. One per world, beside the people pass.
//
// Keeper side (a GPU machine that keeps the system the fluid is in): the bake candidates its GPU made on a view-only
// person (anchored, settled goo), the patches its skin pass saw fluid run over, and that person's contact bits and stuck
// limbs become coat proposals. Each coat is one entry that says where on the part it goes and which ledger cell it
// counts in (FluidCoatPlacement), so the wearer's coat lands where the keeper saw the fluid, and the wearer's Pickup
// claims exactly that cell. A guest keeper's proposals ride its judged reports (the ledger's FluidCoatProposals); a host
// that keeps writes them on its own report ring, which nothing judges. Either way they share the keeper's report bytes
// with the ledger's cells, so they get BudgetShare of them: contacts first, then the newest bakes, then wet patches no
// one proposed lately. A particle is proposed once, and never again while the keeper still sees it. It stays in the
// keeper's simulation until the wearer's Pickup claims it: the claim book keeps which particles went to which wearer
// from which cell, so the ledger's keeper side takes exactly those out by id (Claim) and only the rest of a Pickup
// from the cell. Taking the cell's nearest fluid instead would leave the coated goo behind and take water.
//
// Wearer side (the view-only person's own machine): reads every keeper's ring with its own cursor, keeps the proposals
// meant for this user from that system's keeper (FluidCoatProposals.For), and applies them as if its own GPU had found
// them: bakes through the baker (its stick mode, limits, spots, and a Pickup for the cell), wet patches as the film,
// contacts and limbs through the publisher. -xlinka
public sealed class FluidCoatRelay
{
    // share of the keeper's report bytes (half of LedgerBytesPerSecond) coats may take
    public const float BudgetShare = 0.4f;
    // s of budget that may be saved up
    public const double BudgetBurst = 0.375;
    public const double ContactGap = 0.25;
    // s between contact entries while the bits do not change
    public const double ContactKeepAlive = 1.0;
    // a limb's stuck strength is sent again once it moved this much, or crossed the publisher's stuck bit
    public const float LimbStep = 1f / 16f;
    public const int MaxQueuedBakes = 64;
    public const int MaxQueuedWets = 96;
    // s before the same patch of skin is proposed wet again; once it was proposed soaked, only to keep it looking wet
    public const double WetRepeat = 1.5;
    public const double WetSoakedRepeat = 5.0;
    // m, the patch a wet sample is remembered by (the skin pass's own cell)
    public const float WetPatch = 0.035f;
    // s a proposed particle is remembered after the keeper last saw it, so later bake frames do not propose it again,
    // and how long its claim waits for the wearer's Pickup
    public const double ProposedFor = 30.0;

    private readonly World _world;
    private readonly FluidPeopleGather _people;
    private readonly FluidResidueBaker _baker;
    private readonly FluidPersonPublisher _publisher;
    private readonly Func<int, FluidSubstance?> _frameSubstance;

    // keeper
    private readonly Dictionary<uint, double> _proposed = new();
    private readonly Dictionary<(User, FluidSystem, int), List<(uint Id, double Litres, double At)>> _claims = new();
    private readonly List<(User, FluidSystem, int)> _claimKeys = new();
    private readonly LinkedList<Queued> _bakes = new();
    private readonly HashSet<uint> _queuedIds = new();
    private readonly Dictionary<(User, int, int, int, int, int), Queued> _wets = new();
    private readonly Dictionary<(User, int, int, int, int, int), (double At, bool Soaked)> _wetSent = new();
    private readonly List<(User User, FluidSystem System, int4 Payload)> _contacts = new();
    private readonly int[] _touch = new int[FluidPeopleGather.MaxPeople];
    private readonly float[,] _stuck = new float[FluidPeopleGather.MaxPeople, FluidBodyParts.Limbs.Length];
    private readonly Dictionary<User, Sent> _sent = new();
    private readonly List<(User, int, int, int, int, int)> _wetKeys = new();
    private readonly List<uint> _old = new();
    private double _tokens;
    private double _lastRefill = double.NaN;
    private double _nextContacts;
    private double _nextPrune;

    // wearer
    private readonly Dictionary<FluidPerson, int> _cursors = new();
    private readonly List<(int4 A, int4 B)> _raw = new();
    private readonly List<FluidReportEntry> _entries = new();
    private readonly List<(FluidSystem System, int4 Payload)> _mine = new();
    private readonly List<FluidLimbRecord> _limbs = new();
    private readonly HashSet<User> _keepers = new();

    private sealed class Queued
    {
        public User User = null!;
        public FluidSystem System = null!;
        public int4 Payload;
        public uint Id;
        public int Contacts;
        public int Part;
    }

    private sealed class Sent
    {
        public int Bits;
        public double At = double.NegativeInfinity;
        public readonly float[] Stuck = new float[FluidBodyParts.Limbs.Length];
        public readonly double[] LimbAt = new double[FluidBodyParts.Limbs.Length];
    }

    public FluidCoatRelay(World world, FluidPeopleGather people, FluidResidueBaker baker, FluidPersonPublisher publisher, Func<int, FluidSubstance?> frameSubstance)
    {
        _world = world;
        _people = people;
        _baker = baker;
        _publisher = publisher;
        _frameSubstance = frameSubstance;
    }

    // Keeper side, for the inspector and tests.
    public int ProposedBakes { get; private set; }
    public int ProposedWets { get; private set; }
    public int ProposedContacts { get; private set; }
    public int ProposedLimbs { get; private set; }
    // Entries this machine put on report rings, and their bytes as the judge counts them.
    public int EntriesSent { get; private set; }
    public long BytesSent => (long)EntriesSent * FluidReportJudge.EntryBytes;
    // Bakes with no ledger cell near them (left in the simulation), and coats that waited past their queue.
    public int NoCell { get; private set; }
    public int Dropped { get; private set; }
    public int Waiting => _bakes.Count + _wets.Count + _contacts.Count;
    // Particles a wearer's Pickup claimed: the people pass sends them as RemoveParticles this frame.
    public readonly List<uint> PendingRemoves = new();
    public int Claimed { get; private set; }
    public double ClaimedLitres { get; private set; }
    public readonly int[] ProposedByPart = new int[FluidBodyParts.Count];
    // Every part a view-only person touched fluid with here, as FluidBodyParts bits, and the newest coats proposed
    // (part, point in its bone's space, kind), trimmed to 512.
    public int TouchSeenBits { get; private set; }
    public readonly List<(int Part, float3 Local, int Kind)> Recent = new();

    // Wearer side.
    public int EntriesRead { get; private set; }
    public int AppliedBakes { get; private set; }
    public int AppliedWets { get; private set; }
    public int AppliedContacts { get; private set; }
    // Bakes the wearer did not take (its stick mode, its limits, a part its rig does not have), and wet patches that
    // changed nothing (the film there was already whole and wet).
    public int Refused { get; private set; }
    public int WetsUnchanged { get; private set; }
    // Proposals in a kind this side does not place (the ledger's own Bake carries no point on the part).
    public int Unplaced { get; private set; }
    public int LastTouchBits { get; private set; }
    public readonly int[] AppliedByPart = new int[FluidBodyParts.Count];

    private double Now => _world.Time.TotalTime;

    // ---- keeper side

    // The summary's contacts, limbs and bakes for view-only people this machine simulates in a system it keeps.
    public void OnSummary(in FluidSummaryView view)
    {
        int local = _people.LocalSlot;
        foreach (var c in view.Contacts)
        {
            int slot = (int)c.Person;
            int part = (int)(c.Part & 0xFF);
            if (slot != local && slot < _touch.Length && part < FluidBodyParts.Count)
            {
                _touch[slot] |= FluidBodyParts.Bit(part);
                if (ViewOnly(slot) != null)
                    TouchSeenBits |= FluidBodyParts.Bit(part);
            }
        }
        foreach (var l in view.Limbs)
        {
            int slot = (int)l.Person;
            if (slot != local && slot < _touch.Length && l.Limb < (uint)FluidBodyParts.Limbs.Length)
            {
                _stuck[slot, l.Limb] = MathF.Max(_stuck[slot, l.Limb], l.Stuck);
            }
        }
        double now = Now;
        foreach (var c in view.Bakes)
        {
            int slot = (int)(c.Owner >> 16);
            if (slot == local || !(c.Volume > 0f) || !float.IsFinite(c.Volume) || _queuedIds.Contains(c.Id))
                continue;
            if (_proposed.ContainsKey(c.Id))
            {
                // still on them here: never proposed twice while it lasts
                _proposed[c.Id] = now;
                continue;
            }
            int part = (int)((c.Owner >> 8) & 0xFF);
            if (part >= FluidBodyParts.Count)
                continue;
            var user = ViewOnly(slot);
            if (user == null || !Kept(_frameSubstance((int)(c.Owner & 0xFF)), out var system, out int index))
                continue;
            var bone = _people.PartBone(slot, part);
            int cell = bone != null ? FluidCoatPlacement.LedgerCellAt(system, bone.LocalPointToGlobal(c.Local)) : -1;
            if (cell < 0)
            {
                NoCell++;
                continue;
            }
            var payload = FluidCoatPlacement.Bake(user, FluidBodyParts.Nodes[part], index, c.Local, c.Volume * 1e9f, cell, c.Tint);
            _bakes.AddLast(new Queued { User = user, System = system!, Payload = payload, Id = c.Id, Part = part });
            _queuedIds.Add(c.Id);
            if (_bakes.Count > MaxQueuedBakes)
            {
                // the oldest waiting bake makes room; its particle is still there and comes back at a later bake frame
                _queuedIds.Remove(_bakes.First!.Value.Id);
                _bakes.RemoveFirst();
                Dropped++;
            }
        }
    }

    // The wet read-back's patches on view-only people: the newest sample per patch waits for its turn.
    public void OnWet(IReadOnlyList<FluidWetSample> samples)
    {
        int local = _people.LocalSlot;
        double now = Now;
        foreach (var w in samples)
        {
            if (w.Person == local || w.Part < 0 || w.Part >= FluidBodyParts.Count || w.Contacts < FluidResidueBaker.WetMinContacts)
                continue;
            if (!float.IsFinite(w.Local.x + w.Local.y + w.Local.z))
                continue;
            var user = ViewOnly(w.Person);
            var substance = _frameSubstance(w.FrameSubstance);
            // a sticky substance coats through its bakes; the wearer would refuse its film
            if (user == null || (substance != null && substance.Stickiness.Value > FluidResidueBaker.WetMaxStickiness)
                || !Kept(substance, out var system, out int index))
                continue;
            var key = (user, w.Part, index, (int)MathF.Floor(w.Local.x / WetPatch), (int)MathF.Floor(w.Local.y / WetPatch), (int)MathF.Floor(w.Local.z / WetPatch));
            if (_wetSent.TryGetValue(key, out var was) && now - was.At < (was.Soaked ? WetSoakedRepeat : WetRepeat))
                continue;
            if (_wets.TryGetValue(key, out var queued) && queued.Contacts >= w.Contacts)
                continue;
            if (queued == null && _wets.Count >= MaxQueuedWets)
            {
                Dropped++;
                continue;
            }
            _wets[key] = new Queued
            {
                User = user,
                System = system!,
                Payload = FluidCoatPlacement.Wet(user, FluidBodyParts.Nodes[w.Part], index, w.Local, w.Contacts, w.Volume),
                Contacts = w.Contacts,
                Part = w.Part,
            };
        }
    }

    // ---- both sides, each update

    public void Update(FluidSpace space)
    {
        double now = Now;
        if (space.Simulating && KeepsAny(space))
        {
            QueueContacts(space, now);
            Send(now);
        }
        else if (Waiting > 0)
        {
            ForgetQueued();
        }
        Prune(now);
        var me = FluidPerson.FindFor(_world.LocalUser);
        if (me != null && me.IsLocalWearer && me.IsViewOnly)
            Apply(space);
    }

    // At 4 Hz, each view-only person's contact bits when they change (and once a second while they touch), and the
    // limbs whose stuck strength moved; one empty set when they stop.
    private void QueueContacts(FluidSpace space, double now)
    {
        if (now < _nextContacts)
            return;
        _nextContacts = now + ContactGap;
        FluidSystem? system = null;
        foreach (var s in space.Systems)
        {
            if (s != null && !s.IsDestroyed && ReferenceEquals(s.Keeper.Target, _world.LocalUser))
            {
                system = s;
                break;
            }
        }
        for (int slot = 0; slot < _touch.Length; slot++)
        {
            int bits = _touch[slot];
            _touch[slot] = 0;
            var user = system != null ? ViewOnly(slot) : null;
            if (user == null || !_sent.TryGetValue(user, out var sent))
            {
                if (user == null || (bits == 0 && !AnyStuck(slot)))
                {
                    ClearStuck(slot);
                    continue;
                }
                _sent[user] = sent = new Sent();
            }
            if (bits != sent.Bits || (bits != 0 && now - sent.At >= ContactKeepAlive))
            {
                _contacts.Add((user, system!, FluidCoatProposals.Contact(user, bits)));
                sent.Bits = bits;
                sent.At = now;
                ProposedContacts++;
            }
            for (int l = 0; l < FluidBodyParts.Limbs.Length; l++)
            {
                float stuck = _stuck[slot, l];
                float had = sent.Stuck[l];
                bool crossed = (stuck > FluidPersonPublisher.StuckBit) != (had > FluidPersonPublisher.StuckBit);
                if (!crossed && MathF.Abs(stuck - had) < LimbStep && !(stuck == 0f && had > 0f)
                    && !(stuck > 0f && now - sent.LimbAt[l] >= ContactKeepAlive))
                    continue;
                _contacts.Add((user, system!, FluidCoatProposals.Limb(user, l, stuck)));
                sent.Stuck[l] = stuck;
                sent.LimbAt[l] = now;
                ProposedLimbs++;
            }
            ClearStuck(slot);
        }
    }

    private bool AnyStuck(int slot)
    {
        for (int l = 0; l < FluidBodyParts.Limbs.Length; l++)
            if (_stuck[slot, l] > 0f)
                return true;
        return false;
    }

    private void ClearStuck(int slot)
    {
        for (int l = 0; l < FluidBodyParts.Limbs.Length; l++)
            _stuck[slot, l] = 0f;
    }

    // What waits goes out within the budget: contacts, then the newest bakes, then wet patches, most soaked first.
    private void Send(double now)
    {
        var rules = _world.RootSlot?.GetComponent<FluidWorldRules>();
        double perSecond = Math.Max(256, (rules?.LedgerBytesPerSecond.Value ?? 4096) * 0.5) * BudgetShare / FluidReportJudge.EntryBytes;
        double cap = Math.Max(1.0, perSecond * BudgetBurst);
        _tokens = double.IsNaN(_lastRefill) ? cap : Math.Min(cap, _tokens + perSecond * Math.Max(0, now - _lastRefill));
        _lastRefill = now;

        int c = 0;
        for (; c < _contacts.Count && _tokens >= 1; c++)
            Propose(_contacts[c].User, _contacts[c].System, _contacts[c].Payload);
        _contacts.RemoveRange(0, c);
        // contacts that wait more than a second are stale; the next flush sends the state as it is then
        if (_contacts.Count > 4 * FluidPeopleGather.MaxPeople)
        {
            Dropped += _contacts.Count;
            _contacts.Clear();
        }

        while (_bakes.Count > 0 && _tokens >= 1)
        {
            var q = _bakes.Last!.Value;
            _bakes.RemoveLast();
            _queuedIds.Remove(q.Id);
            if (q.User.IsDestroyed || q.System.IsDestroyed)
                continue;
            Propose(q.User, q.System, q.Payload);
            _proposed[q.Id] = now;
            var claim = (q.User, q.System, FluidCoatPlacement.CellOf(q.Payload));
            if (!_claims.TryGetValue(claim, out var list))
                _claims[claim] = list = new List<(uint, double, double)>();
            list.Add((q.Id, FluidCoatPlacement.MicrolitresOf(q.Payload) * 1e-6, now));
            ProposedBakes++;
            ProposedByPart[q.Part]++;
            Remember(q.Part, q.Payload, FluidCoatPlacement.KindPlacedBake);
        }

        while (_wets.Count > 0 && _tokens >= 1)
        {
            (User, int, int, int, int, int) best = default;
            Queued? pick = null;
            foreach (var (key, q) in _wets)
            {
                if (pick == null || q.Contacts > pick.Contacts)
                {
                    pick = q;
                    best = key;
                }
            }
            _wets.Remove(best);
            if (pick == null || pick.User.IsDestroyed || pick.System.IsDestroyed)
                continue;
            Propose(pick.User, pick.System, pick.Payload);
            _wetSent[best] = (now, pick.Contacts >= FluidResidueBaker.WetSoakContacts);
            ProposedWets++;
            ProposedByPart[pick.Part]++;
            Remember(pick.Part, pick.Payload, FluidCoatPlacement.KindPlacedWet);
        }
    }

    private void Remember(int part, int4 payload, int kind)
    {
        if (Recent.Count >= 512)
            Recent.RemoveAt(0);
        Recent.Add((part, FluidCoatPlacement.LocalOf(payload), kind));
    }

    // The ledger's keeper side, for an accepted Pickup from a view-only wearer: the particles this machine proposed to
    // them from that cell, oldest first, up to the litres claimed, leave the simulation by id this frame. Returns the
    // litres they cover; the caller takes only the rest from the cell.
    public static double Claim(World? world, User wearer, FluidSystem system, int cell, double litres)
        => FluidPeopleLink.Find(world)?.Coats.TakeClaimed(wearer, system, cell, litres) ?? 0;

    public double TakeClaimed(User wearer, FluidSystem system, int cell, double litres)
    {
        if (wearer == null || system == null || !(litres > 0) || !_claims.TryGetValue((wearer, system, cell), out var list))
            return 0;
        double covered = 0;
        int taken = 0;
        while (taken < list.Count && litres - covered > list[taken].Litres * 0.5)
        {
            PendingRemoves.Add(list[taken].Id);
            covered += list[taken].Litres;
            taken++;
        }
        list.RemoveRange(0, taken);
        if (list.Count == 0)
            _claims.Remove((wearer, system, cell));
        Claimed += taken;
        ClaimedLitres += covered;
        return Math.Min(covered, litres);
    }

    // A guest keeper's proposals join its next judged report; a host that keeps writes its own ring.
    private void Propose(User user, FluidSystem system, int4 payload)
    {
        _tokens -= 1;
        if (_world.IsAuthority)
        {
            var me = FluidPerson.FindFor(_world.LocalUser);
            if (me == null || !me.IsLocalWearer || me.Report.Count < FluidPerson.ReportCount)
                return;
            new FluidReportEntry(system.ReferenceID.RawValue, FluidReportKind.CoatProposal, 0, 1, payload).Write(out var header, out var body);
            FluidRings.Write(me.Report, me.ReportSequence, header, body);
            EntriesSent++;
            return;
        }
        var proposals = FluidLedgerWorld.Find(_world)?.Proposals;
        if (proposals == null)
            return;
        int before = proposals.Proposed;
        proposals.Add(user, system, payload);
        if (proposals.Proposed != before)
            EntriesSent++;
    }

    private void Prune(double now)
    {
        if (now < _nextPrune)
            return;
        _nextPrune = now + 5.0;
        _old.Clear();
        foreach (var (id, at) in _proposed)
            if (now - at > ProposedFor)
                _old.Add(id);
        foreach (uint id in _old)
            _proposed.Remove(id);
        _claimKeys.Clear();
        foreach (var (key, list) in _claims)
        {
            list.RemoveAll(e => now - e.At > ProposedFor);
            if (list.Count == 0 || key.Item1.IsDestroyed || key.Item2.IsDestroyed)
                _claimKeys.Add(key);
        }
        foreach (var key in _claimKeys)
            _claims.Remove(key);
        _wetKeys.Clear();
        foreach (var (key, was) in _wetSent)
            if (now - was.At > WetSoakedRepeat)
                _wetKeys.Add(key);
        foreach (var key in _wetKeys)
            _wetSent.Remove(key);
        List<User>? gone = null;
        foreach (var user in _sent.Keys)
            if (user.IsDestroyed)
                (gone ??= new()).Add(user);
        if (gone != null)
            foreach (var user in gone)
                _sent.Remove(user);
    }

    // This machine stopped keeping: what waits is some other keeper's to say now.
    private void ForgetQueued()
    {
        Dropped += Waiting;
        _bakes.Clear();
        _queuedIds.Clear();
        _claims.Clear();
        _wets.Clear();
        _contacts.Clear();
        _sent.Clear();
        Array.Clear(_touch);
        Array.Clear(_stuck);
    }

    // ---- wearer side

    // The proposals for this view-only wearer on the rings of the keepers of the systems here.
    private void Apply(FluidSpace space)
    {
        var me = _world.LocalUser;
        _keepers.Clear();
        foreach (var s in space.Systems)
        {
            if (s != null && !s.IsDestroyed && s.Keeper.Target is { IsDestroyed: false } k && !ReferenceEquals(k, me))
                _keepers.Add(k);
        }
        _mine.Clear();
        foreach (var keeper in _keepers)
        {
            var ring = FluidPerson.FindFor(keeper);
            if (ring == null)
                continue;
            int cursor = _cursors.TryGetValue(ring, out int c) ? c : FluidRings.NewCursor;
            _raw.Clear();
            FluidRings.Read(ring.Report, ring.ReportSequence.Value, ref cursor, _raw);
            _cursors[ring] = cursor;
            if (_raw.Count == 0)
                continue;
            _entries.Clear();
            foreach (var (a, b) in _raw)
                _entries.Add(FluidReportEntry.Read(a, b));
            int before = _mine.Count;
            FluidCoatProposals.For(me, keeper, _world, _entries, _mine);
            EntriesRead += _mine.Count - before;
        }
        if (_cursors.Count > _keepers.Count + 4)
        {
            var stale = new List<FluidPerson>();
            foreach (var ring in _cursors.Keys)
                if (ring.IsDestroyed)
                    stale.Add(ring);
            foreach (var ring in stale)
                _cursors.Remove(ring);
        }
        if (_mine.Count == 0)
            return;

        bool contact = false;
        int bits = 0;
        _limbs.Clear();
        foreach (var (system, payload) in _mine)
        {
            int kind = FluidCoatProposals.KindOf(payload);
            if (kind == FluidCoatPlacement.KindPlacedBake || kind == FluidCoatPlacement.KindPlacedWet)
                Place(system, payload, kind);
            else if (kind == FluidCoatProposals.KindContact)
            {
                contact = true;
                bits = payload.y;
            }
            else if (kind == FluidCoatProposals.KindLimb)
                _limbs.Add(new FluidLimbRecord { Limb = (uint)payload.z, Stuck = payload.y / 65535f });
            else
                Unplaced++;
        }
        if (contact || _limbs.Count > 0)
        {
            _publisher.ApplyProposal(contact ? bits : _publisher.TouchBits, _limbs.ToArray(), contact);
            if (contact)
            {
                LastTouchBits = bits;
                AppliedContacts++;
            }
        }
    }

    private void Place(FluidSystem system, int4 coat, int kind)
    {
        int part = FluidBodyParts.PartOf(FluidCoatProposals.PartOf(coat), out _);
        if (part < 0 || part >= FluidBodyParts.Count)
        {
            Refused++;
            return;
        }
        int index = FluidCoatProposals.SubstanceOf(coat);
        var substance = index < system.Substances.Count ? system.Substances[index] : null;
        var local = FluidCoatPlacement.LocalOf(coat);
        bool done;
        if (kind == FluidCoatPlacement.KindPlacedBake)
        {
            done = _baker.ApplyCoat(substance, part, local, FluidCoatPlacement.MicrolitresOf(coat) * 1e-3f, FluidCoatPlacement.TintOf(coat),
                unchecked((uint)FluidCoatPlacement.CellOf(coat)));
            if (done)
                AppliedBakes++;
        }
        else
        {
            done = _baker.WetPatch(substance, part, local, FluidCoatPlacement.ContactsOf(coat));
            if (done)
                AppliedWets++;
        }
        if (done)
            AppliedByPart[part]++;
        else if (kind == FluidCoatPlacement.KindPlacedBake)
            Refused++;
        else
            WetsUnchanged++;
    }

    // ---- helpers

    // The user in a gathered slot when their machine never simulates; null for anyone with their own GPU.
    private User? ViewOnly(int slot)
    {
        var user = _people.UserAt(slot);
        if (user == null || user.IsDestroyed || ReferenceEquals(user, _world.LocalUser))
            return null;
        var person = FluidPerson.FindFor(user);
        return person != null && person.IsViewOnly ? user : null;
    }

    private bool KeepsAny(FluidSpace space)
    {
        foreach (var s in space.Systems)
            if (s != null && !s.IsDestroyed && ReferenceEquals(s.Keeper.Target, _world.LocalUser))
                return true;
        return false;
    }

    // The substance's system, when this machine keeps it, and the substance's index in it.
    private bool Kept(FluidSubstance? substance, out FluidSystem? system, out int index)
    {
        system = substance?.Slot?.GetComponentInParents<FluidSystem>();
        index = system != null ? system.Substances.IndexOf(substance!) : -1;
        return system != null && index >= 0 && ReferenceEquals(system.Keeper.Target, _world.LocalUser);
    }
}
