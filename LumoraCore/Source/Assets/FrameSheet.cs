// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Assets;

// The frames of an animated picture (GIF, animated WebP) on one texture, in a grid read left to right, top row
// first: frame k is in column k % Columns, row k / Columns. A grid and not one tall strip, because a strip ran out
// of height after fifteen frames of an ordinary 480x270 GIF.
//
// The grid is planned at full frame size within MaxEdge a side, keeping every Nth frame once the file has more
// than fit (or more than MaxFrames); the frame a kept one stands for is shown for all of their time together, so a
// loop lasts as long as the file says however many frames were dropped. Under a lower size cap the whole sheet is
// then made smaller: a capped machine gets the same frames at a lower resolution, never fewer. -xlinka
public sealed class FrameSheet
{
    public const int MaxEdge = 4096;
    public const int MaxFrames = 128;

    // A frame shown for no time at all, or for a hundredth of a second, is shown for a tenth: what the format's
    // readers have always done with it.
    public const int ShortestFrameMs = 20;
    public const int UnsetFrameMs = 100;

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Frames { get; }
    public int Columns { get; }
    public int Rows => (Frames + Columns - 1) / Columns;

    // The file's own frame size, before any reduction.
    public int FrameWidth { get; }
    public int FrameHeight { get; }

    // How long each kept frame shows, in milliseconds.
    public int[] DurationsMs { get; }

    public bool Reduced => Width != Columns * FrameWidth || Height != Rows * FrameHeight;

    public int LoopMs
    {
        get
        {
            long total = 0;
            foreach (var ms in DurationsMs)
                total += ms;
            return (int)System.Math.Min(int.MaxValue, total);
        }
    }

    // The average rate, for whatever plays the sheet at one speed.
    public float Fps
    {
        get
        {
            int loop = LoopMs;
            return loop > 0 ? (float)System.Math.Clamp(Frames * 1000.0 / loop, 0.1, 50.0) : 10f;
        }
    }

