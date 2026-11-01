// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids;

// People near fluid, packed into the frame block in late update: their bones now and last frame, their proxies, and
// the contact meshes the GPU skins. One per world.
//
// Each person holds a slot for as long as they stay near, and the GPU keys anchors by that slot, so a slot never
// changes under a live anchor: a new occupant, a vacated slot, an avatar swap, a re-cut contact mesh and a jump of
// more than 0.5 m all drop that slot's anchors with a command in the same frame.
//
// The People list runs by slot, so a person's index on the GPU is their slot. Per person the bone records are two
// ranges, written back to back: the frame range (BoneStart, BoneCount: the 16 part bones in FluidBodyParts order, all
// zero where the rig has no such bone, then one frame per proxy, part bone x the proxy's frame) and the skin range
// (SkinBoneStart, SkinBoneCount: the contact mesh's skin bones, bone x bind). Everything the GPU keeps across frames
// counts from the person's own starts, because the lists are rebuilt every frame and move when anyone else's count
// changes: a contact vertex's bones (low 16 bits; below SkinBoneFirst a part bone, from it a skin bone; the part in
// bits 16-23 of the first), a bone anchor's frame record, a skin anchor's triangle. So a person's own layout only
// changes with their fit or contact mesh, and either change drops their anchors. Proxies, rebuilt with the bones,
// name their frame's absolute record. Contact indices and blend delta vertices are absolute in the frame's contact
// arrays, rewritten at every repack. Bake candidates come back in part-bone space (BoneStart + part), the space spots
// live in.
//
// Call order each late update: block.BeginFrame(), GatherResidue, then Gather. The contact mesh lists of the block
// (ContactVertices, ContactIndices, BlendDeltas) belong to this class; it repacks them whole whenever a mesh comes or
// goes and sets ContactMeshesChanged.
//
// Skin. A person whose contact mesh goes up this frame (their triangles in the record) collides with and sticks to
// that whole-body surface on the GPU (FluidSkinCollide), whatever their stick mode: Off only stops the sticking. Their
// proxies then go up as the inner backstop (FluidProxyFit.Inner), inside the skin. Everyone else keeps the outer
// proxies. Who gets skin: the wearer always; on Low the four nearest others at Full; Medium and up everyone at Full
// (Standalone tiers never simulate). -xlinka
public sealed class FluidPeopleGather
{
    public const int MaxPeople = 32;
    // m, past the systems' bounds
    public const float Reach = 0.5f;
    // m in one frame: a teleport, a seat, a respawn
    public const float JumpDistance = 0.5f;
    // m a part bone moves before its person wakes the fluid around them
    public const float WakeDistance = 0.001f;
    public const double LeaveAfter = 1.0;
    public const double CheckInterval = 1.0;
    public const int MaxBuildRetries = 30;
    // m around a coated person's part bones that the residue bounds take in
    public const float ResidueMargin = 0.35f;
    // FluidSpotRecord.Meta bit for a spot on the wearer's own head: the residue renderer draws it on the head band
    public const uint SpotOnOwnHead = 0x01000000;
    // others skinned on Low besides the wearer, nearest first (est.)
    public const int LowSkinnedOthers = 4;

    private static readonly ConditionalWeakTable<World, FluidPeopleGather> ByWorld = new();

    private readonly World _world;
    private readonly List<User> _near = new();
    private readonly List<(float distance, User user)> _ranked = new();
    private readonly List<User> _everyone = new();

    private readonly User?[] _users = new User?[MaxPeople];
    private readonly UserRoot?[] _roots = new UserRoot?[MaxPeople];
    private readonly Slot?[] _avatars = new Slot?[MaxPeople];
    private readonly HumanoidRig?[] _rigs = new HumanoidRig?[MaxPeople];
    private readonly AvatarFluidContact?[] _contacts = new AvatarFluidContact?[MaxPeople];
    private readonly Slot?[][] _partBones = new Slot?[MaxPeople][];
    private readonly FluidContactMesh?[] _meshes = new FluidContactMesh?[MaxPeople];
    private readonly FluidProxyFit?[] _fits = new FluidProxyFit?[MaxPeople];
    private readonly Task<(FluidContactMesh mesh, FluidProxyFit fit)>?[] _jobs = new Task<(FluidContactMesh, FluidProxyFit)>?[MaxPeople];
    private readonly int[] _generation = new int[MaxPeople];
    private readonly int[] _jobGeneration = new int[MaxPeople];
    private readonly long[] _hashes = new long[MaxPeople];
    private readonly int[] _retries = new int[MaxPeople];
    private readonly double[] _lastSeen = new double[MaxPeople];
    private readonly double[] _nextCheck = new double[MaxPeople];
    private readonly float3[] _lastRoot = new float3[MaxPeople];
    private readonly bool[] _fresh = new bool[MaxPeople];
    private readonly bool[] _frozen = new bool[MaxPeople];
    private readonly bool[] _dropNow = new bool[MaxPeople];
    private readonly bool[] _skinNow = new bool[MaxPeople];
    private readonly float4x4[][] _lastBones = new float4x4[MaxPeople][];
    private readonly float4x4[][] _nowBones = new float4x4[MaxPeople][];
    private readonly FluidPersonRecord[] _records = new FluidPersonRecord[MaxPeople];
    private readonly bool[] _packedNow = new bool[MaxPeople];

