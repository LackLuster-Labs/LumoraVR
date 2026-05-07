// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.IO;
using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Messages;

/// <summary>
/// Stream message - high-frequency continuous data.
/// Sent at 60+ Hz for transforms, audio, etc.
/// Separate from delta batching system.
/// </summary>
public class StreamMessage
{
    /// <summary>
    /// Stream data entries.
    /// Each entry: UserID + StreamID + Data
    /// </summary>
    public List<StreamEntry> Entries = new();

    public MessageType Type => MessageType.Stream;
    public bool Reliable => false; // Streams use unreliable for low latency

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)Type);
        writer.Write(Entries.Count);

        foreach (var entry in Entries)
        {
            entry.Encode(writer);
        }

        return ms.ToArray();
    }

    public static StreamMessage Decode(BinaryReader reader)
    {
        var message = new StreamMessage();
        int count = reader.ReadInt32();

        // Cap entry count: an attacker could otherwise send count=int.MaxValue
        // and walk us off the end of the stream while allocating a giant List.
        if (count < 0 || count > NetworkLimits.MaxStreamEntriesPerMessage)
            throw new InvalidDataException($"StreamMessage entry count {count} out of bounds (cap {NetworkLimits.MaxStreamEntriesPerMessage}).");

        for (int i = 0; i < count; i++)
        {
            message.Entries.Add(StreamEntry.Decode(reader));
        }

        return message;
    }
}

/// <summary>
/// Single stream data entry.
/// </summary>
public struct StreamEntry
{
    /// <summary>
    /// User who owns this stream.
    /// </summary>
    public ulong UserID;

    /// <summary>
    /// Stream identifier (e.g., "HeadTransform", "LeftHandTransform").
    /// </summary>
    public int StreamID;

    /// <summary>
    /// Raw stream data.
    /// </summary>
    public byte[] Data;

    public void Encode(BinaryWriter writer)
    {
        writer.Write(UserID);
        writer.Write(StreamID);
        writer.Write(Data.Length);
        writer.Write(Data);
    }

    public static StreamEntry Decode(BinaryReader reader)
    {
        return new StreamEntry
        {
            UserID = reader.ReadUInt64(),
            StreamID = reader.ReadInt32(),
            // Bounded: stream entries are high-frequency tracking/audio payloads;
            // anything past MaxStreamEntryData is malicious or a bug.
            Data = reader.ReadBoundedBytesInt32(NetworkLimits.MaxStreamEntryData)
        };
    }
}
