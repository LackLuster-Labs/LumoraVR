// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Security.Cryptography;
using System.Text;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Security.Integrity;

// Who this game is to other games: the release it was stamped as ("dev" for anything built from the source tree),
// the synced-component schema, and whether its managed code is what that release shipped. Two games join each other
// only when all three agree, so a modified game plays with games that carry the very same changes and nobody else.
// The same release on Windows and Linux agrees: the schema comes from the source and the verdict only says clean.
// Every game reports its own identity, so this keeps casual changes apart; it proves nothing about a game that has
// patched this class too. -xlinka
public static class BuildIdentity
{
    private const string Label = "lumora-build/1";
    private const string DevRelease = "dev";

    private static readonly object Gate = new();
    private static int _revision = -1;
    private static byte[] _bytes = Array.Empty<byte>();
    private static string _hash = string.Empty;

    public static bool IsRelease => !BuildInfo.Version.StartsWith("0.0.0", StringComparison.Ordinal);

    public static string Release => IsRelease ? BuildInfo.Version : DevRelease;

    // Lower hex SHA-256. Waits for the startup check the first time.
    public static string Hash
    {
        get
        {
            Refresh();
            lock (Gate) return _hash;
        }
    }

    public static byte[] Bytes
    {
        get
        {
            Refresh();
            lock (Gate) return _bytes;
        }
    }

    public static bool IsModified => GameIntegrity.IsModified;

    // Without waiting: false until the startup check has finished.
    public static bool TryGetHash(out string hash)
    {
        if (!GameIntegrity.IsReady)
        {
            hash = string.Empty;
            return false;
        }
        hash = Hash;
        return true;
    }

    // The first twelve hex digits, for logs.
    public static string Short(string? hash)
        => string.IsNullOrEmpty(hash) ? "none" : hash.Length > 12 ? hash.Substring(0, 12) : hash;

    public static bool Matches(string? other)
        => !string.IsNullOrEmpty(other) && string.Equals(other, Hash, StringComparison.OrdinalIgnoreCase);

    private static void Refresh()
    {
        GameIntegrity.WaitReady();
        int revision = GameIntegrity.Revision;
        lock (Gate)
        {
            if (revision == _revision)
                return;
        }

        string verdict = GameIntegrity.Verdict;
        string text = $"{Label}\n{Release}\n{Convert.ToHexString(ProtocolCompatibility.BaseHash)}\n{verdict}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        lock (Gate)
        {
            _bytes = bytes;
            _hash = Convert.ToHexString(bytes).ToLowerInvariant();
            _revision = revision;
        }
    }
}
