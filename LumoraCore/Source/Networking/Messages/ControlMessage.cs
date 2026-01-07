using System.IO;

namespace Lumora.Core.Networking.Messages;

/// <summary>
/// Control message types for session management.
/// </summary>
public enum ControlMessageType : byte
{
    /// <summary>
    /// Client requests to join the session.
    /// </summary>
    JoinRequest = 0,

    /// <summary>
    /// Authority grants join permission and provides initial data.
    /// Contains: UserID, MaxUsers, WorldTime, StateVersion
    /// </summary>
    JoinGrant = 1,

    /// <summary>
    /// Authority signals that full state has been sent, client can start receiving deltas.
    /// </summary>
    JoinStartDelta = 2,

    /// <summary>
    /// User is leaving the session.
    /// </summary>
    Leave = 3,

    /// <summary>
    /// Authority kicked a user.
    /// </summary>
    Kick = 4,

    /// <summary>
    /// Ping/keepalive message.
    /// </summary>
    Ping = 5,

    /// <summary>
    /// Pong response to ping.
    /// </summary>
    Pong = 6
}

/// <summary>
/// Control message for session management.
/// </summary>
public class ControlMessage
{
    public ControlMessageType SubType;
    public byte[] Data;

    public MessageType Type => MessageType.Control;
    public bool Reliable => true;

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)Type);
        writer.Write((byte)SubType);

        if (Data != null && Data.Length > 0)
        {
            writer.Write(Data.Length);
            writer.Write(Data);
        }
        else
        {
            writer.Write(0);
        }

        return ms.ToArray();
    }

    public static ControlMessage Decode(BinaryReader reader)
    {
        var message = new ControlMessage
        {
            SubType = (ControlMessageType)reader.ReadByte()
        };

        int dataLength = reader.ReadInt32();
        if (dataLength > 0)
        {
            message.Data = reader.ReadBytes(dataLength);
        }

        return message;
    }
}

/// <summary>
/// Join grant data sent by authority.
/// </summary>
public struct JoinGrantData
{
    public ulong AssignedUserID;
    public ulong AllocationIDStart;
    public ulong AllocationIDEnd;
    public int MaxUsers;
    public double WorldTime;
    public ulong StateVersion;

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(AssignedUserID);
        writer.Write(AllocationIDStart);
        writer.Write(AllocationIDEnd);
        writer.Write(MaxUsers);
        writer.Write(WorldTime);
        writer.Write(StateVersion);

        return ms.ToArray();
    }

    public static JoinGrantData Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        return new JoinGrantData
        {
            AssignedUserID = reader.ReadUInt64(),
            AllocationIDStart = reader.ReadUInt64(),
            AllocationIDEnd = reader.ReadUInt64(),
            MaxUsers = reader.ReadInt32(),
            WorldTime = reader.ReadDouble(),
            StateVersion = reader.ReadUInt64()
        };
    }
}
