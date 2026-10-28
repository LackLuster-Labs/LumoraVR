// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Lumora.Nexus.Cloud;

public static class SessionUrlBuilder
{
    public const string LNLScheme = "lnl";

    public const int DefaultPort = 7777;

    public static Uri BuildLNLUrl(string host, int port, string sessionId = null!)
    {
        string path = !string.IsNullOrEmpty(sessionId) ? $"/{sessionId}" : "";
        if (IPAddress.TryParse(host, out var literal) && literal.AddressFamily == AddressFamily.InterNetworkV6)
            host = "[" + literal + "]";
        return new Uri($"{LNLScheme}://{host}:{port}{path}");
    }

    public static Uri BuildLANUrl(int port, string sessionId = null!)
    {
        return BuildLNLUrl("*", port, sessionId);
    }

    public static bool TryParseSessionId(Uri uri, out string sessionId)
    {
        sessionId = null!;

        if (uri == null)
            return false;

        string path = uri.AbsolutePath.TrimStart('/');

        if (SessionIdentifier.IsValid(path))
        {
            sessionId = path;
            return true;
        }

        return false;
    }

    public static bool TryParseHostPort(Uri uri, out string host, out int port)
    {
        host = null!;
        port = 0;

        if (uri == null)
            return false;

        host = uri.Host;
        port = uri.Port > 0 ? uri.Port : DefaultPort;

        return !string.IsNullOrEmpty(host);
    }

    public static List<Uri> GetLocalSessionUrls(int port, string sessionId)
    {
        var urls = new List<Uri>();

        foreach (var ip in GetLocalIPAddresses())
        {
            urls.Add(BuildLNLUrl(ip.ToString(), port, sessionId));
        }

        // A public IPv6 address reaches the listener directly, with no NAT to get through.
        if (Lumora.Nexus.Transport.NetworkOptions.AllowIpv6)
        {
            foreach (var ip in Lumora.Nexus.Transport.HostAddresses.GlobalIpv6)
                urls.Add(BuildLNLUrl(ip.ToString(), port, sessionId));
        }

        return urls;
    }

    public static Uri GetPrimaryLocalUrl(int port, string sessionId)
    {
        var primaryIP = GetLocalIPAddresses().FirstOrDefault();

        if (primaryIP != null)
        {
            return BuildLNLUrl(primaryIP.ToString(), port, sessionId);
        }

        return BuildLNLUrl("127.0.0.1", port, sessionId);
    }

    public static IEnumerable<IPAddress> GetLocalIPAddresses()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            return host.AddressList
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .Where(ip => !IPAddress.IsLoopback(ip));
        }
        catch
        {
            return new[] { IPAddress.Loopback };
        }
    }

    public static bool IsLNLUrl(Uri uri)
    {
        return uri != null &&
               string.Equals(uri.Scheme, LNLScheme, StringComparison.OrdinalIgnoreCase);
    }

    public static string ToEndpointString(Uri uri)
    {
        if (uri == null)
            return null!;

        int port = uri.Port > 0 ? uri.Port : DefaultPort;
        return $"{uri.Host}:{port}";
    }
}
