// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Scripting;

// What the movement, seat and grab nodes do to the person at this machine, done as that person, the way their
// own movement, sitting and letting go do it: inside their role's scale, the seat's rules and the world's
// locomotion rules. The nodes call it for this machine's person; a person's relay calls it for what a script on
// the host asked of them. -xlinka
internal static class ScriptUserActions
{
    // The fastest a script on the host sets anyone moving, the same bound a thrown body keeps as it changes
    // machines.
    public const float MaxSpeed = RigidBody.MaxTransferSpeed;

    // In doubles, so a finite velocity whose square overflows a float still comes out at the bound, not zero.
    public static float3 Bounded(float3 velocity)
    {
        double length = System.Math.Sqrt((double)velocity.x * velocity.x + (double)velocity.y * velocity.y + (double)velocity.z * velocity.z);
        return length > MaxSpeed ? velocity * (float)(MaxSpeed / length) : velocity;
    }

    // A push never takes someone past the bound, and never brakes someone already past it.
    private static float3 Pushed(float3 now, float3 change)
    {
        var after = now + Bounded(change);
        float limit = MathF.Max(now.Length, MaxSpeed);
        float length = after.Length;
        return length > limit ? after * (limit / length) : after;
    }

    private static IDisposable? AsSelf(World world, User user) => world.DataModelPermissions?.EnterActor(user);

    public static CharacterController? Character(User user)
    {
        var body = ScriptReads.RootOf(user)?.Slot;
        var controller = body is { IsDestroyed: false } ? body.GetComponent<CharacterController>() : null;
        return controller is { IsDestroyed: false, IsReady: true } ? controller : null;
    }

    public static void Push(CharacterController character, float3 change, bool bounded)
        => character.SetBodyVelocity(bounded ? Pushed(character.BodyVelocity, change) : character.BodyVelocity + change);

    public static void SetVelocity(CharacterController character, float3 velocity, bool bounded)
        => character.SetBodyVelocity(bounded ? Bounded(velocity) : velocity);

    public static void Scale(World world, User user, UserRoot root, float scale, float seconds)
    {
        using (AsSelf(world, user))
            root.ScaleTo(scale, seconds);
    }

    public static void PlaceHips(World world, User user, UserRoot root, float3 position)
    {
        using (AsSelf(world, user))
            root.HipsPosition = position;
    }

    public static void TurnHips(World world, User user, UserRoot root, floatQ rotation)
    {
        using (AsSelf(world, user))
            root.SetGlobalRotation(UserRoot.UserNode.Hips, rotation.Normalized);
    }

    public static bool Sit(World world, User user, Seat chair)
    {
        using (AsSelf(world, user))
            return chair.TrySit(user);
    }

    // False with nothing done when this person does not sit in it.
    public static bool Leave(World world, User user, Seat chair)
    {
        if (!chair.IsLocalUserSeated)
            return false;
        using (AsSelf(world, user))
            return chair.Release();
    }

    // The first of their locomotions the name picks, exactly or by what it holds, and whether they now use it.
    public static bool Switch(World world, User user, LocomotionController controller, string name, bool exact, out LocomotionModule? match)
    {
        match = null;
        foreach (var module in controller.Modules)
        {
            if (module == null || module.IsDestroyed || string.IsNullOrEmpty(module.DisplayName))
                continue;
            if (exact ? module.DisplayName != name : !module.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase))
                continue;
            match = module;
            using (AsSelf(world, user))
                controller.ActivateModule(module);
            return ReferenceEquals(controller.ActiveModule, module);
        }
        return false;
    }

    public static int Install(World world, User user, LocomotionController controller, Slot from, bool clear)
    {
        using (AsSelf(world, user))
            return controller.Install(from, clear);
    }

    // One hand, or both with a side of None, opens its grip. False when there is no hand to do it with.
    public static bool LetGo(World world, User user, UserRoot root, Chirality side)
    {
        var grabbers = new List<Grabber>(4);
        var found = new List<Grabber>(2);
        foreach (var hand in side == Chirality.None ? new[] { Chirality.Left, Chirality.Right } : new[] { side })
        {
            ScriptHands.GrabbersOf(ScriptHands.Hand(root, hand), found);
            grabbers.AddRange(found);
        }
        if (grabbers.Count == 0)
            return false;
        using (AsSelf(world, user))
        {
            foreach (var grabber in grabbers)
                grabber.ReleaseAll();
        }
        return true;
    }

    private static LocomotionController? Locomotion(UserRoot root)
        => root.Slot?.GetComponent<LocomotionController>() is { IsDestroyed: false } controller ? controller : null;

    private static Seat? SeatAt(World world, Slot? slot)
        => slot is { IsDestroyed: false } && ReferenceEquals(slot.World, world) && slot.GetComponent<Seat>() is { IsDestroyed: false } chair
            ? chair
            : null;

    // On a person's own machine: one thing the host asked of them. The host checked the values; they are checked
    // again here, and what is not here to act on does nothing. A velocity waits in a body that is not stepping
    // while they are in another world, and would throw them on the way back, so it is not taken then. False when
    // nothing was done.
    public static bool Apply(World world, User user, ScriptUserAsk ask)
    {
        if (ScriptReads.RootOf(user) is not { } root || !root.IsLocalUserRoot)
            return false;
        var vector = ask.Vector.Value;
        switch (ask.Kind.Value)
        {
            case ScriptUserAskKind.Push:
                if (!world.IsFocused || !ScriptPhysics.IsFinite(vector) || Character(user) is not { } pushed)
                    return false;
                Push(pushed, vector, bounded: true);
                return true;
            case ScriptUserAskKind.SetVelocity:
                if (!world.IsFocused || !ScriptPhysics.IsFinite(vector) || Character(user) is not { } set)
                    return false;
                SetVelocity(set, vector, bounded: true);
                return true;
            case ScriptUserAskKind.Scale:
                float scale = ask.Amount.Value, seconds = ask.Seconds.Value;
                if (!float.IsFinite(scale) || !float.IsFinite(seconds))
                    return false;
                Scale(world, user, root, scale, seconds);
                return true;
            case ScriptUserAskKind.PlaceHips:
                if (!ScriptPhysics.IsFinite(vector))
                    return false;
                PlaceHips(world, user, root, vector);
                return true;
            case ScriptUserAskKind.TurnHips:
                var rotation = ask.Rotation.Value;
                float length = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
                if (!float.IsFinite(length) || length < 1e-8f)
                    return false;
                TurnHips(world, user, root, rotation);
                return true;
            case ScriptUserAskKind.SitInSeat:
                return SeatAt(world, ask.Target.Target) is { } chair && Sit(world, user, chair);
            case ScriptUserAskKind.LeaveSeat:
                return SeatAt(world, ask.Target.Target) is { } taken && Leave(world, user, taken);
            case ScriptUserAskKind.SwitchLocomotion:
                return !string.IsNullOrEmpty(ask.Text.Value) && Locomotion(root) is { } switching
                    && Switch(world, user, switching, ask.Text.Value, ask.Flag.Value, out _);
            case ScriptUserAskKind.InstallLocomotions:
                return ask.Target.Target is { IsDestroyed: false } from && ReferenceEquals(from.World, world) && !from.IsLocalElement
                    && Locomotion(root) is { } installing && Install(world, user, installing, from, ask.Flag.Value) > 0;
            case ScriptUserAskKind.LetGo:
                return LetGo(world, user, root, ask.Side.Value);
            default:
                return false;
        }
    }
}
