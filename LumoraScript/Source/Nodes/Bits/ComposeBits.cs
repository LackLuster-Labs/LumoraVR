// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The byte eight bits make, bit0 the lowest. Signed is the same eight bits as an sbyte, bit7 its sign.
[Node("Math/Bits/Compose Bits", Description = "The byte eight bits make, bit0 the lowest.",
    Keywords = "pack bits, bits to byte, booleans to byte, bitfield")]
public sealed class ComposeBits : ValueNode
{
    [return: Port("value")]
    public static byte Run(bool bit0, bool bit1, bool bit2, bool bit3, bool bit4, bool bit5, bool bit6, bool bit7, out sbyte signed)
    {
        int bits = (bit0 ? 1 : 0) | (bit1 ? 2 : 0) | (bit2 ? 4 : 0) | (bit3 ? 8 : 0)
            | (bit4 ? 16 : 0) | (bit5 ? 32 : 0) | (bit6 ? 64 : 0) | (bit7 ? 128 : 0);
        signed = unchecked((sbyte)bits);
        return (byte)bits;
    }
}
