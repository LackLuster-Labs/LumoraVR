// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using System.Text;

namespace Lumora.Core.Assets;

// The renderer's WebP decoder reads still images only; an animated one (chat emotes above all) is
// refused outright. The first frame of an animated file is a complete still bitstream sitting in
// its own chunk, so it is cut out and re-wrapped as a plain file before decoding. Where a single picture is
// wanted that is the first frame; the platform layer composites all of them into a FrameSheet. -xlinka
public static class WebpStill
{
    // One frame of an animated file: its own still bytes, where it lands on the canvas, how long it
    // shows, and how it composes over what came before.
    public readonly struct Frame
    {
        public readonly byte[] Still;
        public readonly int X;
        public readonly int Y;
        public readonly int Width;
        public readonly int Height;
        public readonly int DurationMs;
        public readonly bool BlendAlpha;
        public readonly bool DisposeToBackground;

        public Frame(byte[] still, int x, int y, int width, int height, int durationMs, bool blendAlpha, bool disposeToBackground)
        {
            Still = still;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            DurationMs = durationMs;
            BlendAlpha = blendAlpha;
            DisposeToBackground = disposeToBackground;
        }
    }

    // Every frame of an animated file as a still plus its placement, in order, with the canvas size.
    public static bool TryExtractFrames(ReadOnlySpan<byte> data, out int canvasWidth, out int canvasHeight, out System.Collections.Generic.List<Frame> frames)
    {
        canvasWidth = 0;
        canvasHeight = 0;
        frames = new System.Collections.Generic.List<Frame>();
        if (!IsWebp(data))
            return false;

        bool animated = false;
        int position = 12;
        while (position + 8 <= data.Length)
        {
            uint size = ReadUInt32(data, position + 4);
            int payload = position + 8;
            long end = (long)payload + size;
            if (end > data.Length)
                break;

            if (Matches(data, position, "VP8X"))
            {
                animated = (data[payload] & 0x02) != 0;
                if (!animated)
                    return false;
                canvasWidth = 1 + ReadUInt24(data, payload + 4);
                canvasHeight = 1 + ReadUInt24(data, payload + 7);
            }
            else if (animated && Matches(data, position, "ANMF") && size >= 16)
            {
                int x = 2 * ReadUInt24(data, payload);
                int y = 2 * ReadUInt24(data, payload + 3);
                int width = 1 + ReadUInt24(data, payload + 6);
                int height = 1 + ReadUInt24(data, payload + 9);
                int duration = ReadUInt24(data, payload + 12);
                byte flags = data[payload + 15];
                bool blend = (flags & 0x02) == 0;
                bool dispose = (flags & 0x01) != 0;
                if (TryBuildStill(data, payload + 16, (int)end, width, height, out byte[] still))
                    frames.Add(new Frame(still, x, y, width, height, duration, blend, dispose));
            }

            position = (int)(end + (size & 1));
        }
        return animated && frames.Count > 0;
    }

    public static bool IsAnimated(ReadOnlySpan<byte> data)
    {
        if (!IsWebp(data)) return false;
        int position = 12;
        while (position + 8 <= data.Length)
        {
            uint size = ReadUInt32(data, position + 4);
            if (Matches(data, position, "VP8X"))
                return position + 8 < data.Length && (data[position + 8] & 0x02) != 0;
            position += 8 + (int)size + (int)(size & 1);
        }
        return false;
    }

    // ANMF chunks of an animated file, stopping at stopAt; 0 for anything else.
    public static int CountFrames(ReadOnlySpan<byte> data, int stopAt = int.MaxValue)
    {
        if (!IsAnimated(data))
            return 0;
        int count = 0;
        int position = 12;
        while (position + 8 <= data.Length && count < stopAt)
        {
            uint size = ReadUInt32(data, position + 4);
            if ((long)position + 8 + size > data.Length)
                break;
            if (Matches(data, position, "ANMF"))
                count++;
            position = (int)((long)position + 8 + size + (size & 1));
        }
        return count;
    }

