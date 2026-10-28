// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using Lumora.Core.Networking;
using Lumora.Nexus.Protocol;
using Lumora.Nexus.Transport;

namespace Lumora.Core.Networking.Sync;

public abstract class SyncMessage : IDisposable
{
    public ulong SenderStateVersion { get; set; }
    public ulong SenderSyncTick { get; set; }
    public double SenderTime { get; set; }
    public float SecondsSinceReceived { get; set; }
    // Stopwatch ticks when the transport handed it over; zero for one made here.
    public long Arrived { get; set; }
    public User SenderUser { get; set; } = null!;
    public IConnection Sender { get; set; }
    public List<IConnection> Targets { get; } = new();

    public abstract MessageType MessageType { get; }
    public virtual bool Reliable => true;
    public virtual bool Background => false;

    // Reliable, and free to arrive out of order with other bulk messages. Only set on a message whose
    // receiver has said it takes that.
    public bool Bulk { get; set; }

    // Minimum encoded size (bytes) at/above which this message's frame is worth compressing,
    // or 0 to never compress. Small/latency-critical messages (Confirmation, Control, Ping,
    // RawFrame) leave this at 0; batched and stream payloads override it. The encoder gates
    // on the actual encoded length, so a message under its threshold always ships raw. -xlinka
    public virtual int CompressionThreshold => 0;

    protected SyncMessage(ulong stateVersion, ulong syncTick, IConnection sender = null!)
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

    // The frame written into a stream the caller keeps, through the writer it keeps on it, from the start; how
    // many bytes it came to. The encode thread sends from that stream's buffer, so a message costs no array of
    // its own. A kind that writes no frame here hands over what Encode made. -xlinka
    public virtual int EncodeInto(MemoryStream output, BinaryWriter writer)
    {
        var bytes = Encode();
        output.SetLength(0);
        output.Write(bytes, 0, bytes.Length);
        return bytes.Length;
    }

    public static SyncMessage Decode(RawInMessage raw)
    {
        // Transparently unwrap a compressed frame before anything reads it. For an
        // uncompressed full-array frame this returns raw.Data unchanged (no copy), so the
        // common path is identical to before; it also normalizes the frame to start at
        // index 0, keeping the reader and the batch decoder consistent. - xlinka
        // Unwrapped into this thread's own buffer and read through this thread's own reader: a copy, a stream
        // and a reader or two a message were most of what decoding allocated besides what a message keeps.
        // Every decoder reads all it keeps before it returns. -xlinka
        int offset = raw.Offset, length = raw.Length;
        var frame = SyncFrameCodec.UnwrapInto(raw.Data, ref offset, ref length, ref t_unwrapped);
        try
        {
            return DecodeFrame(raw, frame, offset, length);
        }
        finally
        {
            if (t_unwrapped != null && t_unwrapped.Length > MaxKeptFrameBytes)
                t_unwrapped = null;
        }
    }

    private const int MaxKeptFrameBytes = 256 * 1024;
    [ThreadStatic] private static byte[]? t_unwrapped;
    [ThreadStatic] private static FrameStream? t_frame;
    [ThreadStatic] private static BinaryReader? t_reader;

    // This thread's reader, pointed at data[offset .. offset + length) from its start.
    internal static BinaryReader ReaderOver(byte[] data, int offset, int length)
    {
        var stream = t_frame ??= new FrameStream();
        stream.Reset(data, offset, length);
        return t_reader ??= new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    }

    private static SyncMessage DecodeFrame(RawInMessage raw, byte[] frame, int offset, int length)
    {
        var reader = ReaderOver(frame, offset, length);

        var messageType = (MessageType)(reader.ReadByte() & ~SyncFrameCodec.CompressedFlag);
        var stateVersion = reader.Read7BitEncoded();
        var syncTick = reader.Read7BitEncoded();

        SyncMessage message = (messageType switch
        {
            MessageType.Delta => BinaryMessageBatch.Decode(frame, offset, length) as DeltaBatch,
            MessageType.Full => BinaryMessageBatch.Decode(frame, offset, length) as FullBatch,
            MessageType.Confirmation => BinaryMessageBatch.Decode(frame, offset, length) as ConfirmationMessage,
            MessageType.Control => ControlMessage.Decode(reader),
            MessageType.Stream => StreamMessage.Decode(reader),
            MessageType.RawFrame => RawFrameMessage.Decode(reader),
            _ => throw new InvalidOperationException($"Unknown message type: {messageType}")
        })!;

        message.SenderStateVersion = stateVersion;
        message.SenderSyncTick = syncTick;
        message.Sender = raw.Sender;
        message.Arrived = raw.Arrived;
        return message;
    }

    public virtual void Dispose()
    {
        Targets.Clear();
    }

    // A read-only view of part of an array that a reader can be pointed at again and again.
    private sealed class FrameStream : Stream
    {
        private byte[] _data = Array.Empty<byte>();
        private int _start;
        private int _length;
        private int _position;

        public void Reset(byte[] data, int offset, int length)
        {
            _data = data;
            _start = offset;
            _length = length;
            _position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = (int)System.Math.Clamp(value, 0L, _length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = System.Math.Min(buffer.Length, _length - _position);
            if (n <= 0)
                return 0;
            _data.AsSpan(_start + _position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override int ReadByte() => _position < _length ? _data[_start + _position++] : -1;

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset,
            };
            Position = target;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
