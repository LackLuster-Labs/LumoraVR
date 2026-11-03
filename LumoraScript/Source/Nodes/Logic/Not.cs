// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Wasm;

namespace Lumora.Script;

// A bool vector flips lane by lane; a whole number flips every bit.
[Node("Logic/Not", Description = "Inverts a boolean or flips every bit of an integer.",
    Keywords = "invert, negate, flip, complement, bitwise not")]
public sealed class Not : ValueNode
{
    [Wasm(WasmOp.I32Eqz)] public static bool Run(bool value) => !value;
    [Wasm(WasmOp.I32Eqz, Lanes = 2)] public static bool2 Run(bool2 value) => !value;
    [Wasm(WasmOp.I32Eqz, Lanes = 3)] public static bool3 Run(bool3 value) => !value;
    [Wasm(WasmOp.I32Eqz, Lanes = 4)] public static bool4 Run(bool4 value) => !value;
    public static int Run(int value) => ~value;
    public static long Run(long value) => ~value;
    public static uint Run(uint value) => ~value;
    public static ulong Run(ulong value) => ~value;
}
