using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Networking.Streams;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Sync;

public class SyncController
{
	private Dictionary<RefID, SyncElement> syncElements;
	private List<SyncElement> dirtySyncElements;
	private readonly object _dirtyLock = new object();

	public World Owner { get; private set; }

	public SyncController(World owner)
	{
		Owner = owner;
		syncElements = new Dictionary<RefID, SyncElement>();
		dirtySyncElements = new List<SyncElement>();
	}

	public void RegisterSyncElement(SyncElement element)
	{
		syncElements.Add(element.ReferenceID, element);

		// Log slot name registrations for debugging
		if (element.Parent is Slot parentSlot && element is SyncField<string> sf)
		{
			var memberName = ((ISyncMember)sf).Name;
			if (memberName == "Name")
			{
				AquaLogger.Log($"SyncController.RegisterSyncElement: Slot.Name RefID={element.ReferenceID}, Value='{sf.Value}', ParentSlot={parentSlot.ReferenceID}");
			}
		}
	}

	public void UnregisterSyncElement(SyncElement element)
	{
		syncElements.Remove(element.ReferenceID);
	}

	public void AddDirtySyncElement(SyncElement element)
	{
		if (Owner.State != World.WorldState.Running && !Owner.IsAuthority)
		{
			throw new Exception("Cannot Process Dirty Elements when not Running");
		}
		lock (_dirtyLock)
		{
			dirtySyncElements.Add(element);
		}
	}

	public DeltaBatch CollectDeltaMessages()
	{
		DeltaBatch deltaBatch = new DeltaBatch(Owner.StateVersion, Owner.SyncTick);
		List<SyncElement> elementsToSend;
		lock (_dirtyLock)
		{
			if (dirtySyncElements.Count == 0)
			{
				return deltaBatch;
			}

			elementsToSend = new List<SyncElement>(dirtySyncElements);
			dirtySyncElements.Clear();
		}

		elementsToSend.Sort((SyncElement a, SyncElement b) => a.ReferenceID.CompareTo(b.ReferenceID));
		foreach (SyncElement dirtySyncElement in elementsToSend)
		{
			// Skip invalid or disposed elements - they'll be retried once valid
			if (!dirtySyncElement.IsValid || dirtySyncElement.IsDisposed)
			{
				// Re-add to dirty list to retry later
				if (!dirtySyncElement.IsDisposed)
				{
					AddDirtySyncElement(dirtySyncElement);
				}
				continue;
			}

			BinaryWriter writer = deltaBatch.BeginNewDataRecord(dirtySyncElement.ReferenceID);
			dirtySyncElement.EncodeDelta(writer, deltaBatch);
			deltaBatch.FinishDataRecord(dirtySyncElement.ReferenceID);
		}

		return deltaBatch;
	}

	public FullBatch EncodeFullBatch()
	{
		return EncodeFullBatch(syncElements.Values);
	}

	public FullBatch EncodeFullBatch(IEnumerable<SyncElement> elements)
	{
		FullBatch fullBatch = new FullBatch(Owner.StateVersion, Owner.SyncTick);
		var list = new List<SyncElement>(elements);
		list.Sort((SyncElement a, SyncElement b) => a.ReferenceID.CompareTo(b.ReferenceID));

		AquaLogger.Log($"SyncController.EncodeFullBatch: Encoding {list.Count} sync elements");
		int slotFieldCount = 0, componentFieldCount = 0, otherCount = 0;

		foreach (SyncElement element in list)
		{
			// Count by parent type for summary
			if (element.Parent is Slot parentSlot)
			{
				slotFieldCount++;
				// Log slot name fields specifically
				if (element is SyncField<string> sf)
				{
					var memberName = ((ISyncMember)sf).Name;
					if (memberName == "Name")
					{
						AquaLogger.Log($"  Encoding Slot.Name: RefID={element.ReferenceID}, Value='{sf.Value}', ParentSlot={parentSlot.ReferenceID}");
					}
				}
			}
			else if (element.Parent is Component) componentFieldCount++;
			else otherCount++;

			BinaryWriter writer = fullBatch.BeginNewDataRecord(element.ReferenceID);
			element.EncodeFull(writer, fullBatch, forFullBatch: true);
			fullBatch.FinishDataRecord(element.ReferenceID);
		}

		AquaLogger.Log($"SyncController.EncodeFullBatch: Summary - SlotFields={slotFieldCount}, ComponentFields={componentFieldCount}, Other={otherCount}");

		return fullBatch;
	}

