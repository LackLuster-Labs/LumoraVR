using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Lumora.Core.Networking.Session;

/// <summary>
/// Utilities for building and parsing session URLs.
/// </summary>
public static class SessionUrlBuilder
{
    /// <summary>
    /// URI scheme for LiteNetLib connections.
    /// </summary>
    public const string LNLScheme = "lnl";

    /// <summary>
    /// Default port for session connections.
    /// </summary>
    public const int DefaultPort = 7777;

    /// <summary>
    /// Build a LNL connection URL.
    /// </summary>
    /// <param name="host">Host address or IP</param>
    /// <param name="port">Port number</param>
    /// <param name="sessionId">Optional session ID to include in path</param>
    /// <returns>The constructed URI</returns>
    public static Uri BuildLNLUrl(string host, int port, string sessionId = null)
    {
        string path = !string.IsNullOrEmpty(sessionId) ? $"/{sessionId}" : "";
        return new Uri($"{LNLScheme}://{host}:{port}{path}");
    }

    /// <summary>
    /// Build a LAN wildcard URL (for local network discovery).
    /// </summary>
    public static Uri BuildLANUrl(int port, string sessionId = null)
    {
        return BuildLNLUrl("*", port, sessionId);
    }

    /// <summary>
    /// Try to parse a session ID from a URI path.
    /// </summary>
    /// <param name="uri">The URI to parse</param>
    /// <param name="sessionId">The extracted session ID</param>
    /// <returns>True if a valid session ID was found</returns>
    public static bool TryParseSessionId(Uri uri, out string sessionId)
    {
        sessionId = null;

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

    /// <summary>
    /// Parse host and port from a session URI.
    /// </summary>
    public static bool TryParseHostPort(Uri uri, out string host, out int port)
    {
        host = null;
        port = 0;

        if (uri == null)
            return false;

        host = uri.Host;
        port = uri.Port > 0 ? uri.Port : DefaultPort;

        return !string.IsNullOrEmpty(host);
    }

    /// <summary>
    /// Get all local session URLs for the given port and session ID.
    /// Returns URLs for all local network interfaces.
    /// </summary>
    public static List<Uri> GetLocalSessionUrls(int port, string sessionId)
    {
        var urls = new List<Uri>();

        foreach (var ip in GetLocalIPAddresses())
        {
            urls.Add(BuildLNLUrl(ip.ToString(), port, sessionId));
        }

        return urls;
    }

    /// <summary>
    /// Get the primary local session URL.
    /// </summary>
    public static Uri GetPrimaryLocalUrl(int port, string sessionId)
    {
        var primaryIP = GetLocalIPAddresses().FirstOrDefault();

        if (primaryIP != null)
        {
            return BuildLNLUrl(primaryIP.ToString(), port, sessionId);
        }

        return BuildLNLUrl("127.0.0.1", port, sessionId);
    }

    /// <summary>
    /// Get all local IPv4 addresses.
    /// </summary>
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

    /// <summary>
    /// Check if a URI is a LNL scheme URI.
    /// </summary>
    public static bool IsLNLUrl(Uri uri)
    {
        return uri != null &&
               string.Equals(uri.Scheme, LNLScheme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Convert a LNL URI to a standard connection endpoint string.
    /// </summary>
    public static string ToEndpointString(Uri uri)
    {
        if (uri == null)
            return null;

        int port = uri.Port > 0 ? uri.Port : DefaultPort;
        return $"{uri.Host}:{port}";
    }
}
