// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Core.Assets;

// GIF, the one animated format nothing in the renderer reads, decoded here into the frame sheet an animated WebP
// becomes too (FrameSheet): every frame composited the way the file says (keep, clear, or restore), every Nth kept
// once there are more than the sheet holds. The file is walked once to count its pictures, so only the frames the
// sheet keeps are ever copied out. Small and forgiving rather than complete. -xlinka
public static class GifDecoder
{
    public static bool IsGif(ReadOnlySpan<byte> data)
        => data.Length > 13 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8' && (data[4] == '7' || data[4] == '9') && data[5] == 'a';

    // Pictures in the file, by walking its blocks without decoding any; stops counting at stopAt. A file that
    // breaks off counts what came before the break.
    public static int CountFrames(ReadOnlySpan<byte> data, int stopAt = int.MaxValue)
    {
        if (!IsGif(data))
            return 0;
        int position = 13;
        byte packed = data[10];
        if ((packed & 0x80) != 0)
            position += 3 << ((packed & 0x07) + 1);

        int count = 0;
        while (position < data.Length && count < stopAt)
        {
            byte block = data[position++];
            if (block == 0x3B)
                break;
            if (block == 0x21)
            {
                if (position >= data.Length)
                    break;
                position++;
                if (!SkipSubBlocks(data, ref position))
                    break;
                continue;
            }
            if (block != 0x2C || position + 9 > data.Length)
                break;
            byte framePacked = data[position + 8];
            position += 9;
            if ((framePacked & 0x80) != 0)
                position += 3 << ((framePacked & 0x07) + 1);
            // The LZW minimum code size, then the image's sub-blocks.
            position++;
            if (position > data.Length || !SkipSubBlocks(data, ref position))
                break;
            count++;
        }
        return count;
    }

    private static bool SkipSubBlocks(ReadOnlySpan<byte> data, ref int position)
    {
        while (position < data.Length)
        {
            int length = data[position++];
            if (length == 0)
                return true;
            position += length;
        }
        return false;
    }

    // The first picture alone, as a still: what a material slot takes from a GIF.
    public static byte[]? DecodeFirstFrame(byte[] data, out int width, out int height)
    {
        var sheet = DecodeSheet(data, 0, 1);
        width = sheet?.Width ?? 0;
        height = sheet?.Height ?? 0;
        return sheet?.Pixels;
    }

