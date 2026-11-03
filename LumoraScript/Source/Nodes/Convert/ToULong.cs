// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Wasm;

namespace Lumora.Script;

// Rounds toward zero. Below 0 it is 0, past the top of ulong it holds there, and NaN is 0.
[Node("Math/Conversion/To ULong", Description = "Converts a value to an unsigned 64-bit integer, dropping any fractional part toward zero.",
    Keywords = "cast, convert, number type")]
public sealed class ToULong : ValueNode
{
    public static ulong Run(int value) => ulong.CreateSaturating(value);
    public static ulong Run(long value) => ulong.CreateSaturating(value);
    public static ulong Run(float value) => Saturate.Whole<ulong>(value);
    public static ulong Run(double value) => Saturate.Whole<ulong>(value);
    public static ulong Run(Half value) => Saturate.Whole<ulong>((double)value);
    public static ulong Run(sbyte value) => ulong.CreateSaturating(value);
    [Wasm(WasmOp.I64ExtendI32U)] public static ulong Run(byte value) => value;
    public static ulong Run(short value) => ulong.CreateSaturating(value);
    [Wasm(WasmOp.I64ExtendI32U)] public static ulong Run(ushort value) => value;
    [Wasm(WasmOp.I64ExtendI32U)] public static ulong Run(uint value) => value;
}
