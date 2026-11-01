// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Fluids;

// Keep the submitted body's identity until its readback arrives; the next gather may have a different order. -xlinka
public sealed class FluidImpulses
{
    public const float MaxSpeedChange = 1.5f;
    public const float MaxSpinChange = 6f;
    private readonly FluidSpace _space;
    private readonly Dictionary<uint, RigidBody[]> _frames = new();
    private readonly Queue<uint> _order = new();
    private IFluidGpuWorld? _gpu;
    private uint _lastFrame;
    private bool _seen;

    public FluidImpulses(FluidSpace space) => _space = space;

    public int Applied { get; private set; }
    public int Unmatched { get; private set; }

    public void Capture(FluidFrameBlock block)
    {
        if (!ReferenceEquals(_gpu, _space.Gpu))
        {
            _gpu = _space.Gpu;
            _frames.Clear();
            _order.Clear();
            _seen = false;
        }
        uint sequence = unchecked((uint)block.Sequence + 1);
        var bodies = _space.Colliders.Bodies;
        RigidBody[]? snapshot = null;
        if (_frames.TryGetValue(sequence, out var same))
            snapshot = same;
        else if (_order.Count >= 32)
        {
            uint old = _order.Dequeue();
            _frames.Remove(old, out snapshot);
        }
        if (snapshot == null || snapshot.Length < bodies.Count)
            snapshot = new RigidBody[bodies.Count];
        else
            Array.Clear(snapshot, bodies.Count, snapshot.Length - bodies.Count);
        for (int i = 0; i < bodies.Count; i++)
            snapshot[i] = bodies[i];
        if (!_frames.ContainsKey(sequence))
            _order.Enqueue(sequence);
        _frames[sequence] = snapshot;
    }

    public void TakeSummary(in FluidSummaryView summary)
    {
        var header = summary.Header;
        if (_seen && unchecked((int)(header.Frame - _lastFrame)) <= 0)
            return;
        _seen = true;
        _lastFrame = header.Frame;
        if (summary.Impulses.IsEmpty)
            return;
        if (!_frames.TryGetValue(header.SubmittedSequence, out var bodies))
        {
            Unmatched++;
            return;
        }
        if (_space.World.Hook is not IPhysicsContactHook hook)
            return;
        foreach (var impulse in summary.Impulses)
        {
            if (impulse.Body >= (uint)bodies.Length || impulse.Count == 0)
                continue;
            var body = bodies[impulse.Body];
            if (body == null || body.IsDestroyed || !body.Enabled.Value || !body.IsSimulationOwner || body.IsKinematic.Value
                || !hook.TryGetMotion(body, out var motion))
                continue;
            if (!Limit(impulse.Linear, impulse.Angular, motion, out var linear, out var angular))
                continue;
            hook.ApplyImpulse(body, linear, angular);
            Applied++;
        }
    }

    public static bool Limit(float3 linear, float3 angular, in RigidBodyMotion motion, out float3 limitedLinear, out float3 limitedAngular)
    {
        limitedLinear = limitedAngular = float3.Zero;
        if (!Finite(linear) || !Finite(angular) || !(motion.InverseMass > 0f) || !float.IsFinite(motion.InverseMass))
            return false;
        float dv = (linear * motion.InverseMass).Length;
        var spin = motion.ApplyInverseInertia(angular);
        if (!float.IsFinite(dv) || !Finite(spin))
            return false;
        limitedLinear = linear * (dv > MaxSpeedChange ? MaxSpeedChange / dv : 1f);
        float dw = spin.Length;
        limitedAngular = angular * (dw > MaxSpinChange ? MaxSpinChange / dw : 1f);
        return limitedLinear.LengthSquared > 0f || limitedAngular.LengthSquared > 0f;
    }

    private static bool Finite(float3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
}