    // Null when the file is not a GIF, holds no picture, or its canvas is beyond the sheet. cap: the texture's
    // size cap, 0 for none. limit: decode no further than this many pictures.
    public static FrameSheet? DecodeSheet(byte[] data, int cap, int limit = int.MaxValue)
    {
        if (!IsGif(data))
            return null;

        FrameSheet.Builder? sheet = null;
        try
        {
            var reader = new Reader(data);
            reader.Position = 6;
            int canvasWidth = reader.ReadUInt16();
            int canvasHeight = reader.ReadUInt16();
            byte packed = reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            if (canvasWidth <= 0 || canvasHeight <= 0 || canvasWidth > FrameSheet.MaxEdge || canvasHeight > FrameSheet.MaxEdge)
                return null;

            int total = System.Math.Min(CountFrames(data), limit);
            sheet = FrameSheet.Builder.Begin(canvasWidth, canvasHeight, System.Math.Max(1, total), cap);
            if (sheet == null)
                return null;

            byte[]? globalPalette = null;
            if ((packed & 0x80) != 0)
            {
                int size = 3 << ((packed & 0x07) + 1);
                globalPalette = reader.ReadBytes(size);
            }

            var canvas = new byte[canvasWidth * canvasHeight * 4];
            byte[]? restore = null;
            int delay = 0;
            int transparentIndex = -1;
            int disposal = 0;
            int index = 0;

            while (!reader.End && index < sheet.Count)
            {
                byte block = reader.ReadByte();
                if (block == 0x3B)
                    break;

                if (block == 0x21)
                {
                    byte label = reader.ReadByte();
                    if (label == 0xF9)
                    {
                        int length = reader.ReadByte();
                        int start = reader.Position;
                        byte flags = reader.ReadByte();
                        delay = reader.ReadUInt16();
                        int transparent = reader.ReadByte();
                        disposal = (flags >> 2) & 0x07;
                        transparentIndex = (flags & 0x01) != 0 ? transparent : -1;
                        reader.Position = start + length;
                        reader.SkipSubBlocks();
                    }
                    else
                    {
                        reader.SkipSubBlocks();
                    }
                    continue;
                }

                if (block != 0x2C)
                    break;

                int left = reader.ReadUInt16();
                int top = reader.ReadUInt16();
                int frameWidth = reader.ReadUInt16();
                int frameHeight = reader.ReadUInt16();
                byte framePacked = reader.ReadByte();
                bool interlaced = (framePacked & 0x40) != 0;
                byte[]? palette = globalPalette;
                if ((framePacked & 0x80) != 0)
                {
                    int size = 3 << ((framePacked & 0x07) + 1);
                    palette = reader.ReadBytes(size);
                }
                if (palette == null)
                    break;

                int minCodeSize = reader.ReadByte();
                byte[] compressed = reader.ReadSubBlocks();
                byte[] indices = Lzw.Decode(compressed, minCodeSize, frameWidth * frameHeight);

                if (disposal == 3)
                {
                    restore = new byte[canvas.Length];
                    Buffer.BlockCopy(canvas, 0, restore, 0, canvas.Length);
                }

                Draw(canvas, canvasWidth, canvasHeight, indices, palette, transparentIndex, left, top, frameWidth, frameHeight, interlaced);

                // The delay is in hundredths of a second.
                sheet.Add(index, sheet.Wants(index) ? canvas : null, delay * 10);
                index++;

                // What the next frame starts from: this one, this area cleared, or the one before.
                if (disposal == 2)
                    Clear(canvas, canvasWidth, canvasHeight, left, top, frameWidth, frameHeight);
                else if (disposal == 3 && restore != null)
                    Buffer.BlockCopy(restore, 0, canvas, 0, canvas.Length);

                delay = 0;
                transparentIndex = -1;
                disposal = 0;
            }

            return sheet.Finish();
        }
        catch (Exception)
        {
            // A file that breaks off keeps the frames it had.
            return sheet?.Finish();
        }
    }

    private static void Draw(byte[] canvas, int canvasWidth, int canvasHeight, byte[] indices, byte[] palette, int transparentIndex,
        int left, int top, int frameWidth, int frameHeight, bool interlaced)
    {
        int paletteEntries = palette.Length / 3;
        int source = 0;
        for (int row = 0; row < frameHeight; row++)
        {
            int y = top + (interlaced ? InterlacedRow(row, frameHeight) : row);
            for (int x = 0; x < frameWidth; x++, source++)
            {
                if (source >= indices.Length) return;
                int index = indices[source];
                if (index == transparentIndex || index >= paletteEntries) continue;
                int px = left + x;
                if (px < 0 || px >= canvasWidth || y < 0 || y >= canvasHeight) continue;
                int at = (y * canvasWidth + px) * 4;
                canvas[at] = palette[index * 3];
                canvas[at + 1] = palette[index * 3 + 1];
                canvas[at + 2] = palette[index * 3 + 2];
                canvas[at + 3] = 255;
            }
        }
    }

    private static int InterlacedRow(int row, int height)
    {
        // Four passes: every 8th row from 0, every 8th from 4, every 4th from 2, every 2nd from 1.
        int pass0 = (height + 7) / 8;
        int pass1 = (height + 3) / 8;
        int pass2 = (height + 1) / 4;
        if (row < pass0) return row * 8;
        row -= pass0;
        if (row < pass1) return row * 8 + 4;
        row -= pass1;
        if (row < pass2) return row * 4 + 2;
        row -= pass2;
        return row * 2 + 1;
    }

    private static void Clear(byte[] canvas, int canvasWidth, int canvasHeight, int left, int top, int frameWidth, int frameHeight)
    {
        for (int y = System.Math.Max(0, top); y < System.Math.Min(canvasHeight, top + frameHeight); y++)
        {
            int start = (y * canvasWidth + System.Math.Max(0, left)) * 4;
            int count = (System.Math.Min(canvasWidth, left + frameWidth) - System.Math.Max(0, left)) * 4;
            if (count > 0) Array.Clear(canvas, start, count);
        }
    }

