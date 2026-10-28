// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Networking.Streams;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Sync;

public class SyncController
{
	private RefIdMap<SyncElement> syncElements;
	private List<SyncElement> dirtySyncElements;
	private readonly object _dirtyLock = new object();

	// Scratch buffers for the sync loop, which is the only caller of the methods that use them and always
	// runs on one thread. Cleared per use, kept so the per-tick copies stop allocating. -xlinka
	private readonly List<SyncElement> _dirtyScratch = new List<SyncElement>();
	private readonly MemoryStream _streamScratch = new MemoryStream();
	private readonly BinaryWriter _streamScratchWriter;

	public World Owner { get; private set; }

	public SyncController(World owner)
	{
		Owner = owner;
		syncElements = new RefIdMap<SyncElement>();
		dirtySyncElements = new List<SyncElement>();
		_streamScratchWriter = new BinaryWriter(_streamScratch);
	}

	public void RegisterSyncElement(SyncElement element)
	{
		syncElements.Add(element.ReferenceID, element);

		// Guarded up front: this runs for every sync member ever created, and even the type probes
		// plus the interpolated string add up to real join-time cost when debug is off. -xlinka
		if (LumoraLogger.EnableDebug && element.Parent is Slot parentSlot && element is SyncField<string> sf)
		{
			var memberName = ((ISyncMember)sf).Name;
			if (memberName == "Name")
			{
				LumoraLogger.Debug($"SyncController.RegisterSyncElement: Slot.Name RefID={element.ReferenceID}, Value='{sf.Value}', ParentSlot={parentSlot.ReferenceID}");
			}
		}
	}

	// Look up a registered sync element by RefID. Used by the targeted-resync path, which is handed RefIDs off
	// the wire and needs the elements to hand to EncodeFullBatch.
	public bool TryGetElement(RefID id, out SyncElement element)
	{
		return syncElements.TryGetValue(id, out element!);
	}

	public void UnregisterSyncElement(SyncElement element)
	{
		syncElements.Remove(element.ReferenceID);
	}

	// Set by the sync loop while this world hosts nobody, cleared the moment a peer connects.
	internal volatile bool DropFieldDeltas;

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

