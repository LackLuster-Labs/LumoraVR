// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Simulation;
using Collider = Lumora.Core.Components.Collider;

namespace Lumora.Godot.Hooks;

[StructLayout(LayoutKind.Sequential)]
internal struct GpuRigidState
{
    public Vector4 Position, Rotation, Linear, Angular, Extent, Inertia, Force, Torque, MoveMask, TurnMask, Contact, Previous;
}

internal sealed class GpuRigidWorld : IDisposable
{
    private readonly WorldHook _world;
    private readonly List<Collider> _colliders = new();
    private readonly List<RigidBodyHook> _bodies = new();
    private readonly List<GpuRigidState> _shapes = new();
    private readonly List<GpuRigidState> _static = new();
    private GpuRigidSolver? _solver;
    private string? _failure;
    private string? _fallbackReason;
    private string _fallbackStatus = "CPU";

    internal GpuRigidWorld(WorldHook world) => _world = world;

    public void Step(IEnumerable<RigidBodyHook> all, int ticks)
    {
        _bodies.Clear(); _shapes.Clear(); _static.Clear();
        var compute = ComputePhysics.Device;
        string? reason = _failure ?? (compute == null ? "compute unavailable"
            : _world.Owner.Physics.WatchesContacts ? "contact events require CPU" : null);
        bool requested = false;
        foreach (var hook in all)
            requested |= hook.GpuEligible && hook.Component.SimulationDevice.Value == SimulationDevice.Gpu;
        // Readback and query mirrors currently cost more than the CPU step. -xlinka
        if (!requested || reason != null)
        {
            if (!requested) _failure = null;
            Fallback(all, reason ?? "automatic selection; GPU is opt-in");
            return;
        }
        foreach (var hook in all)
        {
            if (!hook.GpuEligible)
            {
                hook.StopGpu();
                hook.Component.SimulationStatus = "CPU proxy: held, inactive or remote";
                hook.Component.SimulationGpuBytes = 0;
                continue;
            }
            var body = hook.Component;
            if (body.SimulationDevice.Value == SimulationDevice.Cpu) reason ??= "a dynamic body selects CPU";
            Collider? solid = null;
            foreach (var collider in body.Colliders)
            {
                if (!Active(collider)) continue;
                if (solid != null) { reason ??= "compound dynamic collider"; break; }
                solid = collider;
            }
            GpuRigidState shape = default;
            if (solid == null || !ReferenceEquals(solid.Slot, body.Slot) || solid.Offset.Value != float3.Zero
                || !Shape(solid, out shape)) reason ??= "dynamic shape requires CPU";
            if (body.FreezePositionX.Value || body.FreezePositionY.Value || body.FreezePositionZ.Value
                || body.FreezeRotationX.Value || body.FreezeRotationY.Value || body.FreezeRotationZ.Value)
                reason ??= "axis locks require CPU";
            float mass = body.Mass.Value;
            if (!float.IsFinite(mass) || mass <= 0f) reason ??= "invalid body mass";
            else
            {
                shape.Position.W = 1f / mass;
                var half = new Vector3(shape.Extent.X, shape.Extent.Y, shape.Extent.Z);
                var square = half * half;
                var inertia = shape.Extent.W > 0 ? Vector3.One * (0.4f * mass * square.X)
                    : new Vector3(square.Y + square.Z, square.X + square.Z, square.X + square.Y) * (mass / 3f);
                shape.Inertia.X = 1f / MathF.Max(inertia.X, 1e-8f);
                shape.Inertia.Y = 1f / MathF.Max(inertia.Y, 1e-8f);
                shape.Inertia.Z = 1f / MathF.Max(inertia.Z, 1e-8f);
            }
            _bodies.Add(hook); _shapes.Add(shape);
        }
        if (_bodies.Count == 0) { _solver?.Dispose(); _solver = null; return; }
        _world.Owner.CopyCollidersTo(_colliders);
        foreach (var collider in _colliders)
        {
            if (!Active(collider)) continue;
            if (collider.Body?.Hook is RigidBodyHook hook && hook.GpuEligible) continue;
            if (!Shape(collider, out var shape)) { reason ??= "world shape requires CPU"; break; }
            if (collider.Body != null)
            {
                var linear = collider.Body.LinearVelocity.Value;
                var angular = collider.Body.AngularVelocity.Value;
                shape.Linear = new Vector4(linear.x, linear.y, linear.z, 0);
                shape.Angular = new Vector4(angular.x, angular.y, angular.z, 0);
            }
            _static.Add(shape);
        }
        if (reason != null) { Fallback(all, reason); return; }
        for (int i = 0; i < _bodies.Count; i++) _bodies[i].StartGpu(_shapes[i]);
        if (ticks == 0) return;
        try
        {
            _solver ??= new GpuRigidSolver(compute!);
            var states = new GpuRigidState[_bodies.Count + _static.Count];
            for (int i = 0; i < _bodies.Count; i++) states[i] = _bodies[i].GpuState;
            _static.CopyTo(states, _bodies.Count);
            _solver.Step(states, _bodies.Count, ticks, 1f / global::Godot.Engine.PhysicsTicksPerSecond, _world.Owner.Physics.Gravity);
            for (int i = 0; i < _bodies.Count; i++)
            {
                _bodies[i].AcceptGpu(states[i], ticks);
                _bodies[i].Component.SimulationStatus = "GPU: experimental primitive rigid bodies";
                _bodies[i].Component.SimulationGpuBytes = _solver.AllocatedBytes;
            }
        }
        catch (Exception e)
        {
            _failure = e.Message;
            Lumora.Core.Logging.Logger.Warn("Rigid compute fallback: " + e.Message);
            Fallback(all, e.Message);
        }
    }