	public void EncodeFull(RefID id, BinaryMessageBatch syncMessage)
	{
		BinaryWriter writer = syncMessage.BeginNewDataRecord(id);
		// Use forFullBatch: true to allow encoding dirty elements when sending corrections.
		// The authority's current state (even if dirty) is the correct state to send.
		syncElements[id].EncodeFull(writer, syncMessage, forFullBatch: true);
		syncMessage.FinishDataRecord(id);
	}

	public void ValidateDeltaMessages(DeltaBatch batch)
	{
		List<ValidationGroup.Rule> list = new List<ValidationGroup.Rule>();
		List<ValidationGroup> list2 = new List<ValidationGroup>();
		for (int i = 0; i < batch.DataRecordCount; i++)
		{
			DataRecord dataRecord = batch.GetDataRecord(i);
			if (syncElements.TryGetValue(dataRecord.TargetID, out var value))
			{
				BinaryReader reader = batch.SeekDataRecord(i);
				MessageValidity messageValidity = value.Validate(batch, reader, list);
				if (messageValidity != MessageValidity.Valid)
				{
					batch.InvalidateDataRecord(i, messageValidity == MessageValidity.Conflict);
				}
				if (list.Count > 0)
				{
					ValidationGroup item = new ValidationGroup();
					item.Set(i, value, list, batch, Owner);
					list2.Add(item);
					list = new List<ValidationGroup.Rule>();
				}
			}
		}
		Dictionary<RefID, int> dictionary = new Dictionary<RefID, int>();
		foreach (ValidationGroup item2 in list2)
		{
			foreach (ValidationGroup.Rule validationRule in item2.ValidationRules)
			{
				RefID otherMessage = validationRule.OtherMessage;
				if (!dictionary.ContainsKey(otherMessage))
				{
					dictionary.Add(otherMessage, batch.FindDataRecordIndex(otherMessage));
				}
			}
		}
		foreach (ValidationGroup item3 in list2)
		{
			bool flag = batch.GetDataRecord(item3.RequestingRecordIndex).Validity == MessageValidity.Conflict;
			List<int> list3 = new List<int>();
			foreach (ValidationGroup.Rule validationRule2 in item3.ValidationRules)
			{
				int num = dictionary[validationRule2.OtherMessage];
				if (num < 0)
				{
					if (validationRule2.MustExist)
					{
						flag = true;
					}
					continue;
				}
				DataRecord dataRecord2 = batch.GetDataRecord(num);
				list3.Add(num);
				if (dataRecord2.Validity == MessageValidity.Conflict)
				{
					flag = true;
				}
				if (validationRule2.CustomValidation != null)
				{
					BinaryReader arg = batch.SeekDataRecord(num);
					if (!validationRule2.CustomValidation(arg))
					{
						flag = true;
					}
				}
			}
			if (!flag)
			{
				continue;
			}
			batch.InvalidateDataRecord(item3.RequestingRecordIndex, conflict: true);
			item3.RequestingSyncElement.Invalidate();
			foreach (int item4 in list3)
			{
				batch.InvalidateDataRecord(item4, conflict: true);
				DataRecord dataRecord3 = batch.GetDataRecord(item4);
				if (syncElements.TryGetValue(dataRecord3.TargetID, out var value2))
				{
					value2.Invalidate();
				}
			}
		}
	}

	public void ApplyConfirmations(IEnumerable<RefID> ids, ulong confirmTime)
	{
		foreach (RefID id in ids)
		{
			if (syncElements.TryGetValue(id, out var value))
			{
				value.Confirm(confirmTime);
			}
		}
	}

	public bool DecodeDeltaMessage(int recordIndex, DeltaBatch batch)
	{
		return DecodeBinaryMessage(recordIndex, batch, isFull: false);
	}

	public bool DecodeFullMessage(int recordIndex, FullBatch batch)
	{
		return DecodeBinaryMessage(recordIndex, batch, isFull: true);
	}

	public bool DecodeCorrection(int recordIndex, ConfirmationMessage batch)
	{
		return DecodeBinaryMessage(recordIndex, batch, isFull: true);
	}

