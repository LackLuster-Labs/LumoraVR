// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;

namespace Lumora.Godot.Hooks;

// A WORLD'S SIMULATED BODIES, ONCE A FRAME.
//
// The hook queue is budgeted, six milliseconds a frame with the rest waiting, so ten thousand falling cubes
// going through it one hook each would catch up with their bodies only every thirty frames or so, and a pile
// would draw where it was a second ago. This walks only the bodies that are moving, in one pass with no queue: on a
// frame with a physics step each hands its new pose to the data model, and on every frame each is drawn
// between its last two steps, so motion is smooth at any frame rate. A body at rest is not here and costs
// nothing; a collision that wakes it puts it back.
//
// Bodies another machine simulates are drawn the same way between the poses that arrive from it. -xlinka
internal sealed class BodyPump
{
    private readonly WorldHook _world;
    private readonly List<RigidBodyHook> _awake = new();
    private readonly List<RigidBodyHook> _starting = new();
    private readonly List<RigidBodyHook> _following = new();
    private readonly HashSet<RigidBodyHook> _all = new();
    private readonly List<RigidBodyHook> _wakeLater = new();
    private ulong _physicsFrame = ulong.MaxValue;

    public BodyPump(WorldHook world) => _world = world;

    public int AwakeCount => _awake.Count;

    public int FollowingCount => _following.Count;

    // Few enough moving that the shadow copies for things that keep moving still pay off, and that moving each
    // object's node costs too little to go round it (RigidBodyHook draws past the node otherwise).
    public bool FewMoving => _awake.Count + _following.Count <= 64;

    // This frame's count and where the world's root node is, for drawing past the nodes under it.
    public int Frame { get; private set; }

    public Transform3D RootTransform { get; private set; } = Transform3D.Identity;

    public bool RootIsIdentity { get; private set; } = true;

    // Moving objects that draw alike, drawn together (MoverBatches).
    public MoverBatches Batches => _batches ??= new MoverBatches(_world);

    private MoverBatches? _batches;
    private GpuRigidWorld? _gpu;
    private readonly HashSet<RigidBodyHook> _gpuRequested = new();
    private bool _gpuChanged;

    public void Dispose()
    {
        _gpu?.Dispose();
        _gpu = null;
        _batches?.Dispose();
        _batches = null;
    }

    private const int ParallelReadsFrom = 1024;
    private const int ReadsPerPart = 512;
    private int _readTicks, _readParts;
    private System.Action<int>? _readPart;

    private void ReadPart(int part)
    {
        int count = _awake.Count;
        int from = part * count / _readParts;
        int to = (part + 1) * count / _readParts;
        for (int i = from; i < to; i++)
            _awake[i].ReadStep(_readTicks, Frame);
    }

    // How many moving objects get their pose written into the data model on one physics step. Past it, each is
    // written every few steps in turn: what is drawn here still comes from every step, and what goes over the
    // network, which another machine draws between arrivals, stays at a size a link can carry. Ten thousand
    // bodies every step was fifteen megabytes a second to each guest.
    private const int PoseWritesPerStep = 2048;

    public void Register(RigidBodyHook body)
    {
        _all.Add(body);
        GpuPreferenceChanged(body);
    }

    public void GpuPreferenceChanged(RigidBodyHook body)
    {
        var component = body.Component;
        bool requested = component.IsSimulationOwner && component.Enabled
            && component.SimulationDevice.Value == Lumora.Simulation.SimulationDevice.Gpu;
        _gpuChanged |= requested ? _gpuRequested.Add(body) : _gpuRequested.Remove(body);
    }

    public void Unregister(RigidBodyHook body)
    {
        _all.Remove(body);
        _gpuChanged |= _gpuRequested.Remove(body);
        _wakeLater.Remove(body);
        Remove(Kind.Awake, body);
        Remove(Kind.Starting, body);
        Remove(Kind.Following, body);
    }

    public void Wake(RigidBodyHook body) => Add(Kind.Awake, body);

    public void Rest(RigidBodyHook body) => Remove(Kind.Awake, body);

    public void Start(RigidBodyHook body) => Add(Kind.Starting, body);

