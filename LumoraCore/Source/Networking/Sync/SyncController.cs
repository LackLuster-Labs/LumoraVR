using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Logging;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Controls synchronization of world state.
/// Collects dirty elements and generates batches for transmission.
/// </summary>
public class SyncController : IDisposable
{
    private List<SyncElement> _dirtySyncElements = new();
    private volatile bool _collectingDeltaMessages;
    private DateTime _lastStreamMessageTime;

    private static readonly Comparison<SyncElement> _compareSyncElements = CompareSyncElements;

    public World World { get; private set; }

    /// <summary>
    /// Currently decoding stream (for reference resolution during decode).
    /// </summary>
    public object CurrentlyDecodingStream { get; set; }

    public SyncController(World world)
    {
        World = world;
    }

    /// <summary>
    /// Add a dirty sync element to be synchronized.
    /// Called by SyncElement.InvalidateSyncElement().
    /// </summary>
    public void AddDirtySyncElement(SyncElement element)
    {
        if (World.State != World.WorldState.Running && !World.IsAuthority)
        {
            Logger.Warn("SyncController: Cannot add dirty elements when not running");
            return;
        }

        if (_collectingDeltaMessages)
        {
            throw new InvalidOperationException("Currently collecting delta messages, cannot register new sync element!");
        }

        if (element.IsLocalElement)
        {
            throw new InvalidOperationException($"Cannot register local element as dirty: {element}");
        }

        _dirtySyncElements.Add(element);
    }

    /// <summary>
    /// Collect all dirty elements into a delta batch.
    /// Called by sync thread after world update.
    /// </summary>
    public DeltaBatch CollectDeltaMessages()
    {
        var deltaBatch = new DeltaBatch(World.StateVersion, World.SyncTick);
        deltaBatch.SetSenderTime(World.TotalTime);

        _collectingDeltaMessages = true;

        try
        {
            // Sort by RefID for deterministic ordering
            _dirtySyncElements.Sort(_compareSyncElements);

            foreach (var element in _dirtySyncElements)
            {
                if (element.IsDisposed)
                    continue;

				try
				{
					var writer = deltaBatch.BeginNewDataRecord(element.ReferenceID);
					element.EncodeDelta(writer, deltaBatch);
					deltaBatch.FinishDataRecord(element.ReferenceID);
				}
				catch (Exception ex)
				{
					Logger.Error($"SyncController: Failed to encode delta for {element.ReferenceID}: {ex.Message}");
				}
			}

            _dirtySyncElements.Clear();
        }
        finally
        {
            _collectingDeltaMessages = false;
        }

        if (deltaBatch.DataRecordCount > 0)
        {
            Logger.Debug($"SyncController: Collected {deltaBatch.DataRecordCount} dirty elements");
        }
        return deltaBatch;
    }

    /// <summary>
    /// Encode full state for all non-local sync elements.
    /// Used when initializing new users.
    /// </summary>
    public FullBatch EncodeFullBatch()
    {
        var elements = new List<SyncElement>();

        foreach (var kvp in World.GetAllElements())
        {
            if (kvp.Value is SyncElement syncElement && !syncElement.IsLocalElement)
            {
                elements.Add(syncElement);
            }
        }

        return EncodeFullBatch(elements);
    }

    /// <summary>
    /// Encode full state for specific elements.
    /// </summary>
    public FullBatch EncodeFullBatch(List<SyncElement> elements)
    {
        var fullBatch = new FullBatch(World.StateVersion, World.SyncTick);
        fullBatch.SetSenderTime(World.TotalTime);

        // Sort by RefID for deterministic ordering
        elements.Sort(_compareSyncElements);

        foreach (var element in elements)
        {
            if (element.IsLocalElement)
            {
                Logger.Warn($"SyncController: Cannot encode local element in full batch: {element.ReferenceID}");
                continue;
            }

			try
			{
				var writer = fullBatch.BeginNewDataRecord(element.ReferenceID);
				element.EncodeFull(writer, fullBatch);
				fullBatch.FinishDataRecord(element.ReferenceID);
			}
			catch (Exception ex)
			{
				Logger.Error($"SyncController: Failed to encode full for {element.ReferenceID}: {ex.Message}");
			}
		}

        return fullBatch;
    }

