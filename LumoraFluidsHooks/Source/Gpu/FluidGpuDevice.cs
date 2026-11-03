// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Lumora.Core;
using Lumora.Core.Math;
using GodotEngine = Godot.Engine;
using LumoraEngine = Lumora.Core.Engine;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids.Hooks;

// THE GPU BEHIND THE DATA SIDE'S SEAM
//
// The device check's answer for the session, a solver per world that simulates here, and the step at
// RenderingServer.FramePreDraw: on the main thread under the Safe model, after every world update and hook flush, and
// before any viewport draws, so both eyes, mirrors and cameras draw what this frame computed.
//
// FramePreDraw is not a clock. It stops while every window is minimized (and in low-processor mode when nothing
// changed) while the worlds keep updating, and Godot also draws now and then while minimized to flush resources. So a
// world steps only on a block its update packed since the last step (a stray draw steps nothing), a background
// world's block is spread one sub-frame per drawn frame, and the device counts drawn frames for the real-time bit a
// keeper needs: XR active, or a window that is not minimized and drew at least 27 frames in the last second (a
// minimized window can go on drawing, about 145 fps here, but nobody is there). The 1/30 s dt cap keeps the fluid in
// real time down to 30 fps; below that it runs slow, and a slow keeper hands its job on.
//
// A stretch with no draws while the world went on updating (a stopped render loop) leaves the simulation stale: the
// blocks of that stretch were never stepped, their commands never applied, and the keeper and the ledger moved on. So
// the first draw after a second or more with three blocks or more unstepped skips the stale block and has the space
// lay every system in again from the ledger, before anything bins.
//
// A seed straight into the solver is noticed by its Seeds counter, and wakes the world the same way
// FluidDeviceWorld.Seed does.
//
// One world's frame, in order: the solver's chain (commands, sort, substeps, frame end, summary), then every
// FluidRenderer of that world records its surface from the particles the frame ended on, then the residue. Two
// timestamps go round all of it; that is the GPU time the budget sees. An asleep world records nothing at all, so its
// surface keeps last frame's triangles byte for byte and costs only its draw. A background world simulates with no
// surface (nobody looks at it), and its first surface after that re-extracts everything.
//
// Residue rides people, not the fluid: coated people walk away from a puddle that sleeps, so a world that steps
// nothing this draw still records its spots.
//
// Pressure. A desktop card with room clocks itself down, and its timestamps then read two to three times the real
// cost (the 3080 Ti sat at 600-900 MHz of 1.8 GHz for a 2 L pour at 75 fps). The device tells the budget whether
// frames are short of time: a headset always is, a desktop is when it draws under 90% of its cap (the refresh rate
// under vsync, the engine's frame cap, the lower of the two) for two one-second windows running. With no cap known
// it counts as pressed.
//
// The kernels are made one per drawn frame before the first world opens (the load hold), never in a burst inside a
// running world's frame: Open answers null until they are all made, so no block is ever packed for a solver that
// cannot run it and no emit is lost.
//
// Its constructor and statics touch nothing of Godot, because the module's boot sets it as the seam before Godot's
// interop exists in an export. Teardown runs in World.WhenDestroyed, which under Safe is the main thread too: buffers
// are freed at once and the device defers the real release until the GPU is done with the frame.
//
// Quitting. Each recorded frame leaves the solver's summary read-back in flight for a frame or two, and the device
// only calls a read-back's callback when it next waits on that frame. At quit the last wait is the device's own
// teardown, which comes after the C# language is gone: Callable.is_valid on a plain-delegate callback asks the deleted
// language for its object, the engine reads through null, and the game dies after "Engine Shutdown Complete" (exit
// 139). So a world closed while the engine shuts down first makes the device wait on every frame with one
// synchronous read, which lands those read-backs while the runtime is still up (the solver, about to be freed, drops
// them), and the last world to go also frees the kernels. In a running session nothing changes: frames keep being
// drawn and the read-backs land on their own. -xlinka
public sealed class FluidGpuDevice : IFluidGpu
{
    public static readonly FluidGpuDevice Instance = new();