    public void Follow(RigidBodyHook body) => Add(Kind.Following, body);

    public void StopFollowing(RigidBodyHook body) => Remove(Kind.Following, body);

    // A wake heard from inside the physics step, where the world may not be touched: taken up next frame.
    public void WakeLater(RigidBodyHook body) => _wakeLater.Add(body);

    public void ContactWatchingChanged()
    {
        foreach (var body in _all)
            body.UpdateContactMonitor();
    }

    public void Step()
    {
        Frame++;
        var root = _world.WorldRoot;
        RootTransform = root != null && GodotObject.IsInstanceValid(root) && root.IsInsideTree() ? root.GlobalTransform : Transform3D.Identity;
        RootIsIdentity = RootTransform.IsEqualApprox(Transform3D.Identity);
        _batches?.BeginFrame();

        if (_wakeLater.Count > 0)
        {
            for (int i = 0; i < _wakeLater.Count; i++)
                _wakeLater[i].Rejoin();
            _wakeLater.Clear();
        }

        if (_starting.Count > 0)
        {
            for (int i = _starting.Count - 1; i >= 0; i--)
            {
                var body = _starting[i];
                if (body.PumpStart())
                    Remove(Kind.Starting, body);
            }
        }

        ulong frame = global::Godot.Engine.GetPhysicsFrames();
        int ticks = _physicsFrame == ulong.MaxValue ? 1 : (int)System.Math.Min(frame - _physicsFrame, 8UL);
        _physicsFrame = frame;
        if (_gpuRequested.Count > 0 || _gpuChanged)
        {
            _gpuChanged = false;
            (_gpu ??= new GpuRigidWorld(_world)).Step(_all, ticks);
        }
        float fraction = (float)global::Godot.Engine.GetPhysicsInterpolationFraction();
        int stride = (_awake.Count + PoseWritesPerStep - 1) / PoseWritesPerStep;

        // Many awake: their states are read on worker threads first, a share each, and the pass below only
        // uses what was read. Reading ten thousand states one after the other was a fifth of the pass.
        if (ticks > 0 && _awake.Count >= ParallelReadsFrom)
        {
            _readTicks = ticks;
            int parts = System.Math.Min(System.Environment.ProcessorCount, (_awake.Count + ReadsPerPart - 1) / ReadsPerPart);
            _readParts = parts;
            System.Threading.Tasks.Parallel.For(0, parts, _readPart ??= ReadPart);
        }

        for (int i = _awake.Count - 1; i >= 0; i--)
        {
            var body = _awake[i];
            if (!body.PumpAwake(ticks, fraction, stride))
                Remove(Kind.Awake, body);
        }

        if (_following.Count > 0)
        {
            double now = _world.Owner.Time.TotalTime;
            for (int i = _following.Count - 1; i >= 0; i--)
            {
                var body = _following[i];
                if (!body.PumpFollowing(now))
                    Remove(Kind.Following, body);
            }
        }

        _batches?.Flush();
    }

    // Each body keeps its own place in each list, so leaving one is a swap with the last, not a search.
    private enum Kind { Awake, Starting, Following }

    private List<RigidBodyHook> ListOf(Kind kind) => kind switch
    {
        Kind.Awake => _awake,
        Kind.Starting => _starting,
        _ => _following,
    };

    private static ref int IndexOf(RigidBodyHook body, Kind kind)
    {
        if (kind == Kind.Awake)
            return ref body.AwakeIndex;
        if (kind == Kind.Starting)
            return ref body.StartingIndex;
        return ref body.FollowingIndex;
    }

    private void Add(Kind kind, RigidBodyHook body)
    {
        ref int index = ref IndexOf(body, kind);
        if (index >= 0)
            return;
        var list = ListOf(kind);
        index = list.Count;
        list.Add(body);
    }

    private void Remove(Kind kind, RigidBodyHook body)
    {
        ref int index = ref IndexOf(body, kind);
        if (index < 0)
            return;
        var list = ListOf(kind);
        int last = list.Count - 1;
        if (index != last)
        {
            var moved = list[last];
            list[index] = moved;
            IndexOf(moved, kind) = index;
        }
        list.RemoveAt(last);
        index = -1;
    }
}