    // Where each slot's contact mesh sits in the block's persistent lists, after the last repack, and the proxy frame
    // count its vertices' skin bones were counted past.
    private readonly int[] _vertexStart = new int[MaxPeople];
    private readonly int[] _triangleStart = new int[MaxPeople];
    private readonly int[] _blendStart = new int[MaxPeople];
    private readonly int[] _shapeBase = new int[MaxPeople];
    private readonly int[] _boneStart = new int[MaxPeople];
    private readonly FluidContactMesh?[] _packed = new FluidContactMesh?[MaxPeople];
    private int _shapeTotal;
    private bool _repack = true;
    private FluidFrameBlock? _gatheredBlock;
    private double _gatheredAt = double.NaN;

    // Residue bones written this frame per user, so a gathered person's record can point at them.
    private readonly Dictionary<User, int> _residueStart = new();
    private readonly Dictionary<User, (HumanoidRig? rig, double until)> _rigCache = new();
    private readonly Dictionary<(ulong system, int substance), (float dry, float cure)> _substanceTimes = new();
    private readonly List<(float ml, int index)> _spotOrder = new();

    public FluidPeopleGather(World world)
    {
        _world = world;
        for (int k = 0; k < MaxPeople; k++)
        {
            _partBones[k] = new Slot?[FluidBodyParts.Count];
            _lastBones[k] = Array.Empty<float4x4>();
            _nowBones[k] = Array.Empty<float4x4>();
            _boneStart[k] = -1;
        }
        if (world != null)
            ByWorld.AddOrUpdate(world, this);
    }

    public static FluidPeopleGather? Find(World? world) => world != null && ByWorld.TryGetValue(world, out var gather) ? gather : null;

    public FluidGpuTier Tier { get; set; } = FluidGpuTier.High;

    // This machine keeps the systems being gathered for: everyone near is simulated at Full, whatever their view LOD.
    public bool Keeper { get; set; }

    // Build contact meshes on the calling thread instead of a worker (tests, for repeatable runs).
    public bool BuildInline { get; set; }

    // The GPU collides with skinned people's contact meshes (FluidSkinCollidePass says so each frame it runs). While
    // it does not (its kernel failed, or it was switched off), skinned people keep their outer proxies: the inner
    // backstop alone would let fluid sink into them.
    public bool SkinCollides { get; set; } = true;

    public int LocalSlot { get; private set; } = -1;

    public int Occupied
    {
        get
        {
            int n = 0;
            foreach (var u in _users)
                n += u != null ? 1 : 0;
            return n;
        }
    }

    // World-space boxes (min, max pairs) around people whose bones moved this frame: L3 turns them into wake boxes.
    public readonly List<float3> MovedBounds = new();

    // What GatherResidue found this frame: a box around every coated person in view, for the residue renderer.
    public bool HasResidue { get; private set; }

    public float3 ResidueMin { get; private set; }

    public float3 ResidueMax { get; private set; }

    // Counters for the inspector and tests.
    public int DropCommands { get; private set; }
    public int FreezeCommands { get; private set; }
    public int Builds { get; private set; }
    public int BuildFailures { get; private set; }
    public int Repacks { get; private set; }
    public readonly List<string> Events = new();

    public User? UserAt(int slot) => slot >= 0 && slot < MaxPeople ? _users[slot] : null;

    public int SlotOf(User? user)
    {
        if (user == null)
            return -1;
        for (int k = 0; k < MaxPeople; k++)
        {
            if (_users[k] == user)
                return k;
        }
        return -1;
    }

    public FluidContactMesh? MeshOf(int slot) => slot >= 0 && slot < MaxPeople ? _meshes[slot] : null;

    public FluidProxyFit? FitOf(int slot) => slot >= 0 && slot < MaxPeople ? _fits[slot] : null;

    public bool IsBuilding(int slot) => slot >= 0 && slot < MaxPeople && _jobs[slot] != null;

    public Slot? AvatarOf(int slot) => slot >= 0 && slot < MaxPeople ? _avatars[slot] : null;

    public Slot? PartBone(int slot, int part)
        => slot >= 0 && slot < MaxPeople && part >= 0 && part < FluidBodyParts.Count ? _partBones[slot][part] : null;

    // Where a slot's bones start in this frame's Bones list; -1 when it was not packed.
    public int BoneStartOf(int slot) => slot >= 0 && slot < MaxPeople ? _boneStart[slot] : -1;

    // The worn avatar under a user root and its rig: what AvatarEquipManager wears, else whatever rig is under the root.
    public static Slot? WornAvatar(UserRoot root, out HumanoidRig? rig)
    {
        rig = null;
        if (root == null || root.IsDestroyed || root.Slot == null)
            return null;
        var worn = root.Slot.GetComponentInChildren<AvatarEquipManager>()?.CurrentAvatar.Target;
        if (worn != null && !worn.IsDestroyed)
        {
            rig = worn.GetComponentInChildren<HumanoidRig>();
            if (rig != null)
                return worn;
        }
        rig = root.Slot.GetComponentInChildren<HumanoidRig>();
        return rig?.Slot;
    }

