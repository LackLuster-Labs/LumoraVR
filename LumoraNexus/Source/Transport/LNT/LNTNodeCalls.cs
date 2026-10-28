// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Net;

namespace Lumora.Nexus.Transport.LNT;

// Where a NAT node's call for a hosted session lands: the session's node link hears it on the session socket,
// from the node it is registered on and nowhere else, and passes it here to the session's TCP listener.
//   tcp-call;<sessionId>;<nonce>;<tcp port>
// -xlinka
public static class LNTNodeCalls
{
    private static readonly ConcurrentDictionary<string, LNTListener> Listeners = new(StringComparer.OrdinalIgnoreCase);

    internal static void Register(string sessionId, LNTListener listener) => Listeners[sessionId] = listener;

    internal static void Unregister(string sessionId, LNTListener listener)
        => Listeners.TryRemove(new System.Collections.Generic.KeyValuePair<string, LNTListener>(sessionId, listener));

    public static bool IsCall(string[] fields) => fields.Length > 0 && fields[0] == LNTNodeGate.Call;

    // The node's fields as they came, and the address they came from: the gate is on that address.
    public static bool Ring(string[] fields, IPAddress node)
    {
        if (fields.Length < 4 || fields[0] != LNTNodeGate.Call)
            return false;
        if (!int.TryParse(fields[3], out int port) || port is <= 0 or > 65535 || !IsNonce(fields[2]))
            return false;
        if (!Listeners.TryGetValue(fields[1], out var listener))
            return false;
        listener.AnswerCall(node.IsIPv4MappedToIPv6 ? node.MapToIPv4() : node, port, fields[2]);
        return true;
    }

    private static bool IsNonce(string value)
    {
        if (value.Length < 16 || value.Length > 64)
            return false;
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }
        return true;
    }
}