    // Frames a second at or above which a desktop machine counts as drawing in real time.
    public const int RealTimeFramesPerSecond = 27;
    public const ulong RealTimeWindowMs = 1000;
    // A surface's bounds are snapped outward to this grid, so a puddle that creeps does not re-set its AABB each frame.
    public const float BoundsSnap = 0.25f;
    // The read-back bounds lag the particles by about two frames: grown by vmax over this many 90 Hz frames, plus a pad.
    public const float BoundsLeadFrames = 4f;
    public const float BoundsPad = 0.25f;
    // A gap between draws this long, with this many blocks packed and never stepped, is a stall (see the header).
    public const ulong StallMs = 1000;
    public const int StallBlocks = 3;

    private readonly Dictionary<World, FluidDeviceWorld> _worlds = new();
    private readonly List<World> _gone = new();
    private RenderingDevice? _device;
    private FluidGpuTier _autoTier = FluidGpuTier.None;
    private FluidGpuTier _tier = FluidGpuTier.None;
    private volatile bool _tierDirty;
    private bool _wasVr;
    private bool _settingsHooked;
    private bool _warming;
    private ulong _windowStartMs;
    private ulong _windowStartFrames;
    private double _framesPerSecond = 60;
    private bool _pressed = true;
    private bool _shortLast;
    private bool _minimized;
    private ulong _lastFrameMs;

    private FluidGpuDevice()
    {
    }

    public FluidGpuPath Path { get; private set; } = FluidGpuPath.Pending;

    // This machine's tier: Auto's answer, or the user's pick for flat or headset play. None on the GPU path means the
    // user chose view only; every world then draws from the ledger and simulates nothing.
    public FluidGpuTier Tier
    {
        get
        {
            if (Path != FluidGpuPath.Gpu)
                return FluidGpuTier.None;
            bool vr = InHeadset;
            if (_tierDirty || vr != _wasVr)
            {
                _tierDirty = false;
                _wasVr = vr;
                _tier = FluidQuality.Resolve(_autoTier);
            }
            return _tier;
        }
    }

    // What the device check found before the user's pick.
    public FluidGpuTier AutoTier => _autoTier;

    public bool DrawingInRealTime
    {
        get
        {
            if (Path != FluidGpuPath.Gpu)
                return false;
            if (InHeadset)
                return true;
            if (_minimized)
                return false;
            SampleDrawing();
            return _framesPerSecond >= RealTimeFramesPerSecond;
        }
    }

    public double DrawnFramesPerSecond => _framesPerSecond;

    // Frames short of time (see the header); sampled with the drawn frame rate.
    public bool FramePressed
    {
        get
        {
            if (InHeadset)
                return true;
            SampleDrawing();
            return _pressed;
        }
    }

    // The frame rate this machine aims at, 0 when unknown; sampled with the drawn frame rate.
    public int TargetFramesPerSecond { get; private set; }

    public string Status { get; private set; } = "pending";

    public int Worlds => _worlds.Count;

    // Sub-frames recorded since the device started, the draws that found nothing to step, and the worlds closed.
    public long Steps { get; private set; }

    public long IdleDraws { get; private set; }

    public long Closed { get; private set; }

    // Worlds closed at quit whose read-backs were landed first (see the header).
    public long ReadBacksDrained { get; private set; }

    // Stale stretches a world was laid in again after, and seeds noticed through the solver's counter.
    public long StaleReseeds { get; private set; }

    public long SeedWakes { get; private set; }

    // The main window was minimized at the last draw.
    public bool Minimized => _minimized;

    // The kernels are all made; Open answers null until then.
    public bool KernelsReady => _device != null && FluidGpuPipelines.For(_device).Ready;

    public string KernelError { get; private set; } = string.Empty;

    // Main-thread ms of the last FramePreDraw, every world's recording included.
    public double LastFrameMs { get; private set; }

    private static bool InHeadset => LumoraEngine.Current?.InputInterface?.IsVRActive == true;