    // Every coated person in view, near fluid or not: their 16 part bones as residue bones and their spots after
    // drying, the largest first up to the tier's cap. Runs on every machine, view-only ones included. The spot records
    // carry the residue renderer's own packing: octahedral normal 12 + 12 bits and flatten 8 in NormalShape; wetness
    // 8, cure 8 and bit 24 for a spot on the wearer's own head in Meta.
    public void GatherResidue(FluidFrameBlock block)
    {
        _residueStart.Clear();
        _substanceTimes.Clear();
        _world.GetAllUsers(_everyone);
        double now = _world.SessionSeconds;
        double clock = _world.Time.TotalTime;
        int cap = SpotsDrawn(Tier);
        var lo = new float3(float.MaxValue);
        var hi = new float3(float.MinValue);
        HasResidue = false;
        foreach (var user in _everyone)
        {
            var person = FluidPerson.FindFor(user);
            if (person == null)
                continue;
            var root = user.Root;
            if (root == null || root.IsDestroyed)
                continue;
            if (root != _world.LocalUser?.Root)
            {
                var lod = AvatarViewLod.For(root.Slot);
                if (lod == AvatarViewLod.Level.Hidden || lod == AvatarViewLod.Level.StandIn)
                    continue;
            }

            _spotOrder.Clear();
            for (int i = 0; i < FluidPerson.SpotCount; i++)
            {
                var spot = person.ReadSpot(i);
                if (spot.IsEmpty)
                    continue;
                Times(person, spot, out float dry, out _);
                float ml = spot.MillilitresAt(now, FluidResidueBaker.DryTimeFor(dry, person.Stick.Value));
                if (ml >= FluidSpot.QuantumMl * 0.5f)
                    _spotOrder.Add((ml, i));
            }
            if (_spotOrder.Count == 0)
                continue;
            if (_spotOrder.Count > cap)
            {
                _spotOrder.Sort((a, b) => b.ml.CompareTo(a.ml));
                _spotOrder.RemoveRange(cap, _spotOrder.Count - cap);
            }

            var rig = CachedRig(user, root, clock);
            int start = block.ResidueBones.Count;
            _residueStart[user] = start;
            for (int p = 0; p < FluidBodyParts.Count; p++)
            {
                var bone = FluidBodyParts.BoneOf(rig, p);
                var m = bone != null && !bone.IsDestroyed ? bone.LocalToWorld : float4x4.Zero;
                block.ResidueBones.Add(new FluidResidueBone { Row0 = Row(m, 0), Row1 = Row(m, 1), Row2 = Row(m, 2) });
                if (m.c3.w == 0f)
                    continue;
                var at = Position(m);
                lo = new float3(MathF.Min(lo.x, at.x), MathF.Min(lo.y, at.y), MathF.Min(lo.z, at.z));
                hi = new float3(MathF.Max(hi.x, at.x), MathF.Max(hi.y, at.y), MathF.Max(hi.z, at.z));
                HasResidue = true;
            }
            bool local = person.IsLocalWearer;
            foreach (var (ml, i) in _spotOrder)
            {
                var spot = person.ReadSpot(i);
                int part = FluidBodyParts.PartOf(spot.Node, out _);
                if (part < 0)
                    continue;
                Times(person, spot, out float dry, out float cure);
                float wet = FluidResidueBaker.WetnessAt(spot, now, FluidResidueBaker.DryTimeFor(dry, person.Stick.Value));
                float cured = cure > 0f ? (float)System.Math.Clamp(spot.AgeAt(now) / cure, 0d, 1d) : 0f;
                block.Spots.Add(new FluidSpotRecord
                {
                    Local = spot.Local,
                    VolumeMl = ml,
                    Bone = (uint)(start + part),
                    Tint = spot.Tint,
                    NormalShape = DrawnNormalShape(spot.Normal, spot.Flatten),
                    Meta = DrawnMeta(wet, cured, local && part == FluidBodyParts.Head),
                });
            }
        }
        if (HasResidue)
        {
            ResidueMin = lo - new float3(ResidueMargin);
            ResidueMax = hi + new float3(ResidueMargin);
        }
        else
        {
            ResidueMin = float3.Zero;
            ResidueMax = float3.Zero;
        }
    }

    // The residue renderer's packing of a spot's normal and shape: octahedral 12 + 12 bits, flatten in the top 8.
    public static uint DrawnNormalShape(float3 normal, float flatten)
    {
        float len = MathF.Sqrt(normal.x * normal.x + normal.y * normal.y + normal.z * normal.z);
        var n = len > 1e-9f ? normal / len : float3.Up;
        float l1 = MathF.Abs(n.x) + MathF.Abs(n.y) + MathF.Abs(n.z);
        float x = n.x / l1, y = n.y / l1;
        if (n.z < 0f)
        {
            float ox = (1f - MathF.Abs(y)) * (x >= 0f ? 1f : -1f);
            float oy = (1f - MathF.Abs(x)) * (y >= 0f ? 1f : -1f);
            x = ox;
            y = oy;
        }
        uint ux = (uint)System.Math.Clamp((int)MathF.Round((x * 0.5f + 0.5f) * 4095f), 0, 4095);
        uint uy = (uint)System.Math.Clamp((int)MathF.Round((y * 0.5f + 0.5f) * 4095f), 0, 4095);
        uint f8 = (uint)System.Math.Clamp((int)MathF.Round(System.Math.Clamp(flatten, 0f, 1f) * 255f), 0, 255);
        return ux | (uy << 12) | (f8 << 24);
    }

