using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Networking;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Base class for batched binary messages (Delta, Full, Confirmation).
/// Manages multiple data records for different sync elements.
/// </summary>
public abstract class BinaryMessageBatch : SyncMessage
{
    private MemoryStream _stream;
    private BinaryWriter _writer;
    private BinaryReader? _reader;
    private List<DataRecord> _dataRecords = new();
    private int _currentRecordIndex = -1;

    /// <summary>
    /// Number of data records in this batch.
    /// </summary>
    public int DataRecordCount => _dataRecords.Count;

    /// <summary>
    /// Message type identifier.
    /// </summary>
    public override abstract MessageType MessageType { get; }

    public override bool Reliable => true;
    public override bool Background => false;

    public BinaryMessageBatch(ulong stateVersion, ulong syncTick, IConnection sender = null)
        : base(stateVersion, syncTick, sender)
    {
        _stream = new MemoryStream();
        _writer = new BinaryWriter(_stream);
    }

    // ===== DATA RECORD MANAGEMENT =====

	/// <summary>
	/// Begin writing a new data record for a sync element.
	/// </summary>
	public BinaryWriter BeginNewDataRecord(RefID targetID)
	{
		if (_currentRecordIndex >= 0)
			throw new InvalidOperationException("Previous data record not finished!");

        var record = new DataRecord
        {
            TargetID = targetID,
            StartOffset = (int)_stream.Position,
            Validity = MessageValidity.Valid
        };

        _dataRecords.Add(record);
        _currentRecordIndex = _dataRecords.Count - 1;

		// Just track the start - we'll write the header in FinishDataRecord
        return _writer;
    }

	/// <summary>
	/// Finish writing the current data record.
	/// </summary>
	public void FinishDataRecord(RefID targetID)
	{
		if (_currentRecordIndex < 0)
			throw new InvalidOperationException("No data record in progress!");

        var record = _dataRecords[_currentRecordIndex];
        if (record.TargetID != targetID)
            throw new InvalidOperationException("TargetID mismatch!");

        record.EndOffset = (int)_stream.Position;
        record.IsProcessed = false;
        _dataRecords[_currentRecordIndex] = record;
        _currentRecordIndex = -1;
    }

	/// <summary>
	/// Cancel the current data record (used when encoding fails).
	/// Removes the record and resets stream position.
	/// </summary>
	public void CancelDataRecord()
	{
		if (_currentRecordIndex < 0)
			return; // Nothing to cancel

		var record = _dataRecords[_currentRecordIndex];
		_stream.Position = record.StartOffset; // Reset stream to before this record
		_dataRecords.RemoveAt(_currentRecordIndex);
		_currentRecordIndex = -1;
	}

    /// <summary>
    /// Get a data record by index.
    /// </summary>
    public DataRecord GetDataRecord(int index)
    {
        return _dataRecords[index];
    }

	/// <summary>
	/// Find data record index by target ID.
	/// </summary>
	public int FindDataRecordIndex(RefID targetID)
	{
		for (int i = 0; i < _dataRecords.Count; i++)
		{
			if (_dataRecords[i].TargetID == targetID)
				return i;
		}
		return -1;
	}

    /// <summary>
    /// Seek to a data record for reading.
    /// </summary>
    public BinaryReader SeekDataRecord(int index)
    {
        if (_reader == null)
        {
            _stream.Position = 0;
            _reader = new BinaryReader(_stream);
        }

        var record = _dataRecords[index];
        _stream.Position = record.StartOffset;

        // Just seek, don't skip anything
        // The stored data doesn't include RefID/length headers - they were parsed during Decode()
        return _reader;
    }

    /// <summary>
    /// Mark a data record as processed.
    /// </summary>
    public void MarkDataRecordAsProcessed(int index)
    {
        var record = _dataRecords[index];
        record.IsProcessed = true;
        _dataRecords[index] = record;
    }

    /// <summary>
    /// Check if a data record has been processed.
    /// </summary>
    public bool IsProcessed(int index)
    {
        return _dataRecords[index].IsProcessed;
    }

    /// <summary>
    /// Invalidate a data record (mark as conflicting).
    /// </summary>
    public void InvalidateDataRecord(int index, bool conflict)
    {
        var record = _dataRecords[index];
        record.Validity = conflict ? MessageValidity.Conflict : MessageValidity.Invalid;
        _dataRecords[index] = record;
    }

