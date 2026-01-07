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
}
