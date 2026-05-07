// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

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
        ServerClose,
        WorldSnapshot,
        RequestFullState,

        // Asset transfer protocol
        /// <summary>Client requests an asset by URI from the authority/owner.</summary>
        AssetRequest,
        /// <summary>Sender is about to transmit an asset: contains job ID, URI, total byte count.</summary>
        AssetTransmissionStart,
        /// <summary>One chunk of asset data: job ID, byte offset, chunk bytes.</summary>
        AssetChunk,
        /// <summary>Receiver pulls the next chunk(s).</summary>
        AssetNextChunkRequest,
        /// <summary>Sender cannot provide the requested asset.</summary>
        AssetNotAvailable,
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

        // Bounded read: peer-declared payload length is capped before allocation.
        msg.Payload = reader.ReadBoundedBytes7Bit(NetworkLimits.MaxControlMessagePayload);
        return msg;
    }
}
