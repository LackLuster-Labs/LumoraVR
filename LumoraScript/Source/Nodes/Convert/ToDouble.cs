// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Wasm;

namespace Lumora.Script;

// Exact from every float and every whole number up to 2^53; a bigger whole number rounds to the nearest
// double, ties to even.
[Node("Math/Conversion/To Double", Description = "Exact from every float and every whole number up to 2^53; a bigger whole number rounds to the nearest double, ties to even.",
    Keywords = "cast, convert, number type")]
public sealed class ToDouble : ValueNode
{
    [Wasm(WasmOp.F64PromoteF32)] public static double Run(float value) => value;
    [Wasm(WasmOp.F64ConvertI32S)] public static double Run(int value) => value;
    [Wasm(WasmOp.F64ConvertI64S)] public static double Run(long value) => value;
    public static double Run(Half value) => (double)value;
    [Wasm(WasmOp.F64ConvertI32S)] public static double Run(sbyte value) => value;
    [Wasm(WasmOp.F64ConvertI32S)] public static double Run(byte value) => value;
    [Wasm(WasmOp.F64ConvertI32S)] public static double Run(short value) => value;
    [Wasm(WasmOp.F64ConvertI32S)] public static double Run(ushort value) => value;
    [Wasm(WasmOp.F64ConvertI32U)] public static double Run(uint value) => value;

    // Past the top of long it is halved with its lost bit kept, then goes through the signed conversion,
    // which rounds once and the same everywhere; the direct unsigned cast rounds twice on some machines.
    [Wasm(WasmOp.F64ConvertI64U)]
    public static double Run(ulong value) => value <= long.MaxValue ? (long)value : 2d * (long)((value >> 1) | (value & 1));

    public static double Run(ScriptBigInt? value) => ScriptBigInt.ToDouble(value);
}
