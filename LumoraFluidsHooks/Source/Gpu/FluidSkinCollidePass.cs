// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;
using Lumora.Core;
using LumoraEngine = Lumora.Core.Engine;

namespace Lumora.Fluids.Hooks;

// Fluid against people's skinned bodies, for one world's solver: FluidSkinCollide.glsl recorded through the solver's
// RecordAfterCollide hook. On the first substep of a frame it marks the coarse cells awake fluid is in, then builds
// the fine hash grid from the contact triangles near them (both of the frame's poses, grown by the reach); every
// substep it collides. A frame with no skinned person (no contact triangles in the block) records nothing.
//
// A triangle is grown by the contact reach only: the largest radius, contact band and skin thickness of the frame's
// substances. Speed is the particle's own business: it marks the coarse cells along its frame's path and walks the
// fine cells along its substep's, so a splash at 6 m/s costs that particle, not every triangle. A per-person box
// round their bones lets a whole avatar no awake fluid can reach drop out before its triangles are read. Every few
// frames the wet table (where skin fluid ran) is read back and posted to the world's FluidSkinWet; the wearer's baker
// turns it into a thin coat. While the pass cannot run (its kernel failed, or Enabled is off) it tells the world's
// people gather, which then packs skinned people's outer proxies instead of the inner backstop.
//
// Attaches itself to every world that simulates here, the way the ledger kernels do; a test attaches it to its own
// solver. Buffers grow to the triangles in the block, never shrink, and go with the world. A wet read-back still in
// flight when the engine shuts down is landed with one synchronous read before anything is freed: the device would
// otherwise call it from its own teardown, after the C# runtime is gone (FluidGpuDevice's quit note). -xlinka
public sealed class FluidSkinCollidePass : IDisposable
{
    public const string ShaderPath = "res://Modules/Fluids/Compute/FluidSkinCollide.glsl";

    // bindings after the solver's table (FluidSkinCollide.glsl)
    public const int Params = 43;
    public const int Occupied = 44;
    public const int Cells = 45;
    public const int Entries = 46;
    public const int Tris = 47;
    public const int State = 48;
    public const int Wet = 49;
    public const int Count = 50;

    public const uint PassMark = 0;
    public const uint PassInsert = 1;
    public const uint PassCollide = 2;
    public const uint PassBlocks = 3;
    public const uint PassScan = 4;
    public const uint PassScatter = 5;

    public const int StateWords = 64;
    public const int StateTris = 0;
    public const int StateEntries = 1;
    public const int StateTriOverflow = 2;
    public const int StateEntryOverflow = 3;
    public const int StateCellClamp = 4;
    public const int StateLookups = 5;
    public const int StateContacts = 6;
    public const int StatePushes = 7;
    public const int StateCrossings = 8;
    public const int StateWetFull = 9;
    public const int StateListClamp = 10;

    // fine cells in the hash table, each a count, a start and a cursor
    public const int CellBits = 14;
    public const int OccupiedBits = 18;
    public const int WetSlots = 1024;
    public const int WetWords = 10;
    public const int TriangleBytes = 112;
    public const int EntriesPerTriangle = 32;
    public const int MaxTriangles = 1 << 17;
    public const int MaxEntries = 1 << 22;

    // Sizing a test tunes (defaults are the constants below).
    public static float CoarseCellSize = CoarseCell;
    public static float CellPerReach = FineCellPerReach;
    public static float ListSpacings = ListThickness;

