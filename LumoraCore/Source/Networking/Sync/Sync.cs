using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Synchronized value field.
/// Automatically tracks changes and synchronizes across the network.
/// </summary>
public class Sync<T> : SyncElement, ISyncMember, IChangeable
{
	private T _value;
	private T _lastSyncedValue;
	private Component _owner;

	public event Action<T> OnChanged;
	public event Action<IChangeable> Changed;

	public T Value
	{
		get => _value;
		set
		{
			if (!EqualityComparer<T>.Default.Equals(_value, value))
			{
				_value = value;
				OnValueChanged();
			}
		}
	}

	// ISyncMember implementation
	public string Name { get; set; }
	public bool IsDirty
	{
		get => IsSyncDirty;
		set
		{
			if (value)
			{
				InvalidateSyncElement();
			}
			else
			{
				IsSyncDirty = false;
				InternalClearDirty();
			}
		}
	}

	// Retained for compatibility with existing discovery logic
	public int MemberIndex { get; set; } = -1;
	public ulong Version { get; set; }

	public Sync()
	{
	}

	public Sync(Component owner)
	{
		Initialize(owner);
	}

	public Sync(Component owner, T initialValue)
	{
		Initialize(owner);
		_value = initialValue;
		_lastSyncedValue = initialValue;
	}

	public void Initialize(Component owner)
	{
		_owner = owner;
		World = owner?.World;
		ReferenceID = owner?.ReferenceID ?? RefID.Null; // Sync members share owner's RefID

		// Check if local element
		if (!ReferenceID.IsNull && ReferenceID.IsLocalID)
		{
			MarkLocalElement();
		}
	}

	private void OnValueChanged()
	{
		Version++;

		// Fire events
		OnChanged?.Invoke(_value);
		Changed?.Invoke(this);

		// Mark for sync (if not loading and not local)
		if (!IsLoading)
		{
			InvalidateSyncElement();
		}

		// Notify owner
		_owner?.NotifyChanged();
	}

	/// <summary>
	/// Check if value changed and clear dirty flag.
	/// </summary>
	public bool GetWasChangedAndClear()
	{
		bool changed = IsSyncDirty;
		// Don't clear here - cleared by EncodeDelta
		return changed;
	}

	// ===== ISyncMember compatibility helpers =====

	public void Encode(BinaryWriter writer)
	{
		EncodeDelta(writer, null);
	}

	public void Decode(BinaryReader reader)
	{
		DecodeDelta(reader, null);
	}

	public object GetValueAsObject()
	{
		return _value;
	}

	// ===== ENCODING/DECODING =====

	protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
	{
		SyncCoder.Encode<T>(writer, _value);
	}

	protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
	{
		_value = SyncCoder.Decode<T>(reader);
		_lastSyncedValue = _value;
		OnChanged?.Invoke(_value);
		Changed?.Invoke(this);
	}

	protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
	{
		// For simple values, delta is the same as full
		SyncCoder.Encode<T>(writer, _value);
	}

	protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
	{
		_value = SyncCoder.Decode<T>(reader);
		_lastSyncedValue = _value;
		OnChanged?.Invoke(_value);
		Changed?.Invoke(this);
	}

	protected override void InternalClearDirty()
	{
		_lastSyncedValue = _value;
	}

	public override string ToString() => $"Sync<{typeof(T).Name}>({_value})";
}