    /// <summary>
    /// Remove all invalid records from the batch.
    /// </summary>
    public void RemoveInvalidRecords()
    {
        _dataRecords.RemoveAll(r => r.Validity != MessageValidity.Valid);
    }

	/// <summary>
	/// Get all conflicting record IDs.
	/// </summary>
	public void GetConflictingDataRecords(List<RefID> output)
	{
		foreach (var record in _dataRecords)
		{
			if (record.Validity == MessageValidity.Conflict)
				output.Add(record.TargetID);
		}
	}

    // ===== ENCODING/DECODING =====

    /// <summary>
    /// Encode this batch to bytes for transmission.
    /// Simple format without complex framing.
    /// </summary>
    public override byte[] Encode()
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        // Header
        writer.Write((byte)MessageType);
        writer.Write7BitEncoded(SenderStateVersion);
        writer.Write7BitEncoded(SenderSyncTick);
        writer.Write(SenderTime);
        if (this is ConfirmationMessage confirmation)
        {
            writer.Write7BitEncoded(confirmation.ConfirmTime);
        }

        // Record count
        writer.Write7BitEncoded((ulong)_dataRecords.Count);

        // Write each record: [RefID][DataLength][Data]
        _stream.Position = 0;
        for (int i = 0; i < _dataRecords.Count; i++)
        {
            var record = _dataRecords[i];
            var dataSize = record.EndOffset - record.StartOffset;

            // Write record header
            writer.WriteRefID(record.TargetID);
            writer.Write7BitEncoded((ulong)dataSize);

            // Write data
            _stream.Position = record.StartOffset;
            var buffer = new byte[dataSize];
            _stream.Read(buffer, 0, dataSize);
            writer.Write(buffer);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decode batch from bytes.
    /// </summary>
    public static BinaryMessageBatch Decode(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var reader = new BinaryReader(input);

        var messageType = (MessageType)reader.ReadByte();
        var stateVersion = reader.Read7BitEncoded();
        var syncTick = reader.Read7BitEncoded();
        var senderTime = reader.ReadDouble();
        var confirmTime = 0UL;
        if (messageType == MessageType.Confirmation)
        {
            confirmTime = reader.Read7BitEncoded();
        }
        var recordCount = (int)reader.Read7BitEncoded();

        BinaryMessageBatch batch = messageType switch
        {
            MessageType.Delta => new DeltaBatch(stateVersion, syncTick),
            MessageType.Full => new FullBatch(stateVersion, syncTick),
            MessageType.Confirmation => new ConfirmationMessage(confirmTime, stateVersion, syncTick),
            _ => throw new InvalidOperationException($"Unknown message type: {messageType}")
        };

        batch.SenderTime = senderTime;

        // Read records directly into batch stream
        batch._stream = new MemoryStream();
        batch._writer = new BinaryWriter(batch._stream);

        for (int i = 0; i < recordCount; i++)
        {
            var targetID = reader.ReadRefID();
            var dataLength = (int)reader.Read7BitEncoded();
            var recordData = reader.ReadBytes(dataLength);

            var record = new DataRecord
            {
                TargetID = targetID,
                StartOffset = (int)batch._stream.Position,
                EndOffset = (int)batch._stream.Position + dataLength,
                Validity = MessageValidity.Valid,
                IsProcessed = false
            };

            batch._dataRecords.Add(record);
            batch._stream.Write(recordData, 0, dataLength);
        }

        batch._reader = new BinaryReader(batch._stream);
        return batch;
    }

    /// <summary>
    /// Parse data records from the stream (not needed with new approach).
    /// </summary>
    protected virtual void ParseDataRecords(int expectedCount)
    {
        // This method is no longer used with the new encoding approach
    }

    public override void Dispose()
    {
        base.Dispose();
        _writer?.Dispose();
        _reader?.Dispose();
        _stream?.Dispose();
        _dataRecords.Clear();
    }
}

/// <summary>
/// Data record within a batch.
/// </summary>
public struct DataRecord
{
	public RefID TargetID;
	public int StartOffset;
	public int EndOffset;
	public MessageValidity Validity;
	public bool IsProcessed;
}

/// <summary>
/// Message type enumeration.
/// </summary>
public enum MessageType : byte
{
    Delta = 1,
    Full = 2,
    Confirmation = 3,
    Control = 4,
    Stream = 5,
    AsyncStream = 6,
    Ping = 7,
    Disconnect = 8
}
