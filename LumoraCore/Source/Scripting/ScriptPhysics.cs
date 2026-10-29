// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Scripting;

// What the physics nodes ask and push. A push reaches only a body this machine simulates, which for a
// script on the host is anything not in someone's body or hands; a push on any other would be dropped
// by the body anyway, and the author has to be allowed to write its velocity. A ray, a sphere cast and
// a search each count once against the script's share of the frame, a cast that goes on past what it
// struck counts every ray it casts, and something only the host machine has, a local slot, is passed
// through: it is not in the world the script sees, and stopped a Shared script's ray at the host's own
// panels.
//
// A cast for people alone finds them the way a search does, by the upright body each stands in, worked
// out from where heads and feet are. A person's own body is not a collider a cast reports, and every
// machine knows where everyone stands. -xlinka
internal static class ScriptPhysics
{
    // How far past a surface the next cast starts: inside what was struck, which a cast never strikes
    // from within.
    private const float StepPast = 0.001f;

    // How many casts one ray or sweep goes on through, past what it ignores or cannot see.
    private const int MaxPasses = 8;

    // The rigid body slot is part of, its own or the nearest above it, that this machine simulates: a body's
    // shape takes in the colliders under it, so a ray's hit on one part of a model pushes the model. One
    // another machine simulates is no failure for a Local script, whose copy there pushes it; for any other
    // script nothing pushes it, and it fails.
    public static RigidBody? Body(ScriptContext ctx, Slot? slot)
    {
        if (!ScriptReads.Reached(ctx, slot))
            return null;
        if (BodyAt(slot!) is not { } body || !ScriptReads.Visible(ctx, body))
        {
            ctx.Fail($"{ScriptReads.Named(slot)} is not part of a rigid body");
            return null;
        }
        if (!body.IsSimulationOwner)
        {
            if (!ctx.IsLocal)
                ctx.Fail($"{ScriptReads.Named(slot)} is simulated on another machine");
            return null;
        }
        ScriptWrites.Check(ctx, body.LinearVelocity);
        return body;
    }

    private static RigidBody? BodyAt(Slot slot)
    {
        for (var current = slot; current != null && !current.IsRootSlot; current = current.Parent)
        {
            if (current.GetComponent<RigidBody>() is { IsDestroyed: false } body)
                return body;
        }
        return null;
    }