    private sealed class Reader
    {
        private readonly byte[] _data;
        public int Position;
        public Reader(byte[] data) => _data = data;
        public bool End => Position >= _data.Length;
        public byte ReadByte() => Position < _data.Length ? _data[Position++] : throw new IndexOutOfRangeException();
        public int ReadUInt16() { int v = ReadByte(); return v | (ReadByte() << 8); }
        public byte[] ReadBytes(int count)
        {
            if (Position + count > _data.Length) throw new IndexOutOfRangeException();
            var bytes = new byte[count];
            Buffer.BlockCopy(_data, Position, bytes, 0, count);
            Position += count;
            return bytes;
        }
        public void SkipSubBlocks()
        {
            while (!End)
            {
                int length = ReadByte();
                if (length == 0) return;
                Position += length;
            }
        }
        public byte[] ReadSubBlocks()
        {
            var chunks = new List<byte>(4096);
            while (!End)
            {
                int length = ReadByte();
                if (length == 0) break;
                if (Position + length > _data.Length) throw new IndexOutOfRangeException();
                for (int i = 0; i < length; i++) chunks.Add(_data[Position + i]);
                Position += length;
            }
            return chunks.ToArray();
        }
    }

    // Variable-width LZW with the clear and end codes the format defines; the dictionary is a prefix
    // chain so no string is ever materialised until it is written out.
    private static class Lzw
    {
        public static byte[] Decode(byte[] input, int minCodeSize, int expected)
        {
            var output = new byte[System.Math.Max(0, expected)];
            if (minCodeSize < 2 || minCodeSize > 11 || expected <= 0)
                return output;

            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;
            int codeSize = minCodeSize + 1;
            int nextCode = endCode + 1;
            int maxCodes = 1 << 12;

            var prefix = new int[maxCodes];
            var suffix = new byte[maxCodes];
            var length = new int[maxCodes];
            for (int i = 0; i < clearCode; i++)
            {
                prefix[i] = -1;
                suffix[i] = (byte)i;
                length[i] = 1;
            }

            int outPos = 0;
            int previous = -1;
            uint bitBuffer = 0;
            int bitCount = 0;
            int inPos = 0;
            var stack = new byte[maxCodes + 1];

            while (outPos < expected)
            {
                while (bitCount < codeSize)
                {
                    if (inPos >= input.Length) return output;
                    bitBuffer |= (uint)input[inPos++] << bitCount;
                    bitCount += 8;
                }
                int code = (int)(bitBuffer & ((1u << codeSize) - 1));
                bitBuffer >>= codeSize;
                bitCount -= codeSize;

                if (code == clearCode)
                {
                    codeSize = minCodeSize + 1;
                    nextCode = endCode + 1;
                    previous = -1;
                    continue;
                }
                if (code == endCode)
                    break;

                int emit;
                byte first;
                if (code < nextCode)
                {
                    emit = code;
                    first = FirstByte(prefix, suffix, code);
                }
                else if (code == nextCode && previous >= 0)
                {
                    emit = previous;
                    first = FirstByte(prefix, suffix, previous);
                }
                else
                {
                    return output;
                }

                // Write the string for emit, then the special-case trailing byte when code == nextCode.
                int count = length[emit];
                int walk = emit;
                for (int i = count - 1; i >= 0; i--)
                {
                    stack[i] = suffix[walk];
                    walk = prefix[walk];
                }
                for (int i = 0; i < count && outPos < expected; i++)
                    output[outPos++] = stack[i];
                if (code == nextCode && outPos < expected)
                    output[outPos++] = first;

                if (previous >= 0 && nextCode < maxCodes)
                {
                    prefix[nextCode] = previous;
                    suffix[nextCode] = first;
                    length[nextCode] = length[previous] + 1;
                    nextCode++;
                    if (nextCode == (1 << codeSize) && codeSize < 12)
                        codeSize++;
                }
                previous = code;
            }
            return output;
        }

        private static byte FirstByte(int[] prefix, byte[] suffix, int code)
        {
            while (prefix[code] >= 0) code = prefix[code];
            return suffix[code];
        }
    }
}