    public static uint DrawnMeta(float wetness, float cured, bool onOwnHead)
        => (uint)System.Math.Clamp((int)(wetness * 255f), 0, 255)
        | ((uint)System.Math.Clamp((int)(cured * 255f), 0, 255) << 8)
        | (onOwnHead ? SpotOnOwnHead : 0u);

    // People within Reach of the box: slots, avatars, bones, proxies and contact meshes, into the block. Called again
    // for a block it already filled this frame, it adds nothing.
    public void Gather(FluidFrameBlock block, in float3 boundsMin, in float3 boundsMax)
    {
        double now = _world.Time.TotalTime;
        if (ReferenceEquals(block, _gatheredBlock) && now == _gatheredAt && block.People.Count > 0)
            return;
        _gatheredBlock = block;
        _gatheredAt = now;
        MovedBounds.Clear();
        Array.Clear(_dropNow);
        Array.Clear(_skinNow);
        for (int k = 0; k < MaxPeople; k++)
            _boneStart[k] = -1;
        LocalSlot = -1;
        Collect(block, boundsMin, boundsMax, now);
        Retire(block, now);

        foreach (var (_, user) in _ranked)
        {
            int k = SlotOf(user);
            if (k < 0 || _lastSeen[k] != now)
                continue;
            Refresh(block, k, now);
        }
        FinishJobs(block);
        if (_repack)
            Repack(block);
        FillWeights(block);

        // Skin triangles: your own body always; on Low the four nearest others too; Medium and up everyone at Full.
        int nearestAllowed = Tier switch
        {
            FluidGpuTier.Standalone => 0,
            FluidGpuTier.Low => LowSkinnedOthers,
            _ => int.MaxValue,
        };
        int othersSkinned = 0;
        foreach (var (_, user) in _ranked)
        {
            int k = SlotOf(user);
            if (k < 0 || _lastSeen[k] != now || _packed[k] is not { Vertices.Count: > 0 })
                continue;
            if (user == _world.LocalUser)
                _skinNow[k] = true;
            else if (othersSkinned < nearestAllowed && (Keeper || AvatarViewLod.For(_roots[k]?.Slot) == AvatarViewLod.Level.Full))
            {
                _skinNow[k] = true;
                othersSkinned++;
            }
        }

        // Every occupied slot is packed every frame, near this frame or about to leave, so a slot's records exist for
        // as long as anchors can name it.
        Array.Clear(_packedNow);
        for (int k = 0; k < MaxPeople; k++)
        {
            var user = _users[k];
            if (user == null)
                continue;
            bool local = user == _world.LocalUser;
            if (local)
                LocalSlot = k;
            var lod = local || Keeper ? AvatarViewLod.Level.Full : AvatarViewLod.For(_roots[k]?.Slot ?? user.Root?.Slot);
            Pack(block, k, lod, local, _skinNow[k] && lod == AvatarViewLod.Level.Full);
        }

        // The GPU knows a person by their place in this list, and anchors keep that index across frames, so the list
        // runs by slot: a slot nobody fills this frame is an empty record that collides with nothing and freezes
        // whatever anchors might still name it.
        int last = -1;
        for (int k = 0; k < MaxPeople; k++)
        {
            if (_packedNow[k])
                last = k;
        }
        for (int k = 0; k <= last; k++)
        {
            block.People.Add(_packedNow[k] ? _records[k] : new FluidPersonRecord
            {
                Flags = FluidLayout.PersonFrozen | ((uint)FluidStickMode.Off & FluidLayout.PersonStickMask),
                ResidueBoneStart = FluidLayout.None,
            });
        }
    }

    // Everything let go at once: world teardown, or the fluid leaving this world.
    public void Clear(FluidFrameBlock block)
    {
        for (int k = 0; k < MaxPeople; k++)
        {
            if (_users[k] != null)
                Free(block, k, "cleared");
        }
        block.ContactVertices.Clear();
        block.ContactIndices.Clear();
        block.BlendDeltas.Clear();
        block.ContactMeshesChanged = true;
        _repack = true;
    }

    private void Collect(FluidFrameBlock block, in float3 boundsMin, in float3 boundsMax, double now)
    {
        _near.Clear();
        _ranked.Clear();
        var size = boundsMax - boundsMin;
        if (!(size.x >= 0f && size.y >= 0f && size.z >= 0f) || !float.IsFinite(size.x + size.y + size.z))
            return;
        var centre = (boundsMin + boundsMax) * 0.5f;
        _world.Physics.OverlapPeople(centre, size + new float3(Reach * 2f), floatQ.Identity, _near);
        foreach (var user in _near)
        {
            var root = user.Root;
            if (root == null || root.IsDestroyed)
                continue;
            float d = float3.Distance(root.Slot.GlobalPosition, centre);
            // The local user first: their own body never loses its slot to a crowd.
            _ranked.Add((user == _world.LocalUser ? -1f : d, user));
        }
        _ranked.Sort((a, b) => a.distance.CompareTo(b.distance));
        foreach (var (_, user) in _ranked)
        {
            int k = SlotOf(user);
            if (k < 0)
            {
                k = FreeSlot();
                if (k < 0)
                    continue;
                Assign(block, k, user);
            }
            _lastSeen[k] = now;
        }
    }

