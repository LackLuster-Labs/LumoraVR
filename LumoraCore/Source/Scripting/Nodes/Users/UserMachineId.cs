// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Security.Cryptography;
using System.Text;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The machine a user is on, as a key that stays the same for that machine all through this session, so a
// guest with no account id can still be told apart, and known again after a rejoin. It is the machine's id
// mixed with the session's, never the id itself: the same machine reads differently in another session, so
// nothing can follow a guest from session to session by it. The same on every machine in the session; empty
// for a user the script cannot see. -xlinka
[Node("Users/Info/Machine ID", Capability.UsersRead, Description = "The machine a user is on, as a key that stays the same for that machine all through this session, so a guest with no account id can still be told apart, and known again after a rejoin.",
    Keywords = "device id, guest id, machine key")]
public sealed class UserMachineId : ReadNode
{
    [return: Port("id")]
    public static string Run(ScriptContext ctx, User? user)
    {
        if (!ScriptReads.Visible(ctx, user) || user!.MachineID.Value is not { Length: > 0 } machine)
            return string.Empty;
        return KeyFor(ctx.World, machine);
    }

    // A machine's key in this world's session, for anyone holding its id: a join check sees the same one.
    internal static string KeyFor(World world, string machine)
    {
        if (string.IsNullOrEmpty(machine))
            return string.Empty;
        string session = world.SessionID?.Value ?? string.Empty;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(session + "\n" + machine), hash);
        return "M-" + Convert.ToHexString(hash[..12]).ToLowerInvariant();
    }
}
