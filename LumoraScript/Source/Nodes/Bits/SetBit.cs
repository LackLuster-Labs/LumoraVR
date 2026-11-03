// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// value with bit index set to bit, 0 the lowest. An index outside the number's width changes nothing.
[Node("Math/Bits/Set Bit", Description = "Value with bit index set to bit, 0 the lowest.",
    Keywords = "clear bit, write bit, set flag, bitmask")]
public sealed class SetBit : ValueNode
{
    public static int Run(int value, int index, bool bit = true)
        => (uint)index >= 32 ? value : bit ? value | (1 << index) : value & ~(1 << index);

    public static long Run(long value, int index, bool bit = true)
        => (uint)index >= 64 ? value : bit ? value | (1L << index) : value & ~(1L << index);

    public static uint Run(uint value, int index, bool bit = true)
        => (uint)index >= 32 ? value : bit ? value | (1u << index) : value & ~(1u << index);

    public static ulong Run(ulong value, int index, bool bit = true)
        => (uint)index >= 64 ? value : bit ? value | (1ul << index) : value & ~(1ul << index);

    public static short Run(short value, int index, bool bit = true) => unchecked((short)Run((ushort)value, index, bit));

    public static ushort Run(ushort value, int index, bool bit = true)
        => (uint)index >= 16 ? value : (ushort)(bit ? value | (1 << index) : value & ~(1 << index));

    public static sbyte Run(sbyte value, int index, bool bit = true) => unchecked((sbyte)Run((byte)value, index, bit));

    public static byte Run(byte value, int index, bool bit = true)
        => (uint)index >= 8 ? value : (byte)(bit ? value | (1 << index) : value & ~(1 << index));
}
