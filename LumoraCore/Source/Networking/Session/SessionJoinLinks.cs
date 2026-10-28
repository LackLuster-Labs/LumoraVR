// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Lumora.Core.Networking.Session;

// A link to a session that works outside the game: the launch address the game registers for itself on a
// computer it runs on, carrying one of the session's addresses. Opening it starts the game, or hands it to the
// one already running, which looks the session up by the id in it for its other addresses and joins. The
// launch side lives with the presence integration (DiscordJoinLink), which parses exactly this. -xlinka
public static class SessionJoinLinks
{
    public const string AppId = "1330400493054464092";
    public const string Scheme = "discord-" + AppId;
    // what the launch side takes from a link: longer, and it is not read at all
    public const int MaxAddressBytes = 128;

    public static string? For(IEnumerable<string>? addresses)
    {
        var best = Best(addresses);
        return best == null ? null : Scheme + "://join/" + Uri.EscapeDataString(best.AbsoluteUri);
    }

    // Through the NAT node first, which reaches across networks and relays when it must; then over its TCP gate;
    // then Steam; then a public address; a private one last, since it only answers on the host's own network.
    public static Uri? Best(IEnumerable<string>? addresses)
    {
        Uri? best = null;
        int bestRank = int.MaxValue;
        foreach (var text in addresses ?? Array.Empty<string>())
        {
            if (!Usable(text, out var uri))
                continue;
            int rank = uri.Scheme switch
            {
                "lnl-nat" => 0,
                "lnt-nat" => 1,
                "steam" => 2,
                _ => IsPrivate(uri) ? 5 : uri.Scheme == "lnl" ? 3 : 4,
            };
            if (rank < bestRank)
            {
                best = uri;
                bestRank = rank;
            }
        }
        return best;
    }

    // Every address an invite carries that the joiner can dial, in the host's order; the join orders them itself.
    public static List<Uri> Dialable(IEnumerable<string>? addresses)
    {
        var list = new List<Uri>();
        foreach (var text in addresses ?? Array.Empty<string>())
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                && uri.Scheme is "lnl" or "lnl-nat" or "lnt" or "lnt-nat" or "lnq" or "steam")
                list.Add(uri);
        }
        return list;
    }

    private static bool Usable(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > MaxAddressBytes
            || !Uri.TryCreate(text, UriKind.Absolute, out var candidate) || string.IsNullOrEmpty(candidate.Host)
            || candidate.UserInfo.Length != 0 || candidate.Query.Length != 0 || candidate.Fragment.Length != 0)
            return false;
        bool ok = candidate.Scheme switch
        {
            "lnl" or "lnt" or "lnq" => candidate.Port is > 0 and <= 65535,
            "lnl-nat" or "lnt-nat" => candidate.Port is > 0 and <= 65535 && candidate.Segments.Length >= 2,
            "steam" => ulong.TryParse(candidate.Host, out ulong id) && id > 0 && candidate.Segments.Length >= 2,
            _ => false,
        };
        if (ok)
            uri = candidate;
        return ok;
    }

    private static bool IsPrivate(Uri uri)
    {
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
            return uri.Host == "localhost";
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            return true;
        byte[] b = address.GetAddressBytes();
        if (b.Length == 16)
            return (b[0] & 0xFE) == 0xFC;
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168
            || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127;
    }
}
