// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The eight bits of a byte or an sbyte, bit0 the lowest. An sbyte's bit7 is its sign.
[Node("Math/Bits/Extract Bits", Description = "The eight bits of a byte or an sbyte, bit0 the lowest.",
    Keywords = "unpack bits, byte to bits, byte to booleans, bitfield")]
public sealed class ExtractBits : ValueNode
{
    public static void Run(byte value, out bool bit0, out bool bit1, out bool bit2, out bool bit3, out bool bit4, out bool bit5,
        out bool bit6, out bool bit7)
    {
        bit0 = (value & 1) != 0;
        bit1 = (value & 2) != 0;
        bit2 = (value & 4) != 0;
        bit3 = (value & 8) != 0;
        bit4 = (value & 16) != 0;
        bit5 = (value & 32) != 0;
        bit6 = (value & 64) != 0;
        bit7 = (value & 128) != 0;
    }

    public static void Run(sbyte value, out bool bit0, out bool bit1, out bool bit2, out bool bit3, out bool bit4, out bool bit5,
        out bool bit6, out bool bit7)
        => Run(unchecked((byte)value), out bit0, out bit1, out bit2, out bit3, out bit4, out bit5, out bit6, out bit7);
}
