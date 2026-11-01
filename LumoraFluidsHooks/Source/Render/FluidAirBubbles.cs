// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace Lumora.Fluids.Hooks;

// Secondary air is local visual state. The GPU measures entry, transports bubbles and releases them at the
// surface; it never modifies the liquid or its ledger. Only small allocation counters come back. -xlinka
public sealed class FluidAirBubbles : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Sample
    {
        public Vector4 Point;
        public Vector4 NormalSpeed;
        public uint State;
        public float Area;
        public uint Fresh;
        public uint Spare;
    }

    private static readonly Vector3[] Directions =
    {
        Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Forward, Vector3.Back,
        new Vector3(1, 1, 1).Normalized(), new Vector3(1, 1, -1).Normalized(),
        new Vector3(1, -1, 1).Normalized(), new Vector3(1, -1, -1).Normalized(),
        new Vector3(-1, 1, 1).Normalized(), new Vector3(-1, 1, -1).Normalized(),
        new Vector3(-1, -1, 1).Normalized(), new Vector3(-1, -1, -1).Normalized(),
    };

    private readonly RenderingDevice _rd;
    private readonly MultiMeshInstance3D _node;
    private readonly SphereMesh _mesh;
    private readonly ShaderMaterial _material;
    private readonly List<Sample> _samples = new();
    private readonly Dictionary<ulong, int> _states = new();
    private readonly HashSet<ulong> _present = new();
    private readonly List<ulong> _gone = new();
    private readonly Stack<int> _freeStates = new();
    private readonly byte[] _params = new byte[240];
    private readonly byte[] _push = new byte[16];
    private readonly byte[] _zeros = new byte[16];
    private readonly Rid[] _inputs = new Rid[14];
    private Rid _heads;
    private Rid _next;
    private int _gridSize;
    private int _particleCapacity;
    private long _meshBytes;
    private bool _failed;
    private Rid _shader;
    private Rid _pipeline;
    private Rid _set;
    private Rid _sampleBuffer;
    private Rid _readings;
    private Rid _bubbles;
    private Rid _counts;
    private Rid _free;
    private Rid _stateBuffer;
    private Rid _parameters;
    private Rid _multimesh;
    private Rid _instanceBuffer;
    private int _sampleCapacity;
    private int _stateCapacity;
    private int _nextState;
    private int _growTo;
    private uint _sequence;
    private bool _reading;
    private bool _disposed;
    private bool _enabled;
    private bool _doubled;
    private bool _setDirty;
    private uint[]? _received;
    private uint _receivedSequence;
    private uint _lastSampleSequence;
    private bool _confirmedEmpty = true;
    private int _readFrame;

    public FluidAirBubbles(RenderingDevice rd, Node3D parent, int initialCapacity = 64)
    {
        _rd = rd;
        _node = new MultiMeshInstance3D { Name = "FluidAir", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _material = new ShaderMaterial { RenderPriority = -1 };
        _mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 20, Rings = 12, Material = _material };
        try
        {
            parent.AddChild(_node);
            _material.Shader = GD.Load<Shader>("res://Modules/Fluids/Shaders/FluidAirBubble.gdshader");
            if (_material.Shader == null) throw new InvalidOperationException("Air material is not imported");
            var file = GD.Load<RDShaderFile>("res://Modules/Fluids/Compute/FluidAir.glsl");
            var spirv = file?.GetSpirV();
            if (spirv == null || !string.IsNullOrEmpty(spirv.CompileErrorCompute))
                throw new InvalidOperationException("Air shader is not imported: " + spirv?.CompileErrorCompute);
            _shader = _rd.ShaderCreateFromSpirV(spirv, "FluidAir");
            if (!_shader.IsValid) throw new InvalidOperationException("Air shader allocation failed");
            _pipeline = _rd.ComputePipelineCreate(_shader);
            if (!_pipeline.IsValid) throw new InvalidOperationException("Air pipeline allocation failed");
            _counts = Buffer(16);
            _parameters = Buffer(_params.Length);
            GrowBubbles(Math.Max(initialCapacity, 1));
            GrowSamples(1, 1);
            var meshData = RenderingServer.MeshGetSurface(_mesh.GetRid(), 0);
            foreach (string key in new[] { "vertex_data", "attribute_data", "skin_data", "index_data" })
                if (meshData.TryGetValue(key, out var bytes)) _meshBytes += bytes.AsByteArray().LongLength;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Capacity { get; private set; }
    public int Count { get; private set; }
    public long AllocatedBytes => (long)Capacity * (48 + 4 + 64 * (_doubled ? 2 : 1))
        + (long)_sampleCapacity * 80 + (long)_stateCapacity * 16 + _params.Length + 16
        + (long)(_gridSize + _particleCapacity) * 4 + _meshBytes;
    public string Status { get; private set; } = string.Empty;
    public Rid BubbleBuffer => _bubbles;
    public Rid CounterBuffer => _counts;

    public void Retry() { _failed = false; Status = string.Empty; }

    public void ApplyEnabled(bool enabled)
    {
        if (!enabled) _node.Visible = false;
    }

    private Rid Buffer(int bytes)
    {
        var result = _rd.StorageBufferCreate((uint)bytes, new byte[bytes]);
        return result.IsValid ? result : throw new InvalidOperationException("Air buffer allocation failed");
    }

    private void Free(ref Rid rid)
    {
        if (rid.IsValid) _rd.FreeRid(rid);
        rid = default;
    }

    private void Drain()
    {
        if (_reading && _counts.IsValid) _rd.BufferGetData(_counts, 0, 4);
    }

    private void DropSet()
    {
        if (_set.IsValid && _rd.UniformSetIsValid(_set)) _rd.FreeRid(_set);
        _set = default;
    }

    private void GrowBubbles(int wanted)
    {
        Drain();
        int maximum = (int)Math.Min(_rd.LimitGet(RenderingDevice.Limit.MaxComputeWorkgroupCountX), (ulong)(int.MaxValue / 128));
        if (wanted > maximum) throw new InvalidOperationException("Air storage reached the device dispatch or address limit");
        int next = Math.Min(maximum, PowerOfTwo(wanted));
        Rid buffer = default, free = default, mm = default;
        try
        {
            buffer = Buffer(checked(next * 48));
            free = Buffer(checked(next * 4));
            mm = RenderingServer.MultimeshCreate();
            RenderingServer.MultimeshSetMesh(mm, _mesh.GetRid());
            RenderingServer.MultimeshAllocateData(mm, next, RenderingServer.MultimeshTransformFormat.Transform3D, false, true, false);
            RenderingServer.MultimeshSetVisibleInstances(mm, next);
            if (_bubbles.IsValid) _rd.BufferCopy(_bubbles, buffer, 0, 0, (uint)(Capacity * 48));
            DropSet();
            _node.SetBase(mm);
        }
        catch
        {
            _node.SetBase(_multimesh);
            if (mm.IsValid) RenderingServer.FreeRid(mm);
            Free(ref buffer); Free(ref free);
            throw;
        }
        if (_multimesh.IsValid) RenderingServer.FreeRid(_multimesh);
        Free(ref _bubbles); Free(ref _free);
        _bubbles = buffer; _free = free; _multimesh = mm;
        _instanceBuffer = default;
        Capacity = next;
        _setDirty = true;
    }

    private void GrowSamples(int samples, int states)
    {
        if (samples <= _sampleCapacity && states <= _stateCapacity) return;
        if ((ulong)samples > _rd.LimitGet(RenderingDevice.Limit.MaxComputeWorkgroupCountX))
            throw new InvalidOperationException("Air contact storage reached the device dispatch limit");
        Drain();
        Rid sample = default, readings = default, state = default;
        int sampleSize = Math.Max(_sampleCapacity, PowerOfTwo(samples));
        int stateSize = Math.Max(_stateCapacity, PowerOfTwo(states));
        try
        {
            if (sampleSize > _sampleCapacity)
            {
                sample = Buffer(checked(sampleSize * 48));
                readings = Buffer(checked(sampleSize * 32));
            }
            if (stateSize > _stateCapacity)
            {
                state = Buffer(checked(stateSize * 16));
                if (_stateBuffer.IsValid) _rd.BufferCopy(_stateBuffer, state, 0, 0, (uint)(_stateCapacity * 16));
            }
        }
        catch
        {
            Free(ref sample); Free(ref readings); Free(ref state);
            throw;
        }
        DropSet();
        if (sample.IsValid)
        {
            Free(ref _sampleBuffer); Free(ref _readings);
            _sampleBuffer = sample; _readings = readings; _sampleCapacity = sampleSize;
        }
        if (state.IsValid)
        {
            Free(ref _stateBuffer); _stateBuffer = state; _stateCapacity = stateSize;
        }
        _setDirty = true;
    }

    private void GrowGrid(int particles)
    {
        if (particles <= _particleCapacity) return;
        int count = PowerOfTwo(particles);
        int size = checked(count * 2);
        Rid heads = default, next = default;
        try { heads = Buffer(checked(size * 4)); next = Buffer(checked(count * 4)); }
        catch { Free(ref heads); Free(ref next); throw; }
        DropSet();
        Free(ref _heads); Free(ref _next);
        _heads = heads; _next = next; _gridSize = size; _particleCapacity = count;
        _setDirty = true;
    }

    private static int PowerOfTwo(int value)
    {
        int result = 1;
        while (result < value) result = checked(result * 2);
        return result;
    }

    private static Vector3 V(Lumora.Core.Math.float3 v) => new(v.x, v.y, v.z);
    private static Quaternion Q(Lumora.Core.Math.floatQ q) => new(q.x, q.y, q.z, q.w);

    private static Transform3D Frame(FluidFrameBlock block, int frame, bool last)
    {
        if (frame <= 0 || frame >= block.Frames.Count) return Transform3D.Identity;
        var f = block.Frames[frame];
        float scale = last ? f.LastScale : f.Scale;
        return new Transform3D(new Basis(Q(last ? f.LastRotation : f.Rotation)).Scaled(Vector3.One * scale), V(last ? f.LastPosition : f.Position));
    }

    private void Samples(FluidFrameBlock block, Transform3D fromWorld, Transform3D fromLastWorld, float spacing, Transform3D toDraw, Aabb bounds)
    {
        _samples.Clear();
        _present.Clear();
        float dt = Math.Max(block.Header.DtFrame, 1e-6f);
        for (int ci = 0; ci < block.Colliders.Count; ci++)
        {
            var c = block.Colliders[ci];
            if (c.Kind > 3 || (c.Flags & (2u | 8u)) != 0 || c.Body == FluidLayout.None && (c.Flags & 5u) == 0) continue;
            float scale = Math.Max(MathF.Abs(c.Scale), 1e-6f);
            float lastScale = Math.Max(MathF.Abs(c.LastScale), 1e-6f);
            var extent = V(c.HalfExtents);
            float area = c.Kind == 0 ? 8f * (extent.X * extent.Y + extent.X * extent.Z + extent.Y * extent.Z)
                : c.Kind == 1 ? 4f * MathF.PI * c.Radius * c.Radius
                : 4f * MathF.PI * c.Radius * (c.Radius + extent.Y);
            for (int di = 0; di < Directions.Length; di++)
            {
                var direction = Directions[di];
                Vector3 local;
                Vector3 surfaceNormal = direction;
                if (c.Kind == 0)
                {
                    float distance = Math.Min(MathF.Abs(direction.X) > 1e-6f ? extent.X / MathF.Abs(direction.X) : float.MaxValue,
                        Math.Min(MathF.Abs(direction.Y) > 1e-6f ? extent.Y / MathF.Abs(direction.Y) : float.MaxValue,
                            MathF.Abs(direction.Z) > 1e-6f ? extent.Z / MathF.Abs(direction.Z) : float.MaxValue));
                    local = direction * distance;
                    var face = new Vector3(MathF.Abs(local.X) / Math.Max(extent.X, 1e-6f), MathF.Abs(local.Y) / Math.Max(extent.Y, 1e-6f), MathF.Abs(local.Z) / Math.Max(extent.Z, 1e-6f));
                    surfaceNormal = face.X >= face.Y && face.X >= face.Z ? new Vector3(MathF.Sign(local.X), 0, 0)
                        : face.Y >= face.Z ? new Vector3(0, MathF.Sign(local.Y), 0) : new Vector3(0, 0, MathF.Sign(local.Z));
                }
                else
                {
                    local = direction * c.Radius;
                    if (c.Kind == 2) local.Y += MathF.Sign(direction.Y) * extent.Y;
                    if (c.Kind == 3)
                    {
                        float side = c.Radius / Math.Max(new Vector2(direction.X, direction.Z).Length(), 1e-6f);
                        float cap = extent.Y / Math.Max(MathF.Abs(direction.Y), 1e-6f);
                        local = direction * Math.Min(side, cap);
                        surfaceNormal = cap < side ? new Vector3(0, MathF.Sign(direction.Y), 0) : new Vector3(direction.X, 0, direction.Z).Normalized();
                    }
                }
                var normalWorld = Q(c.Rotation) * surfaceNormal;
                var centre = fromWorld * (V(c.Position) + Q(c.Rotation) * (local * scale));
                var last = fromLastWorld * (V(c.LastPosition) + Q(c.LastRotation) * (local * lastScale));
                var normal = (fromWorld.Basis * normalWorld).Normalized();
                ulong identity = ((ulong)(c.Reserved1 != 0 ? c.Reserved1 : (uint)ci + 1u) << 8) | (uint)di;
                if (!bounds.Grow(spacing * 3f).HasPoint(toDraw * centre)) continue;
                _present.Add(identity);
                bool fresh = !_states.TryGetValue(identity, out int state);
                if (fresh) _states[identity] = state = _freeStates.Count > 0 ? _freeStates.Pop() : _nextState++;
                centre += normal * (spacing * 0.65f);
                _samples.Add(new Sample
                {
                    Point = new Vector4(centre.X, centre.Y, centre.Z, spacing * 2f),
                    NormalSpeed = new Vector4(normal.X, normal.Y, normal.Z, (centre - normal * (spacing * 0.65f) - last).Length() / dt),
                    State = (uint)state,
                    Area = area * scale * scale / Directions.Length,
                    Fresh = fresh ? 1u : 0u,
                });
            }
        }
        _gone.Clear();
        foreach (var pair in _states) if (!_present.Contains(pair.Key)) _gone.Add(pair.Key);
        foreach (ulong key in _gone) { _freeStates.Push(_states[key]); _states.Remove(key); }
    }

    private void Matrix(Transform3D transform, int offset)
    {
        for (int row = 0; row < 3; row++)
        {
            var value = new Vector4(transform.Basis.X[row], transform.Basis.Y[row], transform.Basis.Z[row], transform.Origin[row]);
            MemoryMarshal.Write(_params.AsSpan(offset + row * 16), in value);
        }
    }

    private void Word(int at, uint value) => MemoryMarshal.Write(_params.AsSpan(at), in value);

    public int Record(in FluidSurfaceSource source, FluidGpuSolver solver, Transform3D toDraw, Aabb bounds, uint layers, bool enabled, float amount, float spacing)
    {
        if (_disposed) return 0;
        _node.Visible = enabled && !_failed && source.System != FluidLayout.None && float.IsFinite(amount) && amount > 0f;
        if (!enabled || source.System == FluidLayout.None || !float.IsFinite(amount) || amount <= 0f)
        {
            if (_enabled)
            {
                try
                {
                    Drain();
                    _rd.BufferClear(_bubbles, 0, (uint)(Capacity * 48));
                    _rd.BufferClear(_stateBuffer, 0, (uint)(_stateCapacity * 16));
                }
                catch (Exception ex) { _failed = true; Status = "Air bubbles: " + ex.Message; }
                Count = 0;
                _confirmedEmpty = true;
                _received = null;
            }
            _enabled = false;
            return 0;
        }
        _enabled = true;
        if (_failed) return 0;
        try
        {
            if (_received is { Length: >= 4 } counters)
            {
                Count = (int)counters[0];
                _confirmedEmpty = Count == 0 && _receivedSequence >= _lastSampleSequence;
                if (counters[3] > 0) _growTo = checked((int)Math.Max((long)Capacity * 2, (long)counters[0] + counters[3]));
                _received = null;
            }
            if (_growTo > Capacity) { GrowBubbles(_growTo); _growTo = 0; }
            var block = solver.Block;
            int system = (int)source.System;
            int frame = system < block.Systems.Count ? (int)block.Systems[system].Frame : 0;
            var toWorld = Frame(block, frame, false);
            var fromWorld = toWorld.AffineInverse();
            var lastInverse = Frame(block, frame, true).AffineInverse();
            Samples(block, fromWorld, lastInverse, spacing, toDraw, bounds);
            if (_samples.Count > 0)
            {
                _lastSampleSequence = _sequence + 1;
                _confirmedEmpty = false;
            }
            else if (_confirmedEmpty && _growTo == 0) return 0;
            GrowGrid(checked((int)source.ParticleCount));
            GrowSamples(Math.Max(_samples.Count, 1), Math.Max(_nextState, 1));
            if (_samples.Count > 0) _rd.BufferUpdate(_sampleBuffer, 0, (uint)(_samples.Count * 48), MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_samples)));
            var instanceBuffer = RenderingServer.MultimeshGetBufferRdRid(_multimesh);
            if (!instanceBuffer.IsValid) return 0;
            if (_instanceBuffer != instanceBuffer)
            {
                _instanceBuffer = instanceBuffer;
                _doubled = _rd.BufferGetData(instanceBuffer).Length >= Capacity * 128;
                _setDirty = true;
            }
            var buffers = solver.Buffers;
            Rid[] inputs = { source.Particles, source.Counters, _sampleBuffer, _readings, _bubbles, _counts, _free,
                _stateBuffer, instanceBuffer, buffers[FluidGpuBuffers.Substances], _parameters, buffers[FluidGpuBuffers.Colliders], _heads, _next };
            for (int i = 0; i < inputs.Length; i++) if (_inputs[i] != inputs[i]) { _inputs[i] = inputs[i]; _setDirty = true; }
            if (_setDirty || !_set.IsValid || !_rd.UniformSetIsValid(_set))
            {
                DropSet();
                var uniforms = new global::Godot.Collections.Array<RDUniform>();
                for (int i = 0; i < inputs.Length; i++)
                {
                    var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = i };
                    uniform.AddId(inputs[i]);
                    uniforms.Add(uniform);
                }
                _set = _rd.UniformSetCreate(uniforms, _shader, 0);
                if (!_set.IsValid) throw new InvalidOperationException("Air bindings allocation failed");
                _setDirty = false;
            }
            Matrix(toDraw, 0); Matrix(toWorld, 48); Matrix(fromWorld, 96);
            var gravity = fromWorld.Basis * V(block.Header.Gravity);
            var gravityDt = new Vector4(gravity.X, gravity.Y, gravity.Z, block.Header.DtFrame);
            MemoryMarshal.Write(_params.AsSpan(144), in gravityDt);
            Word(160, source.ParticleCount); Word(164, (uint)_samples.Count); Word(168, (uint)Capacity); Word(172, source.System);
            Word(176, ++_sequence); Word(180, (uint)block.Colliders.Count); Word(184, (uint)_gridSize); Word(188, _doubled ? 1u : 0u);
            var tuning = new Vector4(spacing, amount, Math.Max(toWorld.Basis.X.Length(), 1e-6f), 0);
            MemoryMarshal.Write(_params.AsSpan(192), in tuning);
            for (int i = 0; i < 8; i++) Word(208 + 4 * i, source.HiddenOrigins != null && i < source.HiddenOrigins.Count ? source.HiddenOrigins[i] : 0);
            _rd.BufferUpdate(_parameters, 0, (uint)_params.Length, _params);
            _rd.BufferUpdate(_counts, 0, 16, _zeros);
            _rd.BufferClear(_heads, 0, (uint)(_gridSize * 4));
            long list = _rd.ComputeListBegin();
            try
            {
                _rd.ComputeListBindComputePipeline(list, _pipeline);
                _rd.ComputeListBindUniformSet(list, _set, 0);
                for (uint pass = 0; pass < 4; pass++)
                {
                    uint groups = pass == 0 ? (source.ParticleCount + 63u) / 64u : pass == 1 ? (uint)_samples.Count
                        : pass == 2 ? (uint)Capacity : (uint)((_samples.Count + 63) / 64);
                    if (groups == 0) continue;
                    MemoryMarshal.Write(_push.AsSpan(), in pass);
                    _rd.ComputeListSetPushConstant(list, _push, 16);
                    _rd.ComputeListDispatch(list, groups, 1, 1);
                    _rd.ComputeListAddBarrier(list);
                }
            }
            finally { _rd.ComputeListEnd(); }
            _node.Layers = layers;
            if (_node.GetParent() is GeometryInstance3D geometry)
            {
                _node.VisibilityRangeBegin = geometry.VisibilityRangeBegin;
                _node.VisibilityRangeEnd = geometry.VisibilityRangeEnd;
                _node.VisibilityRangeBeginMargin = geometry.VisibilityRangeBeginMargin;
                _node.VisibilityRangeEndMargin = geometry.VisibilityRangeEndMargin;
                _node.VisibilityRangeFadeMode = geometry.VisibilityRangeFadeMode;
            }
            _node.CustomAabb = bounds;
            if (!_reading && (++_readFrame % 8) == 0)
            {
                _reading = true;
                uint submitted = _sequence;
                var callback = Callable.From<byte[]>(bytes =>
                {
                    _reading = false;
                    if (!_disposed && _enabled && bytes.Length >= 16)
                    {
                        _received = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(0, 16)).ToArray();
                        _receivedSequence = submitted;
                    }
                });
                _rd.BufferGetDataAsync(_counts, callback, 0, 16);
            }
            Status = string.Empty;
            return _samples.Count > 0 ? 4 : 2;
        }
        catch (Exception ex)
        {
            _failed = true;
            Status = "Air bubbles: " + ex.Message;
            _node.Visible = false;
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Drain();
        DropSet();
        _node.SetBase(default);
        _node.QueueFree();
        if (_multimesh.IsValid) RenderingServer.FreeRid(_multimesh);
        _multimesh = default;
        foreach (var rid in new[] { _sampleBuffer, _readings, _bubbles, _counts, _free, _stateBuffer, _parameters, _heads, _next, _pipeline, _shader })
            if (rid.IsValid) _rd.FreeRid(rid);
        _mesh.Material = null;
        _mesh.Dispose();
        _material.Dispose();
    }
}