    // The engine is tearing down (its WorldManager disposes every world inside that state), so no frame will be drawn
    // again. No engine at all is a test, which keeps drawing.
    private static bool Quitting => LumoraEngine.Current is { State: EngineState.ShuttingDown or EngineState.Disposed };

    public void BeginGpu(RenderingDevice device, FluidGpuTier tier)
    {
        _device = device;
        _autoTier = tier;
        _tierDirty = true;
        Path = FluidGpuPath.Gpu;
        HookSettings();
        _windowStartMs = Time.GetTicksMsec();
        _windowStartFrames = (ulong)GodotEngine.GetFramesDrawn();
        FluidSpace.FramePressed = () => Instance.FramePressed;
        var resolved = Tier;
        Status = resolved == FluidGpuTier.None
            ? $"GPU ready ({FluidPlatform.RendererName}), view only by choice"
            : $"GPU ready ({FluidPlatform.RendererName}, {resolved})";
        // The first fluid hook is starting, so a fluid world is on its way: the kernels start now, one a drawn frame.
        _warming = resolved != FluidGpuTier.None;
    }

    public void BeginViewOnly(string reason)
    {
        _device = null;
        _autoTier = FluidGpuTier.None;
        _tier = FluidGpuTier.None;
        _warming = false;
        Path = FluidGpuPath.ViewOnly;
        Status = $"view only ({reason})";
    }

    // The user's pick can change mid-session from any thread (an account pull); the tier is re-read on next use.
    private void HookSettings()
    {
        if (_settingsHooked)
            return;
        _settingsHooked = true;
        Settings.Changed += (key, _) =>
        {
            if (FluidQuality.IsQualityKey(key))
                _tierDirty = true;
        };
    }

    // Frames drawn over the last full window. A minimized window draws nothing, so its rate falls to zero even though
    // FramePreDraw, which would otherwise do the counting, has stopped firing.
    private void SampleDrawing()
    {
        ulong now = Time.GetTicksMsec();
        ulong elapsed = now - _windowStartMs;
        if (elapsed < RealTimeWindowMs)
            return;
        ulong frames = (ulong)GodotEngine.GetFramesDrawn();
        _framesPerSecond = (frames - _windowStartFrames) * 1000.0 / elapsed;
        _windowStartMs = now;
        _windowStartFrames = frames;
        TargetFramesPerSecond = TargetRate();
        // Two short windows in a row: one hitch (a shader compiling on first draw) is not pressure.
        bool shortNow = TargetFramesPerSecond <= 0 || _framesPerSecond < 0.9 * TargetFramesPerSecond;
        _pressed = shortNow && (_shortLast || TargetFramesPerSecond <= 0);
        _shortLast = shortNow;
    }

    private void SampleWindow()
    {
        try
        {
            _minimized = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Minimized;
        }
        catch (Exception)
        {
            _minimized = false;
        }
    }

    private static int TargetRate()
    {
        int target = 0;
        try
        {
            var vsync = DisplayServer.WindowGetVsyncMode();
            if (vsync is DisplayServer.VSyncMode.Enabled or DisplayServer.VSyncMode.Adaptive)
            {
                float hz = DisplayServer.ScreenGetRefreshRate();
                if (hz > 0f)
                    target = (int)MathF.Round(hz);
            }
            int cap = GodotEngine.MaxFps;
            if (cap > 0)
                target = target > 0 ? Math.Min(target, cap) : cap;
        }
        catch (Exception)
        {
            target = 0;
        }
        return target;
    }

    public static void OnFramePreDraw() => Instance.Frame();