    private void Retire(FluidFrameBlock block, double now)
    {
        for (int k = 0; k < MaxPeople; k++)
        {
            var user = _users[k];
            if (user == null)
                continue;
            bool gone = user.IsDestroyed || user.Root == null || user.Root.IsDestroyed;
            if (gone || now - _lastSeen[k] > LeaveAfter)
                Free(block, k, gone ? "left" : "walked away");
        }
    }

    private int FreeSlot()
    {
        for (int k = 0; k < MaxPeople; k++)
        {
            if (_users[k] == null)
                return k;
        }
        return -1;
    }

    private void Assign(FluidFrameBlock block, int k, User user)
    {
        _users[k] = user;
        _roots[k] = null;
        _avatars[k] = null;
        _rigs[k] = null;
        _contacts[k] = null;
        _meshes[k] = null;
        _fits[k] = null;
        _hashes[k] = 0;
        _retries[k] = 0;
        _nextCheck[k] = double.NegativeInfinity;
        _fresh[k] = true;
        _frozen[k] = false;
        _generation[k]++;
        _jobs[k] = null;
        Array.Clear(_partBones[k]);
        // A slot can still hold a previous occupant's anchors on the GPU.
        Drop(block, k, "slot taken");
    }

    private void Free(FluidFrameBlock block, int k, string why)
    {
        Drop(block, k, why);
        _users[k] = null;
        _roots[k] = null;
        _avatars[k] = null;
        _rigs[k] = null;
        _contacts[k] = null;
        _fits[k] = null;
        _jobs[k] = null;
        _generation[k]++;
        Array.Clear(_partBones[k]);
        if (_meshes[k] != null)
        {
            _meshes[k] = null;
            _repack = true;
        }
    }

    // The avatar, its renderers and its position: a swap, a toggled renderer or a jump drops the slot's anchors.
    private void Refresh(FluidFrameBlock block, int k, double now)
    {
        var user = _users[k]!;
        var root = user.Root;
        bool rootChanged = root != _roots[k];
        if (rootChanged || now >= _nextCheck[k] || _avatars[k] == null || _avatars[k]!.IsDestroyed)
        {
            _nextCheck[k] = now + CheckInterval;
            _roots[k] = root;
            var avatar = WornAvatar(root, out var rig);
            var contact = avatar?.GetComponentInChildren<AvatarFluidContact>();
            if (avatar != _avatars[k] || rig != _rigs[k])
            {
                if (_avatars[k] != null)
                    Drop(block, k, "avatar swap");
                _avatars[k] = avatar;
                _rigs[k] = rig;
                _contacts[k] = contact;
                _retries[k] = 0;
                Prepare(block, k);
            }
            else if (avatar != null)
            {
                _contacts[k] = contact;
                long hash = FluidContactMesh.RendererHash(avatar, contact);
                bool waiting = _meshes[k] is { Waiting: > 0 } && _jobs[k] == null && _retries[k] < MaxBuildRetries;
                if (hash != _hashes[k] || waiting)
                {
                    if (hash != _hashes[k])
                        Drop(block, k, "renderers changed");
                    _retries[k] = hash != _hashes[k] ? 0 : _retries[k] + 1;
                    Prepare(block, k);
                }
            }
        }

        var position = root.Slot.GlobalPosition;
        if (!_fresh[k] && float3.Distance(position, _lastRoot[k]) > JumpDistance)
            Drop(block, k, "jump");
        _lastRoot[k] = position;
    }

    private void Prepare(FluidFrameBlock block, int k)
    {
        Array.Clear(_partBones[k]);
        var avatar = _avatars[k];
        var rig = _rigs[k];
        if (avatar == null)
        {
            _fits[k] = null;
            if (_meshes[k] != null)
            {
                _meshes[k] = null;
                _repack = true;
            }
            return;
        }
        for (int p = 0; p < FluidBodyParts.Count; p++)
            _partBones[k][p] = FluidBodyParts.BoneOf(rig, p);

        var contact = _contacts[k];
        var mesh = FluidContactMesh.Prepare(avatar, rig, contact);
        var fit = FluidProxyFit.Prepare(avatar, rig, contact);
        _hashes[k] = FluidContactMesh.RendererHash(avatar, contact);
        // Rig defaults until the fit lands; no skin triangles until then, so anchors are bone-local meanwhile.
        _fits[k] = fit;
        if (_meshes[k] != null)
        {
            _meshes[k] = null;
            _repack = true;
        }
        int generation = ++_generation[k];
        _jobGeneration[k] = generation;
        Builds++;
        if (BuildInline)
        {
            try
            {
                _jobs[k] = Task.FromResult(Build(mesh, fit));
            }
            catch (Exception ex)
            {
                _jobs[k] = Task.FromException<(FluidContactMesh, FluidProxyFit)>(ex);
            }
        }
        else
        {
            _jobs[k] = Task.Run(() => Build(mesh, fit));
        }
    }

    private static (FluidContactMesh mesh, FluidProxyFit fit) Build(FluidContactMesh mesh, FluidProxyFit prepared)
    {
        mesh.Build();
        var fitted = FluidProxyFit.Fit(prepared, mesh);
        mesh.ReleaseSources();
        return (mesh, fitted);
    }

