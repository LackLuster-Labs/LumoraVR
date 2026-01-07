using System.IO;
using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Control message for session management.
/// </summary>
public class ControlMessage : SyncMessage
{
    public enum Message
    {
        JoinRequest,
        JoinGrant,
        JoinStartDelta,
        JoinReject,
        ServerClose
    }

    public override MessageType MessageType => MessageType.Control;
    public override bool Reliable => true;

    public Message ControlMessageType { get; set; }
    public byte[] Payload { get; set; }

    public ControlMessage(Message type, IConnection sender = null)
        : base(0, 0, sender)
    {
        ControlMessageType = type;
    }

    public override byte[] Encode()
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        writer.Write((byte)MessageType);
        writer.Write7BitEncoded(SenderStateVersion);
        writer.Write7BitEncoded(SenderSyncTick);
        writer.Write((byte)ControlMessageType);

        if (Payload != null)
        {
            writer.Write7BitEncoded((ulong)Payload.Length);
            writer.Write(Payload);
        }
        else
        {
            writer.Write7BitEncoded(0UL);
        }

        return output.ToArray();
    }

    public static ControlMessage Decode(BinaryReader reader)
    {
        var type = (Message)reader.ReadByte();
        var msg = new ControlMessage(type);

        var payloadLength = (int)reader.Read7BitEncodedUInt64();
        if (payloadLength > 0)
        {
            msg.Payload = reader.ReadBytes(payloadLength);
        }

        return msg;
    }
}
