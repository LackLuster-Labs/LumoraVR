// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Reading and writing the int4 rings: commands, releases, requests, reports. A ring is a fixed array of entry pairs
// with one writer and a sequence beside it. The writer puts a pair at (sequence mod N) and bumps the sequence in the
// same frame, so both land in one batch on every peer. A reader keeps the last sequence it saw: a new reader starts at
// the current one and replays nothing, a gap wider than the ring skips the lost entries, and a sequence that went
// backwards (the writer's record was made again, after a respawn) restarts the cursor there. Only changed entries
// travel, as sparse values, so a quiet ring costs nothing. -xlinka
public static class FluidRings
{
    // A cursor that has not read anything yet.
    public const int NewCursor = -1;

    public static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    public static float Float(int bits) => BitConverter.Int32BitsToSingle(bits);

    public static int Low(ulong id) => unchecked((int)(uint)id);

    public static int High(ulong id) => unchecked((int)(uint)(id >> 32));

    public static ulong Join(int low, int high) => (uint)low | ((ulong)(uint)high << 32);

    public static int Low(double value) => Low((ulong)BitConverter.DoubleToInt64Bits(value));

    public static int High(double value) => High((ulong)BitConverter.DoubleToInt64Bits(value));

    public static double JoinDouble(int low, int high) => BitConverter.Int64BitsToDouble((long)Join(low, high));

    // m/s in steps of 1/16, 10 signed bits an axis: about 32 m/s either way, which nothing thrown by hand passes.
    public static int PackVelocity(float3 v) => Axis(v.x) | (Axis(v.y) << 10) | (Axis(v.z) << 20);

    public static float3 UnpackVelocity(int packed) => new(Unaxis(packed), Unaxis(packed >> 10), Unaxis(packed >> 20));

    private static int Axis(float value) => Math.Clamp((int)MathF.Round(value * 16f), -511, 511) & 1023;

    private static float Unaxis(int bits)
    {
        int v = bits & 1023;
        if (v >= 512)
            v -= 1024;
        return v / 16f;
    }

    public static int Entries(SyncArray<int4> ring) => ring.Count / 2;

    // The writer sizes its ring once, in a send with nothing else in the array, before its first entry.
    public static bool EnsureLength(SyncArray<int4> ring, int length)
    {
        if (ring.Count == length)
            return true;
        if (ring.Count > length)
            return false;
        var add = new int4[length - ring.Count];
        ring.AddRange(add);
        return ring.Count == length;
    }

    public static bool Write(SyncArray<int4> ring, Sync<int> sequence, int4 a, int4 b)
    {
        int n = Entries(ring);
        if (n == 0)
            return false;
        int seq = sequence.Value;
        int slot = (int)((uint)seq % (uint)n);
        ring[slot * 2] = a;
        ring[slot * 2 + 1] = b;
        sequence.Value = seq + 1;
        return true;
    }

    // Appends what was written since cursor to output, oldest first, and moves the cursor to the sequence. Returns
    // how many entries were lost to a gap wider than the ring (the caller logs that once).
    public static int Read(SyncArray<int4> ring, int sequence, ref int cursor, List<(int4 A, int4 B)> output)
    {
        int n = Entries(ring);
        if (cursor == NewCursor || sequence < cursor)
        {
            cursor = sequence;
            return 0;
        }
        if (n == 0 || sequence == cursor)
            return 0;
        int skipped = 0;
        if (sequence - cursor > n)
        {
            skipped = sequence - n - cursor;
            cursor = sequence - n;
        }
        for (int s = cursor; s < sequence; s++)
        {
            int slot = (int)((uint)s % (uint)n);
            output.Add((ring[slot * 2], ring[slot * 2 + 1]));
        }
        cursor = sequence;
        return skipped;
    }
}