    private void FinishJobs(FluidFrameBlock block)
    {
        for (int k = 0; k < MaxPeople; k++)
        {
            var job = _jobs[k];
            if (job == null || !job.IsCompleted)
                continue;
            _jobs[k] = null;
            if (_jobGeneration[k] != _generation[k] || _users[k] == null)
                continue;
            if (job.IsFaulted || job.IsCanceled)
            {
                BuildFailures++;
                LumoraLogger.Warn($"Fluids: contact mesh for {_users[k]?.UserName.Value} failed, bone-local anchors only: {job.Exception?.GetBaseException().Message}");
                continue;
            }
            var (mesh, fit) = job.Result;
            // Bone anchors name a proxy frame by its place in the person's range and hold a point in it; a fit that
            // lands with other frames would move every one of them.
            if (!SameFrames(_fits[k], fit))
                Drop(block, k, "proxies fitted");
            _fits[k] = fit;
            _meshes[k] = mesh;
            _repack = true;
            if (mesh.Legacy > 0)
                Events.Add($"slot {k}: {mesh.Legacy} renderer(s) without a mesh asset; their skin is bone-local");
        }
    }

    private static bool SameFrames(FluidProxyFit? a, FluidProxyFit? b)
    {
        if (a == null || b == null)
            return a == b;
        if (a.Proxies.Count != b.Proxies.Count || a.Frames.Count != b.Frames.Count)
            return false;
        for (int i = 0; i < a.Proxies.Count; i++)
        {
            var x = a.Proxies[i];
            var y = b.Proxies[i];
            if (x.Part != y.Part || x.Bone != y.Bone || x.Kind != y.Kind || x.A != y.A || x.B != y.B)
                return false;
        }
        for (int i = 0; i < a.Frames.Count; i++)
        {
            if (a.Frames[i] != b.Frames[i])
                return false;
        }
        return true;
    }

    // The block's persistent contact lists rebuilt from every slot's mesh, in slot order. Vertices go as the mesh
    // wrote them, person-relative. Shape indices are rebased onto the frame's BlendWeights, so FillWeights writes them
    // in the same order every frame until the next repack.
    private void Repack(FluidFrameBlock block)
    {
        _repack = false;
        Repacks++;
        block.ContactVertices.Clear();
        block.ContactIndices.Clear();
        block.BlendDeltas.Clear();
        _shapeTotal = 0;
        for (int k = 0; k < MaxPeople; k++)
        {
            var mesh = _users[k] != null ? _meshes[k] : null;
            _packed[k] = mesh;
            if (mesh == null)
                continue;
            _vertexStart[k] = block.ContactVertices.Count;
            _triangleStart[k] = block.ContactIndices.Count / 3;
            _blendStart[k] = block.BlendDeltas.Count;
            _shapeBase[k] = _shapeTotal;
            uint vertexBase = (uint)_vertexStart[k];
            block.ContactVertices.AddRange(mesh.Vertices);
            foreach (uint index in mesh.Indices)
                block.ContactIndices.Add(vertexBase + index);
            foreach (var delta in mesh.Deltas)
            {
                uint shape = (delta.VertexShape >> FluidContactMesh.VertexBits) + (uint)_shapeTotal;
                uint vertex = (delta.VertexShape & FluidContactMesh.VertexMask) + vertexBase;
                block.BlendDeltas.Add(new FluidBlendDelta
                {
                    Delta = delta.Delta,
                    VertexShape = (vertex & FluidContactMesh.VertexMask) | (shape << FluidContactMesh.VertexBits),
                });
            }
            _shapeTotal += mesh.ShapeRenderers.Count;
        }
        block.ContactMeshesChanged = true;
    }

    private void FillWeights(FluidFrameBlock block)
    {
        for (int i = 0; i < _shapeTotal; i++)
            block.BlendWeights.Add(0f);
        for (int k = 0; k < MaxPeople; k++)
        {
            var mesh = _packed[k];
            if (mesh == null)
                continue;
            for (int i = 0; i < mesh.ShapeRenderers.Count; i++)
            {
                var renderer = mesh.ShapeRenderers[i];
                if (renderer == null || renderer.IsDestroyed)
                    continue;
                int shape = mesh.ShapeIndices[i];
                if (shape < renderer.BlendShapeCount)
                    block.BlendWeights[_shapeBase[k] + i] = renderer.GetEffectiveBlendShapeWeight(shape);
            }
        }
    }