    private void Fallback(IEnumerable<RigidBodyHook> all, string reason)
    {
        _solver?.Dispose();
        _solver = null;
        if (reason != _fallbackReason)
        {
            _fallbackReason = reason;
            _fallbackStatus = "CPU: " + reason;
        }
        foreach (var body in all)
        {
            body.StopGpu();
            body.Component.SimulationStatus = body.GpuEligible ? _fallbackStatus : "CPU proxy: held, inactive or remote";
            body.Component.SimulationGpuBytes = _solver?.AllocatedBytes ?? 0;
        }
    }

    private static bool Active(Collider collider) => !collider.IsDestroyed && collider.Enabled
        && collider.Slot.IsActive && collider.IsSolid;

    private static bool Shape(Collider collider, out GpuRigidState shape)
    {
        shape = default;
        var scale = collider.Slot.GlobalScale;
        var absolute = new Vector3(MathF.Abs(scale.x), MathF.Abs(scale.y), MathF.Abs(scale.z));
        Vector3 extent;
        int kind;
        if (collider is BoxCollider box)
        {
            var size = box.Size.Value;
            extent = (new Vector3(size.x, size.y, size.z) * absolute).Abs() * 0.5f;
            kind = 0;
        }
        else if (collider is SphereCollider sphere)
        {
            extent = Vector3.One * (sphere.Radius.Value * MathF.Max(absolute.X, MathF.Max(absolute.Y, absolute.Z)));
            kind = 1;
        }
        else return false;
        if (!extent.IsFinite() || extent.X < 0.001f || extent.Y < 0.001f || extent.Z < 0.001f) return false;
        var pos = collider.Slot.LocalPointToGlobal(collider.Offset.Value);
        var rotation = collider.Slot.GlobalRotation;
        shape.Position = new Vector4(pos.x, pos.y, pos.z, 0);
        shape.Rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w);
        shape.Extent = new Vector4(extent.X, extent.Y, extent.Z, kind);
        shape.Inertia.W = collider.SurfaceFriction;
        shape.Torque.W = collider.SurfaceBounce;
        return true;
    }

    public void Dispose() { _solver?.Dispose(); _solver = null; }
}

internal sealed class GpuRigidSolver : IDisposable
{
    private readonly ComputePhysicsDevice _device;
    private Rid _a, _b, _heads, _next, _large, _ab, _ba;
    private int _capacity, _buckets;
    public long AllocatedBytes { get; private set; }
    internal GpuRigidSolver(ComputePhysicsDevice device) => _device = device;

    public void Step(GpuRigidState[] states, int moving, int ticks, float dt, float3 gravity)
        => _device.Run(() => { Execute(states, moving, ticks, dt, gravity); return true; });

