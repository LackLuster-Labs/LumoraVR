// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
//
// The bitstream decoding below is adapted from crn_decomp.h, the crunch texture transcoder:
// Copyright (c) 2010-2016 Richard Geldreich, Jr. and Binomial LLC, under the zlib license. This is an
// altered version (ported to C#, BC1 and BC3 only); see THIRD_PARTY_NOTICES.md for the full notice.

using System;

namespace Lumora.Core.Assets;

// .crn files: BC1/BC3 textures stored as palettes of block endpoints and selectors with each block's picks
// Huffman-coded against its neighbours. Getting the blocks back is a table walk, a few milliseconds for a
// 2048 map, where decoding the source picture and compressing it again costs a hundred times that. Packages
// carry their textures this way next to the originals, every mip level of every size they were asked for.
//
// Only the two formats the renderer takes as they are: BC1, and BC3 with its colour in RGB. Anything else
// (swizzled normal maps, ETC) answers null and the caller goes the long way. -xlinka
public static class CrnTranscoder
{
    public enum Format
    {
        Dxt1 = 0,
        Dxt3 = 1,
        Dxt5 = 2,
        Dxt5CCxY = 3,
        Dxt5xGxR = 4,
        Dxt5xGBR = 5,
        Dxt5AGBR = 6,
        DxnXY = 7,
        DxnYX = 8,
        Dxt5A = 9,
        Etc1 = 10,
        Etc2 = 11,
        Etc2A = 12,
        Etc1S = 13,
        Etc2AS = 14,
    }

    public readonly record struct Header(int Width, int Height, int Levels, int Faces, Format Format, int BlockBytes);

    private const int HeaderMinBytes = 74;
    private const int LevelOffsets = 70;

    public static bool IsCrn(ReadOnlySpan<byte> data) => data.Length >= HeaderMinBytes && data[0] == 'H' && data[1] == 'x';

    public static bool TryReadHeader(ReadOnlySpan<byte> data, out Header header)
    {
        header = default;
        if (!IsCrn(data))
            return false;
        int headerSize = U16(data, 2);
        long dataSize = U32(data, 6);
        int width = U16(data, 12), height = U16(data, 14);
        int levels = data[16], faces = data[17];
        var format = (Format)data[18];
        if (headerSize < LevelOffsets + 4 * levels || dataSize > data.Length || width < 1 || height < 1 || levels < 1 || faces != 1)
            return false;
        if (format != Format.Dxt1 && format != Format.Dxt5)
            return false;
        header = new Header(width, height, levels, faces, format, format == Format.Dxt1 ? 8 : 16);
        return true;
    }

    // Bytes of one level's blocks.
    public static int LevelBytes(in Header header, int level)
        => Blocks(System.Math.Max(1, header.Width >> level)) * Blocks(System.Math.Max(1, header.Height >> level)) * header.BlockBytes;

    private static int Blocks(int pixels) => (pixels + 3) >> 2;

