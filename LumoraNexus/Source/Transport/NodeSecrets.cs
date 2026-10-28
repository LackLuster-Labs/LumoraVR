// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Lumora.Nexus.Transport;

// The secret a hosted session holds its place on a node with, one per session for as long as it runs, so its
// UDP registration and a TCP hold of the same session speak for the same owner. -xlinka
public static class NodeSecrets
{
    private static readonly ConcurrentDictionary<string, string> Secrets = new(StringComparer.OrdinalIgnoreCase);

    public static string For(string sessionId)
        => Secrets.GetOrAdd(sessionId ?? "", _ => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));

    public static void Forget(string sessionId)
    {
        if (sessionId != null)
            Secrets.TryRemove(sessionId, out _);
    }
}
