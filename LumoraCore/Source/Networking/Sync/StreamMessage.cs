using System.IO;
using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// High-frequency stream data message.
/// Used for continuous data like voice, tracking, etc.
/// </summary>
public class StreamMessage : SyncMessage
{
	public const float MAX_AGE_SECONDS = 4f;

	private MemoryStream _memoryStream;

	public override MessageType MessageType => IsAsynchronous ? MessageType.AsyncStream : MessageType.Stream;
	public override bool Reliable => false;

	public bool IsAsynchronous { get; set; }
	public ulong UserID { get; set; }
	public uint StreamStateVersion { get; set; }
	public double StreamTime { get; set; }
	public ushort StreamGroup { get; set; }

	public bool IsOutdated => SecondsSinceReceived >= MAX_AGE_SECONDS;

	public StreamMessage(ulong stateVersion, ulong syncTick, IConnection sender = null)
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

		writer.Write((byte)MessageType);
		writer.Write7BitEncoded(SenderStateVersion);
		writer.Write7BitEncoded(SenderSyncTick);
		writer.Write7BitEncoded(UserID);
		writer.Write7BitEncoded(StreamStateVersion);
		writer.Write(StreamTime);
		writer.Write(StreamGroup);
		writer.Write7BitEncoded((ulong)_memoryStream.Length);

		_memoryStream.Position = 0;
		_memoryStream.CopyTo(output);

		return output.ToArray();
	}

	public static StreamMessage Decode(BinaryReader reader)
	{
		var msg = new StreamMessage(0, 0);
		msg.UserID = reader.Read7BitEncoded();
		msg.StreamStateVersion = (uint)reader.Read7BitEncoded();
		msg.StreamTime = reader.ReadDouble();
		msg.StreamGroup = reader.ReadUInt16();

		var length = (int)reader.Read7BitEncoded();
		var data = reader.ReadBytes(length);
		msg._memoryStream = new MemoryStream(data);

		return msg;
	}

	public override void Dispose()
	{
		base.Dispose();
		_memoryStream?.Dispose();
	}
}
