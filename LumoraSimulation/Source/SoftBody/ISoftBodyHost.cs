// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Simulation.SoftBody;

// The frame the solver's rest shape is anchored to. Pinned particles are rebuilt from their rest
// offset through this every step, which is what lets a body be picked up and carried: the pins ride
// the transform and the free particles chase them. The solver never asks what the transform IS, only
// how to convert through it. -xlinka
public interface ISoftBodySpace
{
    float3 LocalPointToGlobal(in float3 local);

    float3 GlobalPointToLocal(in float3 global);
}

// Collision back end. Called once per free particle per step, AFTER the constraints have settled,
// with the particle's world position; push it out of anything it overlaps and return true. The push
// direction doubles as the surface normal for the friction response, so move the particle to the
// surface rather than to some arbitrary safe spot.
//
// The particle index is the solver's own (it matches the vertex order the body was built from) so a
// host can keep per-particle contact state across steps: which surface this particle sat on last
// time and how far it may travel before that answer has to be asked again. -xlinka
public interface ISoftBodyCollisionHandler
{
    bool ResolveParticle(int index, ref float3 position, float radius);

    // Asked right after a ResolveParticle that pushed the particle: was what it met something that moves when pushed?
    // If so, how fast that surface moves at the contact point, how easily it gives along the normal there (one over
    // its effective mass, turning included), whether it is asleep, and a number naming that body for Push until the
    // frame's pushes are handed on. False for whatever does not give: the world, a body another machine simulates,
    // one being held.
    bool MovableContact(int index, in float3 point, in float3 normal, out float3 surfaceVelocity, out float inverseMass, out bool resting, out int body);

    // An impulse, in newton seconds, handed to a body MovableContact named, at a point on it.
    void Push(int body, in float3 impulse, in float3 point);
}