    private void Frame()
    {
        if (Path != FluidGpuPath.Gpu || _device == null)
            return;
        long started = Stopwatch.GetTimestamp();
        SampleDrawing();
        SampleWindow();
        ulong nowMs = Time.GetTicksMsec();
        bool gap = _lastFrameMs != 0 && nowMs - _lastFrameMs >= StallMs;
        _lastFrameMs = nowMs;
        if (_warming)
            Warm();
        _gone.Clear();
        foreach (var (world, gpu) in _worlds)
        {
            if (world.IsDestroyed || world.IsDisposed)
            {
                _gone.Add(world);
                continue;
            }
            var block = gpu.Block;
            if (gpu.Solver.Seeds != gpu.SeedsSeen)
            {
                gpu.SeedsSeen = gpu.Solver.Seeds;
                SeedWakes++;
                FluidSpace.Find(world)?.Wake();
            }
            if (gap && block.Sequence - gpu.Stepped >= StallBlocks && FluidSpace.Find(world) is { } stale)
            {
                gpu.Stepped = block.Sequence;
                gpu.StaleReseeds++;
                StaleReseeds++;
                stale.ReseedFromLedger();
                IdleDraws++;
                continue;
            }
            if (!FluidFramePacker.NextSubframe(block, ref gpu.Stepped, out float dt, out bool first))
            {
                IdleDraws++;
                gpu.IdleDraws++;
                if (block.Focused)
                    RecordResidue(gpu);
                continue;
            }
            FluidFramePacker.ApplySubframe(block, dt, first);
            Record(gpu, first);
        }
        foreach (var world in _gone)
            Close(world);
        LastFrameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // One kernel a drawn frame until all are made. A kernel that fails puts the session on the view-only path, the
    // same as failing the gate: the renderers draw the ledger instead.
    private void Warm()
    {
        var pipelines = FluidGpuPipelines.For(_device!);
        if (pipelines.Ready)
        {
            _warming = false;
            return;
        }
        pipelines.CreateNext();
        if (pipelines.Failed)
        {
            _warming = false;
            KernelError = pipelines.Error;
            LumoraLogger.Warn($"Fluids: a solver kernel failed ({pipelines.Error}); this machine is view only");
            BeginViewOnly("kernel failed");
            foreach (var world in new List<World>(_worlds.Keys))
                Close(world);
        }
        else if (pipelines.Ready)
        {
            _warming = false;
        }
    }

    // One sub-frame of one world. A background block's repeat keeps the block's sequence, so the solver re-runs the
    // simulation only: no upload, no command applied twice, and no surface.
    private void Record(FluidDeviceWorld gpu, bool first)
    {
        long started = Stopwatch.GetTimestamp();
        var solver = gpu.Solver;
        var block = gpu.Block;
        try
        {
            if (!first)
                gpu.Repeats++;
            bool surface = (block.Header.Flags & FluidFramePacker.FrameNoSurface) == 0;
            bool allDirty = surface && (gpu.SurfaceBehind || (first && ClearsWithoutTrace(block)));
            bool removed = surface && !allDirty && first && RemovedBox(block, out _removeMin, out _removeMax);

            gpu.Chain.Collect();
            gpu.Chain.Begin();
            bool stepped = solver.Step();
            gpu.ResourceError = string.Empty;
            bool residue = false;
            if (stepped && solver.LastDispatches > 0)
            {
                Steps++;
                gpu.Steps++;
                if (surface)
                {
                    gpu.LastSurfaceDispatches = RecordSurfaces(gpu, allDirty, removed);
                    FluidResidueRendererHook.RecordWorld(gpu.World);
                    residue = true;
                    gpu.SurfaceRecords++;
                    gpu.SurfaceBehind = false;
                }
                else
                {
                    gpu.LastSurfaceDispatches = 0;
                    gpu.SurfaceBehind = true;
                }
            }
            if (!residue && block.Focused)
                FluidResidueRendererHook.RecordWorld(gpu.World);
            gpu.Chain.End();
            // The repeats that follow read nothing of these (the solver applies a block's commands once); the
            // device's own surface rules look at them only on a first sub-frame too.
            if (first && !block.Focused)
            {
                block.Commands.Clear();
                block.LedgerChanges.Clear();
            }
        }
        catch (Exception ex)
        {
            if (!gpu.LoggedFailure)
            {
                gpu.LoggedFailure = true;
                LumoraLogger.Error($"Fluids: a GPU step failed in '{gpu.World.Name}': {ex.Message}");
            }
            gpu.ResourceError = ex.Message;
        }
        gpu.LastMainMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private float3 _removeMin;
    private float3 _removeMax;

    private static void RecordResidue(FluidDeviceWorld gpu)
    {
        try
        {
            FluidResidueRendererHook.RecordWorld(gpu.World);
        }
        catch (Exception ex)
        {
            if (!gpu.LoggedFailure)
            {
                gpu.LoggedFailure = true;
                LumoraLogger.Error($"Fluids: the residue record failed in '{gpu.World.Name}': {ex.Message}");
            }
        }
    }

    // Commands that take particles away with no place attached, or everything at once: nothing is left behind to
    // stamp the bricks they emptied, so every brick re-extracts.
    private static bool ClearsWithoutTrace(FluidFrameBlock block)
    {
        foreach (var c in block.Commands)
        {
            switch ((FluidGpuOp)c.Op)
            {
                case FluidGpuOp.ClearAll:
                case FluidGpuOp.Reseed:
                case FluidGpuOp.ClearOrigin:
                case FluidGpuOp.Evaporate:
                case FluidGpuOp.FadeOrigin:
                case FluidGpuOp.RemoveParticles:
                    return true;
            }
        }
        return false;
    }

    // The world-space box round every Remove sphere this block (drains, a Loom remove), for the surface's dirty box.
    private static bool RemovedBox(FluidFrameBlock block, out float3 min, out float3 max)
    {
        min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new float3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        var frames = block.Frames;
        foreach (var c in block.Commands)
        {
            if ((FluidGpuOp)c.Op != FluidGpuOp.Remove)
                continue;
            var center = new float3(c.A.x, c.A.y, c.A.z);
            float radius = c.A.w;
            int frame = c.System < (uint)block.Systems.Count ? (int)block.Systems[(int)c.System].Frame : 0;
            if (frame > 0 && frame < frames.Count)
            {
                var f = frames[frame];
                center = f.Position + f.Rotation * (center * f.Scale);
                radius *= MathF.Abs(f.Scale);
            }
            var r = new float3(radius, radius, radius);
            min = Min(min, center - r);
            max = Max(max, center + r);
            any = true;
        }
        return any;
    }

    // Every FluidRenderer of the world, from the particle half the solver's frame ended on. A renderer draws its own
    // system (a container's in that container's frame), or every system when it names none.
    private int RecordSurfaces(FluidDeviceWorld gpu, bool allDirty, bool removed)
    {
        var hooks = FluidRendererHook.InWorld(gpu.World);
        if (hooks.Count == 0)
            return 0;
        var space = FluidSpace.Find(gpu.World);
        var block = gpu.Block;
        var root = WorldRootTransform(gpu.World);
        // The render lane's reading of the solver's buffers; the device adds which system and which frame.
        var source = FluidSurfaceSource.FromSolver(gpu.Solver);
        if (!source.Particles.IsValid)
            return 0;
        int dispatches = 0;
        for (int i = 0; i < hooks.Count; i++)
        {
            var hook = hooks[i];
            var renderer = hook.Owner;
            var pass = hook.Pass;
            if (pass == null || renderer == null || renderer.IsDestroyed || renderer.Slot is not { IsDestroyed: false } slot)
                continue;
            var system = renderer.System.Target;
            int index = space?.SystemIndex(system) ?? -1;
            int frame = index >= 0 ? space!.Packer.FrameOf(index) : 0;
            var local = source;
            local.System = index >= 0 ? (uint)index : FluidLayout.None;
            local.ToLocal = root * FrameTransform(block, frame);
            // The surface's cells follow the spacing the particles really have on this machine's tier.
            if (system != null && space != null)
                pass.Spacing = system.SpacingOn(space.Tier);
            if (allDirty)
            {
                pass.MarkAllDirty();
                gpu.AllDirtyMarks++;
            }
            else if (removed)
            {
                pass.SetDirtyBox(0, ToSlotBox(slot, _removeMin, _removeMax));
            }
            if (space != null)
                FeedBounds(hook, space, system, index, slot, block);
            dispatches += hook.Record(local);
        }
        return dispatches;
    }

    private static Transform3D WorldRootTransform(World world)
        => world.GodotSceneRoot is Node3D node && GodotObject.IsInstanceValid(node) && node.IsInsideTree() ? node.GlobalTransform : Transform3D.Identity;

    // A frame's space in world space: identity for the world, the container's pose for a container frame.
    private static Transform3D FrameTransform(FluidFrameBlock block, int frame)
    {
        if (frame <= 0 || frame >= block.Frames.Count)
            return Transform3D.Identity;
        var f = block.Frames[frame];
        var basis = new Basis(new Quaternion(f.Rotation.x, f.Rotation.y, f.Rotation.z, f.Rotation.w)).Scaled(new Vector3(f.Scale, f.Scale, f.Scale));
        return new Transform3D(basis, new Vector3(f.Position.x, f.Position.y, f.Position.z));
    }

    // FluidSystem.Bounds when set (the fluid never leaves them); else the last summary's bounds grown by how far the
    // fastest particle gets while the read-back catches up, plus a pad. Either is snapped outward, and "moving" asks
    // the hook to re-set its AABB at the moving-shadow rate while anything is awake.
    private static void FeedBounds(FluidRendererHook hook, FluidSpace space, FluidSystem? system, int index, Slot slot, FluidFrameBlock block)
    {
        float3 min, max;
        bool moving;
        if (system != null && system.Slot is { IsDestroyed: false } systemSlot && system.Bounds.Value is var b && b.x > 0f && b.y > 0f && b.z > 0f)
        {
            var c = system.BoundsCenter.Value;
            var h = b * 0.5f;
            Corners(systemSlot, c - h, c + h, out min, out max);
            moving = index >= 0 && space.StatusOf(index).Awake > 0;
        }
        else if (!StatusBounds(space, index, block, out min, out max, out moving))
        {
            return;
        }
        var box = ToSlotBox(slot, min, max);
        var lo = box.Position;
        var hi = box.End;
        lo = new Vector3(MathF.Floor(lo.X / BoundsSnap), MathF.Floor(lo.Y / BoundsSnap), MathF.Floor(lo.Z / BoundsSnap)) * BoundsSnap;
        hi = new Vector3(MathF.Ceiling(hi.X / BoundsSnap), MathF.Ceiling(hi.Y / BoundsSnap), MathF.Ceiling(hi.Z / BoundsSnap)) * BoundsSnap;
        hook.SetBounds(new Aabb(lo, hi - lo), moving);
    }

    // World-space bounds of one system (or of every system when index is -1) from the last summary, grown for lag.
    private static bool StatusBounds(FluidSpace space, int index, FluidFrameBlock block, out float3 min, out float3 max, out bool moving)
    {
        min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new float3(float.MinValue, float.MinValue, float.MinValue);
        moving = false;
        bool any = false;
        int count = space.Systems.Count;
        for (int i = index >= 0 ? index : 0; i < (index >= 0 ? index + 1 : count); i++)
        {
            ref readonly var status = ref space.StatusOf(i);
            if (status.Live == 0 || !Finite(status.BoundsMin) || !Finite(status.BoundsMax))
                continue;
            float lead = MathF.Max(0f, float.IsFinite(status.Vmax) ? status.Vmax : 0f) * BoundsLeadFrames / 90f + BoundsPad;
            var grow = new float3(lead, lead, lead);
            var lo = status.BoundsMin - grow;
            var hi = status.BoundsMax + grow;
            int frame = space.Packer.FrameOf(i);
            if (frame > 0 && frame < block.Frames.Count)
            {
                var f = block.Frames[frame];
                FrameCorners(f, lo, hi, out lo, out hi);
            }
            min = Min(min, lo);
            max = Max(max, hi);
            moving |= status.Awake > 0;
            any = true;
        }
        return any;
    }

    // A local box of a slot as a world box (its eight corners).
    private static void Corners(Slot slot, float3 lo, float3 hi, out float3 min, out float3 max)
    {
        min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new float3(float.MinValue, float.MinValue, float.MinValue);
        for (int k = 0; k < 8; k++)
        {
            var p = slot.LocalPointToGlobal(new float3((k & 1) != 0 ? hi.x : lo.x, (k & 2) != 0 ? hi.y : lo.y, (k & 4) != 0 ? hi.z : lo.z));
            min = Min(min, p);
            max = Max(max, p);
        }
    }

    private static void FrameCorners(in FluidFrameRecord f, float3 lo, float3 hi, out float3 min, out float3 max)
    {
        min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new float3(float.MinValue, float.MinValue, float.MinValue);
        for (int k = 0; k < 8; k++)
        {
            var p = new float3((k & 1) != 0 ? hi.x : lo.x, (k & 2) != 0 ? hi.y : lo.y, (k & 4) != 0 ? hi.z : lo.z);
            p = f.Position + f.Rotation * (p * f.Scale);
            min = Min(min, p);
            max = Max(max, p);
        }
    }

    // A world box in a slot's local space, as the box round its eight corners there.
    private static Aabb ToSlotBox(Slot slot, float3 min, float3 max)
    {
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int k = 0; k < 8; k++)
        {
            var p = slot.GlobalPointToLocal(new float3((k & 1) != 0 ? max.x : min.x, (k & 2) != 0 ? max.y : min.y, (k & 4) != 0 ? max.z : min.z));
            var v = new Vector3(p.x, p.y, p.z);
            lo = lo.Min(v);
            hi = hi.Max(v);
        }
        return new Aabb(lo, hi - lo);
    }

    private static bool Finite(float3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    private static float3 Min(float3 a, float3 b) => new(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));

    private static float3 Max(float3 a, float3 b) => new(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));

