using System.Collections.Generic;
using System.IO;

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
            Data = reader.ReadBytes(reader.ReadInt32())
        };
    }
}