    // Every level, largest first, blocks left to right then top to bottom: the layout of a block-compressed
    // texture with its mips behind one buffer. Null for a file this does not take or one that does not decode.
    public static byte[]? Transcode(byte[] data, out Header header)
    {
        if (!TryReadHeader(data, out header))
            return null;
        try
        {
            var unpacker = new Unpacker(data, header);
            long total = 0;
            for (int level = 0; level < header.Levels; level++)
                total += LevelBytes(header, level);
            var output = new byte[total];
            int offset = 0;
            for (int level = 0; level < header.Levels; level++)
            {
                if (!unpacker.UnpackLevel(level, output, offset))
                    return null;
                offset += LevelBytes(header, level);
            }
            return output;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static int U16(ReadOnlySpan<byte> d, int at) => d[at] << 8 | d[at + 1];

    private static int U24(ReadOnlySpan<byte> d, int at) => d[at] << 16 | d[at + 1] << 8 | d[at + 2];

    private static uint U32(ReadOnlySpan<byte> d, int at) => (uint)(d[at] << 24 | d[at + 1] << 16 | d[at + 2] << 8 | d[at + 3]);

    // A canonical Huffman code given by its code lengths, with a lookup table for the short codes.
    private sealed class Model
    {
        private const int MaxCodeSize = 16;
        private const int MaxTableBits = 11;

        public int TotalSyms;
        public byte[] CodeSizes = Array.Empty<byte>();
        public bool Valid;

        public uint TableBits;
        public uint TableMaxCode;
        public int DecodeStartCodeSize;
        public readonly uint[] MaxCodes = new uint[MaxCodeSize + 1];
        public readonly int[] ValPtrs = new int[MaxCodeSize + 1];
        public uint[] Lookup = Array.Empty<uint>();
        public ushort[] Sorted = Array.Empty<ushort>();

        public bool Prepare()
        {
            TotalSyms = CodeSizes.Length;
            uint tableBits = 0;
            if (TotalSyms > 16)
                tableBits = (uint)System.Math.Min(1 + CeilLog2(TotalSyms), MaxTableBits);
            Valid = Init(tableBits);
            return Valid;
        }

        private static int CeilLog2(int v)
        {
            int l = 0;
            while ((1 << l) < v)
                l++;
            return l;
        }

        private bool Init(uint tableBits)
        {
            int numSyms = TotalSyms;
            if (numSyms == 0 || tableBits > MaxTableBits)
                return false;
            Span<uint> minCodes = stackalloc uint[MaxCodeSize];
            Span<uint> numCodes = stackalloc uint[MaxCodeSize + 1];
            Span<uint> sortedPositions = stackalloc uint[MaxCodeSize + 1];
            minCodes.Clear();
            numCodes.Clear();
            sortedPositions.Clear();
            for (int i = 0; i < numSyms; i++)
            {
                int c = CodeSizes[i];
                if (c > MaxCodeSize)
                    return false;
                if (c != 0)
                    numCodes[c]++;
            }

            uint curCode = 0, totalUsed = 0;
            int minCodeSize = int.MaxValue, maxCodeSize = 0;
            for (int i = 1; i <= MaxCodeSize; i++)
            {
                uint n = numCodes[i];
                if (n == 0)
                    MaxCodes[i - 1] = 0;
                else
                {
                    minCodeSize = System.Math.Min(minCodeSize, i);
                    maxCodeSize = System.Math.Max(maxCodeSize, i);
                    minCodes[i - 1] = curCode;
                    uint last = curCode + n - 1;
                    MaxCodes[i - 1] = 1 + ((last << (16 - i)) | (uint)((1 << (16 - i)) - 1));
                    ValPtrs[i - 1] = (int)totalUsed;
                    sortedPositions[i] = totalUsed;
                    curCode += n;
                    totalUsed += n;
                }
                curCode <<= 1;
            }
            if (totalUsed == 0)
                return false;

            Sorted = new ushort[totalUsed];
            for (int i = 0; i < numSyms; i++)
            {
                int c = CodeSizes[i];
                if (c != 0)
                    Sorted[sortedPositions[c]++] = (ushort)i;
            }

            if (tableBits <= (uint)minCodeSize)
                tableBits = 0;
            TableBits = tableBits;
            if (tableBits != 0)
            {
                Lookup = new uint[1 << (int)tableBits];
                Array.Fill(Lookup, uint.MaxValue);
                for (int codeSize = 1; codeSize <= (int)tableBits; codeSize++)
                {
                    if (numCodes[codeSize] == 0)
                        continue;
                    int fillSize = (int)tableBits - codeSize;
                    int fillNum = 1 << fillSize;
                    uint minCode = minCodes[codeSize - 1];
                    uint maxCode = UnshiftedMaxCode(codeSize);
                    int valPtr = ValPtrs[codeSize - 1];
                    for (uint code = minCode; code <= maxCode; code++)
                    {
                        uint sym = Sorted[valPtr + (int)(code - minCode)];
                        for (int j = 0; j < fillNum; j++)
                            Lookup[j + (int)(code << fillSize)] = sym | ((uint)codeSize << 16);
                    }
                }
            }

            for (int i = 0; i < MaxCodeSize; i++)
                ValPtrs[i] -= (int)minCodes[i];

            TableMaxCode = 0;
            DecodeStartCodeSize = minCodeSize;
            if (tableBits != 0)
            {
                int i;
                for (i = (int)tableBits; i >= 1; i--)
                {
                    if (numCodes[i] != 0)
                    {
                        TableMaxCode = MaxCodes[i - 1];
                        break;
                    }
                }
                if (i >= 1)
                {
                    DecodeStartCodeSize = (int)tableBits + 1;
                    for (int j = (int)tableBits + 1; j <= maxCodeSize; j++)
                    {
                        if (numCodes[j] != 0)
                        {
                            DecodeStartCodeSize = j;
                            break;
                        }
                    }
                }
            }

            MaxCodes[MaxCodeSize] = uint.MaxValue;
            ValPtrs[MaxCodeSize] = 0xFFFFF;
            return true;
        }

        private uint UnshiftedMaxCode(int len)
        {
            uint k = MaxCodes[len - 1];
            if (k == 0)
                return uint.MaxValue;
            return (k - 1) >> (16 - len);
        }
    }

    // MSB-first bit reader over one section of the file, with the canonical Huffman decode.
    private struct Codec
    {
        private byte[] _data;
        private int _next, _end;
        private uint _bits;
        private int _count;

        public void Start(byte[] data, int offset, int size)
        {
            if (size <= 0 || offset < 0 || offset + size > data.Length)
                throw new ArgumentException("section outside the file");
            _data = data;
            _next = offset;
            _end = offset + size;
            _bits = 0;
            _count = 0;
        }

        public uint DecodeBits(int numBits)
        {
            if (numBits == 0)
                return 0;
            if (numBits > 16)
            {
                uint a = GetBits(numBits - 16);
                uint b = GetBits(16);
                return (a << 16) | b;
            }
            return GetBits(numBits);
        }

        private uint GetBits(int numBits)
        {
            while (_count < numBits)
            {
                uint c = _next != _end ? _data[_next++] : 0u;
                _count += 8;
                _bits |= c << (32 - _count);
            }
            uint result = _bits >> (32 - numBits);
            _bits <<= numBits;
            _count -= numBits;
            return result;
        }

        public uint Decode(Model model)
        {
            if (_count < 24)
            {
                if (_count < 16)
                {
                    uint c0 = _next < _end ? _data[_next++] : 0u;
                    uint c1 = _next < _end ? _data[_next++] : 0u;
                    _count += 16;
                    _bits |= ((c0 << 8) | c1) << (32 - _count);
                }
                else
                {
                    uint c = _next < _end ? _data[_next++] : 0u;
                    _count += 8;
                    _bits |= c << (32 - _count);
                }
            }

            uint k = (_bits >> 16) + 1;
            uint sym;
            int len;
            if (k <= model.TableMaxCode)
            {
                uint t = model.Lookup[_bits >> (32 - (int)model.TableBits)];
                if (t == uint.MaxValue)
                    throw new InvalidOperationException("bad code");
                sym = t & 0xFFFF;
                len = (int)(t >> 16);
            }
            else
            {
                len = model.DecodeStartCodeSize;
                while (k > model.MaxCodes[len - 1])
                    len++;
                int valPtr = model.ValPtrs[len - 1] + (int)(_bits >> (32 - len));
                if ((uint)valPtr >= (uint)model.TotalSyms || (uint)valPtr >= (uint)model.Sorted.Length)
                    throw new InvalidOperationException("bad code");
                sym = model.Sorted[valPtr];
            }
            _bits <<= len;
            _count -= len;
            return sym;
        }

        private static readonly byte[] MostProbableCodeLengthCodes = { 17, 18, 19, 20, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15, 16 };

        public Model ReceiveModel()
        {
            var model = new Model();
            int totalUsed = (int)DecodeBits(14);
            if (totalUsed == 0)
                return model;
            model.CodeSizes = new byte[totalUsed];

            int send = (int)DecodeBits(5);
            if (send < 1 || send > 21)
                throw new InvalidOperationException("bad code length table");
            var lengths = new Model { CodeSizes = new byte[21] };
            for (int i = 0; i < send; i++)
                lengths.CodeSizes[MostProbableCodeLengthCodes[i]] = (byte)DecodeBits(3);
            if (!lengths.Prepare())
                throw new InvalidOperationException("bad code length table");

            int ofs = 0;
            while (ofs < totalUsed)
            {
                int remaining = totalUsed - ofs;
                uint code = Decode(lengths);
                if (code <= 16)
                    model.CodeSizes[ofs++] = (byte)code;
                else if (code == 17 || code == 18)
                {
                    int run = code == 17 ? (int)DecodeBits(3) + 3 : (int)DecodeBits(7) + 11;
                    if (run > remaining)
                        throw new InvalidOperationException("bad zero run");
                    ofs += run;
                }
                else
                {
                    int run = code == 19 ? (int)DecodeBits(2) + 3 : (int)DecodeBits(6) + 7;
                    if (ofs == 0 || run > remaining || model.CodeSizes[ofs - 1] == 0)
                        throw new InvalidOperationException("bad repeat");
                    byte prev = model.CodeSizes[ofs - 1];
                    for (int end = ofs + run; ofs < end; ofs++)
                        model.CodeSizes[ofs] = prev;
                }
            }
            if (!model.Prepare())
                throw new InvalidOperationException("bad code lengths");
            return model;
        }
    }

    private sealed class Unpacker
    {
        private static readonly byte[] Dxt5FromLinear = { 0, 2, 3, 4, 5, 6, 7, 1 };

        private readonly byte[] _data;
        private readonly Header _header;
        private Codec _codec;
        private readonly Model _reference;
        private readonly Model _colorEndpointDelta = new(), _colorSelectorDelta = new();
        private readonly Model _alphaEndpointDelta = new(), _alphaSelectorDelta = new();
        private uint[] _colorEndpoints = Array.Empty<uint>();
        private uint[] _colorSelectors = Array.Empty<uint>();
        private ushort[] _alphaEndpoints = Array.Empty<ushort>();
        private ushort[] _alphaSelectors = Array.Empty<ushort>();
        private (int Reference, int Color, int Alpha)[] _row = Array.Empty<(int, int, int)>();

        public Unpacker(byte[] data, Header header)
        {
            _data = data;
            _header = header;
            ReadOnlySpan<byte> d = data;
            int colorEndpointsNum = U16(d, 39), alphaEndpointsNum = U16(d, 55);
            if (colorEndpointsNum == 0 && alphaEndpointsNum == 0)
                throw new InvalidOperationException("no palettes");

            _codec.Start(data, U24(d, 67), U16(d, 65));
            _reference = _codec.ReceiveModel();
            if (colorEndpointsNum != 0)
            {
                _colorEndpointDelta = _codec.ReceiveModel();
                _colorSelectorDelta = _codec.ReceiveModel();
            }
            if (alphaEndpointsNum != 0)
            {
                _alphaEndpointDelta = _codec.ReceiveModel();
                _alphaSelectorDelta = _codec.ReceiveModel();
            }

            if (colorEndpointsNum != 0)
            {
                ColorEndpoints(U24(d, 33), U24(d, 36), colorEndpointsNum);
                ColorSelectors(U24(d, 41), U24(d, 44), U16(d, 47));
            }
            if (alphaEndpointsNum != 0)
            {
                AlphaEndpoints(U24(d, 49), U24(d, 52), alphaEndpointsNum);
                AlphaSelectors(U24(d, 57), U24(d, 60), U16(d, 63));
            }
            if (header.Format == Format.Dxt5 && (alphaEndpointsNum == 0 || colorEndpointsNum == 0))
                throw new InvalidOperationException("BC3 without both palettes");
        }

        private void ColorEndpoints(int ofs, int size, int num)
        {
            _codec.Start(_data, ofs, size);
            var dm0 = _codec.ReceiveModel();
            var dm1 = _codec.ReceiveModel();
            _colorEndpoints = new uint[num];
            uint a = 0, b = 0, c = 0, d = 0, e = 0, f = 0;
            for (int i = 0; i < num; i++)
            {
                a = (a + _codec.Decode(dm0)) & 31;
                b = (b + _codec.Decode(dm1)) & 63;
                c = (c + _codec.Decode(dm0)) & 31;
                d = (d + _codec.Decode(dm0)) & 31;
                e = (e + _codec.Decode(dm1)) & 63;
                f = (f + _codec.Decode(dm0)) & 31;
                _colorEndpoints[i] = c | (b << 5) | (a << 11) | (f << 16) | (e << 21) | (d << 27);
            }
        }

        private void ColorSelectors(int ofs, int size, int num)
        {
            _codec.Start(_data, ofs, size);
            var dm = _codec.ReceiveModel();
            _colorSelectors = new uint[num];
            uint s = 0;
            for (int i = 0; i < num; i++)
            {
                for (int j = 0; j < 32; j += 4)
                    s ^= _codec.Decode(dm) << j;
                _colorSelectors[i] = ((s ^ s << 1) & 0xAAAAAAAA) | (s >> 1 & 0x55555555);
            }
        }

        private void AlphaEndpoints(int ofs, int size, int num)
        {
            _codec.Start(_data, ofs, size);
            var dm = _codec.ReceiveModel();
            _alphaEndpoints = new ushort[num];
            uint a = 0, b = 0;
            for (int i = 0; i < num; i++)
            {
                a = (a + _codec.Decode(dm)) & 255;
                b = (b + _codec.Decode(dm)) & 255;
                _alphaEndpoints[i] = (ushort)(a | (b << 8));
            }
        }

        private void AlphaSelectors(int ofs, int size, int num)
        {
            _codec.Start(_data, ofs, size);
            var dm = _codec.ReceiveModel();
            _alphaSelectors = new ushort[num * 3];
            Span<byte> fromLinear = stackalloc byte[64];
            for (int i = 0; i < 64; i++)
                fromLinear[i] = (byte)(Dxt5FromLinear[i & 7] | Dxt5FromLinear[i >> 3] << 3);
            uint s0Linear = 0, s1Linear = 0;
            for (int i = 0; i < _alphaSelectors.Length;)
            {
                uint s0 = 0, s1 = 0;
                for (int j = 0; j < 24; j += 6)
                {
                    s0Linear ^= _codec.Decode(dm) << j;
                    s0 |= (uint)fromLinear[(int)(s0Linear >> j & 0x3F)] << j;
                }
                for (int j = 0; j < 24; j += 6)
                {
                    s1Linear ^= _codec.Decode(dm) << j;
                    s1 |= (uint)fromLinear[(int)(s1Linear >> j & 0x3F)] << j;
                }
                _alphaSelectors[i++] = (ushort)s0;
                _alphaSelectors[i++] = (ushort)(s0 >> 16 | s1 << 8);
                _alphaSelectors[i++] = (ushort)(s1 >> 8);
            }
        }

        public bool UnpackLevel(int level, byte[] output, int offset)
        {
            ReadOnlySpan<byte> d = _data;
            int start = (int)U32(d, LevelOffsets + 4 * level);
            int end = level + 1 < _header.Levels ? (int)U32(d, LevelOffsets + 4 * (level + 1)) : (int)U32(d, 6);
            if (end <= start)
                return false;
            _codec.Start(_data, start, end - start);

            int blocksX = Blocks(System.Math.Max(1, _header.Width >> level));
            int blocksY = Blocks(System.Math.Max(1, _header.Height >> level));
            int width = (blocksX + 1) & ~1;
            int height = (blocksY + 1) & ~1;
            if (_row.Length < width)
                _row = new (int, int, int)[width];
            bool alpha = _header.Format == Format.Dxt5;
            int blockBytes = _header.BlockBytes;
            int colorCount = _colorEndpoints.Length, alphaCount = _alphaEndpoints.Length;
            int color = 0, alphaIndex = 0;
            uint group = 0;

            for (int y = 0; y < height; y++)
            {
                bool rowVisible = y < blocksY;
                for (int x = 0; x < width; x++)
                {
                    if ((y & 1) == 0 && (x & 1) == 0)
                        group = _codec.Decode(_reference);
                    ref var cell = ref _row[x];
                    int reference;
                    if ((y & 1) != 0)
                        reference = cell.Reference;
                    else
                    {
                        reference = (int)(group & 3);
                        group >>= 2;
                        cell.Reference = (int)(group & 3);
                        group >>= 2;
                    }
                    if (reference == 0)
                    {
                        color += (int)_codec.Decode(_colorEndpointDelta);
                        if (color >= colorCount)
                            color -= colorCount;
                        cell.Color = color;
                        if (alpha)
                        {
                            alphaIndex += (int)_codec.Decode(_alphaEndpointDelta);
                            if (alphaIndex >= alphaCount)
                                alphaIndex -= alphaCount;
                            cell.Alpha = alphaIndex;
                        }
                    }
                    else if (reference == 1)
                    {
                        cell.Color = color;
                        cell.Alpha = alphaIndex;
                    }
                    else
                    {
                        color = cell.Color;
                        alphaIndex = cell.Alpha;
                    }

                    uint colorSelector = _codec.Decode(_colorSelectorDelta);
                    uint alphaSelector = alpha ? _codec.Decode(_alphaSelectorDelta) : 0;
                    if (!rowVisible || x >= blocksX)
                        continue;

                    int at = offset + (y * blocksX + x) * blockBytes;
                    if (alpha)
                    {
                        int s = (int)alphaSelector * 3;
                        Write(output, at, _alphaEndpoints[alphaIndex] | (uint)_alphaSelectors[s] << 16);
                        Write(output, at + 4, _alphaSelectors[s + 1] | (uint)_alphaSelectors[s + 2] << 16);
                        at += 8;
                    }
                    Write(output, at, _colorEndpoints[color]);
                    Write(output, at + 4, _colorSelectors[colorSelector]);
                }
            }
            return true;
        }

        private static void Write(byte[] output, int at, uint value)
        {
            output[at] = (byte)value;
            output[at + 1] = (byte)(value >> 8);
            output[at + 2] = (byte)(value >> 16);
            output[at + 3] = (byte)(value >> 24);
        }
    }
}
