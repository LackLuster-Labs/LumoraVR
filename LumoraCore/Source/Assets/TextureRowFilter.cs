// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;

namespace Lumora.Core.Assets;

// Predict neighbouring pixels before compression. Each row records its predictor, so decoding restores
// every channel exactly; alpha and normal-map channels are treated like any other byte. -xlinka
internal static class TextureRowFilter
{
    public static void Write(Stream output, ReadOnlySpan<byte> rgba, int width, int height)
    {
        int stride = checked(width * 4);
        var left = new byte[stride];
        var above = new byte[stride];
        var paeth = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            long leftScore = 0, aboveScore = 0, paethScore = 0;
            for (int x = 0; x < stride; x++)
            {
                int at = y * stride + x;
                int a = x < 4 ? 0 : rgba[at - 4];
                int b = y == 0 ? 0 : rgba[at - stride];
                int c = x < 4 || y == 0 ? 0 : rgba[at - stride - 4];
                left[x] = unchecked((byte)(rgba[at] - a));
                above[x] = unchecked((byte)(rgba[at] - b));
                paeth[x] = unchecked((byte)(rgba[at] - Predict(a, b, c)));
                leftScore += System.Math.Abs((int)unchecked((sbyte)left[x]));
                aboveScore += System.Math.Abs((int)unchecked((sbyte)above[x]));
                paethScore += System.Math.Abs((int)unchecked((sbyte)paeth[x]));
            }
            int filter = aboveScore < leftScore ? 2 : 1;
            if (paethScore < System.Math.Min(leftScore, aboveScore)) filter = 4;
            output.WriteByte((byte)filter);
            output.Write(filter == 1 ? left : filter == 2 ? above : paeth);
        }
    }

    public static bool Read(ReadOnlySpan<byte> filtered, Span<byte> rgba, int width, int height)
    {
        int stride = checked(width * 4);
        if (filtered.Length != (long)(stride + 1) * height || rgba.Length != (long)stride * height)
            return false;
        for (int y = 0; y < height; y++)
        {
            int start = y * (stride + 1);
            int filter = filtered[start];
            if (filter != 1 && filter != 2 && filter != 4) return false;
            for (int x = 0; x < stride; x++)
            {
                int at = y * stride + x;
                int a = x < 4 ? 0 : rgba[at - 4];
                int b = y == 0 ? 0 : rgba[at - stride];
                int c = x < 4 || y == 0 ? 0 : rgba[at - stride - 4];
                int prediction = filter == 1 ? a : filter == 2 ? b : Predict(a, b, c);
                rgba[at] = unchecked((byte)(filtered[start + x + 1] + prediction));
            }
        }
        return true;
    }

    private static int Predict(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = System.Math.Abs(p - a), pb = System.Math.Abs(p - b), pc = System.Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
