// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Lumora.Nexus.Transport.LNL;

// What a session socket says to a NAT/relay node and hears back, protocol 2. Requests to the node ride LNL's
// introduce-request token; everything else is an unconnected message holding one string of fields joined by
// ';', except the ping and pong, which carry binary fields after their tag. -xlinka
internal static class LNLNodeWire
{
    public const int Version = 2;

    public const string Ping = "lumora-ping";
    public const string Pong = "lumora-pong";
    // A ping shorter than this is ignored by the node, so its answer is never bigger than the question.
    private const int PingBytes = 48;

    public const string Registered = "registered";
    public const string Taken = "taken";
    public const string Busy = "busy";
    public const string Closing = "closing";
    public const string Error = "error";
    public const string Gone = "gone";
    public const string RelayPort = "relay-port";
    public const string RelayReady = "relay-ready";
    public const string RelayFail = "relay-fail";
    // Host to joiner, from the session socket once the joiner's punch got through, so a joiner whose copy of
    // the host's punch was lost still finds it.
    public const string Punched = "punched";

    public static string HostToken(string sessionId, string secret) => $"host;{Version};{sessionId};{secret}";

    public static string HostByeToken(string sessionId, string secret) => $"host-bye;{Version};{sessionId};{secret}";

    public static string JoinToken(string sessionId) => $"join;{Version};{sessionId}";

    public static string RelayRequest(string sessionId) => $"relay;{Version};{sessionId}";

    public static string RelayHello(string nonce) => $"relay-hello;{nonce}";

    public static void SendText(NetManager socket, IPEndPoint to, string text)
    {
        var writer = new NetDataWriter();
        writer.Put(text);
        socket.SendUnconnectedMessage(writer, to);
    }

    public static void SendPing(NetManager socket, IPEndPoint to, long nonce)
    {
        var writer = new NetDataWriter();
        writer.Put(Ping);
        writer.Put(nonce);
        while (writer.Length < PingBytes)
            writer.Put((byte)0);
        socket.SendUnconnectedMessage(writer, to);
    }

    // The leading string split on ';', or null for anything that does not start with one.
    public static string[]? ReadFields(NetDataReader reader)
    {
        try
        {
            return reader.TryGetString(out var text) && !string.IsNullOrEmpty(text) ? text.Split(';') : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool SameSession(string[] fields, int index, string sessionId)
        => fields.Length > index && string.Equals(fields[index], sessionId, StringComparison.OrdinalIgnoreCase);

    public static bool SameEndPoint(IPEndPoint a, IPEndPoint b)
        => a.Port == b.Port && Plain(a.Address).Equals(Plain(b.Address));

    public static bool SameAddress(IPAddress a, IPAddress b) => Plain(a).Equals(Plain(b));

    private static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public static async Task<IPEndPoint?> ResolveAsync(string host, int port)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            return new IPEndPoint(literal, port);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            IPAddress? pick = null;
            foreach (var address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    pick = address;
                    break;
                }
                pick ??= address;
            }
            return pick != null ? new IPEndPoint(pick, port) : null;
        }
        catch
        {
            return null;
        }
    }

    public static Uri BuildUri(string host, int port, string sessionId)
    {
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
            host = "[" + host + "]";
        return new Uri($"{LNLNetworkManager.NAT_SCHEME}://{host}:{port}/{sessionId}");
    }
}
