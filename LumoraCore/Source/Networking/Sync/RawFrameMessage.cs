// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers;
using System.IO;
using Lumora.Core.Networking;

namespace Lumora.Core.Networking.Sync;

/// <summary>
/// Lightweight per-user payload message designed for tight latency loops such as
/// voice. Sibling of <see cref="StreamMessage"/> but skips its per-tick
/// gather/batch/copy pipeline:
///   - <see cref="Encode"/> writes once into a single right-sized byte[].
///   - <see cref="Decode"/> reads the payload into a pooled buffer; receivers
///     either consume it during the dispatch callback or copy it into their own
///     storage before the message is disposed (which returns the buffer to the
///     pool).
///
/// Dispatch flows through the same authority/sender-validation path as
/// StreamMessage, so the spoof check (issue #80) applies here too.
///
/// Wire format (the leading <see cref="MessageType"/> byte and sender state
/// version / sync tick are written by this class' <see cref="Encode"/> and
/// consumed by <see cref="SyncMessage.Decode"/> before this class' Decode runs):
/// <code>
///   UserID         varint   claimed origin (validated against sender connection)
///   StreamRefID    u64      routes to a per-user stream consumer
///   Sequence       u16      caller-managed; useful for jitter buffers
///   PayloadLength  varint   capped at NetworkLimits.MaxRawFrameBytes
///   Payload        bytes
/// </code>
/// </summary>
public class RawFrameMessage : SyncMessage
{
    public override MessageType MessageType => Networking.Sync.MessageType.RawFrame;
    public override bool Reliable => false;

    /// <summary>Claimed user ID of the originating peer.</summary>
    public ulong UserID { get; set; }

    /// <summary>Per-user stream this frame belongs to (e.g. a voice stream).</summary>
    public RefID StreamRefID { get; set; }

    /// <summary>Caller-managed sequence number; framework does not interpret it.</summary>
    public ushort Sequence { get; set; }

    private byte[] _payloadBuffer;
    private int _payloadLength;
    private bool _payloadPooled;

    /// <summary>Read-only view of the payload bytes. Valid only until <see cref="Dispose"/>.</summary>
    public ReadOnlyMemory<byte> Payload => new(_payloadBuffer, 0, _payloadLength);

    /// <summary>Length of the payload in bytes.</summary>
    public int PayloadLength => _payloadLength;

    public RawFrameMessage(ulong stateVersion, ulong syncTick, IConnection sender = null)
        : base(stateVersion, syncTick, sender)
    {
    }

    /// <summary>
    /// Copy <paramref name="source"/> into the message as the outgoing payload.
    /// The caller's span can be reused after this call returns.
    /// </summary>
    public void SetPayload(ReadOnlySpan<byte> source)
    {
        if (source.Length > NetworkLimits.MaxRawFrameBytes)
            throw new ArgumentException($"Payload length {source.Length} exceeds cap {NetworkLimits.MaxRawFrameBytes}.", nameof(source));

        ReleasePayload();
        if (source.Length == 0)
        {
            _payloadBuffer = Array.Empty<byte>();
            _payloadLength = 0;
            _payloadPooled = false;
            return;
        }

        _payloadBuffer = new byte[source.Length];
        _payloadLength = source.Length;
        _payloadPooled = false;
        source.CopyTo(_payloadBuffer);
    }

    public override byte[] Encode()
    {
        // Upper bound: type(1) + 3×varint64(10) + RefID(8) + seq(2) + payloadLen varint(10).
        const int HeaderUpperBound = 1 + 10 + 10 + 10 + 8 + 2 + 10;
        int upper = HeaderUpperBound + _payloadLength;
        var buf = new byte[upper];
        using var ms = new MemoryStream(buf, writable: true);
        using var w = new BinaryWriter(ms);

        w.Write((byte)MessageType);
        w.Write7BitEncoded(SenderStateVersion);
        w.Write7BitEncoded(SenderSyncTick);
        w.Write7BitEncoded(UserID);
        w.Write((ulong)StreamRefID);
        w.Write(Sequence);
        w.Write7BitEncoded((ulong)_payloadLength);
        if (_payloadLength > 0)
            w.Write(_payloadBuffer, 0, _payloadLength);

        int written = (int)ms.Position;
        if (written == buf.Length)
            return buf;

        var fitted = new byte[written];
        Buffer.BlockCopy(buf, 0, fitted, 0, written);
        return fitted;
    }

    public static RawFrameMessage Decode(BinaryReader reader)
    {
        var msg = new RawFrameMessage(0, 0)
        {
            UserID = reader.Read7BitEncodedUInt64(),
            StreamRefID = new RefID(reader.ReadUInt64()),
            Sequence = reader.ReadUInt16(),
        };

        var declared = reader.Read7BitEncodedUInt64();
        if (declared > (ulong)NetworkLimits.MaxRawFrameBytes)
            throw new InvalidDataException($"RawFrame payload length {declared} exceeds cap {NetworkLimits.MaxRawFrameBytes}.");

        int n = (int)declared;
        if (n == 0)
        {
            msg._payloadBuffer = Array.Empty<byte>();
            msg._payloadLength = 0;
            msg._payloadPooled = false;
            return msg;
        }

        var rented = ArrayPool<byte>.Shared.Rent(n);
        int read = 0;
        while (read < n)
        {
            int chunk = reader.Read(rented, read, n - read);
            if (chunk <= 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                throw new EndOfStreamException($"RawFrame: expected {n} payload bytes, got {read}.");
            }
            read += chunk;
        }
        msg._payloadBuffer = rented;
        msg._payloadLength = n;
        msg._payloadPooled = true;
        return msg;
    }

    /// <summary>
    /// Build an independent message that can be relayed to a different target set.
    /// The clone owns its own (non-pooled) payload copy.
    /// </summary>
    public RawFrameMessage CloneForRelay()
    {
        var clone = new RawFrameMessage(SenderStateVersion, SenderSyncTick)
        {
            UserID = UserID,
            StreamRefID = StreamRefID,
            Sequence = Sequence,
        };
        if (_payloadLength > 0)
            clone.SetPayload(new ReadOnlySpan<byte>(_payloadBuffer, 0, _payloadLength));
        return clone;
    }

    private void ReleasePayload()
    {
        if (_payloadPooled && _payloadBuffer != null)
            ArrayPool<byte>.Shared.Return(_payloadBuffer);
        _payloadBuffer = null;
        _payloadLength = 0;
        _payloadPooled = false;
    }

    public override void Dispose()
    {
        base.Dispose();
        ReleasePayload();
    }
}
