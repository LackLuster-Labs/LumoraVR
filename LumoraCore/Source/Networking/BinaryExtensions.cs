using System.IO;
using System.Runtime.CompilerServices;
using Lumora.Core;

namespace Lumora.Core.Networking;

/// <summary>
/// Extensions for efficient binary encoding of primitive types and RefIDs.
/// </summary>
public static class BinaryExtensions
{
    /// <summary>
    /// Write a ulong using 7-bit variable-length encoding.
    /// Smaller values use fewer bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    /// Read a ulong using 7-bit variable-length encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read7BitEncoded(this BinaryReader reader)
    {
        ulong result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = reader.ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        return result;
    }

    /// <summary>
    /// Write a RefID using 7-bit encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRefID(this BinaryWriter writer, RefID id)
    {
        writer.Write7BitEncoded((ulong)id);
    }

    /// <summary>
    /// Read a RefID using 7-bit encoding.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RefID ReadRefID(this BinaryReader reader)
    {
        return new RefID(reader.Read7BitEncoded());
    }
}
