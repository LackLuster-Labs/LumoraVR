// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Transport.PortMapping;

// One request to the router's port control port (5351) and the first answer that fits, sent again at 250 ms,
// 500 ms and 1 s the way both protocols ask. Null when the router said nothing. -xlinka
internal static class RouterUdp
{
    public const int ControlPort = 5351;
    private static readonly int[] WaitsMs = { 250, 500, 1000 };

    public static async Task<byte[]?> ExchangeAsync(IPAddress gateway, IPAddress local, byte[] request, Func<byte[], bool> fits,
        CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(new IPEndPoint(local, 0));
        var router = new IPEndPoint(gateway, ControlPort);
        foreach (int wait in WaitsMs)
        {
            await socket.SendAsync(request, request.Length, router).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(wait);
            try
            {
                while (true)
                {
                    var answer = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    if (answer.RemoteEndPoint.Address.Equals(gateway) && fits(answer.Buffer))
                        return answer.Buffer;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
                // A port-unreachable from the router: nothing listens there.
                return null;
            }
        }
        return null;
    }

    public static void WriteUInt16(byte[] buffer, int at, int value)
    {
        buffer[at] = (byte)(value >> 8);
        buffer[at + 1] = (byte)value;
    }

    public static void WriteUInt32(byte[] buffer, int at, uint value)
    {
        buffer[at] = (byte)(value >> 24);
        buffer[at + 1] = (byte)(value >> 16);
        buffer[at + 2] = (byte)(value >> 8);
        buffer[at + 3] = (byte)value;
    }

    public static int ReadUInt16(byte[] buffer, int at) => (buffer[at] << 8) | buffer[at + 1];

    public static uint ReadUInt32(byte[] buffer, int at)
        => ((uint)buffer[at] << 24) | ((uint)buffer[at + 1] << 16) | ((uint)buffer[at + 2] << 8) | buffer[at + 3];
}