    private void Pack(FluidFrameBlock block, int k, AvatarViewLod.Level lod, bool local, bool skin)
    {
        var user = _users[k]!;
        var fit = _fits[k];
        var mesh = _packed[k];
        var person = FluidPerson.FindFor(user);

        bool frozen = !local && !Keeper && (lod == AvatarViewLod.Level.Hidden || lod == AvatarViewLod.Level.StandIn);
        if (frozen != _frozen[k])
        {
            _frozen[k] = frozen;
            Freeze(block, k, frozen);
        }

        // Bones: the frame range (parts, proxy frames), then the skin range.
        int frames = fit?.Proxies.Count ?? 0;
        int skinBones = mesh?.SkinBones.Count ?? 0;
        int frameCount = FluidBodyParts.Count + frames;
        int count = frameCount + skinBones;
        if (_nowBones[k].Length != count)
        {
            _nowBones[k] = new float4x4[count];
            _lastBones[k] = new float4x4[count];
            _fresh[k] = true;
        }
        var nowBones = _nowBones[k];
        var lastBones = _lastBones[k];
        var parts = _partBones[k];
        for (int p = 0; p < FluidBodyParts.Count; p++)
        {
            var bone = parts[p];
            nowBones[p] = bone != null && !bone.IsDestroyed ? bone.LocalToWorld : float4x4.Zero;
        }
        for (int i = 0; i < frames; i++)
        {
            int part = (int)fit!.Proxies[i].Part;
            nowBones[FluidBodyParts.Count + i] = nowBones[part] * fit.Frames[(int)fit.Proxies[i].Bone];
        }
        for (int i = 0; i < skinBones; i++)
        {
            var bone = mesh!.SkinBones[i];
            nowBones[frameCount + i] = bone != null && !bone.IsDestroyed ? bone.LocalToWorld * mesh.SkinBind[i] : mesh.SkinAtEquip[i];
        }

        bool moved = _fresh[k];
        for (int p = 0; p < FluidBodyParts.Count && !moved; p++)
            moved = float3.DistanceSquared(Position(nowBones[p]), Position(lastBones[p])) > WakeDistance * WakeDistance;
        if (_fresh[k])
            Array.Copy(nowBones, lastBones, count);

        int boneStart = block.Bones.Count;
        _boneStart[k] = boneStart;
        for (int i = 0; i < count; i++)
            block.Bones.Add(Record(nowBones[i], lastBones[i]));
        Array.Copy(nowBones, lastBones, count);
        _fresh[k] = false;

        // Skinned this frame: the whole-body mesh collides, and the proxies shrink to the inner backstop.
        bool useSkin = skin && mesh != null && !frozen;

        // Proxies by LOD: everything at Full, soles and hands at Reduced, soles at Distant, none frozen. Inner and outer
        // share their frames, so a person moving between the two keeps every bone anchor.
        int proxyStart = block.Proxies.Count;
        if (fit != null && !frozen)
        {
            bool inner = useSkin && SkinCollides && fit.Inner.Count == fit.Proxies.Count;
            for (int i = 0; i < fit.Proxies.Count; i++)
            {
                var record = inner ? fit.Inner[i] : fit.Proxies[i];
                int part = (int)record.Part;
                bool sole = part <= FluidBodyParts.RightToes;
                bool hand = part == FluidBodyParts.LeftHand || part == FluidBodyParts.RightHand;
                if (lod == AvatarViewLod.Level.Distant && !sole)
                    continue;
                if (lod == AvatarViewLod.Level.Reduced && !sole && !hand)
                    continue;
                record.Bone = (uint)(boneStart + FluidBodyParts.Count + i);
                record.Person = (uint)k;
                block.Proxies.Add(record);
            }
        }

        // The person's own choice, then the world's: the world can turn sticking off, never on for someone who chose
        // Off. A distant person only pushes.
        var stick = person?.Stick.Value ?? FluidStickMode.Sticky;
        if (!WorldSticks() && stick == FluidStickMode.Sticky)
            stick = FluidStickMode.Wet;
        if (lod == AvatarViewLod.Level.Distant)
            stick = FluidStickMode.Off;
        uint flags = ((uint)stick & FluidLayout.PersonStickMask) | ((uint)lod << FluidLayout.PersonLodShift);
        if (local)
            flags |= FluidLayout.PersonLocal;
        if (person == null || person.IsViewOnly)
            flags |= FluidLayout.PersonViewOnly;
        if (frozen)
            flags |= FluidLayout.PersonFrozen;
        if (_dropNow[k])
            flags |= FluidLayout.PersonDropAnchors;

        var contact = _contacts[k];
        bool hasContact = contact != null && !contact.IsDestroyed;
        _packedNow[k] = true;
        _records[k] = new FluidPersonRecord
        {
            BoneStart = (uint)boneStart,
            BoneCount = (uint)frameCount,
            SkinBoneStart = skinBones > 0 ? (uint)(boneStart + frameCount) : 0,
            SkinBoneCount = (uint)skinBones,
            ProxyStart = (uint)proxyStart,
            ProxyCount = (uint)(block.Proxies.Count - proxyStart),
            VertexStart = useSkin ? (uint)_vertexStart[k] : 0,
            VertexCount = useSkin ? (uint)mesh!.Vertices.Count : 0,
            TriangleStart = useSkin ? (uint)_triangleStart[k] : 0,
            TriangleCount = useSkin ? (uint)mesh!.TriangleCount : 0,
            BlendStart = mesh != null ? (uint)_blendStart[k] : 0,
            BlendCount = mesh != null ? (uint)mesh.Deltas.Count : 0,
            Flags = flags,
            User = user.AllocationID.Value,
            Stickiness = hasContact ? System.Math.Max(0f, contact!.Stickiness.Value) : 1f,
            SoleInset = hasContact ? contact!.SoleInset.Value : 0f,
            SkinFriction = hasContact ? System.Math.Max(0f, contact!.Friction.Value) : 1f,
            SkinWetting = hasContact ? System.Math.Max(0f, contact!.Wetting.Value) : 1f,
            ResidueBoneStart = _residueStart.TryGetValue(user, out int residue) ? (uint)residue : FluidLayout.None,
            ResidueBoneCount = _residueStart.ContainsKey(user) ? (uint)FluidBodyParts.Count : 0,
        };

        if (moved && !frozen)
            AddMovedBounds(nowBones, fit);
    }