	// Null when nothing is dirty: an idle tick used to build (and immediately dispose) a batch, and a
	// DeltaBatch carries a MemoryStream and a BinaryWriter. That is per tick, per world, forever. -xlinka
	// anyReceiver false: nobody is connected to take the batch, so a plain value's delta is let go unencoded
	// (DropDelta) and only what keeps state between deltas is encoded, for that state to move on.
	public DeltaBatch? CollectDeltaMessages(bool anyReceiver = true)
	{
		var elementsToSend = _dirtyScratch;
		lock (_dirtyLock)
		{
			if (dirtySyncElements.Count == 0)
			{
				return null;
			}

			elementsToSend.Clear();
			elementsToSend.AddRange(dirtySyncElements);
			dirtySyncElements.Clear();
		}

		DeltaBatch deltaBatch = new DeltaBatch(Owner.StateVersion, Owner.SyncTick);
		if (anyReceiver)
			elementsToSend.Sort((SyncElement a, SyncElement b) => a.ReferenceID.CompareTo(b.ReferenceID));
		for (int i = 0; i < elementsToSend.Count; i++)
		{
			var dirtySyncElement = elementsToSend[i];

			if (!anyReceiver && dirtySyncElement.DeltaIsCurrentValue && dirtySyncElement.IsValid && !dirtySyncElement.IsDisposed)
			{
				dirtySyncElement.DropDelta();
				continue;
			}

			// Skip invalid or disposed elements - they'll be retried once valid
			if (!dirtySyncElement.IsValid || dirtySyncElement.IsDisposed)
			{
				if (!dirtySyncElement.IsDisposed)
				{
					AddDirtySyncElement(dirtySyncElement);
				}
				continue;
			}

			BinaryWriter writer = deltaBatch.BeginNewDataRecord(dirtySyncElement.ReferenceID);
			try
			{
				dirtySyncElement.EncodeDelta(writer, deltaBatch);
				deltaBatch.FinishDataRecord(dirtySyncElement.ReferenceID);
				if (dirtySyncElement.DeltaIsCurrentValue && dirtySyncElement.StampDelta(Owner.SyncTick))
					deltaBatch.MarkLastRecordCoalescable();
			}
			catch (Exception ex)
			{
				// One element that can't be serialized - e.g. a guest whose local change touched a slot
				// collection it isn't allowed to replicate - must NOT throw out of here and take the whole
				// delta batch (every other user's pending changes) down with it / stall the sync loop. Drop
				// just this record and keep going. The permission layer already logged the reason (deduped),
				// so this stays at Debug. -xlinka
				deltaBatch.CancelDataRecord();
				LumoraLogger.Debug($"SyncController.CollectDeltaMessages: skipped {dirtySyncElement.GetType().Name} RefID={dirtySyncElement.ReferenceID} - {ex.Message}");
			}
		}

		elementsToSend.Clear();
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

		bool debugLog = LumoraLogger.EnableDebug;
		if (debugLog)
			LumoraLogger.Debug($"SyncController.EncodeFullBatch: Encoding {list.Count} sync elements");
		int slotFieldCount = 0, componentFieldCount = 0, otherCount = 0;

		foreach (SyncElement element in list)
		{
			// Local elements (LOCAL_BYTE RefIDs) live on one machine only and never replicate. They
			// must NOT go into a full batch: a joining peer has no way to resolve a Local RefID from
			// our machine, so it logs NOT_FOUND and, worse, the half-written record knocks the rest of
			// the stream out of alignment and the whole world fails to decode. The delta path already
			// skips them (InvalidateSyncElement), but this full-state path never did. A slot subtree
			// built with AddLocalSlot (e.g. a per-viewer pointer cursor) is exactly the kind of thing
			// that used to slip through here. -xlinka
			if (element.IsLocalElement)
				continue;

			if (element.Parent is Slot parentSlot)
			{
				slotFieldCount++;
				if (debugLog && element is SyncField<string> sf)
				{
					var memberName = ((ISyncMember)sf).Name;
					if (memberName == "Name")
					{
						LumoraLogger.Debug($"  Encoding Slot.Name: RefID={element.ReferenceID}, Value='{sf.Value}', ParentSlot={parentSlot.ReferenceID}");
					}
				}
			}
			else if (element.Parent is Component) componentFieldCount++;
			else otherCount++;

			EncodeFullRecord(element, fullBatch);
		}

		if (debugLog)
			LumoraLogger.Debug($"SyncController.EncodeFullBatch: Summary - SlotFields={slotFieldCount}, ComponentFields={componentFieldCount}, Other={otherCount}");

		return fullBatch;
	}

	internal void EncodeFullRecord(SyncElement element, FullBatch fullBatch)
	{
		BinaryWriter writer = fullBatch.BeginNewDataRecord(element.ReferenceID);
		try
		{
			element.EncodeFull(writer, fullBatch, forFullBatch: true);
			fullBatch.FinishDataRecord(element.ReferenceID);
		}
		catch (Exception ex)
		{
			// One element that throws while encoding must not abort the entire world's full state
			// for every joiner. Drop just this record (rewinds the stream) and keep going. -xlinka
			fullBatch.CancelDataRecord();
			LumoraLogger.Error($"SyncController.EncodeFullBatch: skipped {element.GetType().Name} RefID={element.ReferenceID} - encode threw: {ex.Message}");
		}
	}