	private bool DecodeBinaryMessage(int recordIndex, BinaryMessageBatch message, bool isFull)
	{
		DataRecord dataRecord = message.GetDataRecord(recordIndex);
		if (syncElements.TryGetValue(dataRecord.TargetID, out var value))
		{
			BinaryReader reader = message.SeekDataRecord(recordIndex);
			if (!isFull)
			{
				value.DecodeDelta(reader, message);
			}
			else
			{
				value.DecodeFull(reader, message);

				// Log slot name decode for debugging
				if (value.Parent is Slot parentSlot && value is SyncField<string> sf)
				{
					var memberName = ((ISyncMember)sf).Name;
					if (memberName == "Name")
					{
						AquaLogger.Log($"SyncController.DecodeFullMessage: Decoded Slot.Name RefID={value.ReferenceID}, Value='{sf.Value}', ParentSlot={parentSlot.ReferenceID}");
					}
				}
			}
			return true;
		}

		AquaLogger.Warn($"SyncController.DecodeBinaryMessage: Element not found for RefID={dataRecord.TargetID}");
		return false;
	}

	/// <summary>
	/// Gather stream data from local user's streams into messages.
	/// Called by SessionSyncManager during the sync loop.
	/// </summary>
	public void GatherStreams(List<StreamMessage> messages)
	{
		if (Owner == null)
			return;

		var localUser = Owner.LocalUser;
		if (localUser == null || !localUser.IsLocal)
			return;

		var syncTick = Owner.SyncTick;

		// Iterate through all stream groups
		foreach (var group in localUser.StreamGroupManager.Groups)
		{
			bool hasData = false;
			using var dataStream = new MemoryStream();
			using var writer = new BinaryWriter(dataStream);

			// Check each stream in the group
			foreach (var stream in group.Streams)
			{
				if (!stream.Active)
					continue;

				// Check if this stream should send data
				bool shouldSend = stream.IsImplicitUpdatePoint(syncTick) ||
				                  stream.IsExplicitUpdatePoint(syncTick);

				if (shouldSend && stream.HasValidData)
				{
					// Write stream RefID and encode data
					writer.Write((ulong)stream.ReferenceID);
					stream.Encode(writer);
					hasData = true;
				}
			}

			// Create message if we have data
			if (hasData)
			{
				var message = new StreamMessage(Owner.StateVersion, syncTick)
				{
					UserID = (ulong)localUser.ReferenceID,
					StreamStateVersion = localUser.StreamConfigurationVersion,
					StreamTime = Owner.TotalTime,
					StreamGroup = group.GroupIndex
				};

				// Copy the data to the message
				dataStream.Position = 0;
				var msgData = message.GetData();
				dataStream.CopyTo(msgData);

				messages.Add(message);
			}
		}
	}

	/// <summary>
	/// Apply received stream data to remote user's streams.
	/// Called by SessionSyncManager when a StreamMessage is received.
	/// </summary>
	public void ApplyStreams(StreamMessage message)
	{
		if (message == null || message.IsOutdated)
			return;

		// Find the user that owns these streams
		var userElement = Owner?.ReferenceController?.GetObjectOrNull(new RefID(message.UserID));
		if (userElement is not User user)
		{
			AquaLogger.Warn($"ApplyStreams: User not found for ID {message.UserID}");
			return;
		}

		// Don't apply streams to local user
		if (user.IsLocal)
			return;

		// Check stream configuration version
		if (message.StreamStateVersion < user.StreamConfigurationVersion)
		{
			return;
		}

		// Get the stream group
		var group = user.StreamGroupManager.GetGroup(message.StreamGroup);
		if (group == null)
		{
			return;
		}

		// Read and apply stream data
		var data = message.GetData();
		using var reader = new BinaryReader(data);

		while (data.Position < data.Length)
		{
			try
			{
				// Read stream RefID
				var streamRefID = new RefID(reader.ReadUInt64());

				// Find the stream
				var streamElement = Owner?.ReferenceController?.GetObjectOrNull(streamRefID);
				if (streamElement is IStream stream && stream.Active)
				{
					stream.Decode(reader, message);
				}
				else
				{
					AquaLogger.Warn($"ApplyStreams: Stream {streamRefID} not found or inactive");
					break; // Can't continue if we don't know the stream's data format
				}
			}
			catch (EndOfStreamException)
			{
				break; // End of data
			}
			catch (Exception ex)
			{
				AquaLogger.Error($"ApplyStreams: Error decoding stream data: {ex.Message}");
				break;
			}
		}
	}

	public void Dispose()
	{
		syncElements.Clear();
		lock (_dirtyLock)
		{
			dirtySyncElements.Clear();
		}
	}
}