    public static bool TryExtractFirstFrame(ReadOnlySpan<byte> data, out byte[] still)
    {
        still = Array.Empty<byte>();
        if (!IsWebp(data))
            return false;

        bool animated = false;
        int position = 12;
        while (position + 8 <= data.Length)
        {
            uint size = ReadUInt32(data, position + 4);
            int payload = position + 8;
            long end = (long)payload + size;
            if (end > data.Length)
                return false;

            if (Matches(data, position, "VP8X"))
            {
                animated = (data[payload] & 0x02) != 0;
                if (!animated)
                    return false;
            }
            else if (animated && Matches(data, position, "ANMF"))
            {
                if (size < 16)
                    return false;
                int width = 1 + ReadUInt24(data, payload + 6);
                int height = 1 + ReadUInt24(data, payload + 9);
                return TryBuildStill(data, payload + 16, (int)end, width, height, out still);
            }

            position = (int)(end + (size & 1));
        }
        return false;
    }

    private static bool TryBuildStill(ReadOnlySpan<byte> data, int from, int to, int width, int height, out byte[] still)
    {
        still = Array.Empty<byte>();
        ReadOnlySpan<byte> alpha = default;
        ReadOnlySpan<byte> bitstream = default;
        string type = string.Empty;
        bool hasAlpha = false;

        int position = from;
        while (position + 8 <= to)
        {
            uint size = ReadUInt32(data, position + 4);
            int payload = position + 8;
            long end = (long)payload + size;
            if (end > to)
                break;
            if (Matches(data, position, "ALPH"))
            {
                alpha = data.Slice(payload, (int)size);
                hasAlpha = true;
            }
            else if (Matches(data, position, "VP8 ") || Matches(data, position, "VP8L"))
            {
                bitstream = data.Slice(payload, (int)size);
                type = Encoding.ASCII.GetString(data.Slice(position, 4));
                break;
            }
            position = (int)(end + (size & 1));
        }

        if (bitstream.Length == 0)
            return false;

        using var stream = new MemoryStream(bitstream.Length + alpha.Length + 64);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0u);
        writer.Write(Encoding.ASCII.GetBytes("WEBP"));

        // Lossy with a separate alpha plane needs the extended header to say so; lossless carries its
        // own alpha, and lossy without alpha is the simplest file there is.
        if (hasAlpha && type == "VP8 ")
        {
            writer.Write(Encoding.ASCII.GetBytes("VP8X"));
            writer.Write(10u);
            writer.Write((byte)0x10);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            WriteUInt24(writer, width - 1);
            WriteUInt24(writer, height - 1);
            WriteChunk(writer, "ALPH", alpha);
        }
        WriteChunk(writer, type, bitstream);

        writer.Flush();
        still = stream.ToArray();
        uint riffSize = (uint)(still.Length - 8);
        still[4] = (byte)riffSize;
        still[5] = (byte)(riffSize >> 8);
        still[6] = (byte)(riffSize >> 16);
        still[7] = (byte)(riffSize >> 24);
        return true;
    }

    private static void WriteChunk(BinaryWriter writer, string fourcc, ReadOnlySpan<byte> payload)
    {
        writer.Write(Encoding.ASCII.GetBytes(fourcc));
        writer.Write((uint)payload.Length);
        writer.Write(payload);
        if ((payload.Length & 1) == 1)
            writer.Write((byte)0);
    }

    private static bool IsWebp(ReadOnlySpan<byte> data)
        => data.Length >= 20 && Matches(data, 0, "RIFF") && Matches(data, 8, "WEBP");

    private static bool Matches(ReadOnlySpan<byte> data, int at, string fourcc)
    {
        if (at + 4 > data.Length) return false;
        for (int i = 0; i < 4; i++)
        {
            if (data[at + i] != (byte)fourcc[i]) return false;
        }
        return true;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int at)
        => at + 4 <= data.Length
            ? (uint)(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24))
            : 0u;

    private static int ReadUInt24(ReadOnlySpan<byte> data, int at)
        => at + 3 <= data.Length ? data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) : 0;

    private static void WriteUInt24(BinaryWriter writer, int value)
    {
        writer.Write((byte)value);
        writer.Write((byte)(value >> 8));
        writer.Write((byte)(value >> 16));
    }
}
