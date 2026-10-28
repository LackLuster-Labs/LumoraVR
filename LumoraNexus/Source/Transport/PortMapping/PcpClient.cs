// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Transport.PortMapping;

// Port Control Protocol (RFC 6887), MAP for one UDP or TCP port over IPv4. The nonce stays the same for the life of a
// forward, so a renewal and the final removal speak for the same mapping. A router that only knows the older
// NAT-PMP answers with version 0, which reads here as "not this protocol". -xlinka
internal sealed class PcpClient
{
    private const byte Version = 2;
    private const byte OpMap = 1;
    private const byte ProtocolUdp = 17;
    private const byte ProtocolTcp = 6;
    private const int RequestLength = 60;

    private readonly IPAddress _gateway;
    private readonly IPAddress _local;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(12);
    private readonly byte _protocol;

    public PcpClient(IPAddress gateway, IPAddress local, PortProtocol protocol = PortProtocol.Udp)
    {
        _gateway = gateway;
        _local = local;
        _protocol = protocol == PortProtocol.Tcp ? ProtocolTcp : ProtocolUdp;
    }

    public async Task<PortMapResult?> MapAsync(int internalPort, int externalPort, int lifetimeSeconds, CancellationToken cancellationToken)
    {
        var request = new byte[RequestLength];
        request[0] = Version;
        request[1] = OpMap;
        RouterUdp.WriteUInt32(request, 4, (uint)lifetimeSeconds);
        WriteMappedV4(request, 8, _local);
        Buffer.BlockCopy(_nonce, 0, request, 24, 12);
        request[36] = _protocol;
        RouterUdp.WriteUInt16(request, 40, internalPort);
        RouterUdp.WriteUInt16(request, 42, externalPort);
        WriteMappedV4(request, 44, IPAddress.Any);

        var answer = await RouterUdp.ExchangeAsync(_gateway, _local, request,
            a => a.Length >= 4 && (a[0] != Version || (a.Length >= RequestLength && a[1] == (0x80 | OpMap) && Same(a, 24, _nonce))),
            cancellationToken).ConfigureAwait(false);
        if (answer == null || answer[0] != Version)
            return null;
        int result = answer[3];
        if (result != 0)
            return PortMapResult.No($"PCP result {result}");
        return new PortMapResult
        {
            Mapped = true,
            LifetimeSeconds = (int)RouterUdp.ReadUInt32(answer, 4),
            ExternalPort = RouterUdp.ReadUInt16(answer, 42),
            External = ReadMappedV4(answer, 44),
        };
    }

    public Task UnmapAsync(int internalPort, CancellationToken cancellationToken)
        => MapAsync(internalPort, 0, 0, cancellationToken);

    private static void WriteMappedV4(byte[] buffer, int at, IPAddress address)
    {
        buffer[at + 10] = 0xFF;
        buffer[at + 11] = 0xFF;
        Buffer.BlockCopy(address.GetAddressBytes(), 0, buffer, at + 12, 4);
    }

    private static IPAddress ReadMappedV4(byte[] buffer, int at)
    {
        var bytes = new byte[4];
        Buffer.BlockCopy(buffer, at + 12, bytes, 0, 4);
        return new IPAddress(bytes);
    }

    private static bool Same(byte[] buffer, int at, byte[] expected)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            if (buffer[at + i] != expected[i])
                return false;
        }
        return true;
    }
}
