// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Warden;

namespace Lumora.Fluids;

// Refuses network writes to a system's saved ledger members from anyone but the host, apart from the creator's
// first 2 s, so a saved cup a guest spawns arrives whole.
//
// Two engine rules make a plain MarkHostOnly wrong for these members. A guest's loaded copy of a host-only member is
// never sent, so a spawned item's Epoch would never leave the guest; and SyncArray.Load replicates through Clear and
// Add like any edit, so Cells would be sent and, outside the creation batch, refused at the highest denial weight.
// The cup would arrive full, empty or half-set depending on how the load split into batches. So the saved members stay
// ordinary, and this rule, which runs before the ownership checks, lets only the host write them, except that the user
// who minted the system (its RefID byte is theirs) may write them for its first 2 s on the host and until the host
// has written any ledger value of its own. That covers a load split over several batches and nothing after it.
//
// The host itself writes inside the system bypass, which never asks a rule. A refusal is reported as ForeignWrite;
// the gate adds its own unclassified mark for any rule's denial on top. -xlinka
public sealed class FluidLedgerRule : IPermissionRule
{
    public const double CreationWindowSeconds = 2.0;

    private readonly World _world;
    private readonly Dictionary<FluidSystem, double> _firstSeen = new();
    private readonly HashSet<FluidSystem> _closed = new();

    public FluidLedgerRule(World world)
    {
        _world = world;
    }

    public int Refused { get; private set; }

    public int LetThrough { get; private set; }

    // When the host first saw a system. The coordinator notes each system on the update it appears in; a system the
    // rule is asked about before that is noted then.
    public void Note(FluidSystem system)
    {
        if (system != null && !_firstSeen.ContainsKey(system))
            _firstSeen[system] = _world.Time.TotalTime;
    }

    // The host wrote the system's ledger: the creator's window is over.
    public void Close(FluidSystem system)
    {
        if (system != null)
            _closed.Add(system);
    }

    public void Forget(FluidSystem system)
    {
        _firstSeen.Remove(system);
        _closed.Remove(system);
    }

    public static bool IsGuarded(FluidSystem system, object? member)
        => member != null
            && (ReferenceEquals(member, system.Cells) || ReferenceEquals(member, system.CellTint)
                || ReferenceEquals(member, system.AnchorSlots) || ReferenceEquals(member, system.Stains)
                || ReferenceEquals(member, system.StainTint) || ReferenceEquals(member, system.Epoch)
                || ReferenceEquals(member, system.LedgerVersion));

    public PermissionResult Evaluate(in PermissionRequest request, IPermissionActor actor, out string? reason)
    {
        reason = null;
        if (!request.IsNetwork || !_world.IsAuthority || actor == null || actor.IsHost)
            return PermissionResult.Abstain;
        if (request.Parent is not FluidSystem system || system.World != _world)
            return PermissionResult.Abstain;
        if (!IsGuarded(system, request.Member ?? request.Target))
            return PermissionResult.Abstain;

        if (InCreationWindow(system, actor))
        {
            LetThrough++;
            return PermissionResult.Abstain;
        }

        Refused++;
        reason = "only the host writes a fluid system's ledger";
        _world.DataModelPermissions?.ReportDenial(actor as User, PermissionDenialKind.ForeignWrite, reason);
        return PermissionResult.Deny;
    }

    public bool InCreationWindow(FluidSystem system, IPermissionActor actor)
    {
        if (actor.AllocationByte == RefIDConstants.AUTHORITY_BYTE || actor.AllocationByte != system.ReferenceID.GetUserByte())
            return false;
        Note(system);
        if (_closed.Contains(system) || _world.Time.TotalTime - _firstSeen[system] > CreationWindowSeconds)
            return false;
        if (HostWrote(system))
        {
            _closed.Add(system);
            return false;
        }
        return true;
    }

    // Whether the host already put its own value into any saved ledger member. The engine records the last writer of
    // each member as the host encodes it.
    private bool HostWrote(FluidSystem system)
    {
        var host = _world.LocalUser;
        return host != null
            && (ReferenceEquals(system.Cells.LastModifyingUser, host) || ReferenceEquals(system.CellTint.LastModifyingUser, host)
                || ReferenceEquals(system.AnchorSlots.LastModifyingUser, host) || ReferenceEquals(system.Stains.LastModifyingUser, host)
                || ReferenceEquals(system.StainTint.LastModifyingUser, host) || ReferenceEquals(system.Epoch.LastModifyingUser, host)
                || ReferenceEquals(system.LedgerVersion.LastModifyingUser, host));
    }
}
