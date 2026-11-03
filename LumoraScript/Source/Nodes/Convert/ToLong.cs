// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Wasm;

namespace Lumora.Script;

// Rounds toward zero. Past the ends of long it holds at the end, and NaN is 0.
[Node("Math/Conversion/To Long", Description = "Converts a value to a signed 64-bit integer, dropping any fractional part toward zero.",
    Keywords = "cast, convert, number type")]
public sealed class ToLong : ValueNode
{
    [Wasm(WasmOp.I64ExtendI32S)] public static long Run(int value) => value;
    public static long Run(float value) => Saturate.Whole<long>(value);
    public static long Run(double value) => Saturate.Whole<long>(value);
    public static long Run(Half value) => Saturate.Whole<long>((double)value);
    [Wasm(WasmOp.I64ExtendI32S)] public static long Run(sbyte value) => value;
    [Wasm(WasmOp.I64ExtendI32U)] public static long Run(byte value) => value;
    [Wasm(WasmOp.I64ExtendI32S)] public static long Run(short value) => value;
    [Wasm(WasmOp.I64ExtendI32U)] public static long Run(ushort value) => value;
    [Wasm(WasmOp.I64ExtendI32U)] public static long Run(uint value) => value;
    public static long Run(ulong value) => long.CreateSaturating(value);

    public static long Run(ScriptBigInt? value) => ScriptBigInt.ToLong(value);
}
