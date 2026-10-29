// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Components;
using Lumora.Core.Math;

namespace Lumora.Core.Scripting;

// How people move, as any machine sees them. A person's character is only simulated on their own
// machine, so what everyone else knows is where the body is: velocity is worked out from that, sampled
// every update, and whether they stand on something is a short ray down from their feet, the same probe
// the avatar's feet use.
//
// Sampling starts the first time a script asks about someone and stops once nobody has asked for a
// while, so a world with no such reads pays nothing. The first read of a person is zero: there is no
// earlier place to measure from. -xlinka
internal sealed class ScriptMotion
{
    // Seconds without a read before the sampling stops, and the smoothing that keeps a body that is
    // updated in bursts from reading as stop and go.
    private const double IdleSeconds = 5.0;
    private const float SmoothSeconds = 0.1f;

    // Past this in one update the body was placed, not moved.
    private const float JumpMetres = 10f;

    // Feet this close to what the ray strikes, times the person's scale, stand on it.
    private const float ProbeAbove = 0.1f;
    private const float ProbeBelow = 0.1f;
    private const float StandTolerance = 0.08f;

    private static readonly ConditionalWeakTable<World, ScriptMotion> _worlds = new();

    private readonly World _world;
    private readonly Dictionary<User, Track> _tracks = new();
    private readonly List<User> _gone = new();
    private bool _sampling;
    private double _lastAsked;

    private sealed class Track
    {
        public float3 Position;
        public double Time;
        public float3 Velocity;
        public bool Placed;
    }

    private ScriptMotion(World world) => _world = world;

    public static float3 VelocityOf(World world, User user) => _worlds.GetValue(world, static w => new ScriptMotion(w)).Velocity(user);

    // The character of the user at this machine, for a node that pushes its own person, who must have a body
    // that walks. A velocity that is not a finite number fails.
    public static CharacterController? OwnCharacter(ScriptContext ctx, User? user, float3 velocity)
    {
        if (!ScriptReads.HereFor(ctx, user))
            return null;
        if (ScriptUserActions.Character(user) is not { } controller)
        {
            ctx.Fail("the user has no body that walks here");
            return null;
        }
        return Finite(ctx, velocity) ? controller : null;
    }

    public static bool Finite(ScriptContext ctx, float3 velocity)
    {
        if (ScriptPhysics.IsFinite(velocity))
            return true;
        ctx.Fail("the velocity is not a finite number");
        return false;
    }

    // Where a body stands for movement: over the ground where the head is, at the height of the feet, so
    // walking round a play space counts as much as walking with a stick.
    private static bool TryBodyPoint(User user, out float3 point)
    {
        point = default;
        var root = ScriptReads.RootOf(user);
        var body = root?.Slot;
        if (root == null || body == null || body.IsDestroyed)
            return false;
        var feet = body.GlobalPosition;
        var head = root.HeadSlot is { IsDestroyed: false } headSlot ? headSlot.GlobalPosition : feet;
        point = new float3(head.x, feet.y, head.z);
        return ScriptPhysics.IsFinite(point);
    }

    private float3 Velocity(User user)
    {
        _lastAsked = _world.Time.TotalTime;
        if (!_tracks.TryGetValue(user, out var track))
        {
            track = new Track();
            _tracks[user] = track;
            Sample(user, track);
        }
        if (!_sampling)
        {
            _sampling = true;
            _world.StartCoroutine(Run());
        }
        return track.Velocity;
    }

    private IEnumerator Run()
    {
        while (!_world.IsDisposed)
        {
            yield return null;
            if (_world.Time.TotalTime - _lastAsked > IdleSeconds)
            {
                _tracks.Clear();
                _sampling = false;
                yield break;
            }
            _gone.Clear();
            foreach (var (user, track) in _tracks)
            {
                if (user.IsDestroyed)
                    _gone.Add(user);
                else
                    Sample(user, track);
            }
            foreach (var user in _gone)
                _tracks.Remove(user);
        }
        _sampling = false;
    }

    private void Sample(User user, Track track)
    {
        double now = _world.Time.TotalTime;
        if (!TryBodyPoint(user, out var point))
        {
            track.Placed = false;
            track.Velocity = float3.Zero;
            return;
        }
        if (track.Placed && now > track.Time)
        {
            var moved = point - track.Position;
            float seconds = (float)(now - track.Time);
            if (moved.Length > JumpMetres)
            {
                track.Velocity = float3.Zero;
            }
            else
            {
                var raw = moved / seconds;
                float blend = 1f - MathF.Exp(-seconds / SmoothSeconds);
                track.Velocity += (raw - track.Velocity) * blend;
            }
        }
        track.Position = point;
        track.Time = now;
        track.Placed = true;
    }

    // Whether a person stands on something, and the slot they stand on when a ray finds it. On the
    // person's own machine their character says, and the ray only names the ground.
    public static bool Grounded(ScriptContext ctx, User user, out Slot? ground)
    {
        ground = null;
        var root = ScriptReads.RootOf(user);
        var body = root?.Slot;
        if (root == null || body == null || body.IsDestroyed)
            return false;
        float scale = MathF.Max(MathF.Abs(root.GlobalScale), 0.01f);
        var feet = body.GlobalPosition;
        if (!ScriptPhysics.IsFinite(feet))
            return false;

        ctx.CountCast();
        var exclude = new List<Slot>(1) { body };
        bool struck = ctx.World.Physics.Raycast(feet + float3.Up * (ProbeAbove * scale), float3.Down, (ProbeAbove + ProbeBelow) * scale,
            exclude, out var hit);
        bool standing = struck && MathF.Abs(feet.y - hit.Point.y) <= StandTolerance * scale;

        var controller = body.GetComponent<CharacterController>();
        if (root.IsLocalUserRoot && controller is { IsDestroyed: false, IsReady: true, Hook: ICharacterControllerHook })
            standing = controller.IsGrounded();
        if (standing && struck && ScriptReads.Visible(ctx, hit.Slot))
            ground = hit.Slot;
        return standing;
    }
}
