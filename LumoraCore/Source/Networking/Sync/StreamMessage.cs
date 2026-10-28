// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.IO;
using Lumora.Core.Networking;
using Lumora.Nexus.Transport;

namespace Lumora.Core.Networking.Sync;

public class StreamMessage : SyncMessage
{
    public const float MAX_AGE_SECONDS = 4f;

    private MemoryStream _memoryStream;

    public override MessageType MessageType => IsAsynchronous ? MessageType.AsyncStream : MessageType.Stream;
    public override bool Reliable => false;

    // Streams (voice/tracking) are unreliable and latency-sensitive, so only compress the
    // larger ones where the ratio outweighs the per-frame CPU; tiny tracking frames ship
    // raw. - xlinka
    public override int CompressionThreshold => 200;

    public bool IsAsynchronous { get; set; }
    public ulong UserID { get; set; }
    public uint StreamStateVersion { get; set; }
    public double StreamTime { get; set; }
    public ushort StreamGroup { get; set; }

    public bool IsOutdated => SecondsSinceReceived >= MAX_AGE_SECONDS;

    public StreamMessage(ulong stateVersion, ulong syncTick, IConnection sender = null!)
        : base(stateVersion, syncTick, sender)
    {
        _memoryStream = new MemoryStream();
    }

    public MemoryStream GetData()
    {
        _memoryStream.Position = 0;
        return _memoryStream;
    }

    public override byte[] Encode()
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        WriteFrame(writer, output);
        return output.ToArray();
    }

    public override int EncodeInto(MemoryStream output, BinaryWriter writer)
    {
        output.SetLength(0);
        WriteFrame(writer, output);
        writer.Flush();
        return (int)output.Length;
    }

    private void WriteFrame(BinaryWriter writer, Stream output)
    {
        writer.Write((byte)MessageType);
        writer.Write7BitEncoded(SenderStateVersion);
        writer.Write7BitEncoded(SenderSyncTick);
        writer.Write7BitEncoded(UserID);
        writer.Write7BitEncoded(StreamStateVersion);
        writer.Write(StreamTime);
        writer.Write(StreamGroup);
        writer.Write7BitEncoded((ulong)_memoryStream.Length);

        writer.Flush();
        _memoryStream.Position = 0;
        _memoryStream.CopyTo(output);
    }

    public static StreamMessage Decode(BinaryReader reader)
    {
        var userId = reader.Read7BitEncodedUInt64();
        var streamStateVersion = (uint)reader.Read7BitEncodedUInt64();
        var streamTime = reader.ReadDouble();
        var streamGroup = reader.ReadUInt16();

        // Bounded: peer-declared payload size is capped before allocation.
        var data = reader.ReadBoundedBytes7Bit(NetworkLimits.MaxStreamMessageData);

        return new StreamMessage(new MemoryStream(data))
        {
            UserID = userId,
            StreamStateVersion = streamStateVersion,
            StreamTime = streamTime,
            StreamGroup = streamGroup,
        };
    }

    // A received message holds the bytes it came with; the empty stream the other constructor makes is not made.
    private StreamMessage(MemoryStream data)
        : base(0, 0)
    {
        _memoryStream = data;
    }

    public override void Dispose()
    {
        base.Dispose();
        _memoryStream?.Dispose();
    }
}
