// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using Lumora.Nexus.Protocol.Compression;
using Lumora.Nexus.Protocol.Compression.lz4;
using Lumora.Nexus.Transport;

namespace Lumora.Nexus.Protocol;

// Wraps an encoded sync frame in a compressed envelope when it pays off, and unwraps it on
// receive. Byte 0 stays the plaintext message-type tag with its high bit set as the
// "compressed" flag, so the receiver tells compressed from raw without touching the codec.
// Envelope when compressed:
//   [ type | 0x80 ] [ uncompressed-tail-length : LEB128 ] [ codec(frame[1..]) ]
// Uncompressed frames travel verbatim (high bit clear). Message types only ever use values
// 1..9, so the 0x80 bit is always free. Compression is transparent to message type: every
// peer runs the same codec, and the receiver always unwraps before dispatch. -xlinka
public static class SyncFrameCodec
{
    // High bit of byte 0, set when the rest of the frame is compressed.
    public const byte CompressedFlag = 0x80;

    // Stateless + thread-safe, so the whole session shares one instance. Swap this line to
    // change the wire codec (the framing stays the same).
    private static readonly ICompressable Codec = new Lz4BlockCompressable();

    // Returns a compressed envelope of the frame if it ends up smaller, otherwise the frame unchanged
    // (so an incompressible payload just travels raw). The caller decides WHETHER to try (via a size
    // threshold); this only decides whether compression actually helped.
    public static byte[] WrapCompressed(byte[] frame)
    {
        if (frame == null || frame.Length < 2)
            return frame!;

        int tailLen = frame.Length - 1; // everything after the type tag
        var scratch = new byte[Codec.MaxCompressedLength(tailLen)];
        int written = Codec.Compress(new ReadOnlySpan<byte>(frame, 1, tailLen), scratch);
        if (written <= 0)
            return frame; // incompressible / codec declined

        int headerLen = 1 + Varint.Length((uint)tailLen);
        if (headerLen + written >= frame.Length)
            return frame; // envelope wouldn't be smaller -> send raw

        var output = new byte[headerLen + written];
        output[0] = (byte)(frame[0] | CompressedFlag);
        int pos = Varint.Write(output, 1, (uint)tailLen);
        Buffer.BlockCopy(scratch, 0, output, pos, written);
        return output;
    }

    // The same envelope written into a buffer the caller keeps, grown as frames need it, for a sender that
    // hands each frame on before it writes the next. Returns what to send: the buffer, or the frame itself
    // when compressing did not pay; length becomes its byte count. -xlinka
    public static byte[] WrapCompressed(byte[] frame, ref int length, ref byte[]? buffer)
    {
        if (frame == null || length < 2)
            return frame!;

        int tailLen = length - 1;
        int headerLen = 1 + Varint.Length((uint)tailLen);
        int needed = headerLen + Codec.MaxCompressedLength(tailLen);
        if (buffer == null || buffer.Length < needed)
            buffer = new byte[System.Math.Max(needed, 4096)];
        int written = Codec.Compress(new ReadOnlySpan<byte>(frame, 1, tailLen), new Span<byte>(buffer, headerLen, buffer.Length - headerLen));
        if (written <= 0 || headerLen + written >= length)
            return frame;

        buffer[0] = (byte)(frame[0] | CompressedFlag);
        Varint.Write(buffer, 1, (uint)tailLen);
        length = headerLen + written;
        return buffer;
    }