	// Every element a joiner gets, as the world stands now, in RefID order: the order a full batch has
	// always gone out in, owners ahead of what they own. Sorted on the raw ids, a primitive sort instead of
	// a comparison call per pair over tens of thousands of elements. -xlinka
	internal (ulong[] Keys, SyncElement[] Elements) SnapshotReplicated()
	{
		int count = 0;
		foreach (var element in syncElements.Values)
		{
			if (!element.IsLocalElement)
				count++;
		}
		var keys = new ulong[count];
		var elements = new SyncElement[count];
		int at = 0;
		foreach (var element in syncElements.Values)
		{
			if (element.IsLocalElement)
				continue;
			keys[at] = element.ReferenceID;
			elements[at] = element;
			at++;
		}
		Array.Sort(keys, elements);
		return (keys, elements);
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
		// Scoped for the whole pass so a validator can ask what ELSE this batch carries. The grab rule needs
		// it: a pickup authors the holder claim and the reparent as one batch, and every record is judged
		// before any is decoded, so the reparent has to be able to see the claim sitting next to it. Cleared
		// in a finally - a stale batch here would let the next pass read a disposed stream. -xlinka
		Owner.ValidatingBatch = batch;
		try
		{
			ValidateDeltaRecords(batch);
		}
		finally
		{
			Owner.ValidatingBatch = null;
		}
	}

