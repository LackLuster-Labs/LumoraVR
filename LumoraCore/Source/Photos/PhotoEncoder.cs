// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.IO.Compression;
using Lumora.Core.Assets;

namespace Lumora.Core.Photos;

// Raw pixels as one picture file. The renderer side sets Platform to its own encoders (WebP, JPG and PNG);
// without one, as on a machine with no renderer, everything is written as PNG here. -xlinka
public static class PhotoEncoder
{
    public static Func<byte[], int, int, ImageEncoding, float, byte[]?>? Platform { get; set; }

    // Row 0 at the top, four bytes a pixel. The encoding actually written comes back with the bytes.
    public static (byte[]? Bytes, ImageEncoding Encoding) Encode(byte[] rgba, int width, int height, ImageEncoding encoding, float quality)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            return (null, encoding);
        var platform = Platform;
        if (platform != null)
        {
            try
            {
                var bytes = platform(rgba, width, height, encoding, quality);
                if (bytes is { Length: > 0 })
                    return (bytes, encoding);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"Photos: encoding failed, writing PNG instead: {ex.Message}");
            }
        }
        return (Png(rgba, width, height), ImageEncoding.Png);
    }

    public static byte[] Png(byte[] rgba, int width, int height, bool compact = false)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        WriteBig(header, 0, (uint)width);
        WriteBig(header, 4, (uint)height);
        header[8] = 8;
        header[9] = 6;
        Chunk(output, "IHDR", header);
        using (var packed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(packed, compact ? CompressionLevel.SmallestSize : CompressionLevel.Fastest, leaveOpen: true))
            {
                int stride = width * 4;
                if (compact)
                    TextureRowFilter.Write(zlib, rgba, width, height);
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        zlib.WriteByte(0);
                        zlib.Write(rgba, y * stride, stride);
                    }
                }
            }
            Chunk(output, "IDAT", packed.ToArray());
        }
        Chunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var head = new byte[8];
        WriteBig(head, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++)
            head[4 + i] = (byte)type[i];
        output.Write(head);
        output.Write(data);
        uint crc = Crc(head.AsSpan(4, 4), 0xFFFFFFFFu);
        crc = Crc(data, crc) ^ 0xFFFFFFFFu;
        var tail = new byte[4];
        WriteBig(tail, 0, crc);
        output.Write(tail);
    }

    private static uint[]? _crcTable;

    private static uint Crc(ReadOnlySpan<byte> data, uint crc)
    {
        var table = _crcTable ??= BuildTable();
        for (int i = 0; i < data.Length; i++)
            crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static void WriteBig(byte[] buffer, int at, uint value)
    {
        buffer[at] = (byte)(value >> 24);
        buffer[at + 1] = (byte)(value >> 16);
        buffer[at + 2] = (byte)(value >> 8);
        buffer[at + 3] = (byte)value;
    }
}
