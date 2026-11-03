// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Numerics;
using Lumora.Wasm;

namespace Lumora.Script;

// How many unset bits come above the highest set one, counted across value's own width, so 0 gives the
// whole width: 8 for a byte, 32 for an int.
[Node("Math/Bits/Leading Zeros", Description = "How many unset bits come above the highest set one, counted across value's own width, so 0 gives the whole width: 8 for a byte, 32 for an int.",
    Keywords = "clz, count leading zeros, highest bit")]
public sealed class LeadingZeros : ValueNode
{
    [Wasm(WasmOp.I32Clz)] public static int Run(int value) => BitOperations.LeadingZeroCount((uint)value);
    public static int Run(long value) => BitOperations.LeadingZeroCount((ulong)value);
    [Wasm(WasmOp.I32Clz)] public static int Run(uint value) => BitOperations.LeadingZeroCount(value);
    public static int Run(ulong value) => BitOperations.LeadingZeroCount(value);
    public static int Run(short value) => BitOperations.LeadingZeroCount((uint)(ushort)value) - 16;
    public static int Run(ushort value) => BitOperations.LeadingZeroCount((uint)value) - 16;
    public static int Run(sbyte value) => BitOperations.LeadingZeroCount((uint)(byte)value) - 24;
    public static int Run(byte value) => BitOperations.LeadingZeroCount((uint)value) - 24;
}
