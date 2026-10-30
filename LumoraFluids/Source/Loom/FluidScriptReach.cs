// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids;

// What a fluid node may reach, by the same answer the core's own nodes give, from public members only: a slot or
// user from another world, one already gone, or a slot local to the machine running the script reads as nothing,
// and a Local script also sees the local slots under its own object. Plus the two things every fluid node shares:
// the warnings, which never stop a call, and the way an action reaches the fluid, a host command or a request on
// the author's own ring. -xlinka
public static class FluidScriptReach
{
    public const int WarnSphereReadsPerFrame = 256;
    public const float WarnLitresPerSecond = 50f;

    // Per script: the frame its reads were counted in, how many, the start of its emit second, and the litres in it.
    private static readonly ConditionalWeakTable<ScriptContext, double[]> _counts = new();
    private static readonly ConditionalWeakTable<World, List<FluidSystem>> _systems = new();
    private static readonly ConditionalWeakTable<World, StrongBox<ulong>> _systemsFrame = new();

    public static bool Visible(ScriptContext ctx, Slot? slot)
        => slot != null && !slot.IsDestroyed && ReferenceEquals(slot.World, ctx.World)
            && (!slot.IsLocalElement || (ctx.IsLocal && ctx.Self != null && slot.IsDescendantOf(ctx.Self)));

    public static bool Visible(ScriptContext ctx, User? user)
        => user != null && !user.IsDestroyed && ReferenceEquals(user.World, ctx.World);

    public static bool Reached(ScriptContext ctx, [NotNullWhen(true)] Slot? slot, string what = "slot")
    {
        if (Visible(ctx, slot))
            return true;
        ctx.Fail(slot == null ? $"no {what}"
            : slot.IsDestroyed ? $"the {what} is gone"
            : !ReferenceEquals(slot.World, ctx.World) ? $"the {what} is in another world"
            : ctx.IsLocal ? $"{Named(slot)} is not under this script's object"
            : $"{Named(slot)} is only on one machine");
        return false;
    }

    public static bool Reached(ScriptContext ctx, [NotNullWhen(true)] User? user, string what = "user")
    {
        if (Visible(ctx, user))
            return true;
        ctx.Fail(user == null ? $"no {what}" : user.IsDestroyed ? $"the {what} has left" : $"the {what} is in another world");
        return false;
    }

    public static string Named(Slot slot) => string.IsNullOrEmpty(slot.Name.Value) ? "a slot with no name" : $"'{slot.Name.Value}'";

    public static string Named(User user) => string.IsNullOrEmpty(user.UserName.Value) ? "a user with no name" : $"'{user.UserName.Value}'";

    // The fluid system a node is pointed at, by the slot holding it. An action fails when it is not there; a read
    // just answers nothing.
    public static FluidSystem? System(ScriptContext ctx, Slot? slot, bool fail)
    {
        if (fail ? !Reached(ctx, slot, "fluid system") : !Visible(ctx, slot))
            return null;
        var system = slot!.GetComponent<FluidSystem>();
        if (system is { IsDestroyed: false })
            return system;
        if (fail)
            ctx.Fail($"{Named(slot)} holds no fluid system");
        return null;
    }

    // Every system in the world a script may see, for a read given none. Walked once a frame per world, however many
    // reads ask.
    public static IReadOnlyList<FluidSystem> Systems(ScriptContext ctx)
    {
        var world = ctx.World;
        var list = _systems.GetValue(world, static _ => new List<FluidSystem>());
        var walked = _systemsFrame.GetValue(world, static _ => new StrongBox<ulong>(ulong.MaxValue));
        ulong frame = world.Time.UpdateIndex;
        if (walked.Value != frame)
        {
            walked.Value = frame;
            list.Clear();
            var root = world.RootSlot;
            if (root != null)
            {
                foreach (var system in root.GetComponentsInChildren<FluidSystem>())
                {
                    if (!system.IsDestroyed && !system.Slot.IsLocalElement)
                        list.Add(system);
                }
            }
        }
        return list;
    }

    // The user at this machine, when the script may act for them: a world script for whoever is here, a user's own
    // script for its author only.
    public static bool IsMachineUser(ScriptContext ctx, User? user)
        => user is { IsDestroyed: false } && ReferenceEquals(user, ctx.World.LocalUser)
            && (ctx.Principal.IsWorldContent || ReferenceEquals(user, ctx.Principal.Author));

