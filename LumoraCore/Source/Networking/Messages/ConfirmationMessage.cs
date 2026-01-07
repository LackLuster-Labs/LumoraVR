using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Messages;

/// <summary>
/// Confirms or corrects client delta changes.
/// </summary>
public class ConfirmationMessage
{
    public MessageType Type => MessageType.Confirmation;
    public bool Reliable => true;

    /// <summary>
    /// Authority's current state version after applying validated changes.
    /// </summary>
    public ulong AuthorityStateVersion { get; set; }

    /// <summary>
    /// State version that the client's delta claimed to be based on.
    /// Used for detecting conflicts.
    /// </summary>
    public ulong ClientStateVersion { get; set; }

    /// <summary>
    /// Records of confirmations or corrections.
    /// </summary>
    public List<ConfirmationRecord> Records { get; set; } = new();

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)Type);
        writer.Write(AuthorityStateVersion);
        writer.Write(ClientStateVersion);
        writer.Write(Records.Count);

		foreach (var record in Records)
		{
			writer.WriteRefID(record.TargetID);
			writer.Write(record.MemberIndex);
			writer.Write(record.Accepted);

            if (!record.Accepted)
            {
                // Write corrected value
                writer.Write(record.CorrectedData.Length);
                writer.Write(record.CorrectedData);
                writer.Write(record.RejectionReason ?? "");
            }
        }

        return ms.ToArray();
    }

    public static ConfirmationMessage Decode(BinaryReader reader)
    {
        var message = new ConfirmationMessage
        {
            AuthorityStateVersion = reader.ReadUInt64(),
            ClientStateVersion = reader.ReadUInt64()
        };

		int recordCount = reader.ReadInt32();
		for (int i = 0; i < recordCount; i++)
		{
			var record = new ConfirmationRecord
			{
				TargetID = reader.ReadRefID(),
				MemberIndex = reader.ReadInt32(),
				Accepted = reader.ReadBoolean()
			};

            if (!record.Accepted)
            {
                int dataLength = reader.ReadInt32();
                record.CorrectedData = reader.ReadBytes(dataLength);
                record.RejectionReason = reader.ReadString();
            }

            message.Records.Add(record);
        }

        return message;
    }
}

/// <summary>
/// Single confirmation or correction for a sync member change.
/// </summary>
	public class ConfirmationRecord
	{
	/// <summary>
	/// RefID of the element (User, Slot, Component) being confirmed.
	/// </summary>
	public RefID TargetID { get; set; }

    /// <summary>
    /// Index of the sync member within the element.
    /// </summary>
    public int MemberIndex { get; set; }

    /// <summary>
    /// Whether the authority accepted this change.
    /// </summary>
    public bool Accepted { get; set; }

    /// <summary>
    /// If rejected, the corrected value from authority.
    /// </summary>
    public byte[] CorrectedData { get; set; }

    /// <summary>
    /// Human-readable reason for rejection (for debugging/logging).
    /// </summary>
    public string RejectionReason { get; set; }
}
