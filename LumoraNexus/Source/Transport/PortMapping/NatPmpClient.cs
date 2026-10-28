// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Transport.PortMapping;

// NAT-PMP (RFC 6886): the router's public address, and a UDP or TCP forward with a lifetime. -xlinka
internal sealed class NatPmpClient
{
    private const byte OpAddress = 0;
    private const byte OpMapUdp = 1;
    private const byte OpMapTcp = 2;

    private readonly IPAddress _gateway;
    private readonly IPAddress _local;
    private readonly byte _op;

    public NatPmpClient(IPAddress gateway, IPAddress local, PortProtocol protocol = PortProtocol.Udp)
    {
        _gateway = gateway;
        _local = local;
        _op = protocol == PortProtocol.Tcp ? OpMapTcp : OpMapUdp;
    }

    public async Task<PortMapResult?> MapAsync(int internalPort, int externalPort, int lifetimeSeconds, CancellationToken cancellationToken)
    {
        var request = new byte[12];
        request[1] = _op;
        RouterUdp.WriteUInt16(request, 4, internalPort);
        RouterUdp.WriteUInt16(request, 6, externalPort);
        RouterUdp.WriteUInt32(request, 8, (uint)lifetimeSeconds);
        var answer = await RouterUdp.ExchangeAsync(_gateway, _local, request,
            a => a.Length >= 16 && a[0] == 0 && a[1] == 128 + _op && RouterUdp.ReadUInt16(a, 8) == internalPort,
            cancellationToken).ConfigureAwait(false);
        if (answer == null)
            return null;
        int result = RouterUdp.ReadUInt16(answer, 2);
        if (result != 0)
            return PortMapResult.No($"NAT-PMP result {result}");
        var mapped = new PortMapResult
        {
            Mapped = true,
            ExternalPort = RouterUdp.ReadUInt16(answer, 10),
            LifetimeSeconds = (int)RouterUdp.ReadUInt32(answer, 12),
        };
        if (lifetimeSeconds > 0)
            mapped.External = await PublicAddressAsync(cancellationToken).ConfigureAwait(false) ?? IPAddress.None;
        return mapped;
    }

    public Task UnmapAsync(int internalPort, CancellationToken cancellationToken)
        => MapAsync(internalPort, 0, 0, cancellationToken);

    private async Task<IPAddress?> PublicAddressAsync(CancellationToken cancellationToken)
    {
        var answer = await RouterUdp.ExchangeAsync(_gateway, _local, new byte[] { 0, OpAddress },
            a => a.Length >= 12 && a[0] == 0 && a[1] == 128 + OpAddress, cancellationToken).ConfigureAwait(false);
        if (answer == null || RouterUdp.ReadUInt16(answer, 2) != 0)
            return null;
        return new IPAddress(new[] { answer[8], answer[9], answer[10], answer[11] });
    }
}
