// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Scripting;

// Someone asking the host to join, as a join check script sees them, and what the checks made of it. Only
// the host makes one, once the joiner has proved who they are and passed every rule of the session's own; a
// check can only turn them away, let them in, and pick the role they arrive with. Everything here is read
// and written on the world's thread. -xlinka
public sealed class JoinRequest
{
    internal JoinRequest(World world, string accountId, string name, string machineKey, bool guest, bool friend, bool silenced)
    {
        World = world;
        AccountId = accountId;
        Name = name;
        MachineKey = machineKey;
        Guest = guest;
        Friend = friend;
        Silenced = silenced;
    }

    public World World { get; }

    // The proved account, empty for a guest.
    public string AccountId { get; }
    public string Name { get; }

    // The joiner's machine as Users/Machine ID gives it for this session, never the machine's own id.
    public string MachineKey { get; }
    public bool Guest { get; }

    // On the host's friends list.
    public bool Friend { get; }

    // Arriving muted because the platform muted them.
    public bool Silenced { get; }

    public JoinAnswer Answer { get; private set; }
    public string? Reason { get; private set; }
    public string? Role { get; private set; }

    // Answered for good: nothing a check does after counts.
    public bool Settled { get; internal set; }

    // A refusal stands whatever else is said; letting in only stands where nothing refused.
    internal bool Allow()
    {
        if (Settled)
            return false;
        if (Answer == JoinAnswer.None)
            Answer = JoinAnswer.Allowed;
        return true;
    }

    internal bool Refuse(string? reason)
    {
        if (Settled)
            return false;
        if (Answer != JoinAnswer.Refused)
        {
            Answer = JoinAnswer.Refused;
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        }
        return true;
    }

    internal bool ChooseRole(string role)
    {
        if (Settled)
            return false;
        Role = role;
        return true;
    }
}