    // A user's root as any machine finds it: the replicated reference, which is there everywhere.
    public static UserRoot? RootOf(User? user)
    {
        if (user == null || user.IsDestroyed)
            return null;
        var root = user.Root ?? user.UserRootRef.Target;
        return root is { IsDestroyed: false } ? root : null;
    }

    // The FluidPerson a reader trusts for a user: on their own root, made by their own machine, every array at its
    // fixed length, the oldest when there are several. Anything else is somebody's forgery and reads as nothing.
    public static FluidPerson? PersonOf(User? user) => user == null || user.IsDestroyed ? null : FluidProvenance.Of(user);

    // A RefID packed into two lanes, low half first.
    public static ulong PackedId(int low, int high) => FluidRings.Join(low, high);

    public static User? UserById(World world, ulong id)
        => id == 0 ? null : world.ReferenceController.GetObjectOrNull(new RefID(id)) as User is { IsDestroyed: false } user ? user : null;

    // RGBA8 with red in the low byte.
    public static colorHDR Rgba8(int packed)
    {
        uint value = (uint)packed;
        return new colorHDR((value & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, ((value >> 16) & 0xFF) / 255f, ((value >> 24) & 0xFF) / 255f);
    }

    // WARNINGS: past these a script is told, never stopped.

    public static void CountSphereRead(ScriptContext ctx)
    {
        var counts = Counted(ctx);
        double frame = ctx.World.Time.UpdateIndex;
        if (counts[0] != frame)
        {
            counts[0] = frame;
            counts[1] = 0;
        }
        if (++counts[1] == WarnSphereReadsPerFrame + 1)
            ctx.Warn(ScriptWarning.Rays, $"Fluid In Sphere: more than {WarnSphereReadsPerFrame} fluid reads in one frame");
    }

    public static void CountEmit(ScriptContext ctx, float litres)
    {
        var counts = Counted(ctx);
        double now = ctx.Now;
        if (now - counts[2] >= 1.0)
        {
            counts[2] = now;
            counts[3] = 0;
        }
        double before = counts[3];
        counts[3] += litres;
        if (before <= WarnLitresPerSecond && counts[3] > WarnLitresPerSecond)
            ctx.Warn(ScriptWarning.SyncedValues, $"Emit Fluid: more than {WarnLitresPerSecond:0} litres in one second");
    }

    private static double[] Counted(ScriptContext ctx)
        => _counts.GetValue(ctx, static _ => new[] { -1.0, 0.0, double.NegativeInfinity, 0.0 });

    // ROUTES: how an action reaches the fluid.

    // The host keeps the command ring, so a script running there has the world's fluid space write the command. The
    // ring is simulation input like a body pose and goes in under the system's own bypass, not as the script's author.
    public static bool OnHost(ScriptContext ctx) => ctx.World.IsAuthority && !ctx.IsLocal;

    // point is in world space; the ring carries it in the system's own.
    public static bool Command(FluidSystem system, FluidCommandKind kind, float3 point, float radius, float litres, int origin = FluidCommand.AnyOrigin)
    {
        var space = FluidSpace.Ensure(system.World);
        if (space == null || system.Slot == null)
            return false;
        space.Command(system, kind, litres, system.Slot.GlobalPointToLocal(point), radius, origin);
        return true;
    }

    // A user's Author script on its own machine asks the host through its author's request ring; the host judges it.
    public static bool Ask(ScriptContext ctx, in FluidRequest request)
    {
        var author = ctx.Principal.Author;
        if (author == null || !ReferenceEquals(author, ctx.World.LocalUser))
        {
            ctx.Fail("only the host or the script's own author can ask for that");
            return false;
        }
        var person = FluidTool.OwnPerson(ctx.World, attach: true);
        if (person == null || !FluidTool.Ask(person, request))
        {
            ctx.Fail("the request could not be sent");
            return false;
        }
        return true;
    }

    public static int SubstanceIndex(FluidSystem system, Slot? substance)
    {
        if (substance == null)
            return 0;
        var wanted = substance.GetComponent<FluidSubstance>();
        for (int i = 0; i < system.Substances.Count; i++)
        {
            if (ReferenceEquals(system.Substances[i], wanted))
                return i;
        }
        return 0;
    }

    public static bool Finite(float3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
}
