// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net.Sockets;
using System.Threading;
using Lumora.Nexus.Transport.Framed;
using Lumora.Nexus.Transport.LNL;

namespace Lumora.Nexus.Transport.LNT;

// A session link over one TCP socket: every lane on the one stream, our frames deciding the order.
public abstract class LNTLink : FramedLink
{
    private static readonly byte[] Lanes =
    {
        FramedWire.LaneForeground, FramedWire.LaneBackground, FramedWire.LaneUnreliable, FramedWire.FirstBulkLane
    };

    private Socket? _socket;

    protected LNTLink(bool isClient, Uri address, string identifier)
        : base(isClient, LNLManager.APP_ID, address, identifier, paced: true)
    {
    }

    public override string TransportName => "TCP";

    protected void Run(Socket socket)
    {
        Interlocked.Exchange(ref _socket, socket)?.Dispose();
        if (socket.RemoteEndPoint is System.Net.IPEndPoint remote)
            IP = FramedAddresses.Plain(remote.Address);
        var stream = new NetworkStream(socket, ownsSocket: false);
        var leg = AddLeg(0, Lanes);
        leg.StartWriter(stream, Token);
        leg.StartReader(stream, Token);
        Begin();
        if (IsClosed)
            ShutdownTransport();
    }

    protected override void ShutdownTransport()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket == null)
            return;
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception)
        {
        }
        socket.Dispose();
    }
}
