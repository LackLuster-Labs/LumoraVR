// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether bit index of value is set, 0 the lowest. An index outside the number's width reads as unset.
[Node("Math/Bits/Get Bit", Description = "Whether bit index of value is set, 0 the lowest.",
    Keywords = "test bit, check bit, read flag, bitmask")]
public sealed class GetBit : ValueNode
{
    [return: Port("bit")] public static bool Run(int value, int index) => (uint)index < 32 && ((value >> index) & 1) != 0;
    [return: Port("bit")] public static bool Run(long value, int index) => (uint)index < 64 && ((value >> index) & 1) != 0;
    [return: Port("bit")] public static bool Run(uint value, int index) => (uint)index < 32 && ((value >> index) & 1) != 0;
    [return: Port("bit")] public static bool Run(ulong value, int index) => (uint)index < 64 && ((value >> index) & 1) != 0;
    [return: Port("bit")] public static bool Run(short value, int index) => (uint)index < 16 && ((value >> index) & 1) != 0;
    [return: Port("bit")] public static bool Run(ushort value, int index) => (uint)index < 16 && ((value >> index) & 1) != 0;
    [return: Port("bit")] public static bool Run(sbyte value, int index) => (uint)index < 8 && ((value >> index) & 1) != 0;
    [return: Port("bit")] public static bool Run(byte value, int index) => (uint)index < 8 && ((value >> index) & 1) != 0;
}