    public IFluidGpuWorld? Open(World world)
    {
        if (Path != FluidGpuPath.Gpu || _device == null || world == null || world.IsDestroyed || Tier == FluidGpuTier.None)
            return null;
        if (_worlds.TryGetValue(world, out var gpu))
            return gpu;
        if (!FluidGpuPipelines.For(_device).Ready)
        {
            _warming = true;
            return null;
        }
        gpu = new FluidDeviceWorld(world, new FluidGpuSolver(_device));
        _worlds[world] = gpu;
        // The ledger's bin, reconcile and reseed kernels record through this solver from its first frame.
        try
        {
            FluidLedgerKernels.Attach(gpu);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Fluids: the ledger kernels did not attach in '{world.Name}': {ex.Message}");
        }
        // Whoever opened it, the world's teardown frees it, minimized window or not.
        world.WhenDestroyed += Close;
        return gpu;
    }

    // The device's own record of a world, for the inspector and the in-game checks.
    public FluidDeviceWorld? WorldOf(World world) => world != null && _worlds.TryGetValue(world, out var gpu) ? gpu : null;

    public void Close(World world)
    {
        if (world == null)
            return;
        if (!_worlds.Remove(world, out var gpu))
            return;
        world.WhenDestroyed -= Close;
        Closed++;
        bool quitting = Quitting;
        try
        {
            if (quitting)
                LandReadBacks(gpu);
            gpu.Dispose();
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Fluids: freeing '{world.Name}' on the device failed: {ex.Message}");
        }
        if (quitting && _worlds.Count == 0)
            FreeKernels();
    }

    // At quit (see the header): one synchronous read makes the device wait on every frame, the one being recorded
    // included, so each summary read-back still in flight lands now. Only a solver that recorded has any.
    private void LandReadBacks(FluidDeviceWorld gpu)
    {
        var solver = gpu.Solver;
        var buffers = solver.Buffers;
        if (solver.Recorded == 0 || !buffers.Created)
            return;
        solver.Device.BufferGetData(buffers[FluidGpuBuffers.Counters], 0, 4);
        ReadBacksDrained++;
        LumoraLogger.Log($"Fluids: '{gpu.World.Name}' closed at quit; its read-backs landed first");
    }

    // The kernels outlive every world in a session; at quit they go with the last one, while the device is whole.
    private void FreeKernels()
    {
        if (_device == null)
            return;
        _warming = false;
        try
        {
            FluidGpuPipelines.Forget(_device);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Fluids: freeing the solver kernels failed: {ex.Message}");
        }
    }
}
