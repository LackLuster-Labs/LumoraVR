using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Base class for all sync messages.
/// </summary>
public abstract class SyncMessage : IDisposable
{
	public ulong SenderStateVersion { get; set; }
	public ulong SenderSyncTick { get; set; }
	public double SenderTime { get; set; }
	public float SecondsSinceReceived { get; set; }
	public User SenderUser { get; set; }
	public IConnection Sender { get; set; }
	public List<IConnection> Targets { get; } = new();

	public abstract MessageType MessageType { get; }
	public virtual bool Reliable => true;
	public virtual bool Background => false;

	protected SyncMessage(ulong stateVersion, ulong syncTick, IConnection sender = null)
	{
		SenderStateVersion = stateVersion;
		SenderSyncTick = syncTick;
		Sender = sender;
	}

	public void SetSenderTime(double time)
	{
		SenderTime = time;
	}

	public void LinkSenderUser(User user)
	{
		SenderUser = user;
	}

	public abstract byte[] Encode();

	public static SyncMessage Decode(RawInMessage raw)
	{
		using var ms = new MemoryStream(raw.Data, raw.Offset, raw.Length);
		using var reader = new BinaryReader(ms);

		var messageType = (MessageType)reader.ReadByte();
		var stateVersion = reader.Read7BitEncoded();
		var syncTick = reader.Read7BitEncoded();

		SyncMessage message = messageType switch
		{
			MessageType.Delta => BinaryMessageBatch.Decode(raw.Data) as DeltaBatch,
			MessageType.Full => BinaryMessageBatch.Decode(raw.Data) as FullBatch,
			MessageType.Confirmation => BinaryMessageBatch.Decode(raw.Data) as ConfirmationMessage,
			MessageType.Control => ControlMessage.Decode(reader),
			MessageType.Stream => StreamMessage.Decode(reader),
			_ => throw new InvalidOperationException($"Unknown message type: {messageType}")
		};

		message.SenderStateVersion = stateVersion;
		message.SenderSyncTick = syncTick;
		message.Sender = raw.Sender;
		return message;
	}

	public virtual void Dispose()
	{
		Targets.Clear();
	}
}
