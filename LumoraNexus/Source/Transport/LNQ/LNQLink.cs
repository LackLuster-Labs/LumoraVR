// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Transport.Framed;
using Lumora.Nexus.Transport.LNL;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.LNQ;

// A session link over one QUIC connection. Each side opens one-way streams, named by their first byte: one for the
// control frames, foreground, background and unreliable lanes under our own scheduler, and four for bulk, since
// .NET keeps one write in flight per stream and a single stream carried 1.2 MB/s at 80 ms. msquic sends streams
// first come, first served and .NET gives no way to rank them, so with a stream per lane a backlog held
// foreground behind it for seconds; the link's window keeps what msquic holds near one round trip, as on TCP.
// -xlinka
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public abstract class LNQLink : FramedLink
{
    public const int LegCount = 5;
    internal const long StreamErrorCode = 0x4C51;
    internal const long CloseErrorCode = 0x4C52;
    public static readonly SslApplicationProtocol Protocol = new("lumora-lnq");

    private static readonly byte[][] LegLanes =
    {
        new[] { FramedWire.LaneForeground, FramedWire.LaneBackground, FramedWire.LaneUnreliable },
        new[] { FramedWire.FirstBulkLane },
        new[] { (byte)(FramedWire.FirstBulkLane + 1) },
        new[] { (byte)(FramedWire.FirstBulkLane + 2) },
        new[] { (byte)(FramedWire.FirstBulkLane + 3) },
    };

    internal static readonly QuicReceiveWindowSizes Windows = new()
    {
        Connection = 16 * 1024 * 1024,
        UnidirectionalStream = 4 * 1024 * 1024,
    };

    private QuicConnection? _connection;
    private readonly FramedLeg[] _legs = new FramedLeg[LegCount];

    protected LNQLink(bool isClient, Uri address, string identifier)
        : base(isClient, LNLManager.APP_ID, address, identifier, paced: true)
    {
        for (int i = 0; i < LegCount; i++)
            _legs[i] = AddLeg(i, LegLanes[i]);
    }

    public override string TransportName => "QUIC";

    protected async Task RunAsync(QuicConnection connection)
    {
        Interlocked.Exchange(ref _connection, connection);
        IP = FramedAddresses.Plain(connection.RemoteEndPoint.Address);
        Begin();
        var token = Token;
        _ = Task.Run(() => AcceptStreamsAsync(connection, token));
        try
        {
            for (int i = 0; i < LegCount; i++)
            {
                var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token).ConfigureAwait(false);
                await stream.WriteAsync(new[] { (byte)i }, token).ConfigureAwait(false);
                _legs[i].StartWriter(stream, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fail("opening streams failed: " + ex.Message);
        }
        if (IsClosed)
            ShutdownTransport();
    }

    private async Task AcceptStreamsAsync(QuicConnection connection, CancellationToken token)
    {
        var seen = new HashSet<int>();
        var tag = new byte[1];
        try
        {
            while (seen.Count < LegCount && !token.IsCancellationRequested)
            {
                var stream = await connection.AcceptInboundStreamAsync(token).ConfigureAwait(false);
                int read = await stream.ReadAsync(tag, token).ConfigureAwait(false);
                if (read != 1 || tag[0] >= LegCount || !seen.Add(tag[0]))
                {
                    LumoraLogger.Warn($"[QUIC] {Identifier}: unexpected stream {(read == 1 ? tag[0].ToString() : "without a tag")}");
                    Fail("unexpected stream");
                    return;
                }
                _legs[tag[0]].StartReader(stream, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosed)
                Fail("accepting streams failed: " + ex.Message);
        }
    }

    protected override void ShutdownTransport()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection == null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await connection.CloseAsync(CloseErrorCode).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        });
    }
}
