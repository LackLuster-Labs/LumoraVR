// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Lumora.Nexus.Transport.LNQ;

// The joining end of a QUIC link: lnq://<host>:<port>/<sessionId>, straight to the host.
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class LNQConnection : LNQLink
{
    private static readonly TimeSpan HandshakeWithin = TimeSpan.FromSeconds(8);

    public LNQConnection(Uri address)
        : base(isClient: true, address, $"QUIC:{address.Host}:{address.Port}")
    {
    }

    public override void Connect(Action<string> statusCallback)
    {
        statusCallback?.Invoke($"Connecting over QUIC to {Address.Host}:{Address.Port}");
        _ = Task.Run(ConnectAsync);
    }

    private async Task ConnectAsync()
    {
        QuicConnection? connection = null;
        try
        {
            string host = Address.DnsSafeHost;
            EndPoint remote = IPAddress.TryParse(host, out var literal)
                ? new IPEndPoint(literal, Address.Port)
                : new DnsEndPoint(host, Address.Port);
            var options = new QuicClientConnectionOptions
            {
                RemoteEndPoint = remote,
                DefaultStreamErrorCode = StreamErrorCode,
                DefaultCloseErrorCode = CloseErrorCode,
                MaxInboundUnidirectionalStreams = LegCount,
                MaxInboundBidirectionalStreams = 0,
                IdleTimeout = TimeSpan.FromMilliseconds(Framed.FramedLink.SilenceTimeoutMs + 5000),
                KeepAliveInterval = TimeSpan.FromSeconds(5),
                HandshakeTimeout = HandshakeWithin,
                InitialReceiveWindowSizes = Windows,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol> { Protocol },
                    TargetHost = "lumora-session",
                    // Any certificate: trust comes from the crypto handshake and the signed join inside.
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            };
            connection = await QuicConnection.ConnectAsync(options, Token).ConfigureAwait(false);
            if (IsClosed)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return;
            }
            await RunAsync(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (connection != null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (connection != null)
                await connection.DisposeAsync().ConfigureAwait(false);
            Fail($"QUIC to {Address.Host}:{Address.Port}: {ex.Message}");
        }
    }
}