    private void Execute(GpuRigidState[] states, int moving, int ticks, float dt, float3 gravity)
    {
        var rd = _device.Rd;
        var kernel = _device.Kernel("Rigid");
        if (_capacity < states.Length)
        {
            Release();
            _capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)System.Math.Max(16, states.Length));
            _buckets = checked(_capacity * 4);
            int bytes = checked(_capacity * Marshal.SizeOf<GpuRigidState>());
            _a = _device.Buffer(bytes); _b = _device.Buffer(bytes);
            _heads = _device.Buffer((_buckets + 1) * 4); _next = _device.Buffer(_capacity * 4); _large = _device.Buffer(_capacity * 4);
            _ab = _device.Uniforms(kernel.Shader, _a, _b, _heads, _next, _large);
            _ba = _device.Uniforms(kernel.Shader, _b, _a, _heads, _next, _large);
            AllocatedBytes = bytes * 2L + (_buckets + 1) * 4L + _capacity * 8L;
        }
        float radius = 0.01f, feature = float.MaxValue;
        for (int i = 0; i < moving; i++)
        {
            var size = states[i].Extent;
            radius = MathF.Max(radius, new Vector3(size.X, size.Y, size.Z).Length());
            feature = MathF.Min(feature, MathF.Min(size.X, MathF.Min(size.Y, size.Z)));
        }
        float cell = 2f * radius + 0.01f;
        int substeps = System.Math.Max(1, (int)MathF.Ceiling(MathF.Max(8f, 16f * radius) * dt / (feature * 0.5f)));
        if (substeps > 128) throw new InvalidOperationException("thin fast bodies require continuous CPU collision");
        var large = new List<int>();
        for (int i = 0; i < states.Length; i++)
        {
            var size = states[i].Extent;
            bool big = new Vector3(size.X, size.Y, size.Z).Length() > radius;
            states[i].MoveMask.W = big ? 1 : 0;
            if (big) large.Add(i);
        }
        var packed = MemoryMarshal.AsBytes(states.AsSpan()).ToArray();
        rd.BufferUpdate(_a, 0, (uint)packed.Length, packed);
        rd.BufferUpdate(_heads, (uint)(_buckets * 4), 4, new byte[4]);
        if (large.Count > 0)
        {
            var bytes = MemoryMarshal.AsBytes(large.ToArray().AsSpan()).ToArray();
            rd.BufferUpdate(_large, 0, (uint)bytes.Length, bytes);
        }
        var pc = new float[] { 0, states.Length, _buckets, large.Count, dt / substeps, cell, 0, moving, gravity.x, gravity.y, gravity.z, 0 };
        var push = new byte[48];
        bool swapped = false;
        long list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, kernel.Pipeline);
        void Dispatch(int stage, int count, bool swap)
        {
            pc[0] = stage;
            System.Buffer.BlockCopy(pc, 0, push, 0, 48);
            rd.ComputeListBindUniformSet(list, swapped ? _ba : _ab, 0);
            rd.ComputeListSetPushConstant(list, push, 48);
            rd.ComputeListDispatch(list, (uint)((count + 63) / 64), 1, 1);
            rd.ComputeListAddBarrier(list);
            if (swap) swapped = !swapped;
        }
        for (int tick = 0; tick < ticks; tick++)
            for (int sub = 0; sub < substeps; sub++)
            {
                pc[11] = tick == 0 ? 1 : 0;
                Dispatch(0, states.Length, true);
                for (int iteration = 0; iteration < 8; iteration++)
                {
                    pc[6] = iteration == 0 ? 1 : 0;
                    Dispatch(1, _buckets, false);
                    Dispatch(2, states.Length, false);
                    Dispatch(3, states.Length, true);
                }
                Dispatch(4, states.Length, true);
            }
        rd.ComputeListEnd(); rd.Submit(); rd.Sync();
        var output = rd.BufferGetData(swapped ? _b : _a, 0, (uint)packed.Length);
        if (BitConverter.ToInt32(rd.BufferGetData(_heads, (uint)(_buckets * 4), 4)) != 0)
            throw new InvalidOperationException("invalid GPU broadphase chain");
        var result = MemoryMarshal.Cast<byte, GpuRigidState>(output.AsSpan());
        for (int i = 0; i < moving; i++)
            if (!result[i].Position.IsFinite() || !result[i].Rotation.IsFinite()
                || !result[i].Linear.IsFinite() || !result[i].Angular.IsFinite())
                throw new InvalidOperationException("non-finite rigid-body result");
        result.CopyTo(states);
    }

    private void Release()
    {
        _device.Free(ref _ab); _device.Free(ref _ba);
        _device.Free(ref _a); _device.Free(ref _b); _device.Free(ref _heads); _device.Free(ref _next); _device.Free(ref _large);
        _capacity = _buckets = 0; AllocatedBytes = 0;
    }

    public void Dispose()
    {
        try { _device.Run(() => { Release(); return true; }); }
        catch (ObjectDisposedException) { }
    }
}