    // The same unwrapping into a buffer the caller keeps, grown as frames need it. A frame that is not compressed
    // is left where it lies; either way the frame is data[offset .. offset + length) afterwards, data being what
    // comes back. -xlinka
    public static byte[] UnwrapInto(byte[] data, ref int offset, ref int length, ref byte[]? buffer)
    {
        if (data == null || length <= 0)
            return data!;

        byte b0 = data[offset];
        if ((b0 & CompressedFlag) == 0)
            return data;

        int pos = offset + 1;
        uint tailLenRaw = Varint.Read(data, ref pos);
        if (tailLenRaw == 0 || tailLenRaw > (uint)NetworkLimits.MaxDecompressedFrameBytes)
            throw new InvalidDataException($"Compressed sync frame declares tail length {tailLenRaw}, cap {NetworkLimits.MaxDecompressedFrameBytes}.");
        int tailLen = (int)tailLenRaw;
        int compressedLen = (offset + length) - pos;
        if (compressedLen <= 0)
            throw new InvalidDataException("Compressed sync frame has no payload.");

        if (buffer == null || buffer.Length < 1 + tailLen)
            buffer = new byte[System.Math.Max(1 + tailLen, 4096)];
        buffer[0] = (byte)(b0 & ~CompressedFlag);
        int produced = Codec.Decompress(
            new ReadOnlySpan<byte>(data, pos, compressedLen),
            new Span<byte>(buffer, 1, tailLen),
            tailLen);
        if (produced != tailLen)
            throw new InvalidDataException($"Sync frame decompression size mismatch: got {produced}, expected {tailLen}.");
        offset = 0;
        length = 1 + tailLen;
        return buffer;
    }

    // If the frame is compressed (byte 0 high bit set), decompress it into a full plaintext frame
    // (type tag + tail) and return that; otherwise return the bytes as a frame that starts at index 0
    // (the original array when it's the whole slice, else a copy). The returned frame is always safe to
    // re-parse from offset 0.
    public static byte[] UnwrapIfCompressed(byte[] data, int offset, int length)
    {
        if (data == null || length <= 0)
            return data!;

        byte b0 = data[offset];
        if ((b0 & CompressedFlag) == 0)
        {
            if (offset == 0 && length == data.Length)
                return data;
            var slice = new byte[length];
            Buffer.BlockCopy(data, offset, slice, 0, length);
            return slice;
        }

        int pos = offset + 1;
        uint tailLenRaw = Varint.Read(data, ref pos);
        // The tail length crosses the network trust boundary, so bound it before allocating -
        // the same discipline as every other peer-declared size on the receive path. Unsigned
        // compare so a value with bit 31 set can't slip through as a negative int. - xlinka
        if (tailLenRaw == 0 || tailLenRaw > (uint)NetworkLimits.MaxDecompressedFrameBytes)
            throw new InvalidDataException($"Compressed sync frame declares tail length {tailLenRaw}, cap {NetworkLimits.MaxDecompressedFrameBytes}.");
        int tailLen = (int)tailLenRaw;
        int compressedLen = (offset + length) - pos;
        if (compressedLen <= 0)
            throw new InvalidDataException("Compressed sync frame has no payload.");

        var frame = new byte[1 + tailLen];
        frame[0] = (byte)(b0 & ~CompressedFlag); // restore the plaintext type tag
        int produced = Codec.Decompress(
            new ReadOnlySpan<byte>(data, pos, compressedLen),
            new Span<byte>(frame, 1, tailLen),
            tailLen);
        if (produced != tailLen)
            throw new InvalidDataException($"Sync frame decompression size mismatch: got {produced}, expected {tailLen}.");
        return frame;
    }

    // Minimal LEB128 for the envelope's tail-length. Deliberately independent of the
    // protocol's Write7BitEncoded so this codec stays self-contained and unit-testable. -xlinka
    private static class Varint
    {
        public static int Length(uint value)
        {
            int n = 1;
            while (value >= 0x80) { value >>= 7; n++; }
            return n;
        }

        public static int Write(byte[] buffer, int offset, uint value)
        {
            while (value >= 0x80)
            {
                buffer[offset++] = (byte)(value | 0x80);
                value >>= 7;
            }
            buffer[offset++] = (byte)value;
            return offset;
        }

        public static uint Read(byte[] buffer, ref int offset)
        {
            uint result = 0;
            int shift = 0;
            byte b;
            do
            {
                // A uint is at most 5 LEB128 bytes; refuse a malformed all-high-bit run rather
                // than spin reading garbage (or shifting past the width). - xlinka
                if (shift >= 35)
                    throw new InvalidDataException("Malformed compressed sync-frame length varint.");
                b = buffer[offset++];
                result |= (uint)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return result;
        }
    }
}
