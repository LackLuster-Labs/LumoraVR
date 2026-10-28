// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Transport.LNT;

// The joining end of a TCP link. lnt:// dials the host itself; lnt-nat:// goes through the TCP gate of the
// host's NAT node, for a network that lets no UDP out. Only a node on the official list is ever dialled.
// -xlinka
public sealed class LNTConnection : LNTLink
{
    private readonly bool _viaNode;
    private readonly string _sessionId;

    public LNTConnection(Uri address)
        : base(isClient: true, address, address.Host)
    {
        _viaNode = string.Equals(address.Scheme, LNTNetworkManager.NAT_SCHEME, StringComparison.OrdinalIgnoreCase);
        _sessionId = address.AbsolutePath.Trim('/');
        ViaRelay = _viaNode;
    }

    public override void Connect(Action<string> statusCallback)
    {
        statusCallback?.Invoke(_viaNode
            ? $"Reaching the host over TCP through {Address.Host}:{Address.Port}"
            : $"Connecting over TCP to {Address.Host}:{Address.Port}");
        _ = Task.Run(ConnectAsync);
    }

    private async Task ConnectAsync()
    {
        Socket? socket = null;
        try
        {
            if (_viaNode)
            {
                var node = await NetworkNodeDirectory.FindListedAsync(Address.DnsSafeHost, Address.Port, Token).ConfigureAwait(false);
                if (node == null)
                {
                    Fail($"{Address.Host}:{Address.Port} is not a listed node");
                    return;
                }
                if (!LNTNodeGate.HasGate(node))
                {
                    Fail($"node {node.Name} takes no TCP");
                    return;
                }
                int port = LNTNodeGate.TcpPortOf(node);
                LumoraLogger.Debug($"[TCP] Asking node {node.Name} at {Address.DnsSafeHost}:{port} for {_sessionId}");
                socket = await LNTNodeGate.JoinAsync(Address.DnsSafeHost, port, _sessionId, Token).ConfigureAwait(false);
                LumoraLogger.Log($"[TCP] Node {node.Name} joined us to {_sessionId}");
            }
            else
            {
                socket = await LNTNodeGate.ConnectAsync(Address.DnsSafeHost, Address.Port, Token).ConfigureAwait(false);
            }
            if (IsClosed)
            {
                socket.Dispose();
                return;
            }
            Identifier = _viaNode ? $"TCP:{Address.Host} {_sessionId}" : $"TCP:{Address.Host}:{Address.Port}";
            Run(socket);
        }
        catch (OperationCanceledException)
        {
            socket?.Dispose();
        }
        catch (Exception ex)
        {
            socket?.Dispose();
            Fail(_viaNode ? $"TCP through {Address.Host}: {ex.Message}" : $"TCP to {Address.Host}:{Address.Port}: {ex.Message}");
        }
    }
}
