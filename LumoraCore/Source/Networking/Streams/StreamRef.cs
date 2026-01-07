using System;
using System.IO;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Networking.Streams;

/// <summary>
/// Type-safe reference to a stream.
/// Similar to SyncRef but for stream references.
/// </summary>
/// <typeparam name="T">The stream type.</typeparam>
public class StreamRef<T> : SyncElement where T : Stream
{
    private RefID _targetID;
    private T _target;

    /// <summary>
    /// The RefID of the target stream.
    /// </summary>
    public RefID TargetID
    {
        get => _targetID;
        set
        {
            if (_targetID == value)
                return;

            _targetID = value;
            _target = null; // Will be resolved on next access
            InvalidateSyncElement();
        }
    }

    /// <summary>
    /// The target stream.
    /// </summary>
    public T Target
    {
        get
        {
            if (_target == null && !_targetID.IsNull)
            {
                // Try to resolve the reference
                var element = World?.ReferenceController?.GetObjectOrNull(_targetID);
                _target = element as T;
            }
            return _target;
        }
        set
        {
            if (_target == value)
                return;

            _target = value;
            _targetID = value?.ReferenceID ?? RefID.Null;
            InvalidateSyncElement();
        }
    }

    /// <summary>
    /// Whether this reference points to a valid stream.
    /// </summary>
    public bool HasTarget => Target != null;

    public override SyncMemberType MemberType => SyncMemberType.Field;

    protected override void InternalEncodeFull(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        writer.Write((ulong)_targetID);
    }

    protected override void InternalDecodeFull(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        _targetID = new RefID(reader.ReadUInt64());
        _target = null; // Will be resolved on next access
    }

    protected override void InternalEncodeDelta(BinaryWriter writer, BinaryMessageBatch outboundMessage)
    {
        InternalEncodeFull(writer, outboundMessage);
    }

    protected override void InternalDecodeDelta(BinaryReader reader, BinaryMessageBatch inboundMessage)
    {
        InternalDecodeFull(reader, inboundMessage);
    }

    protected override void InternalClearDirty()
    {
        // Nothing to clear
    }

    public override void Dispose()
    {
        _target = null;
        base.Dispose();
    }

    public static implicit operator T(StreamRef<T> streamRef) => streamRef?.Target;
}