    private FrameSheet(byte[] pixels, int width, int height, int frames, int columns, int frameWidth, int frameHeight, int[] durations)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Frames = frames;
        Columns = columns;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        DurationsMs = durations;
    }

    public static int FrameDuration(int ms) => ms < ShortestFrameMs ? UnsetFrameMs : ms;

    // A GIF with two or more pictures, or an animated WebP with two or more frames. Reads the container only.
    public static bool IsAnimatedSource(ReadOnlySpan<byte> data)
        => GifDecoder.IsGif(data) ? GifDecoder.CountFrames(data, 2) >= 2
            : WebpStill.IsAnimated(data) && WebpStill.CountFrames(data, 2) >= 2;

    public static bool IsAnimatedFile(string path)
    {
        try
        {
            return System.IO.File.Exists(path) && IsAnimatedSource(System.IO.File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Which kept frame shows at this many seconds into a loop that runs for ever from zero.
    public static int FrameAt(int[]? durationsMs, double seconds)
    {
        if (durationsMs == null || durationsMs.Length <= 1)
            return 0;
        long loop = 0;
        foreach (var ms in durationsMs)
            loop += System.Math.Max(1, ms);
        // The session clock reads like the wall clock, so the milliseconds overflow an int; wrap in long first.
        long at = (long)System.Math.Floor(seconds * 1000.0) % loop;
        if (at < 0)
            at += loop;
        for (int i = 0; i < durationsMs.Length; i++)
        {
            at -= System.Math.Max(1, durationsMs[i]);
            if (at < 0)
                return i;
        }
        return durationsMs.Length - 1;
    }

    // Where frame k sits on a sheet of this layout, in UV (V counts down from the top row).
    public static Math.Rect FrameRect(int frame, int frames, int columns)
    {
        if (frames <= 1)
            return Math.Rect.UnitRect;
        columns = System.Math.Clamp(columns, 1, frames);
        int rows = (frames + columns - 1) / columns;
        frame = ((frame % frames) + frames) % frames;
        float w = 1f / columns;
        float h = 1f / rows;
        return new Math.Rect(frame % columns * w, frame / columns * h, w, h);
    }

    // Collects the composited frames of one file in order and keeps the ones the plan wants.
    public sealed class Builder
    {
        private readonly int _count;
        private readonly int _step;
        private readonly int _kept;
        private readonly int _columns;
        private readonly int _frameWidth;
        private readonly int _frameHeight;
        private readonly int _cellWidth;
        private readonly int _cellHeight;
        private readonly int _width;
        private readonly byte[] _pixels;
        private readonly int[] _durations;
        private int _placed;

        private Builder(int count, int step, int kept, int columns, int rows, int frameWidth, int frameHeight, int cellWidth, int cellHeight)
        {
            _count = count;
            _step = step;
            _kept = kept;
            _columns = columns;
            _frameWidth = frameWidth;
            _frameHeight = frameHeight;
            _cellWidth = cellWidth;
            _cellHeight = cellHeight;
            _width = columns * cellWidth;
            _pixels = new byte[(long)_width * rows * cellHeight * 4];
            _durations = new int[kept];
        }

        // count: frames the file holds. cap: the size cap the texture loads under, 0 for none.
        public static Builder? Begin(int frameWidth, int frameHeight, int count, int cap)
        {
            if (frameWidth <= 0 || frameHeight <= 0 || count <= 0)
                return null;

            int maxColumns = System.Math.Max(1, MaxEdge / frameWidth);
            int maxRows = System.Math.Max(1, MaxEdge / frameHeight);
            long capacity = (long)maxColumns * maxRows;
            int limit = (int)System.Math.Max(1, System.Math.Min(System.Math.Min(count, MaxFrames), capacity));
            int step = System.Math.Max(1, (count + limit - 1) / limit);
            int kept = (count + step - 1) / step;

            // As near square as the frames allow, so neither edge reaches the limit before it has to.
            int columns = (int)System.Math.Ceiling(System.Math.Sqrt(kept * (double)frameHeight / frameWidth));
            columns = System.Math.Clamp(columns, 1, System.Math.Min(maxColumns, kept));
            int rows = (kept + columns - 1) / columns;
            if (rows > maxRows)
            {
                rows = maxRows;
                columns = (kept + rows - 1) / rows;
            }

            int edge = cap > 0 ? System.Math.Min(cap, MaxEdge) : MaxEdge;
            long sheetWidth = (long)columns * frameWidth;
            long sheetHeight = (long)rows * frameHeight;
            int cellWidth = frameWidth, cellHeight = frameHeight;
            long longest = System.Math.Max(sheetWidth, sheetHeight);
            if (longest > edge)
            {
                double scale = edge / (double)longest;
                cellWidth = System.Math.Max(1, (int)(frameWidth * scale));
                cellHeight = System.Math.Max(1, (int)(frameHeight * scale));
            }
            return new Builder(count, step, kept, columns, rows, frameWidth, frameHeight, cellWidth, cellHeight);
        }

        public int Count => _count;

        // Whether frame index is one the sheet keeps; the caller only has to hand over pixels for those.
        public bool Wants(int index) => index % _step == 0 && index / _step < _kept;

        // Every frame comes through here for its time; rgba (the whole canvas, top-down rows) only for wanted ones.
        public void Add(int index, byte[]? rgba, int durationMs)
        {
            if (index < 0 || index >= _count)
                return;
            int slot = System.Math.Min(index / _step, _kept - 1);
            _durations[slot] += FrameDuration(durationMs);
            if (rgba == null || !Wants(index) || slot != _placed)
                return;

            var cell = _cellWidth == _frameWidth && _cellHeight == _frameHeight
                ? rgba
                : BoxReducer.Reduce(rgba, _frameWidth, _frameHeight, _cellWidth, _cellHeight);
            int x = slot % _columns * _cellWidth;
            int y = slot / _columns * _cellHeight;
            int row = _cellWidth * 4;
            for (int r = 0; r < _cellHeight; r++)
                Buffer.BlockCopy(cell, r * row, _pixels, (((y + r) * _width) + x) * 4, row);
            _placed++;
        }

        // Null when no frame landed. A file that ends early keeps the frames it had.
        public FrameSheet? Finish()
        {
            if (_placed == 0)
                return null;
            int frames = _placed;
            int columns = System.Math.Min(_columns, frames);
            var durations = new int[frames];
            Array.Copy(_durations, durations, frames);
            for (int i = 0; i < frames; i++)
            {
                if (durations[i] <= 0)
                    durations[i] = UnsetFrameMs;
            }

            int rows = (frames + columns - 1) / columns;
            int width = columns * _cellWidth;
            int height = rows * _cellHeight;
            byte[] pixels = _pixels;
            if (width != _width || (long)height * width * 4 != pixels.LongLength)
            {
                pixels = new byte[(long)width * height * 4];
                for (int r = 0; r < height; r++)
                    Buffer.BlockCopy(_pixels, r * _width * 4, pixels, r * width * 4, width * 4);
            }
            return new FrameSheet(pixels, width, height, frames, columns, _frameWidth, _frameHeight, durations);
        }
    }
}
