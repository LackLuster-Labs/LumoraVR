// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Godot.Hooks;

public partial class RigidBodyHook
{
    private bool _gpuActive;
    private float3 _mirrorPos = new(float.PositiveInfinity);
    private floatQ _mirrorRot;
    private GpuRigidState _gpu;
    internal bool GpuEligible => _body.IsValid && _inSpace && _framesSinceInit >= 3 && Owner != null
        && !Owner.IsDestroyed && Owner.Enabled && Owner.Slot.IsActive && Owner.IsSimulationOwner
        && !Owner.IsKinematic.Value && !IsHeld;
    internal bool OnGpu => _gpuActive;
    internal Lumora.Core.Components.RigidBody Component => Owner;

    internal void StartGpu(in GpuRigidState shape)
    {
        if (!_gpuActive)
        {
            var state = State;
            _gpu = shape;
            if (state != null)
            {
                var q = state.Transform.Basis.GetRotationQuaternion();
                _gpu.Position = new Vector4(state.Transform.Origin.X, state.Transform.Origin.Y, state.Transform.Origin.Z, shape.Position.W);
                _gpu.Rotation = new Vector4(q.X, q.Y, q.Z, q.W);
                _gpu.Linear = new Vector4(state.LinearVelocity.X, state.LinearVelocity.Y, state.LinearVelocity.Z, 0);
                _gpu.Angular = new Vector4(state.AngularVelocity.X, state.AngularVelocity.Y, state.AngularVelocity.Z, 0);
            }
            _gpuActive = true;
            _freezeKinematic = true;
            SetFrozen(true);
            Rebase(ToCore(_gpu.Position), ToRotation(_gpu.Rotation));
            EnterAwake();
        }
        _gpu.Position.W = shape.Position.W;
        _gpu.Extent = shape.Extent;
        _gpu.Inertia = shape.Inertia;
        _gpu.Torque.W = shape.Torque.W;
        _gpu.Linear.W = MathF.Max(0f, Owner.LinearDamping.Value);
        _gpu.Angular.W = MathF.Max(0f, Owner.AngularDamping.Value);
        _gpu.Force.W = Owner.UseGravity.Value ? 1 : 0;
        _gpu.MoveMask = new Vector4(Owner.FreezePositionX.Value ? 0 : 1, Owner.FreezePositionY.Value ? 0 : 1, Owner.FreezePositionZ.Value ? 0 : 1, 0);
        _gpu.TurnMask = new Vector4(Owner.FreezeRotationX.Value ? 0 : 1, Owner.FreezeRotationY.Value ? 0 : 1, Owner.FreezeRotationZ.Value ? 0 : 1, _gpu.TurnMask.W);
        ReadGpuRequests();
    }

    private void ReadGpuRequests()
    {
        var pos = Owner.Slot.GlobalPosition;
        var rot = Owner.Slot.GlobalRotation;
        bool teleport = Owner.HasPendingTeleport;
        if (teleport || MovedFromOutside(pos, rot))
        {
            _gpu.Position.X = pos.x; _gpu.Position.Y = pos.y; _gpu.Position.Z = pos.z;
            _gpu.Rotation = new Vector4(rot.x, rot.y, rot.z, rot.w);
            PlaceBody(pos, rot);
            _gpu.TurnMask.W = 0;
            if (teleport) { ClearVector(ref _gpu.Linear); ClearVector(ref _gpu.Angular); Owner.HasPendingTeleport = false; }
        }
        if (Owner.PendingForce != float3.Zero || Owner.PendingImpulse != float3.Zero
            || Owner.PendingTorque != float3.Zero || Owner.HasPendingVelocity)
        {
            if (Owner.IsSleeping) { ClearVector(ref _gpu.Linear); ClearVector(ref _gpu.Angular); }
            _gpu.TurnMask.W = 0;
            Owner.IsSleeping = false;
        }
        Add(ref _gpu.Force, Owner.PendingForce);
        Add(ref _gpu.Torque, Owner.PendingTorque);
        Add(ref _gpu.Linear, Owner.PendingImpulse * _gpu.Position.W);
        if (Owner.HasPendingVelocity)
        {
            SetVector(ref _gpu.Linear, Owner.PendingLinearVelocity);
            SetVector(ref _gpu.Angular, Owner.PendingAngularVelocity);
        }
        Owner.ClearPendingForces();
    }

    internal GpuRigidState GpuState => _gpu;

