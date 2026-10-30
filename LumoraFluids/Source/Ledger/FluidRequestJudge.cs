// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Warden;

namespace Lumora.Fluids;

// The host's check of a person's requests, read off their own request ring. An allowed request becomes a host
// command; there is no answer channel, because the requester's machine runs the same checks first and shows its own
// notice. Past 10 a second the extras are dropped as rate limiting, which never counts as a denial: a script spamming
// Remove Fluid is not an attack on anyone.
//
//   Remove         the fluid is the requester's own pour; or they may edit the system; or they moderate
//   UndoPour       it is their own pour
//   ClearMine      always
//   Emit, AdoptSubstance, EnsureLoose
//                  they may spawn here and WhoMayPour lets them -xlinka
public sealed class FluidRequestJudge
{
    public const int PerSecond = 10;

    private readonly Dictionary<User, (double Second, int Count)> _rate = new();

    public FluidRequestJudge(World world)
    {
        World = world;
    }

    public World World { get; }

    public int Allowed { get; private set; }

    public int Refused { get; private set; }

    public int RateLimited { get; private set; }

    // AdoptSubstance and EnsureLoose make things on the host (a loose system, a substance under the host's Fluids
    // slot); whoever builds those listens here.
    public event Action<User, FluidRequest>? Approved;

    // One more request from this user this second, or false when they are past the limit.
    public bool Take(User user, double now)
    {
        double second = Math.Floor(now);
        var (at, count) = _rate.GetValueOrDefault(user, (second, 0));
        if (at != second)
            count = 0;
        if (count >= PerSecond)
        {
            _rate[user] = (second, count);
            RateLimited++;
            return false;
        }
        _rate[user] = (second, count + 1);
        return true;
    }

    public void Forget(User user) => _rate.Remove(user);

    // Judges one request against its system, adding the commands it allows. False when refused.
    public bool Judge(User requester, in FluidRequest request, FluidSystem? system, FluidLedger? ledger, FluidWorldRules? rules, List<FluidCommand> output)
    {
        if (requester == null || system == null || system.IsDestroyed || ledger == null || system.World != World)
        {
            Refused++;
            return false;
        }
        ulong user = requester.ReferenceID.RawValue;
        bool ok;
        switch (request.Kind)
        {
            case FluidRequestKind.Remove:
                if (MayEditSystem(World, requester, system) || IsModerator(World, requester))
                {
                    output.Add(new FluidCommand(FluidCommandKind.Remove, FluidCommand.AnyOrigin, request.Point, request.Radius, request.Litres, 0, 0));
                    ok = true;
                }
                else
                {
                    ok = false;
                    foreach (int origin in OriginsOf(ledger, user))
                    {
                        output.Add(new FluidCommand(FluidCommandKind.Remove, origin, request.Point, request.Radius, request.Litres, 0, 0));
                        ok = true;
                    }
                }
                break;
            case FluidRequestKind.UndoPour:
            {
                int origin = ledger.FindOrigin(user, request.Number);
                ok = origin > 0;
                if (ok)
                    output.Add(new FluidCommand(FluidCommandKind.ClearOrigin, origin, default, 0f, 0f, 0, 0));
                break;
            }
            case FluidRequestKind.ClearMine:
                ok = true;
                foreach (int origin in OriginsOf(ledger, user))
                    output.Add(new FluidCommand(FluidCommandKind.ClearOrigin, origin, default, 0f, 0f, 0, 0));
                break;
            case FluidRequestKind.Emit:
                ok = MayPour(World, requester, rules, system);
                if (ok)
                {
                    int origin = ledger.OriginFor(user, request.Number);
                    output.Add(new FluidCommand(FluidCommandKind.Emit, origin, request.Point, 0f, request.Litres, request.Substance,
                        unchecked((int)(user * 31 + (ulong)request.Number)), request.Velocity));
                }
                break;
            case FluidRequestKind.AdoptSubstance:
            case FluidRequestKind.EnsureLoose:
                ok = MayPour(World, requester, rules, system);
                if (ok)
                    Approved?.Invoke(requester, request);
                break;
            default:
                ok = false;
                break;
        }
        if (ok)
            Allowed++;
        else
            Refused++;
        return ok;
    }

    public static IEnumerable<int> OriginsOf(FluidLedger ledger, ulong user)
    {
        for (int i = 1; i < ledger.OriginCount; i++)
        {
            var entry = ledger.OriginEntry(i);
            if (FluidLedger.OriginUser(entry) == user)
                yield return i;
        }
    }

    // Whether the user could write the system's own fields from their machine, asked the way the host asks of a write
    // that arrives over the wire, and quietly: a question, not an attempt.
    public static bool MayEditSystem(World world, User user, FluidSystem system)
    {
        var permissions = world?.DataModelPermissions;
        if (permissions == null || user == null)
            return true;
        IPermissionTarget target = system.Simulate;
        var request = new PermissionRequest(world, user, target, target.OwnershipParent, target, PermissionSurface.Field,
            PermissionAction.Write | PermissionAction.Replicate, isNetwork: true);
        return permissions.Allows(request);
    }

    public static bool IsModerator(World world, User user)
    {
        var permissions = world?.DataModelPermissions;
        if (permissions == null || user == null)
            return false;
        if (user.IsHost)
            return true;
        var role = permissions.GetRole(user);
        return ReferenceEquals(role, permissions.HostRole) || ReferenceEquals(role, permissions.AdminRole)
            || ReferenceEquals(role, permissions.ModeratorRole);
    }

    public static bool MayPour(World world, User user, FluidWorldRules? rules, FluidSystem? system)
    {
        var permissions = world?.DataModelPermissions;
        if (user == null)
            return false;
        var access = rules?.WhoMayPour.Value ?? FluidPourAccess.Spawners;
        bool spawn = permissions == null || permissions.AllowsDomain(user, PermissionDomain.Spawn);
        return access switch
        {
            FluidPourAccess.Everyone => true,
            FluidPourAccess.Spawners => spawn,
            FluidPourAccess.Builders => spawn && (system == null || MayEditSystem(world!, user, system)),
            _ => false,
        };
    }

    public static float3 ToSystem(FluidSystem system, float3 world) => system.Slot != null ? system.Slot.GlobalPointToLocal(world) : world;
}
