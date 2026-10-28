// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net;
using System.Net.Sockets;
using Lumora.Nexus.Transport.Framed;

namespace Lumora.Nexus.Transport.LNT;

// The hosting end of a TCP link: a joiner that dialled the listener, or one the node joined to us. A joiner
// through the node shares the node's address with every other one, so it is told apart by its own identifier.
public sealed class LNTPeer : LNTLink
{
    private readonly Socket _socket;

    internal LNTPeer(Socket socket, bool viaRelay, string? identifier = null)
        : base(isClient: false, AddressOf(socket), identifier ?? IdentifierOf(socket))
    {
        _socket = socket;
        ViaRelay = viaRelay;
    }

    internal void Start() => Run(_socket);

    public override void Connect(Action<string> statusCallback)
        => throw new NotSupportedException("A hosted TCP peer is already connected");

    private static IPEndPoint? Remote(Socket socket) => socket.RemoteEndPoint as IPEndPoint;

    private static Uri AddressOf(Socket socket)
    {
        var remote = Remote(socket);
        return remote == null
            ? new Uri($"{LNTNetworkManager.SCHEME}://0.0.0.0:0")
            : new Uri($"{LNTNetworkManager.SCHEME}://{FramedAddresses.UriHost(remote.Address)}:{remote.Port}");
    }

    private static string IdentifierOf(Socket socket)
    {
        var remote = Remote(socket);
        return remote == null ? "TCP:?" : $"TCP:{FramedAddresses.UriHost(remote.Address)}:{remote.Port}";
    }
}
