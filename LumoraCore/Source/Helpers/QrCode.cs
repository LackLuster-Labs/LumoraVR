// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Text;

namespace Lumora.Core.Helpers;

public enum QrErrorLevel { L, M, Q, H }

// Byte-mode QR encoder, enough for otpauth URIs and other short text. Modules come back [row, column],
// true = dark, with no quiet zone: the caller pads 4 light modules on every side. -xlinka
public static class QrCode
{
    // ISO/IEC 18004 ECC bytes per block and block count, [level L/M/Q/H, version], column 0 unused.
    private static readonly byte[,] EccPerBlock =
    {
        { 0,  7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        { 0, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        { 0, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly byte[,] BlockCount =
    {
        { 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4,  4,  4,  4,  4,  6,  6,  6,  6,  7,  8,  8,  9,  9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5,  5,  8,  9,  9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        { 0, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8,  8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        { 0, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    // Format-info level codes are not in enum order.
    private static readonly int[] LevelBits = { 1, 0, 3, 2 };

    public static bool[,]? Encode(string text, QrErrorLevel level = QrErrorLevel.M)
    {
        int lv = (int)level;
        if (lv < 0 || lv > 3)
            return null;

        byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);

        int version = 0, countBits = 0, dataLen = 0;
        for (int v = 1; v <= 40; v++)
        {
            int cb = v <= 9 ? 8 : 16;
            int cap = RawDataModules(v) / 8 - EccPerBlock[lv, v] * BlockCount[lv, v];
            if (bytes.Length < (1 << cb) && 4 + cb + bytes.Length * 8 <= cap * 8)
            {
                version = v;
                countBits = cb;
                dataLen = cap;
                break;
            }
        }
        if (version == 0)
            return null;

        byte[] data = new byte[dataLen];
        int bit = 0;
        PutBits(data, ref bit, 0x4, 4);
        PutBits(data, ref bit, bytes.Length, countBits);
        for (int i = 0; i < bytes.Length; i++)
            PutBits(data, ref bit, bytes[i], 8);
        bit += System.Math.Min(4, dataLen * 8 - bit);
        for (int i = (bit + 7) >> 3, pad = 0xEC; i < dataLen; i++, pad ^= 0xEC ^ 0x11)
            data[i] = (byte)pad;

        byte[] codewords = AddEccAndInterleave(data, version, lv);

        int size = version * 4 + 17;
        var grid = new bool[size, size];
        var fixedMod = new bool[size, size];
        DrawFunctionPatterns(grid, fixedMod, version, size);
        PlaceCodewords(grid, fixedMod, codewords, size);

        // Try all 8 masks with their real format bits in place and keep the lowest penalty.
        int best = 0, bestScore = int.MaxValue;
        for (int m = 0; m < 8; m++)
        {
            ApplyMask(grid, fixedMod, size, m);
            DrawFormat(grid, fixedMod, size, lv, m);
            int score = Penalty(grid, size);
            if (score < bestScore)
            {
                bestScore = score;
                best = m;
            }
            ApplyMask(grid, fixedMod, size, m);
        }
        ApplyMask(grid, fixedMod, size, best);
        DrawFormat(grid, fixedMod, size, lv, best);
        return grid;
    }

    private static int RawDataModules(int v)
    {
        int r = (16 * v + 128) * v + 64;
        if (v >= 2)
        {
            int align = v / 7 + 2;
            r -= (25 * align - 10) * align - 55;
            if (v >= 7)
                r -= 36;
        }
        return r;
    }

    private static void PutBits(byte[] buf, ref int bit, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--, bit++)
            if (((value >> i) & 1) != 0)
                buf[bit >> 3] |= (byte)(0x80 >> (bit & 7));
    }

    private static byte[] AddEccAndInterleave(byte[] data, int version, int lv)
    {
        int blocks = BlockCount[lv, version];
        int ecc = EccPerBlock[lv, version];
        int raw = RawDataModules(version) / 8;
        int shortBlocks = blocks - raw % blocks;
        int shortData = raw / blocks - ecc;

        byte[] divisor = RsDivisor(ecc);
        byte[] eccAll = new byte[blocks * ecc];
        for (int b = 0, off = 0; b < blocks; b++)
        {
            int len = shortData + (b < shortBlocks ? 0 : 1);
            RsRemainder(data, off, len, divisor, eccAll, b * ecc);
            off += len;
        }

        byte[] result = new byte[raw];
        int k = 0;
        for (int i = 0; i <= shortData; i++)
            for (int b = 0; b < blocks; b++)
                if (i < shortData || b >= shortBlocks)
                    result[k++] = data[b * shortData + System.Math.Max(0, b - shortBlocks) + i];
        for (int i = 0; i < ecc; i++)
            for (int b = 0; b < blocks; b++)
                result[k++] = eccAll[b * ecc + i];
        return result;
    }

    // Generator with roots a^0..a^(degree-1), highest term first, leading 1 dropped.
    private static byte[] RsDivisor(int degree)
    {
        byte[] r = new byte[degree];
        r[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                r[j] = (byte)GfMul(r[j], root);
                if (j + 1 < degree)
                    r[j] ^= r[j + 1];
            }
            root = GfMul(root, 2);
        }
        return r;
    }

    private static void RsRemainder(byte[] data, int off, int len, byte[] divisor, byte[] dst, int dstOff)
    {
        int n = divisor.Length;
        for (int i = 0; i < len; i++)
        {
            int factor = data[off + i] ^ dst[dstOff];
            Array.Copy(dst, dstOff + 1, dst, dstOff, n - 1);
            dst[dstOff + n - 1] = 0;
            for (int j = 0; j < n; j++)
                dst[dstOff + j] ^= (byte)GfMul(divisor[j], factor);
        }
    }

    // GF(256) over x^8 + x^4 + x^3 + x^2 + 1.
    private static int GfMul(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z;
    }

    private static void Set(bool[,] grid, bool[,] fixedMod, int row, int col, bool dark)
    {
        grid[row, col] = dark;
        fixedMod[row, col] = true;
    }

    private static bool Bit(int value, int i) => ((value >> i) & 1) != 0;

    private static void DrawFunctionPatterns(bool[,] grid, bool[,] fixedMod, int version, int size)
    {
        for (int i = 0; i < size; i++)
        {
            Set(grid, fixedMod, 6, i, i % 2 == 0);
            Set(grid, fixedMod, i, 6, i % 2 == 0);
        }

        DrawFinder(grid, fixedMod, size, 3, 3);
        DrawFinder(grid, fixedMod, size, 3, size - 4);
        DrawFinder(grid, fixedMod, size, size - 4, 3);

        if (version >= 2)
        {
            int count = version / 7 + 2;
            int step = (version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
            int[] pos = new int[count];
            pos[0] = 6;
            for (int i = count - 1, p = size - 7; i >= 1; i--, p -= step)
                pos[i] = p;
            for (int i = 0; i < count; i++)
                for (int j = 0; j < count; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == count - 1) || (i == count - 1 && j == 0))
                        continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            Set(grid, fixedMod, pos[i] + dy, pos[j] + dx, System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != 1);
                }
        }

        DrawFormat(grid, fixedMod, size, 0, 0);

        if (version >= 7)
        {
            int rem = version;
            for (int i = 0; i < 12; i++)
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = version << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool dark = Bit(bits, i);
                int a = size - 11 + i % 3;
                int b = i / 3;
                Set(grid, fixedMod, b, a, dark);
                Set(grid, fixedMod, a, b, dark);
            }
        }
    }

    // Finder plus its light separator ring.
    private static void DrawFinder(bool[,] grid, bool[,] fixedMod, int size, int row, int col)
    {
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
            {
                int r = row + dy, c = col + dx;
                if (r < 0 || r >= size || c < 0 || c >= size)
                    continue;
                int dist = System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy));
                Set(grid, fixedMod, r, c, dist != 2 && dist != 4);
            }
    }

    private static void DrawFormat(bool[,] grid, bool[,] fixedMod, int size, int lv, int mask)
    {
        int data = LevelBits[lv] << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++)
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++)
            Set(grid, fixedMod, i, 8, Bit(bits, i));
        Set(grid, fixedMod, 7, 8, Bit(bits, 6));
        Set(grid, fixedMod, 8, 8, Bit(bits, 7));
        Set(grid, fixedMod, 8, 7, Bit(bits, 8));
        for (int i = 9; i < 15; i++)
            Set(grid, fixedMod, 8, 14 - i, Bit(bits, i));