    private static FluidBoneRecord Record(in float4x4 now, in float4x4 last) => new()
    {
        Row0 = Row(now, 0), Row1 = Row(now, 1), Row2 = Row(now, 2),
        LastRow0 = Row(last, 0), LastRow1 = Row(last, 1), LastRow2 = Row(last, 2),
    };

    private bool WorldSticks()
    {
        var rules = _world.RootSlot?.GetComponent<FluidWorldRules>();
        return rules == null || rules.StickToPeople.Value;
    }

    private void AddMovedBounds(float4x4[] bones, FluidProxyFit? fit)
    {
        var lo = new float3(float.MaxValue);
        var hi = new float3(float.MinValue);
        bool any = false;
        for (int p = 0; p < FluidBodyParts.Count; p++)
        {
            if (bones[p].c3.w == 0f)
                continue;
            var at = Position(bones[p]);
            lo = new float3(MathF.Min(lo.x, at.x), MathF.Min(lo.y, at.y), MathF.Min(lo.z, at.z));
            hi = new float3(MathF.Max(hi.x, at.x), MathF.Max(hi.y, at.y), MathF.Max(hi.z, at.z));
            any = true;
        }
        if (!any)
            return;
        float grow = 0.15f * (fit?.Scale ?? 1f);
        MovedBounds.Add(lo - new float3(grow));
        MovedBounds.Add(hi + new float3(grow));
    }

    // One drop a slot a frame: its anchors are gone after the first.
    private void Drop(FluidFrameBlock block, int k, string why)
    {
        if (_dropNow[k])
        {
            Note($"slot {k}: drop anchors ({why}, already dropped)");
            return;
        }
        _dropNow[k] = true;
        DropCommands++;
        block.Commands.Add(new FluidGpuCommand
        {
            Op = (uint)FluidGpuOp.DropAnchors,
            System = FluidLayout.None,
            Count = FluidLayout.AnchorBone,
            Seed = (uint)k,
        });
        Note($"slot {k}: drop anchors ({why})");
    }

    private void Freeze(FluidFrameBlock block, int k, bool freeze)
    {
        FreezeCommands++;
        block.Commands.Add(new FluidGpuCommand
        {
            Op = (uint)FluidGpuOp.FreezeAnchors,
            System = FluidLayout.None,
            Count = FluidLayout.AnchorBone,
            Seed = (uint)k,
            C = new float4(freeze ? 1f : 0f, 0f, 0f, 0f),
        });
        Note($"slot {k}: {(freeze ? "freeze" : "thaw")} anchors");
    }

    private void Note(string line)
    {
        if (Events.Count >= 64)
            Events.RemoveAt(0);
        Events.Add(line);
    }

    private HumanoidRig? CachedRig(User user, UserRoot root, double clock)
    {
        if (_rigCache.TryGetValue(user, out var cached) && clock < cached.until && (cached.rig == null || !cached.rig.IsDestroyed))
            return cached.rig;
        if (_rigCache.Count > 4 * MaxPeople)
            _rigCache.Clear();
        WornAvatar(root, out var rig);
        _rigCache[user] = (rig, clock + CheckInterval);
        return rig;
    }

    // A spot's substance times, through the system its Touched entry names.
    private void Times(FluidPerson person, in FluidSpot spot, out float dry, out float cure)
    {
        dry = 900f;
        cure = 0f;
        if (spot.Touched < 0 || spot.Touched >= person.Touched.Count)
            return;
        var id = FluidPerson.UnpackRef(person.Touched[spot.Touched]);
        var key = (id.RawValue, spot.Substance);
        if (_substanceTimes.TryGetValue(key, out var times))
        {
            dry = times.dry;
            cure = times.cure;
            return;
        }
        if (!id.IsNull && _world.ReferenceController.GetObjectOrNull(id) is FluidSystem system
            && spot.Substance < system.Substances.Count && system.Substances[spot.Substance] is { IsDestroyed: false } substance)
        {
            dry = substance.DryTime.Value;
            cure = substance.CureTime.Value;
        }
        _substanceTimes[key] = (dry, cure);
    }

    // Spots drawn per avatar, by tier (11.2).
    private static int SpotsDrawn(FluidGpuTier tier) => tier switch
    {
        FluidGpuTier.Medium => 24,
        FluidGpuTier.High or FluidGpuTier.Ultra => 48,
        _ => 12,
    };

    private static float4 Row(in float4x4 m, int row) => row switch
    {
        0 => new float4(m.c0.x, m.c1.x, m.c2.x, m.c3.x),
        1 => new float4(m.c0.y, m.c1.y, m.c2.y, m.c3.y),
        _ => new float4(m.c0.z, m.c1.z, m.c2.z, m.c3.z),
    };

    private static float3 Position(in float4x4 m) => new(m.c3.x, m.c3.y, m.c3.z);
}