    internal void AcceptGpu(in GpuRigidState state, int ticks)
    {
        _gpu = state;
        ClearVector(ref _gpu.Force);
        ClearVector(ref _gpu.Torque);
        var pos = ToCore(state.Position);
        var rot = ToRotation(state.Rotation);
        float back = ticks > 1 ? 1f - 1f / ticks : 0f;
        _prevPos = float3.Lerp(_currPos, pos, back);
        _prevRot = floatQ.Slerp(_currRot, rot, back);
        _currPos = pos;
        _currRot = rot;
        float dot = rot.x * _mirrorRot.x + rot.y * _mirrorRot.y + rot.z * _mirrorRot.z + rot.w * _mirrorRot.w;
        if (float3.DistanceSquared(pos, _mirrorPos) > 1e-10f || MathF.Abs(dot) < 0.9999999f)
        {
            PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.Transform,
                new Transform3D(new Basis(new Quaternion(rot.x, rot.y, rot.z, rot.w)), new Vector3(pos.x, pos.y, pos.z)));
            _mirrorPos = pos;
            _mirrorRot = rot;
        }
        Owner.IsColliding = state.Contact.W > 0f;
        Owner.IsSleeping = state.TurnMask.W >= 0.5f;
    }

    internal void StopGpu()
    {
        if (!_gpuActive) return;
        if (Owner.IsSimulationOwner && !IsHeld && !MovedFromOutside(Owner.Slot.GlobalPosition, Owner.Slot.GlobalRotation))
            WritePose();
        _gpuActive = false;
        Owner.IsSleeping = false;
        if (_body.IsValid)
        {
            SetFrozen(false);
            SetVelocities(_gpu.TurnMask.W >= 0.5f ? Vector3.Zero : ToVector(_gpu.Linear),
                _gpu.TurnMask.W >= 0.5f ? Vector3.Zero : ToVector(_gpu.Angular));
            PhysicsServer3D.BodyApplyCentralForce(_body, ToVector(_gpu.Force));
            PhysicsServer3D.BodyApplyTorque(_body, ToVector(_gpu.Torque));
            PhysicsServer3D.BodySetState(_body, PhysicsServer3D.BodyState.Sleeping, false);
        }
        QueueApply();
    }

    private bool PumpGpu(int ticks, float fraction, int stride)
    {
        if (!GpuEligible) { StopGpu(); return false; }
        if (ticks > 0)
        {
            int before = _ticks;
            _ticks += ticks;
            if (stride <= 1 || Crossed(before, _ticks, _phase % stride, stride - 1)) WritePose();
            if (Crossed(before, _ticks, _phase, System.Math.Max(8, 4 * stride) - 1)) WriteVelocities(false);
        }
        DrawBetweenSteps(fraction);
        return true;
    }

    internal bool TryGpuMotion(out RigidBodyMotion motion)
    {
        motion = default;
        if (!_gpuActive || !GpuEligible) return false;
        var q = new Quaternion(_gpu.Rotation.X, _gpu.Rotation.Y, _gpu.Rotation.Z, _gpu.Rotation.W);
        var basis = new Basis(q);
        var diagonal = new Basis(new Vector3(_gpu.Inertia.X, 0, 0), new Vector3(0, _gpu.Inertia.Y, 0), new Vector3(0, 0, _gpu.Inertia.Z));
        var inertia = basis * diagonal * basis.Transposed();
        var mask = new Vector3(_gpu.TurnMask.X, _gpu.TurnMask.Y, _gpu.TurnMask.Z);
        motion = new RigidBodyMotion
        {
            InverseMass = _gpu.Position.W,
            CenterOfMass = ToCore(_gpu.Position),
            LinearVelocity = Owner.IsSleeping ? float3.Zero : ToCore(_gpu.Linear),
            AngularVelocity = Owner.IsSleeping ? float3.Zero : ToCore(_gpu.Angular),
            InverseInertia0 = ToCore(inertia.Column0 * mask * mask.X),
            InverseInertia1 = ToCore(inertia.Column1 * mask * mask.Y),
            InverseInertia2 = ToCore(inertia.Column2 * mask * mask.Z),
            Sleeping = Owner.IsSleeping,
        };
        return true;
    }

    internal bool ApplyGpuImpulse(in float3 linear, in float3 angular)
    {
        if (!TryGpuMotion(out var motion)) return false;
        if (Owner.IsSleeping) { ClearVector(ref _gpu.Linear); ClearVector(ref _gpu.Angular); }
        _gpu.TurnMask.W = 0;
        Add(ref _gpu.Linear, linear * motion.InverseMass);
        Add(ref _gpu.Angular, motion.ApplyInverseInertia(angular));
        Owner.IsSleeping = false;
        return true;
    }

    internal bool SettleGpu()
    {
        if (!_gpuActive || !GpuEligible) return false;
        ClearVector(ref _gpu.Linear); ClearVector(ref _gpu.Angular);
        _gpu.TurnMask.W = 0.5f;
        Owner.IsSleeping = true;
        return true;
    }

    private static float3 ToCore(Vector4 v) => new(v.X, v.Y, v.Z);
    private static float3 ToCore(Vector3 v) => new(v.X, v.Y, v.Z);
    private static Vector3 ToVector(Vector4 v) => new(v.X, v.Y, v.Z);
    private static floatQ ToRotation(Vector4 v) => new(v.X, v.Y, v.Z, v.W);
    private static void ClearVector(ref Vector4 v) { v.X = v.Y = v.Z = 0; }
    private static void SetVector(ref Vector4 v, float3 xyz) { v.X = xyz.x; v.Y = xyz.y; v.Z = xyz.z; }
    private static void Add(ref Vector4 v, float3 xyz) { v.X += xyz.x; v.Y += xyz.y; v.Z += xyz.z; }
}