    // m, cell sizes and the skin's thickness floor (est.)
    public const float CoarseCell = 0.08f;
    public const float MinFineCell = 0.015f;
    public const float MaxFineCell = 0.05f;
    public const float WetCell = 0.035f;
    // spacings: how deep under a closed surface a particle is still taken back out the front; past it the inner
    // proxies hold. The density solve can press a particle half a spacing under in a crease, then it falls on (est.)
    public const float SkinThickness = 1.5f;
    // fine cells per contact reach, and frames of a particle's own velocity it marks ahead (gravity and the solver
    // can speed it up inside the frame, est.)
    public const float FineCellPerReach = 2.5f;
    // spacings of skin thickness a cell's list carries past the radius and band: a particle deeper than that under a
    // surface still finds it through the cell it sits in, deeper still the inner proxies hold it (est.)
    public const float ListThickness = 0.5f;
    public const float TravelMargin = 1.5f;
    // m a skinned surface can hang off the bones it is weighted to (hair, a hem, a sleeve; est.)
    public const float BoneReach = 0.35f;
    // the params block: 64 B of settings, then a min and a max (vec4 each) per person
    public const int ParamsHead = 80;
    public const int MaxBoxes = 32;
    // s between wet read-backs
    public const double WetGap = 0.25;

    private static readonly Dictionary<ulong, (Rid shader, Rid pipeline, string error)> PerDevice = new();
    private static readonly ConditionalWeakTable<World, FluidSkinCollidePass> ByWorld = new();

    private readonly Rid[] _own = new Rid[Count];
    private readonly uint[] _ownSizes = new uint[Count];
    private readonly byte[] _push = new byte[FluidPushConstants.Size];
    private readonly byte[] _params = new byte[ParamsHead + MaxBoxes * 32];
    private readonly List<FluidWetSample> _wetDecoded = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Rid _set;
    private int _solverVersion = -1;
    private int _ownVersion;
    private int _setOwnVersion = -1;
    private int _triCap;
    private int _entryCap;
    private bool _built;
    private bool _disposed;
    private double _nextWet;
    private Callable _onWet;
    private bool _callableMade;
    private bool _wetInFlight;
    private float _rMax = 0.005f, _bandMax = 0.004f, _thick = 0.01f, _spacing = 0.01f;

    private FluidSkinCollidePass(FluidGpuSolver solver, World? world)
    {
        Solver = solver;
        World = world;
        Device = solver.Device;
    }

    public FluidGpuSolver Solver { get; }

    public World? World { get; }

    public RenderingDevice Device { get; }

    public string Error { get; private set; } = string.Empty;

    // Off: records nothing, and every person falls back to their outer proxies (the inspector and a test's
    // control runs).
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Tell(value && Error.Length == 0);
        }
    }

    private bool _enabled = true;

    // The world's people gather packs inner backstops only while this pass collides.
    private void Tell(bool collides)
    {
        var gather = FluidPeopleGather.Find(World);
        if (gather != null)
            gather.SkinCollides = collides;
    }

    // Collect where skin fluid ran, for the wet coat.
    public bool WetOn { get; set; } = true;

    public long Frames { get; private set; }

    public long Substeps { get; private set; }

    public int LastTriangles { get; private set; }

    // The reach and cells the last frame used, for tests and the inspector (the coarse cell in LastReachCoarse).
    public float LastFineCell { get; private set; }

    public float LastReachFine { get; private set; }

    public float LastReachCoarse { get; private set; }

    // A test on a local device reads the wet table itself after its Sync (ReadWet); the game gets it posted.
    public Action<IReadOnlyList<FluidWetSample>>? WetRead { get; set; }

#pragma warning disable CA2255 // a module's own boot: fills a managed event list and touches nothing of Godot
    [ModuleInitializer]
    internal static void Boot() => FluidSpace.Created += Watch;
