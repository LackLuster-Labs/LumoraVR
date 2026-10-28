// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Lumora.Nexus.Protocol;

// Writes unsigned values bit by bit (MSB first), packing them into bytes. Quantized stream encoding
// uses this so a value quantized to N bits costs ~N bits on the wire, not a whole int. -xlinka
public sealed class BitWriter
{
    private readonly List<byte> _bytes = new();
    private byte _current;
    private int _bitInByte;

    public void WriteBits(uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            if (((value >> i) & 1u) != 0)
                _current |= (byte)(1 << (7 - _bitInByte));

            if (++_bitInByte == 8)
            {
                _bytes.Add(_current);
                _current = 0;
                _bitInByte = 0;
            }
        }
    }

    // Flushes any partial byte, then returns the packed bytes.
    public byte[] ToArray()
    {
        Flush();
        return _bytes.ToArray();
    }

    // Flushes any partial byte; the packed bytes, good until the next write or reset.
    public ReadOnlySpan<byte> Packed()
    {
        Flush();
        return CollectionsMarshal.AsSpan(_bytes);
    }

    // Back to empty, keeping the storage, for a writer used again frame after frame.
    public void Reset()
    {
        _bytes.Clear();
        _current = 0;
        _bitInByte = 0;
    }

    private void Flush()
    {
        if (_bitInByte > 0)
        {
            _bytes.Add(_current);
            _current = 0;
            _bitInByte = 0;
        }
    }
}

// Reads unsigned values bit by bit (MSB first) from a byte buffer. Pair of BitWriter. -xlinka
public sealed class BitReader
{
    private byte[] _bytes;
    private int _length;
    private int _bitPos;

    public BitReader(byte[] bytes)
    {
        _bytes = bytes;
        _length = bytes.Length;
    }

    // The first count bytes of a buffer, read from the start.
    public void Reset(byte[] bytes, int count)
    {
        _bytes = bytes;
        _length = count;
        _bitPos = 0;
    }

    public uint ReadBits(int bits)
    {
        uint value = 0;
        for (int i = 0; i < bits; i++)
        {
            int byteIndex = _bitPos >> 3;
            int bitIndex = 7 - (_bitPos & 7);
            uint bit = byteIndex < _length ? (uint)((_bytes[byteIndex] >> bitIndex) & 1) : 0u;
            value = (value << 1) | bit;
            _bitPos++;
        }
        return value;
    }
}

// One writer, one reader and one byte buffer a thread, for the stream coders that pack values every frame:
// each packed value used to cost a writer, its list and an array going out, and an array and a reader coming
// in. Whatever one hands out is only good until the next call on the same thread. -xlinka
public static class BitScratch
{
    [ThreadStatic] private static BitWriter? t_writer;
    [ThreadStatic] private static BitReader? t_reader;
    [ThreadStatic] private static byte[]? t_bytes;

    public static BitWriter Writer()
    {
        var writer = t_writer ??= new BitWriter();
        writer.Reset();
        return writer;
    }

    // The next length bytes of the stream behind this thread's reader; fewer when the stream ends first, as
    // ReadBytes gave. A length past what the stream holds is cut to it before anything is sized from it.
    public static BitReader Read(BinaryReader reader, int length)
    {
        if (length < 0)
            length = 0;
        var stream = reader.BaseStream;
        if (stream.CanSeek)
            length = (int)System.Math.Min(length, System.Math.Max(0L, stream.Length - stream.Position));
        var bytes = t_bytes;
        if (bytes == null || bytes.Length < length)
            t_bytes = bytes = new byte[System.Math.Max(length, 256)];
        int got = 0;
        while (got < length)
        {
            int n = reader.Read(bytes, got, length - got);
            if (n <= 0)
                break;
            got += n;
        }
        var bitReader = t_reader ??= new BitReader(Array.Empty<byte>());
        bitReader.Reset(bytes, got);
        return bitReader;
    }
}

// Scalar quantization: map a float in [min, max] to an N-bit unsigned integer and back. The byte cost
// is implied by the bit count (which is synced), so the wire carries no length. -xlinka
internal static class Quantization
{
    public static uint Quantize(float value, float min, float max, int bits)
    {
        if (bits <= 0 || max <= min)
            return 0;

        uint maxQ = bits >= 32 ? uint.MaxValue : (1u << bits) - 1u;
        float t = (value - min) / (max - min);
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        return (uint)(t * maxQ + 0.5f);
    }

    public static float Dequantize(uint q, float min, float max, int bits)
    {
        if (bits <= 0)
            return min;

        uint maxQ = bits >= 32 ? uint.MaxValue : (1u << bits) - 1u;
        if (maxQ == 0)
            return min;

        return min + ((float)q / maxQ) * (max - min);
    }
}