	private void ValidateDeltaRecords(DeltaBatch batch)
	{
		List<ValidationGroup.Rule> list = new List<ValidationGroup.Rule>();
		List<ValidationGroup> list2 = new List<ValidationGroup>();
		for (int i = 0; i < batch.DataRecordCount; i++)
		{
			DataRecord dataRecord = batch.GetDataRecord(i);
			if (syncElements.TryGetValue(dataRecord.TargetID, out var value))
			{
				BinaryReader reader = batch.SeekDataRecord(i);
				MessageValidity messageValidity;
				try
				{
					messageValidity = value.Validate(batch, reader, list);
				}
				catch (Exception ex)
				{
					// A validator reads the inbound record to decide, so a truncated or hostile record can
					// make it throw. That must cost the sender its one record, not the whole batch and
					// everyone else's changes in it. Treat it as a conflict: the record is dropped and the
					// authority answers with its own current value. -xlinka
					messageValidity = MessageValidity.Conflict;
					list.Clear();
					LumoraLogger.Debug($"SyncController.ValidateDeltaMessages: {value.GetType().Name} RefID={dataRecord.TargetID} validator threw, refusing record - {ex.Message}");
				}
				if (messageValidity != MessageValidity.Valid)
				{
					batch.InvalidateDataRecord(i, messageValidity == MessageValidity.Conflict);
				}
				batch.MarkDataRecordValidated(i);
				if (list.Count > 0)
				{
					ValidationGroup item = new ValidationGroup();
					item.Set(i, value, list, batch, Owner);
					list2.Add(item);
					list = new List<ValidationGroup.Rule>();
				}
			}
			else if (Owner.IsAuthority && !InSendersByte(batch, dataRecord.TargetID))
			{
				// Nothing to judge it against, and nothing in this batch can create it: the sender only
				// creates in its own byte. A write aimed at an id the authority or someone else has not
				// minted yet would otherwise wait in the pending queue and land unjudged the moment that
				// id is born. Invalid, not Conflict, there is no element to answer with. -xlinka
				batch.InvalidateDataRecord(i, conflict: false);
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

	// Members of an element a network decode just created. They load until the batch that created them
	// has applied, so the receiver's own initialisation neither dirties them nor asks the gate about them,
	// and are then released, except any whose record is still pending. The flag used to clear only when a
	// record for that exact member decoded, and a member the creator never touched after init never gets
	// one: it stayed loading for good, and a loading member skips the gate on a local write and never
	// replicates. Every user record on the host was stuck that way. -xlinka
	private readonly List<SyncElement> _heldLoading = new();
	private readonly object _heldLoadingLock = new();

	internal void HoldLoading(SyncElement element)
	{
		element.IsLoading = true;
		lock (_heldLoadingLock)
			_heldLoading.Add(element);
	}

	internal void ReleaseHeldLoading(Func<RefID, bool> isPending)
	{
		lock (_heldLoadingLock)
		{
			int kept = 0;
			for (int i = 0; i < _heldLoading.Count; i++)
			{
				var element = _heldLoading[i];
				if (element.IsDisposed || !element.IsLoading)
					continue;
				if (isPending(element.ReferenceID))
				{
					_heldLoading[kept++] = element;
					continue;
				}
				element.IsLoading = false;
			}
			_heldLoading.RemoveRange(kept, _heldLoading.Count - kept);
		}
	}

	// Where the sender may have minted it: its own byte, below that byte's script fence.
	private bool InSendersByte(BinaryMessageBatch batch, in RefID id)
		=> batch.SenderUser != null && id.GetUserByte() == batch.SenderUser.AllocationID.Value
			&& Owner.ReferenceController?.BelowScriptFence(id) != false;

	// Judges a record the batch pass could not, a member of something the same batch creates, once its
	// target exists and before it decodes. Same validator, same cross-record rules, same sender. A rule
	// that fails refuses this record alone: whatever it points at may already have been applied. -xlinka
	public MessageValidity ValidateLateRecord(DeltaBatch batch, int index)
	{
		var record = batch.GetDataRecord(index);
		if (!syncElements.TryGetValue(record.TargetID, out var element))
			return MessageValidity.Invalid;

		var rules = new List<ValidationGroup.Rule>();
		Owner.ValidatingBatch = batch;
		try
		{
			var validity = element.Validate(batch, batch.SeekDataRecord(index), rules);
			if (validity != MessageValidity.Valid)
				return validity;

			foreach (var rule in rules)
			{
				int other = batch.FindDataRecordIndex(rule.OtherMessage);
				if (other < 0)
				{
					if (rule.MustExist)
						return MessageValidity.Conflict;
					continue;
				}
				if (batch.GetDataRecord(other).Validity == MessageValidity.Conflict)
					return MessageValidity.Conflict;
				if (rule.CustomValidation != null && !rule.CustomValidation(batch.SeekDataRecord(other)))
					return MessageValidity.Conflict;
			}
			return MessageValidity.Valid;
		}
		catch (Exception ex)
		{
			LumoraLogger.Debug($"SyncController.ValidateLateRecord: {element.GetType().Name} RefID={record.TargetID} validator threw, refusing record - {ex.Message}");
			return MessageValidity.Conflict;
		}
		finally
		{
			Owner.ValidatingBatch = null;
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

				if (LumoraLogger.EnableDebug && value.Parent is Slot parentSlot && value is SyncField<string> sf)
				{
					var memberName = ((ISyncMember)sf).Name;
					if (memberName == "Name")
					{
						LumoraLogger.Debug($"SyncController.DecodeFullMessage: Decoded Slot.Name RefID={value.ReferenceID}, Value='{sf.Value}', ParentSlot={parentSlot.ReferenceID}");
					}
				}
			}
			return true;
		}

		if (LumoraLogger.EnableDebug)
			LumoraLogger.Debug($"SyncController.DecodeBinaryMessage: Element not found for RefID={dataRecord.TargetID}");
		return false;
	}

	// Called by SessionSyncManager during the sync loop.
	public void GatherStreams(List<StreamMessage> messages)
	{
		if (Owner == null)
			return;

		var localUser = Owner.LocalUser;
		if (localUser == null || !localUser.IsLocal)
			return;

		var syncTick = Owner.SyncTick;

		foreach (var stream in localUser.Streams)
		{
			if (stream.Active && !localUser.StreamGroupManager.ContainsStream(stream))
			{
				localUser.StreamGroupManager.AssignToGroup(stream, null);
			}
		}

		foreach (var group in localUser.StreamGroupManager.Groups)
		{
			bool hasData = false;
			// One scratch stream for every group, every tick. It used to be a fresh MemoryStream +
			// BinaryWriter per group, copied into the message through CopyTo (which rents its own 80KB
			// buffer). Now the encoded bytes go straight from the scratch buffer into the message. -xlinka
			var dataStream = _streamScratch;
			var writer = _streamScratchWriter;
			dataStream.SetLength(0);
			dataStream.Position = 0;

			foreach (var stream in group.Streams)
			{
				if (!stream.Active)
					continue;

				bool shouldSend = stream.IsImplicitUpdatePoint(syncTick) ||
				                  stream.IsExplicitUpdatePoint(syncTick);

				// For local streams (sending), we always send if shouldSend is true.
				// HasValidData is for RECEIVING to filter out invalid remote data.
				// Local streams should send their current value regardless.
				if (shouldSend)
				{
					writer.Write((ulong)stream.ReferenceID);
					stream.Encode(writer);
					hasData = true;
				}
			}

			if (hasData)
			{
				var message = new StreamMessage(Owner.StateVersion, syncTick)
				{
					UserID = (ulong)localUser.ReferenceID,
					StreamStateVersion = localUser.StreamConfigurationVersion,
					StreamTime = Owner.TotalTime,
					StreamGroup = group.GroupIndex
				};

				writer.Flush();
				int length = (int)dataStream.Length;
				var msgData = message.GetData();
				if (dataStream.TryGetBuffer(out var buffer))
					msgData.Write(buffer.Array!, buffer.Offset, length);
				else
					msgData.Write(dataStream.ToArray(), 0, length);

				messages.Add(message);
			}
		}

		_streamScratch.SetLength(0);
	}

	// Called by SessionSyncManager when a StreamMessage arrives.
	public void ApplyStreams(StreamMessage message)
	{
		if (message == null || message.IsOutdated)
			return;

		var userElement = Owner?.ReferenceController?.GetObjectOrNull(new RefID(message.UserID));
		if (userElement is not User user)
		{
			LumoraLogger.Warn($"ApplyStreams: User not found for ID {message.UserID}");
			return;
		}

		// Don't apply streams to local user
		if (user.IsLocal)
			return;

		if (message.StreamStateVersion < user.StreamConfigurationVersion)
		{
			return;
		}

		var data = message.GetData();
		using var reader = new BinaryReader(data);

		while (data.Position < data.Length)
		{
			try
			{
				var streamRefID = new RefID(reader.ReadUInt64());

				var streamElement = Owner?.ReferenceController?.GetObjectOrNull(streamRefID);
				if (streamElement is IStream stream && stream.Active)
				{
					// Defense in depth: every stream's actual Owner must equal the user the
					// message claims to be from. A peer could otherwise embed RefIDs of
					// streams owned by a different user - even if the sender check upstream
					// passes for *their own* UserID - and inject data attributed to others.
					if (stream.Owner != user)
					{
						LumoraLogger.Warn($"ApplyStreams: stream {streamRefID} is owned by {stream.Owner?.UserName?.Value} but message claims user {user.UserName?.Value} ({message.UserID}); dropping rest of message.");
						break;
					}

					stream.Decode(reader, message);
				}
				else
				{
					// A sender whose streams changed after the last change we hold is ahead of us: the
					// delta that adds or activates this stream is still on its way, and the next sample
					// replaces this one anyway. -xlinka
					if (message.StreamStateVersion > user.StreamConfigurationVersion)
						LumoraLogger.Debug($"ApplyStreams: Stream {streamRefID} not here yet");
					else
						LumoraLogger.Warn($"ApplyStreams: Stream {streamRefID} not found or inactive");
					break; // Can't continue if we don't know the stream's data format
				}
			}
			catch (EndOfStreamException)
			{
				break; // End of data
			}
			catch (Exception ex)
			{
				LumoraLogger.Error($"ApplyStreams: Error decoding stream data: {ex.Message}");
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
		_dirtyScratch.Clear();
		// Left undisposed on purpose: the sync thread can still be mid-GatherStreams when a world tears
		// down, and an ObjectDisposedException there is worse than letting the GC take a MemoryStream.
		_streamScratch.SetLength(0);
	}
}

