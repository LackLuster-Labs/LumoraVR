// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Lumora.Nexus.Transport.Framed;

namespace Lumora.Nexus.Transport.LNQ;

// The hosting end of a QUIC link.
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class LNQPeer : LNQLink
{
    private readonly QuicConnection _connection;

    internal LNQPeer(QuicConnection connection)
        : base(isClient: false,
            new Uri($"{LNQNetworkManager.SCHEME}://{FramedAddresses.UriHost(connection.RemoteEndPoint.Address)}:{connection.RemoteEndPoint.Port}"),
            $"QUIC:{FramedAddresses.UriHost(connection.RemoteEndPoint.Address)}:{connection.RemoteEndPoint.Port}")
    {
        _connection = connection;
    }

    internal void Start() => _ = Task.Run(() => RunAsync(_connection));

    public override void Connect(Action<string> statusCallback)
        => throw new NotSupportedException("A hosted QUIC peer is already connected");
}