        for (int i = 0; i < 8; i++)
            Set(grid, fixedMod, 8, size - 1 - i, Bit(bits, i));
        for (int i = 8; i < 15; i++)
            Set(grid, fixedMod, size - 15 + i, 8, Bit(bits, i));
        Set(grid, fixedMod, size - 8, 8, true);
    }

    // Column-pair zigzag from the bottom right, hopping the timing column; remainder bits stay light.
    private static void PlaceCodewords(bool[,] grid, bool[,] fixedMod, byte[] codewords, int size)
    {
        int i = 0, total = codewords.Length * 8;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
                right = 5;
            bool up = ((right + 1) & 2) == 0;
            for (int vert = 0; vert < size; vert++)
            {
                int row = up ? size - 1 - vert : vert;
                for (int j = 0; j < 2; j++)
                {
                    int col = right - j;
                    if (fixedMod[row, col] || i >= total)
                        continue;
                    grid[row, col] = ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                    i++;
                }
            }
        }
    }

    private static void ApplyMask(bool[,] grid, bool[,] fixedMod, int size, int mask)
    {
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
            {
                if (fixedMod[r, c])
                    continue;
                bool flip = mask switch
                {
                    0 => (r + c) % 2 == 0,
                    1 => r % 2 == 0,
                    2 => c % 3 == 0,
                    3 => (r + c) % 3 == 0,
                    4 => (r / 2 + c / 3) % 2 == 0,
                    5 => r * c % 2 + r * c % 3 == 0,
                    6 => (r * c % 2 + r * c % 3) % 2 == 0,
                    _ => ((r + c) % 2 + r * c % 3) % 2 == 0,
                };
                if (flip)
                    grid[r, c] = !grid[r, c];
            }
    }

    // The 4 standard rules; off-symbol counts as light, which is what the quiet zone shows a scanner.
    private static int Penalty(bool[,] grid, int n)
    {
        int score = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool cols = pass == 1;
            for (int a = 0; a < n; a++)
            {
                int run = 0;
                bool prev = false;
                for (int b = 0; b < n; b++)
                {
                    bool c = At(grid, a, b, cols);
                    if (run > 0 && c == prev)
                    {
                        run++;
                        continue;
                    }
                    if (run >= 5)
                        score += run - 2;
                    run = 1;
                    prev = c;
                }
                if (run >= 5)
                    score += run - 2;

                for (int b = 0; b + 6 < n; b++)
                {
                    if (At(grid, a, b, cols) && !At(grid, a, b + 1, cols) && At(grid, a, b + 2, cols)
                        && At(grid, a, b + 3, cols) && At(grid, a, b + 4, cols) && !At(grid, a, b + 5, cols)
                        && At(grid, a, b + 6, cols)
                        && (Light4(grid, n, a, b - 4, cols) || Light4(grid, n, a, b + 7, cols)))
                        score += 40;
                }
            }
        }

        int dark = 0;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
            {
                bool v = grid[r, c];
                if (v)
                    dark++;
                if (r + 1 < n && c + 1 < n && v == grid[r, c + 1] && v == grid[r + 1, c] && v == grid[r + 1, c + 1])
                    score += 3;
            }
        int total = n * n;
        score += System.Math.Abs(dark * 20 - total * 10) / total * 10;
        return score;
    }

    private static bool At(bool[,] grid, int a, int b, bool cols) => cols ? grid[b, a] : grid[a, b];

    private static bool Light4(bool[,] grid, int n, int a, int start, bool cols)
    {
        for (int b = start; b < start + 4; b++)
            if (b >= 0 && b < n && At(grid, a, b, cols))
                return false;
        return true;
    }
}
