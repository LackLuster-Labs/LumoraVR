// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using Lumora.Core;

namespace Lumora.Core.Networking;

/// <summary>
/// Extensions for BinaryReader/Writer to support 7-bit encoded values
/// </summary>
public static class BinaryExtensions
{
    /// <summary>
    /// Write a ulong value using 7-bit encoding (variable length)
    /// </summary>
    public static void Write7BitEncoded(this BinaryWriter writer, ulong value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)(value | 0x80));
            value >>= 7;
        }
        writer.Write((byte)value);
    }

    /// <summary>
    /// Read a ulong value using 7-bit encoding (variable length)
    /// </summary>
    public static ulong Read7BitEncoded(this BinaryReader reader)
    {
        ulong result = 0;
        int shift = 0;
        byte b;
        
        do
        {
            if (shift >= 64)
                throw new FormatException("Invalid 7-bit encoded integer");
                
            b = reader.ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        
        return result;
    }

    /// <summary>
    /// Alias for Read7BitEncoded for compatibility
    /// </summary>
    public static ulong Read7BitEncodedUInt64(this BinaryReader reader)
    {
        return reader.Read7BitEncoded();
    }

    /// <summary>
    /// Write a RefID to binary stream
    /// </summary>
    public static void WriteRefID(this BinaryWriter writer, RefID refID)
    {
        writer.Write((ulong)refID);
    }

    /// <summary>
    /// Read a RefID from binary stream
    /// </summary>
    public static RefID ReadRefID(this BinaryReader reader)
    {
        return new RefID(reader.ReadUInt64());
    }

    /// <summary>
    /// Read a length-prefixed byte array (Int32 prefix) from peer-controlled input.
    /// Throws InvalidDataException if the declared length is negative or exceeds
    /// <paramref name="maxBytes"/>. Use this on every untrusted ReadInt32-then-ReadBytes
    /// pattern in network decoders to prevent OOM-via-huge-length DoS.
    /// </summary>
    public static byte[] ReadBoundedBytesInt32(this BinaryReader reader, int maxBytes)
    {
        int length = reader.ReadInt32();
        return ReadBoundedBytesCore(reader, length, maxBytes);
    }

    /// <summary>
    /// Read a length-prefixed byte array (7-bit-encoded prefix) from peer-controlled input.
    /// Throws InvalidDataException if the declared length is negative or exceeds
    /// <paramref name="maxBytes"/>.
    /// </summary>
    public static byte[] ReadBoundedBytes7Bit(this BinaryReader reader, int maxBytes)
    {
        ulong length = reader.Read7BitEncoded();
        if (length > (ulong)int.MaxValue)
            throw new InvalidDataException($"Declared length {length} overflows Int32.");
        return ReadBoundedBytesCore(reader, (int)length, maxBytes);
    }

    /// <summary>
    /// Read exactly <paramref name="length"/> bytes after validating against <paramref name="maxBytes"/>.
    /// Use this when the length has already been read from the stream. Throws on bound
    /// violations and on short reads (peer claimed N bytes but stream had fewer).
    /// </summary>
    public static byte[] ReadBoundedBytes(this BinaryReader reader, int length, int maxBytes)
    {
        return ReadBoundedBytesCore(reader, length, maxBytes);
    }

    private static byte[] ReadBoundedBytesCore(BinaryReader reader, int length, int maxBytes)
    {
        if (length < 0)
            throw new InvalidDataException($"Declared length {length} is negative.");
        if (length > maxBytes)
            throw new InvalidDataException($"Declared length {length} exceeds cap {maxBytes}.");
        if (length == 0)
            return System.Array.Empty<byte>();

        var buffer = reader.ReadBytes(length);
        if (buffer.Length != length)
            throw new EndOfStreamException($"Expected {length} bytes, got {buffer.Length}.");
        return buffer;
    }

    /// <summary>
    /// Read a length-prefixed string written by <see cref="BinaryWriter.Write(string)"/>
    /// with a hard byte-length cap. The wire format is a 7-bit-encoded byte count
    /// followed by UTF-8 bytes; this matches what BinaryReader.ReadString consumes
    /// but refuses to allocate when the peer declares more than <paramref name="maxBytes"/>.
    /// </summary>
    public static string ReadBoundedString(this BinaryReader reader, int maxBytes)
    {
        var bytes = reader.ReadBoundedBytes7Bit(maxBytes);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