    public static bool IsFinite(float3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    // Whether a ray or cast may go out at all: a finite start, a direction, and some range.
    public static bool CanCast(float3 origin, float3 direction, float range)
        => IsFinite(origin) && IsFinite(direction) && direction.LengthSquared >= 1e-12f && range > 0f;

    // A cast's answer as a node gives it.
    public static bool Report(ScriptContext ctx, bool struck, in PhysicsRaycastHit hit, out Slot? slot, out float3 point, out float3 normal,
        out float distance)
    {
        slot = null;
        point = default;
        normal = default;
        distance = 0f;
        if (!struck || (hit.Slot != null && !ScriptReads.Visible(ctx, hit.Slot)))
            return false;
        slot = hit.Slot;
        point = hit.Point;
        normal = hit.Normal;
        distance = hit.Distance;
        return true;
    }

    // What a cast told to ignore a slot leaves out: the slot and everything under it. A slot the script
    // cannot see leaves out nothing.
    public static Slot[]? Ignoring(ScriptContext ctx, Slot? ignore)
        => ScriptReads.Visible(ctx, ignore) ? new[] { ignore! } : null;

    private static bool Ignored(Slot? slot, Slot[]? ignoring)
        => slot != null && ignoring != null && (ReferenceEquals(slot, ignoring[0]) || slot.IsDescendantOf(ignoring[0]));

    // The first collider along a ray the script can see. Something it cannot see is passed through the way
    // StrikeAll passes it, each ray after the first counted.
    public static bool StrikeFirst(ScriptContext ctx, float3 origin, float3 direction, float reach, bool triggers,
        IReadOnlyList<Slot>? ignoring, out PhysicsRaycastHit hit)
    {
        float travelled = 0f;
        for (int ray = 0; ray < MaxPasses && travelled < reach; ray++)
        {
            if (ray > 0)
                ctx.CountCast();
            if (!ctx.World.Physics.Raycast(origin + direction * travelled, direction, reach - travelled, ignoring, out hit, triggers))
                return false;
            hit.Distance += travelled;
            if (hit.Slot == null || ScriptReads.Visible(ctx, hit.Slot))
                return true;
            float next = hit.Distance + StepPast;
            if (!(next > travelled))
                break;
            travelled = next;
        }
        hit = default;
        return false;
    }

    // A ball swept along a ray, passing over what it ignores and what the script cannot see. The sweep
    // itself leaves nothing out, so on touching one of those the ball sweeps on from just inside it, which a
    // sweep passes through; a collider touching it at that very spot is passed over with it. At most
    // MaxPasses sweeps, each after the first counted as a ray.
    public static bool Sweep(ScriptContext ctx, float3 origin, float3 direction, float radius, float reach, bool triggers,
        Slot[]? ignoring, out PhysicsRaycastHit hit)
    {
        float travelled = 0f;
        for (int sweep = 0; sweep < MaxPasses && travelled < reach; sweep++)
        {
            if (sweep > 0)
                ctx.CountCast();
            if (!ctx.World.Physics.SphereCast(origin + direction * travelled, direction, radius, reach - travelled, out hit, triggers))
                return false;
            if (!Ignored(hit.Slot, ignoring) && (hit.Slot == null || ScriptReads.Visible(ctx, hit.Slot)))
            {
                hit.Distance += travelled;
                return true;
            }
            float next = travelled + hit.Distance + StepPast;
            if (!(next > travelled))
                break;
            travelled = next;
        }
        hit = default;
        return false;
    }

    // Every collider along a ray within reach, nearest first. A ray stops at the first
    // thing it strikes, so each one after is another ray from just behind the last, counted like any
    // other. What the script cannot see is passed through and left out.
    public static void StrikeAll(ScriptContext ctx, float3 origin, float3 direction, float reach, bool triggers, Slot[]? ignoring,
        List<PhysicsRaycastHit> into)
    {
        float travelled = 0f;
        for (int ray = 0; travelled < reach; ray++)
        {
            if (ray > 0)
                ctx.CountCast();
            if (!ctx.World.Physics.Raycast(origin + direction * travelled, direction, reach - travelled, ignoring, out var hit, triggers))
                return;
            hit.Distance += travelled;
            if (ScriptReads.Visible(ctx, hit.Slot))
                into.Add(hit);
            // Far out a millimetre can be lost to rounding, and the same surface would be struck forever. -xlinka
            float next = hit.Distance + StepPast;
            if (!(next > travelled))
                return;
            travelled = next;
        }
    }

    // The nearest person a ray, or a ball of radius swept along it, reaches within reach. The hit is where
    // the ball touches their body, and its slot their user root. Someone the cast starts inside is not
    // struck, the way a cast never strikes what it starts in.
    public static bool StrikePerson(ScriptContext ctx, float3 origin, float3 direction, float radius, float reach, Slot[]? ignoring,
        out PhysicsRaycastHit hit)
    {
        hit = default;
        bool struck = false;
        foreach (var user in ScriptReads.Users(ctx))
        {
            if (Person(ctx, user, origin, direction, radius, ignoring, out var candidate) && candidate.Distance <= reach
                && (!struck || candidate.Distance < hit.Distance))
            {
                hit = candidate;
                struck = true;
            }
        }
        return struck;
    }

    // Everyone a ray reaches within reach, nearest first.
    public static void StrikePeople(ScriptContext ctx, float3 origin, float3 direction, float reach, Slot[]? ignoring,
        List<PhysicsRaycastHit> into)
    {
        foreach (var user in ScriptReads.Users(ctx))
        {
            if (Person(ctx, user, origin, direction, 0f, ignoring, out var hit) && hit.Distance <= reach)
                into.Add(hit);
        }
        into.Sort(static (x, y) => x.Distance.CompareTo(y.Distance));
    }

    private static bool Person(ScriptContext ctx, User user, float3 origin, float3 direction, float radius, Slot[]? ignoring,
        out PhysicsRaycastHit hit)
    {
        hit = default;
        var body = ScriptReads.RootOf(user)?.Slot;
        if (!ScriptReads.Visible(ctx, body) || Ignored(body, ignoring)
            || !WorldPhysics.TryGetBody(user, out var bottom, out var top, out float girth))
            return false;
        float reachOut = girth + radius;
        if (!(reachOut > 0f))
            return false;
        float distance = RayCapsule(origin, direction, bottom, top, reachOut);
        if (distance < 0f)
            return false;
        var centre = origin + direction * distance;
        var normal = (centre - Closest(bottom, top, centre)).Normalized;
        hit.Slot = body;
        hit.Distance = distance;
        hit.Normal = normal;
        hit.Point = centre - normal * radius;
        return true;
    }

    // How far along a ray, its direction of unit length, it first meets the capsule round the segment a
    // to b, or -1 for never. Never from inside. The first meeting is the nearest of the side between the
    // ends and the two end balls.
    private static float RayCapsule(float3 origin, float3 direction, float3 a, float3 b, float radius)
    {
        if ((Closest(a, b, origin) - origin).LengthSquared <= radius * radius)
            return -1f;
        float best = float.MaxValue;
        var axis = b - a;
        float axisSquared = float3.Dot(axis, axis);
        if (axisSquared > 1e-10f)
        {
            var fromA = origin - a;
            float along = float3.Dot(axis, direction);
            float startAlong = float3.Dot(axis, fromA);
            float qa = axisSquared - along * along;
            if (qa > 1e-10f)
            {
                float qb = axisSquared * float3.Dot(fromA, direction) - startAlong * along;
                float qc = axisSquared * float3.Dot(fromA, fromA) - startAlong * startAlong - radius * radius * axisSquared;
                float h = qb * qb - qa * qc;
                if (h >= 0f)
                {
                    float t = (-qb - MathF.Sqrt(h)) / qa;
                    float height = startAlong + t * along;
                    if (t >= 0f && height > 0f && height < axisSquared)
                        best = t;
                }
            }
        }
        best = MathF.Min(best, RaySphere(origin, direction, a, radius));
        best = MathF.Min(best, RaySphere(origin, direction, b, radius));
        return best < float.MaxValue ? best : -1f;
    }

    private static float RaySphere(float3 origin, float3 direction, float3 centre, float radius)
    {
        var offset = origin - centre;
        float b = float3.Dot(offset, direction);
        float h = b * b - (float3.Dot(offset, offset) - radius * radius);
        if (h < 0f)
            return float.MaxValue;
        float t = -b - MathF.Sqrt(h);
        return t >= 0f ? t : float.MaxValue;
    }

    private static float3 Closest(float3 a, float3 b, float3 point)
    {
        var axis = b - a;
        float axisSquared = float3.Dot(axis, axis);
        float t = axisSquared > 1e-10f ? System.Math.Clamp(float3.Dot(point - a, axis) / axisSquared, 0f, 1f) : 0f;
        return a + axis * t;
    }

    // Colliders whose shapes reach into the sphere, as a trigger sees them: sensors included, and people
    // by their user roots rather than their avatars' parts. A sphere with no finite, positive size finds
    // nothing.
    public static void SearchSphere(ScriptContext ctx, float3 center, float radius, List<Slot> into)
    {
        if (!IsFinite(center) || !float.IsFinite(radius) || radius <= 0f)
            return;
        ctx.CountCast();
        var hits = ctx.HitScratch;
        ctx.World.Physics.OverlapSphere(center, radius, hits, hitTriggers: true);
        Keep(ctx, hits, into);
        ctx.World.Physics.OverlapPeople(center, radius, ctx.PeopleScratch);
        KeepPeople(ctx, ctx.PeopleScratch, into);
    }

    // The same for a box, size edge to edge. A rotation of zero is none.
    public static void SearchBox(ScriptContext ctx, float3 center, float3 size, floatQ rotation, List<Slot> into)
    {
        if (!IsFinite(center) || !IsFinite(size) || size.x <= 0f || size.y <= 0f || size.z <= 0f)
            return;
        float lengthSquared = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        rotation = float.IsFinite(lengthSquared) && lengthSquared >= 1e-12f ? ScriptWrites.Unit(rotation) : floatQ.Identity;
        ctx.CountCast();
        var hits = ctx.HitScratch;
        ctx.World.Physics.OverlapBox(center, size, rotation, hits, hitTriggers: true);
        Keep(ctx, hits, into);
        ctx.World.Physics.OverlapPeople(center, size, rotation, ctx.PeopleScratch);
        KeepPeople(ctx, ctx.PeopleScratch, into);
    }

    private static void Keep(ScriptContext ctx, List<Slot> hits, List<Slot> into)
    {
        foreach (var slot in hits)
        {
            if (ScriptReads.Visible(ctx, slot) && !ScriptReads.IsWornPart(slot) && !into.Contains(slot))
                into.Add(slot);
        }
        hits.Clear();
    }

    private static void KeepPeople(ScriptContext ctx, List<User> people, List<Slot> into)
    {
        foreach (var user in people)
        {
            var body = ScriptReads.RootOf(user)?.Slot;
            if (ScriptReads.Visible(ctx, body) && !into.Contains(body!))
                into.Add(body!);
        }
        people.Clear();
    }
}