	/// <summary>
	/// Encode full state for a single element into an existing batch.
	/// Used for corrections.
	/// </summary>
	public void EncodeFull(RefID refID, BinaryMessageBatch batch)
	{
		var element = World.FindElement(refID) as SyncElement;
		if (element == null)
		{
			Logger.Warn($"SyncController: Element not found for EncodeFull: {refID}");
			return;
		}

        var writer = batch.BeginNewDataRecord(refID);
        element.EncodeFull(writer, batch);
        batch.FinishDataRecord(refID);
    }

    // ===== DECODING =====

    /// <summary>
    /// Decode a delta message record.
    /// </summary>
    public bool DecodeDeltaMessage(int recordIndex, DeltaBatch batch)
    {
        return DecodeBinaryMessage(recordIndex, batch, isFull: false);
    }

    /// <summary>
    /// Decode a full message record.
    /// </summary>
    public bool DecodeFullMessage(int recordIndex, FullBatch batch)
    {
        return DecodeBinaryMessage(recordIndex, batch, isFull: true);
    }

    /// <summary>
    /// Decode a correction record.
    /// </summary>
    public bool DecodeCorrection(int recordIndex, ConfirmationMessage batch)
    {
        return DecodeBinaryMessage(recordIndex, batch, isFull: true);
    }

    private bool DecodeBinaryMessage(int recordIndex, BinaryMessageBatch message, bool isFull)
    {
        var dataRecord = message.GetDataRecord(recordIndex);
        var element = World.FindElement(dataRecord.TargetID) as SyncElement;

        if (element == null)
        {
            Logger.Warn($"SyncController: Element not found for decode: {dataRecord.TargetID}");
            return false;
        }

        var reader = message.SeekDataRecord(recordIndex);

        try
        {
            if (isFull)
                element.DecodeFull(reader, message);
            else
                element.DecodeDelta(reader, message);

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"SyncController: Exception decoding element {dataRecord.TargetID}: {ex.Message}");
            throw;
        }
    }

    // ===== VALIDATION =====

    /// <summary>
    /// Validate delta messages from a client.
    /// Authority-only operation.
    /// </summary>
    public void ValidateDeltaMessages(DeltaBatch batch)
    {
        var rules = new List<ValidationRule>();

        for (int i = 0; i < batch.DataRecordCount; i++)
        {
            var dataRecord = batch.GetDataRecord(i);
            var element = World.FindElement(dataRecord.TargetID) as SyncElement;

            if (element == null)
            {
                batch.InvalidateDataRecord(i, conflict: false);
                continue;
            }

            var reader = batch.SeekDataRecord(i);
            var validity = element.Validate(batch, reader, rules);

            if (validity != MessageValidity.Valid)
            {
                batch.InvalidateDataRecord(i, validity == MessageValidity.Conflict);
            }

            rules.Clear();
        }
    }

	/// <summary>
	/// Apply confirmations from authority.
	/// </summary>
	public void ApplyConfirmations(IEnumerable<RefID> ids, ulong confirmTime)
	{
		foreach (var id in ids)
		{
			var element = World.FindElement(id) as SyncElement;
			element?.Confirm(confirmTime);
		}
	}

    // ===== STREAMS (high-frequency data) =====

    /// <summary>
    /// Gather stream messages for transmission.
    /// </summary>
    public void GatherStreams(List<StreamMessage> messages)
    {
        // TODO: Implement stream gathering when Stream system is built
        // For now, just send keepalive if authority

        if (World.IsAuthority && (DateTime.UtcNow - _lastStreamMessageTime).TotalSeconds >= 0.5)
        {
            _lastStreamMessageTime = DateTime.UtcNow;
            // Send keepalive stream
        }
    }

    /// <summary>
    /// Apply incoming stream message.
    /// </summary>
    public void ApplyStreams(StreamMessage message)
    {
        // TODO: Implement stream application when Stream system is built
    }

    // ===== HELPERS =====

	private static int CompareSyncElements(SyncElement a, SyncElement b)
	{
		return a.ReferenceID.CompareTo(b.ReferenceID);
	}

    public void Dispose()
    {
        _dirtySyncElements.Clear();
        World = null;
    }
}
