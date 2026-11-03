// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Wasm;

namespace Lumora.Script;

// Rounds to the nearest float, ties to even. A double too big for a float is infinity. A narrow whole
// number rides its lane already widened, so the one signed conversion serves sbyte through ushort.
[Node("Math/Conversion/To Float", Description = "Converts a number to a float, rounding to the nearest float, ties to even.",
    Keywords = "cast, convert, number type")]
public sealed class ToFloat : ValueNode
{
    [Wasm(WasmOp.F32ConvertI32S)] public static float Run(int value) => value;
    [Wasm(WasmOp.F32ConvertI64S)] public static float Run(long value) => value;
    [Wasm(WasmOp.F32DemoteF64)] public static float Run(double value) => (float)value;
    public static float Run(Half value) => (float)value;
    [Wasm(WasmOp.F32ConvertI32S)] public static float Run(sbyte value) => value;
    [Wasm(WasmOp.F32ConvertI32S)] public static float Run(byte value) => value;
    [Wasm(WasmOp.F32ConvertI32S)] public static float Run(short value) => value;
    [Wasm(WasmOp.F32ConvertI32S)] public static float Run(ushort value) => value;
    [Wasm(WasmOp.F32ConvertI32U)] public static float Run(uint value) => value;

    // Past the top of long it is halved with its lost bit kept, then goes through the signed conversion,
    // which rounds once and the same everywhere; the direct unsigned cast rounds twice on some machines.
    [Wasm(WasmOp.F32ConvertI64U)]
    public static float Run(ulong value) => value <= long.MaxValue ? (long)value : 2f * (long)((value >> 1) | (value & 1));

    public static float Run(ScriptBigInt? value) => (float)ScriptBigInt.ToDouble(value);
}