#pragma warning restore CA2255

    private static void Watch(FluidSpace space) => space.Packing += AttachOnce;

    private static void AttachOnce(FluidSpace space, FluidFrameBlock block)
    {
        if (space.Gpu is FluidDeviceWorld gpu && gpu.Solver.RecordAfterCollide == null)
            Attach(gpu.Solver, gpu.World);
    }

    public static FluidSkinCollidePass Attach(FluidGpuSolver solver, World? world)
    {
        if (world != null && ByWorld.TryGetValue(world, out var found) && !found._disposed)
        {
            if (found.Solver == solver)
            {
                solver.RecordAfterCollide = found.Record;
                return found;
            }
            // the world got a new solver (its device world was opened again): the old pass's buffers go now
            found.Dispose();
        }
        var pass = new FluidSkinCollidePass(solver, world);
        solver.RecordAfterCollide = pass.Record;
        if (world != null)
        {
            ByWorld.AddOrUpdate(world, pass);
            world.WhenDestroyed += w =>
            {
                if (ByWorld.TryGetValue(w, out var p) && ReferenceEquals(p, pass))
                    ByWorld.Remove(w);
                pass.Dispose();
            };
        }
        return pass;
    }

    public long AllocatedBytes
    {
        get
        {
            long bytes = 0;
            for (int i = 0; i < Count; i++)
                if (_own[i].IsValid) bytes += _ownSizes[i];
            return bytes;
        }
    }

    public static FluidSkinCollidePass? Find(World? world) => world != null && ByWorld.TryGetValue(world, out var p) ? p : null;

    // A test frees its local device after a check.
    public static void Forget(RenderingDevice device)
    {
        ulong key = device.GetInstanceId();
        if (PerDevice.Remove(key, out var entry))
        {
            if (entry.pipeline.IsValid)
                device.FreeRid(entry.pipeline);
            if (entry.shader.IsValid)
                device.FreeRid(entry.shader);
        }
    }

    // The solver's hook: called after predict in every substep.
    public void Record(FluidGpuSolver solver, int substep)
    {
        if (_disposed || !_enabled || !ReferenceEquals(solver, Solver))
            return;
        var block = solver.Block;
        ref readonly var h = ref block.Header;
        if (substep == 0)
        {
            _built = false;
            if (h.ContactTriangles == 0 || h.People == 0 || h.Substeps == 0)
                return;
            var pipeline = Pipeline();
            if (!pipeline.IsValid)
            {
                Tell(false);
                return;
            }
            Build(solver, block, pipeline);
        }
        if (!_built)
            return;
        Dispatch(PipelineRid(), PassCollide, (uint)substep, indirect: true, groups: 0, argsWord: FluidConstants.ArgsAwakeLanes);
        Substeps++;
        if (substep + 1 >= (int)h.Substeps && WetOn && _clock.Elapsed.TotalSeconds >= _nextWet)
        {
            _nextWet = _clock.Elapsed.TotalSeconds + WetGap;
            ReadWetAsync();
        }
    }

    private void Build(FluidGpuSolver solver, FluidFrameBlock block, Rid pipeline)
    {
        ref readonly var h = ref block.Header;
        Measure(block);
        int triangles = (int)h.ContactTriangles;
        EnsureBuffers(triangles);
        if (!EnsureSet(solver, pipeline))
            return;

        // The reach a triangle is grown by is the contact's alone: a particle marks the coarse cells along its own
        // frame's path, and walks the fine cells along its own substep's, so only fast particles pay for speed.
        float dtFrame = h.DtFrame > 0f ? h.DtFrame : 1f / 90f;
        float reach = _rMax + _bandMax + _thick;
        float listReach = _rMax + _bandMax + ListSpacings * _spacing;
        float fine = Math.Clamp(listReach * CellPerReach, MinFineCell, MaxFineCell);
        float coarse = MathF.Max(CoarseCellSize, 2f * reach);
        LastFineCell = fine;
        LastReachFine = reach;
        LastReachCoarse = coarse;
        LastTriangles = triangles;

        var p = _params.AsSpan();
        p.Clear();
        WriteF(p, 0, fine);
        WriteF(p, 4, 1f / fine);
        WriteF(p, 8, coarse);
        WriteF(p, 12, 1f / coarse);
        WriteF(p, 16, reach);
        WriteF(p, 20, dtFrame * TravelMargin);
        WriteF(p, 24, listReach);
        // the fastest any skinned bone moved this frame, over a substep: what the skin itself can travel into a particle
        WriteF(p, 28, SkinTravel(block) / Math.Max((int)h.Substeps, 1));
        WriteF(p, 40, _thick);
        WriteF(p, 44, WetCell);
        WriteU(p, 48, (uint)_triCap);
        WriteU(p, 52, (uint)_entryCap);
        WriteU(p, 56, (1u << CellBits) - 1);
        WriteU(p, 60, (1u << OccupiedBits) - 1);
        WriteU(p, 64, WetSlots - 1);
        WriteU(p, 68, WetOn ? 1u : 0u);
        int people = Math.Min((int)h.People, Math.Min(block.People.Count, MaxBoxes));
        WriteU(p, 72, (uint)people);
        // Per person a box round every bone they skin with, now and last frame, grown by how far skin hangs off a
        // bone; the mark pass flags each person a particle path reaches, and the rest drop out unread.
        for (int k = 0; k < people; k++)
        {
            var person = block.People[k];
            var lo = new Lumora.Core.Math.float3(float.MaxValue);
            var hi = new Lumora.Core.Math.float3(float.MinValue);
            bool any = false;
            if (person.TriangleCount > 0)
            {
                Grow(block, person.BoneStart, person.BoneCount, ref lo, ref hi, ref any);
                Grow(block, person.SkinBoneStart, person.SkinBoneCount, ref lo, ref hi, ref any);
            }
            if (!any)
            {
                lo = new Lumora.Core.Math.float3(1e9f);
                hi = new Lumora.Core.Math.float3(-1e9f);
            }
            else
            {
                lo -= new Lumora.Core.Math.float3(BoneReach);
                hi += new Lumora.Core.Math.float3(BoneReach);
            }
            int at = ParamsHead + k * 32;
            WriteF(p, at, lo.x);
            WriteF(p, at + 4, lo.y);
            WriteF(p, at + 8, lo.z);
            WriteF(p, at + 16, hi.x);
            WriteF(p, at + 20, hi.y);
            WriteF(p, at + 24, hi.z);
        }
        Device.BufferUpdate(_own[Params], 0, (uint)_params.Length, _params);
        Device.BufferClear(_own[Occupied], 0, _ownSizes[Occupied]);
        Device.BufferClear(_own[Cells], 0, _ownSizes[Cells]);
        Device.BufferClear(_own[State], 0, _ownSizes[State]);

        Dispatch(pipeline, PassMark, 0, indirect: true, groups: 0, argsWord: FluidConstants.ArgsAwake);
        Solver.Timers.Mark("skin mark");
        Dispatch(pipeline, PassInsert, 0, indirect: false, groups: (uint)((triangles + FluidLayout.GroupSize - 1) / FluidLayout.GroupSize));
        Solver.Timers.Mark("skin insert");
        Dispatch(pipeline, PassScan, 0, indirect: false, groups: (uint)((1 << CellBits) / FluidLayout.GroupSize));
        Dispatch(pipeline, PassBlocks, 0, indirect: false, groups: 1);
        Solver.Timers.Mark("skin scan");
        int kept = Math.Min(triangles, _triCap);
        Dispatch(pipeline, PassScatter, 0, indirect: false, groups: (uint)((kept + FluidLayout.GroupSize - 1) / FluidLayout.GroupSize));
        Solver.Timers.Mark("skin scatter");
        _built = true;
        Frames++;
        if (Frames == 1)
            Tell(true);
    }

    // m, the farthest any bone of a skinned person moved between last frame and this one.
    private static float SkinTravel(FluidFrameBlock block)
    {
        float most = 0f;
        foreach (var person in block.People)
        {
            if (person.TriangleCount == 0)
                continue;
            most = MathF.Max(most, Travel(block, person.BoneStart, person.BoneCount));
            most = MathF.Max(most, Travel(block, person.SkinBoneStart, person.SkinBoneCount));
        }
        return most;
    }

    private static float Travel(FluidFrameBlock block, uint start, uint count)
    {
        float most = 0f;
        int end = (int)Math.Min((long)start + count, block.Bones.Count);
        for (int b = (int)start; b < end; b++)
        {
            var r = block.Bones[b];
            float dx = r.Row0.w - r.LastRow0.w, dy = r.Row1.w - r.LastRow1.w, dz = r.Row2.w - r.LastRow2.w;
            most = MathF.Max(most, MathF.Sqrt(dx * dx + dy * dy + dz * dz));
        }
        return most;
    }

    private static void Grow(FluidFrameBlock block, uint start, uint count, ref Lumora.Core.Math.float3 lo, ref Lumora.Core.Math.float3 hi, ref bool any)
    {
        int end = (int)Math.Min((long)start + count, block.Bones.Count);
        for (int b = (int)start; b < end; b++)
        {
            var r = block.Bones[b];
            // a part the rig lacks is an all-zero record
            if (r.Row0.x == 0f && r.Row0.y == 0f && r.Row0.z == 0f)
                continue;
            var now = new Lumora.Core.Math.float3(r.Row0.w, r.Row1.w, r.Row2.w);
            var last = new Lumora.Core.Math.float3(r.LastRow0.w, r.LastRow1.w, r.LastRow2.w);
            lo = Min(Min(lo, now), last);
            hi = Max(Max(hi, now), last);
            any = true;
        }
    }

    private static Lumora.Core.Math.float3 Min(Lumora.Core.Math.float3 a, Lumora.Core.Math.float3 b) => new(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));

    private static Lumora.Core.Math.float3 Max(Lumora.Core.Math.float3 a, Lumora.Core.Math.float3 b) => new(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));

    // The largest radius, contact band and skin thickness of the frame's substances.
    private void Measure(FluidFrameBlock block)
    {
        if (block.Substances.Count == 0)
            return;
        float r = 0f, band = 0f, spacing = 0f;
        foreach (var s in block.Substances)
        {
            if (!(s.Spacing > 0f))
                continue;
            r = MathF.Max(r, 0.5f * s.Spacing);
            band = MathF.Max(band, s.StickDistance + 0.1f * s.Spacing);
            spacing = MathF.Max(spacing, s.Spacing);
        }
        if (r > 0f)
        {
            _rMax = r;
            _bandMax = band;
            _thick = MathF.Max(SkinThickness * spacing, 2f * r);
            _spacing = spacing;
        }
    }

    private void Dispatch(Rid pipeline, uint pass, uint substep, bool indirect, uint groups, int argsWord = 0)
    {
        long list = Device.ComputeListBegin();
        Device.ComputeListBindComputePipeline(list, pipeline);
        Device.ComputeListBindUniformSet(list, _set, 0);
        var pc = new FluidPushConstants { Pass = pass, Substep = substep, Parity = 0, Arg = 0 };
        MemoryMarshal.Write(_push.AsSpan(), in pc);
        Device.ComputeListSetPushConstant(list, (ReadOnlySpan<byte>)_push, (uint)_push.Length);
        if (indirect)
            Device.ComputeListDispatchIndirect(list, Solver.Buffers[FluidGpuBuffers.Args], (uint)(argsWord * 4));
        else
            Device.ComputeListDispatch(list, Math.Max(groups, 1u), 1, 1);
        Device.ComputeListEnd();
    }

    private Rid PipelineRid() => PerDevice.TryGetValue(Device.GetInstanceId(), out var e) ? e.pipeline : default;

    // Made on first use; a world with no skinned person never pays for it.
    private Rid Pipeline()
    {
        ulong key = Device.GetInstanceId();
        if (PerDevice.TryGetValue(key, out var entry))
        {
            if (entry.error.Length > 0)
                Error = entry.error;
            return entry.pipeline;
        }
        string error = string.Empty;
        Rid shader = default, pipeline = default;
        var file = GD.Load<RDShaderFile>(ShaderPath);
        if (file == null)
            error = "not imported: " + ShaderPath;
        else
        {
            var spirv = file.GetSpirV();
            string compile = spirv.CompileErrorCompute ?? string.Empty;
            if (compile.Length > 0)
                error = "FluidSkinCollide: " + compile.Trim();
            else
            {
                shader = Device.ShaderCreateFromSpirV(spirv, "FluidSkinCollide");
                if (!shader.IsValid)
                    error = "FluidSkinCollide: the device refused the shader";
                else
                {
                    pipeline = Device.ComputePipelineCreate(shader);
                    if (!pipeline.IsValid)
                    {
                        Device.FreeRid(shader);
                        shader = default;
                        error = "FluidSkinCollide: the device refused the pipeline";
                    }
                }
            }
        }
        PerDevice[key] = (shader, pipeline, error);
        if (error.Length > 0)
        {
            Error = error;
            Lumora.Core.Logging.Logger.Warn("Fluids: skin collision off, proxies only: " + error);
        }
        return pipeline;
    }

    private void EnsureBuffers(int triangles)
    {
        int triCap = 1024;
        while (triCap < triangles && triCap < MaxTriangles)
            triCap <<= 1;
        int entryCap = Math.Min(triCap * EntriesPerTriangle, MaxEntries);
        bool fresh = !_own[Params].IsValid;
        if (fresh)
        {
            Make(Params, (uint)(ParamsHead + MaxBoxes * 32));
            Make(Occupied, (uint)((1 << OccupiedBits) / 8));
            Make(Cells, (uint)((3 * (1 << CellBits) + 2 * ((1 << CellBits) / FluidLayout.GroupSize)) * 4));
            Make(State, StateWords * 4);
            Make(Wet, WetSlots * WetWords * 4);
        }
        if (triCap > _triCap)
        {
            Replace(Tris, (uint)(triCap * TriangleBytes));
            _triCap = triCap;
        }
        if (entryCap > _entryCap)
        {
            Replace(Entries, (uint)(entryCap * 4));
            _entryCap = entryCap;
        }
    }

    private void Make(int binding, uint bytes)
    {
        bytes = Math.Max(bytes, 16u);
        _own[binding] = Device.StorageBufferCreate(bytes, new byte[bytes]);
        _ownSizes[binding] = bytes;
        _ownVersion++;
    }

    private void Replace(int binding, uint bytes)
    {
        if (_own[binding].IsValid)
            Device.FreeRid(_own[binding]);
        Make(binding, bytes);
    }

    // The solver's whole table, then ours; the shader takes what it declares. Rebuilt when either side remade a buffer.
    private bool EnsureSet(FluidGpuSolver solver, Rid pipeline)
    {
        if (_set.IsValid && _solverVersion == solver.Buffers.Version && _setOwnVersion == _ownVersion && Device.UniformSetIsValid(_set))
            return true;
        if (_set.IsValid && Device.UniformSetIsValid(_set))
            Device.FreeRid(_set);
        _set = default;
        var shader = PerDevice.TryGetValue(Device.GetInstanceId(), out var e) ? e.shader : default;
        if (!shader.IsValid)
            return false;
        var uniforms = new global::Godot.Collections.Array<RDUniform>();
        for (int b = 0; b < FluidGpuBuffers.Count; b++)
        {
            var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = b };
            u.AddId(solver.Buffers[b]);
            uniforms.Add(u);
        }
        for (int b = Params; b < Count; b++)
        {
            var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = b };
            u.AddId(_own[b]);
            uniforms.Add(u);
        }
        _set = Device.UniformSetCreate(uniforms, shader, 0);
        _solverVersion = solver.Buffers.Version;
        _setOwnVersion = _ownVersion;
        return _set.IsValid;
    }

    private void ReadWetAsync()
    {
        if (!_own[Wet].IsValid)
            return;
        if (Solver.Local)
            return;
        if (!_callableMade)
        {
            _onWet = Callable.From<byte[]>(OnWet);
            _callableMade = true;
        }
        Device.BufferGetDataAsync(_own[Wet], _onWet, 0, _ownSizes[Wet]);
        Device.BufferClear(_own[Wet], 0, _ownSizes[Wet]);
        _wetInFlight = true;
    }

    private void OnWet(byte[] data)
    {
        _wetInFlight = false;
        if (_disposed || data == null)
            return;
        Decode(data, _wetDecoded);
        if (_wetDecoded.Count == 0)
            return;
        WetRead?.Invoke(_wetDecoded);
        if (World != null)
            FluidSkinWet.For(World).Post(_wetDecoded);
    }

    // The wet table as samples, every claimed slot once.
    public static void Decode(ReadOnlySpan<byte> data, List<FluidWetSample> into)
    {
        into.Clear();
        var words = MemoryMarshal.Cast<byte, uint>(data);
        for (int e = 0; e + WetWords <= words.Length; e += WetWords)
        {
            uint key = words[e];
            uint contacts = words[e + 1];
            if ((key & 0x80000000u) == 0 || contacts == 0)
                continue;
            float inv = 1f / contacts;
            var local = new Lumora.Core.Math.float3((int)words[e + 3] * 1e-4f * inv, (int)words[e + 4] * 1e-4f * inv, (int)words[e + 5] * 1e-4f * inv);
            var normal = new Lumora.Core.Math.float3((int)words[e + 6] * 1e-3f, (int)words[e + 7] * 1e-3f, (int)words[e + 8] * 1e-3f);
            float len = normal.Length;
            normal = len > 1e-6f ? normal / len : Lumora.Core.Math.float3.Up;
            int substance = words[e + 9] > 0 ? (int)words[e + 9] - 1 : 0;
            into.Add(new FluidWetSample((int)((key >> 25) & 31), (int)((key >> 21) & 15), local, normal, (int)contacts, words[e + 2] * 1e-12f, substance));
        }
    }

    // Local devices (tests), after Submit and Sync.
    public byte[] ReadWet(bool clear = true)
    {
        if (!_own[Wet].IsValid)
            return Array.Empty<byte>();
        var data = Device.BufferGetData(_own[Wet], 0, _ownSizes[Wet]);
        if (clear)
            Device.BufferClear(_own[Wet], 0, _ownSizes[Wet]);
        return data;
    }

    public uint[] ReadState()
    {
        if (!_own[State].IsValid)
            return new uint[StateWords];
        return MemoryMarshal.Cast<byte, uint>(Device.BufferGetData(_own[State], 0, _ownSizes[State]).AsSpan()).ToArray();
    }

    private static void WriteF(Span<byte> span, int offset, float value) => MemoryMarshal.Write(span[offset..], in value);

    private static void WriteU(Span<byte> span, int offset, uint value) => MemoryMarshal.Write(span[offset..], in value);

    public void Dispose()
    {
        if (_disposed)
            return;
        bool quitting = LumoraEngine.Current is { State: EngineState.ShuttingDown or EngineState.Disposed };
        if (quitting && _wetInFlight && _own[State].IsValid)
        {
            try
            {
                Device.BufferGetData(_own[State], 0, 4);
            }
            catch (Exception ex)
            {
                Lumora.Core.Logging.Logger.Warn("Fluids: landing the skin pass's read-back at quit failed: " + ex.Message);
            }
        }
        _disposed = true;
        Tell(false);
        if (ReferenceEquals(Solver.RecordAfterCollide?.Target, this))
            Solver.RecordAfterCollide = null;
        if (_set.IsValid && Device.UniformSetIsValid(_set))
            Device.FreeRid(_set);
        _set = default;
        for (int b = 0; b < Count; b++)
        {
            if (_own[b].IsValid)
                Device.FreeRid(_own[b]);
            _own[b] = default;
        }
    }
}
