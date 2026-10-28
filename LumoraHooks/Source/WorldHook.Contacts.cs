// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Godot;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraRigidBody = Lumora.Core.Components.RigidBody;

namespace Lumora.Godot.Hooks;

// The world's side of a soft body pushing back: which solid a push-out met, how a rigid body this machine simulates
// moves, and the impulse the soft body hands it. Kept beside the rigid body hook rather than in it, which only ever
// has to know its own body. -xlinka
public partial class WorldHook : IPhysicsContactHook
{
    // ResolveSphere as the query hook answers it, naming the slot of what it met as well.
    public bool ResolveSphere(in float3 center, float radius, IReadOnlyList<Lumora.Core.Slot>? exclude, out float3 correctedCenter, out float3 normal, out Lumora.Core.Slot? met)
    {
        correctedCenter = center;
        normal = float3.Up;
        met = null;
        var space = GetSpace();
        if (space == null || radius <= 0f)
            return false;

        _resolveShape ??= new SphereShape3D();
        _resolveShape.Radius = radius;
        _resolveExclude ??= new global::Godot.Collections.Array<Rid>();
        _resolveExclude.Clear();
        var query = _resolveQuery ??= new PhysicsShapeQueryParameters3D();
        query.Shape = _resolveShape;
        query.CollideWithBodies = true;
        query.CollideWithAreas = false;           // solids only, never a query shape
        query.CollisionMask = CollisionBit;
        AddRegisteredExcludes(exclude, _resolveExclude);
        query.Exclude = _resolveExclude;

        var c = new Vector3(center.x, center.y, center.z);
        for (int i = 0; i < MaxQueryIterations; i++)
        {
            query.Transform = new Transform3D(Basis.Identity, c);
            using var info = space.GetRestInfo(query);
            if (info.Count == 0)
                return false;

            var slot = ResolveSlotById(info[ColliderIdKey].AsUInt64());
            if (InThisWorld(slot) && !IsExcluded(slot, exclude))
            {
                var point = (Vector3)info[PointKey];
                var n = (Vector3)info[NormalKey];
                correctedCenter = new float3(point.X + n.X * radius, point.Y + n.Y * radius, point.Z + n.Z * radius);
                normal = new float3(n.X, n.Y, n.Z);
                met = slot;
                return true;
            }
            _resolveExclude.Add(info[RidKey].As<Rid>());
            query.Exclude = _resolveExclude;
        }
        return false;
    }

    // Only a free body this machine simulates: a held, kinematic, starting or followed one sits on the server in
    // another mode, and pushing it would fight whoever moves it.
    public bool TryGetMotion(LumoraRigidBody body, out RigidBodyMotion motion)
    {
        motion = default;
        if (body.Hook is RigidBodyHook gpu && gpu.TryGpuMotion(out motion)) return true;
        if (!FreeHere(body, out var rid))
            return false;
        var state = PhysicsServer3D.BodyGetDirectState(rid);
        if (state == null)
            return false;
        float inverseMass = state.InverseMass;
        if (!(inverseMass > 0f) || !float.IsFinite(inverseMass))
            return false;
        var origin = state.Transform.Origin + state.CenterOfMass;
        var linear = state.LinearVelocity;
        var angular = state.AngularVelocity;
        var inertia = state.InverseInertiaTensor;
        motion = new RigidBodyMotion
        {
            InverseMass = inverseMass,
            CenterOfMass = new float3(origin.X, origin.Y, origin.Z),
            LinearVelocity = new float3(linear.X, linear.Y, linear.Z),
            AngularVelocity = new float3(angular.X, angular.Y, angular.Z),
            InverseInertia0 = new float3(inertia.Column0.X, inertia.Column0.Y, inertia.Column0.Z),
            InverseInertia1 = new float3(inertia.Column1.X, inertia.Column1.Y, inertia.Column1.Z),
            InverseInertia2 = new float3(inertia.Column2.X, inertia.Column2.Y, inertia.Column2.Z),
            Sleeping = state.Sleeping,
        };
        return true;
    }

    public void ApplyImpulse(LumoraRigidBody body, in float3 linear, in float3 angular)
    {
        if (body.Hook is RigidBodyHook gpu && gpu.ApplyGpuImpulse(linear, angular)) return;
        if (!FreeHere(body, out var rid))
            return;
        // A sleeping body swallows impulses, so it is woken first. The body's hook hears of the wake from the
        // server and puts it back on the world's pass.
        if (PhysicsServer3D.BodyGetState(rid, PhysicsServer3D.BodyState.Sleeping).AsBool())
            PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.Sleeping, false);
        if (linear != float3.Zero)
            PhysicsServer3D.BodyApplyCentralImpulse(rid, new Vector3(linear.x, linear.y, linear.z));
        if (angular != float3.Zero)
            PhysicsServer3D.BodyApplyTorqueImpulse(rid, new Vector3(angular.x, angular.y, angular.z));
    }

    public void Settle(LumoraRigidBody body)
    {
        if (body.Hook is RigidBodyHook gpu && gpu.SettleGpu()) return;
        if (!FreeHere(body, out var rid))
            return;
        PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.LinearVelocity, Vector3.Zero);
        PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.AngularVelocity, Vector3.Zero);
        PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.Sleeping, true);
    }

    private static bool FreeHere(LumoraRigidBody? body, out Rid rid)
    {
        rid = default;
        if (body == null || body.IsDestroyed || !body.Enabled || body.IsKinematic.Value || !body.IsSimulationOwner)
            return false;
        if (body.Hook is not RigidBodyHook hook)
            return false;
        rid = hook.BodyRid;
        return rid.IsValid && PhysicsServer3D.BodyGetMode(rid) == PhysicsServer3D.BodyMode.Rigid;
    }
}
