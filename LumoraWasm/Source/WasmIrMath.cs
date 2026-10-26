// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using static Lumora.Wasm.WasmNumerics;

namespace Lumora.Wasm;

// What each IR instruction computes, as the code the native back end calls. Every body is the interpreter's
// own expression for that instruction, the slots read as arguments, so both back ends give the same bits for
// the same inputs; change one and the other has to follow. A trap thrown here carries no place yet: the
// function it runs in pins it on the instruction. -xlinka
internal static class WasmIrMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32AddImm(ulong b, long imm) => (uint)b + (uint)imm;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32MulImm(ulong b, long imm) => Bits(F32(b) * F32((ulong)imm));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Eqz(ulong b) => (uint)b == 0 ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Eq(ulong b, ulong c) => (uint)b == (uint)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Ne(ulong b, ulong c) => (uint)b != (uint)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32LtS(ulong b, ulong c) => (int)b < (int)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32LtU(ulong b, ulong c) => (uint)b < (uint)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32GtS(ulong b, ulong c) => (int)b > (int)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32GtU(ulong b, ulong c) => (uint)b > (uint)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32LeS(ulong b, ulong c) => (int)b <= (int)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32LeU(ulong b, ulong c) => (uint)b <= (uint)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32GeS(ulong b, ulong c) => (int)b >= (int)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32GeU(ulong b, ulong c) => (uint)b >= (uint)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Eqz(ulong b) => b == 0 ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Eq(ulong b, ulong c) => b == c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Ne(ulong b, ulong c) => b != c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64LtS(ulong b, ulong c) => (long)b < (long)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64LtU(ulong b, ulong c) => b < c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64GtS(ulong b, ulong c) => (long)b > (long)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64GtU(ulong b, ulong c) => b > c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64LeS(ulong b, ulong c) => (long)b <= (long)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64LeU(ulong b, ulong c) => b <= c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64GeS(ulong b, ulong c) => (long)b >= (long)c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64GeU(ulong b, ulong c) => b >= c ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Eq(ulong b, ulong c) => F32(b) == F32(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Ne(ulong b, ulong c) => F32(b) != F32(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Lt(ulong b, ulong c) => F32(b) < F32(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Gt(ulong b, ulong c) => F32(b) > F32(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Le(ulong b, ulong c) => F32(b) <= F32(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Ge(ulong b, ulong c) => F32(b) >= F32(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Eq(ulong b, ulong c) => F64(b) == F64(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Ne(ulong b, ulong c) => F64(b) != F64(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Lt(ulong b, ulong c) => F64(b) < F64(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Gt(ulong b, ulong c) => F64(b) > F64(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Le(ulong b, ulong c) => F64(b) <= F64(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Ge(ulong b, ulong c) => F64(b) >= F64(c) ? 1UL : 0UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Clz(ulong b) => (uint)BitOperations.LeadingZeroCount((uint)b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Ctz(ulong b) => (uint)BitOperations.TrailingZeroCount((uint)b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Popcnt(ulong b) => (uint)BitOperations.PopCount((uint)b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Add(ulong b, ulong c) => (uint)b + (uint)c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Sub(ulong b, ulong c) => (uint)b - (uint)c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Mul(ulong b, ulong c) => (uint)b * (uint)c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32And(ulong b, ulong c) => (uint)b & (uint)c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Or(ulong b, ulong c) => (uint)b | (uint)c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Xor(ulong b, ulong c) => (uint)b ^ (uint)c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Shl(ulong b, ulong c) => (uint)b << ((int)c & 31);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32ShrS(ulong b, ulong c) => (uint)((int)b >> ((int)c & 31));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32ShrU(ulong b, ulong c) => (uint)b >> ((int)c & 31);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Rotl(ulong b, ulong c) => BitOperations.RotateLeft((uint)b, (int)c & 31);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Rotr(ulong b, ulong c) => BitOperations.RotateRight((uint)b, (int)c & 31);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Clz(ulong b) => (ulong)BitOperations.LeadingZeroCount(b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Ctz(ulong b) => (ulong)BitOperations.TrailingZeroCount(b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Popcnt(ulong b) => (ulong)BitOperations.PopCount(b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Add(ulong b, ulong c) => b + c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Sub(ulong b, ulong c) => b - c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Mul(ulong b, ulong c) => b * c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64And(ulong b, ulong c) => b & c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Or(ulong b, ulong c) => b | c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Xor(ulong b, ulong c) => b ^ c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Shl(ulong b, ulong c) => b << ((int)c & 63);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64ShrS(ulong b, ulong c) => (ulong)((long)b >> ((int)c & 63));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64ShrU(ulong b, ulong c) => b >> ((int)c & 63);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Rotl(ulong b, ulong c) => BitOperations.RotateLeft(b, (int)c & 63);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Rotr(ulong b, ulong c) => BitOperations.RotateRight(b, (int)c & 63);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Abs(ulong b) => (uint)b & 0x7FFFFFFFu;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Neg(ulong b) => (uint)b ^ 0x80000000u;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Ceil(ulong b) => Bits(Ceil(F32(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Floor(ulong b) => Bits(Floor(F32(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Trunc(ulong b) => Bits(Trunc(F32(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Nearest(ulong b) => Bits(Nearest(F32(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Sqrt(ulong b) => Bits(MathF.Sqrt(F32(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Add(ulong b, ulong c) => Bits(F32(b) + F32(c));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Sub(ulong b, ulong c) => Bits(F32(b) - F32(c));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Mul(ulong b, ulong c) => Bits(F32(b) * F32(c));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Div(ulong b, ulong c) => Bits(F32(b) / F32(c));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Min(ulong b, ulong c) => Bits(Min(F32(b), F32(c)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Max(ulong b, ulong c) => Bits(Max(F32(b), F32(c)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32Copysign(ulong b, ulong c) => ((uint)b & 0x7FFFFFFFu) | ((uint)c & 0x80000000u);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Abs(ulong b) => b & 0x7FFFFFFFFFFFFFFFUL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Neg(ulong b) => b ^ 0x8000000000000000UL;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Ceil(ulong b) => Bits(Ceil(F64(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Floor(ulong b) => Bits(Floor(F64(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Trunc(ulong b) => Bits(Trunc(F64(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Nearest(ulong b) => Bits(Nearest(F64(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Sqrt(ulong b) => Bits(Math.Sqrt(F64(b)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Add(ulong b, ulong c) => Bits(F64(b) + F64(c));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Sub(ulong b, ulong c) => Bits(F64(b) - F64(c));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Mul(ulong b, ulong c) => Bits(F64(b) * F64(c));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Div(ulong b, ulong c) => Bits(F64(b) / F64(c));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Min(ulong b, ulong c) => Bits(Min(F64(b), F64(c)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Max(ulong b, ulong c) => Bits(Max(F64(b), F64(c)));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64Copysign(ulong b, ulong c) => (b & 0x7FFFFFFFFFFFFFFFUL) | (c & 0x8000000000000000UL);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32WrapI64(ulong b) => (uint)b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32TruncF32S(ulong b) => (uint)TruncF32ToI32(F32(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32TruncF32U(ulong b) => TruncF32ToU32(F32(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32TruncF64S(ulong b) => (uint)TruncF64ToI32(F64(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32TruncF64U(ulong b) => TruncF64ToU32(F64(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64ExtendI32S(ulong b) => (ulong)(long)(int)b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64ExtendI32U(ulong b) => (uint)b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64TruncF32S(ulong b) => (ulong)TruncF32ToI64(F32(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64TruncF32U(ulong b) => TruncF32ToU64(F32(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64TruncF64S(ulong b) => (ulong)TruncF64ToI64(F64(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64TruncF64U(ulong b) => TruncF64ToU64(F64(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32ConvertI32S(ulong b) => Bits((float)(int)b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32ConvertI32U(ulong b) => Bits((float)(uint)b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32ConvertI64S(ulong b) => Bits((float)(long)b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32ConvertI64U(ulong b) => Bits(U64ToF32(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F32DemoteF64(ulong b) => Bits((float)F64(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64ConvertI32S(ulong b) => Bits((double)(int)b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64ConvertI32U(ulong b) => Bits((double)(uint)b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64ConvertI64S(ulong b) => Bits((double)(long)b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64ConvertI64U(ulong b) => Bits(U64ToF64(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong F64PromoteF32(ulong b) => Bits((double)F32(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Extend8S(ulong b) => (uint)(int)(sbyte)b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32Extend16S(ulong b) => (uint)(int)(short)b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Extend8S(ulong b) => (ulong)(long)(sbyte)b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Extend16S(ulong b) => (ulong)(long)(short)b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64Extend32S(ulong b) => (ulong)(long)(int)b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32TruncSatF32S(ulong b) => (uint)SatF32ToI32(F32(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32TruncSatF32U(ulong b) => SatF32ToU32(F32(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32TruncSatF64S(ulong b) => (uint)SatF64ToI32(F64(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I32TruncSatF64U(ulong b) => SatF64ToU32(F64(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64TruncSatF32S(ulong b) => (ulong)SatF32ToI64(F32(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64TruncSatF32U(ulong b) => SatF32ToU64(F32(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64TruncSatF64S(ulong b) => (ulong)SatF64ToI64(F64(b));
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static ulong I64TruncSatF64U(ulong b) => SatF64ToU64(F64(b));

    public static ulong I32DivS(ulong x, ulong y)
    {
        int a = (int)x, b = (int)y;
        if (b == 0)
            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
        if (a == int.MinValue && b == -1)
            throw new WasmTrapException(WasmTrapCode.IntegerOverflow);
        return (uint)(a / b);
    }

    public static ulong I32DivU(ulong x, ulong y)
    {
        uint b = (uint)y;
        if (b == 0)
            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
        return (uint)x / b;
    }

    public static ulong I32RemS(ulong x, ulong y)
    {
        int a = (int)x, b = (int)y;
        if (b == 0)
            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
        return b == -1 ? 0UL : (uint)(a % b);
    }

    public static ulong I32RemU(ulong x, ulong y)
    {
        uint b = (uint)y;
        if (b == 0)
            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
        return (uint)x % b;
    }

    public static ulong I64DivS(ulong x, ulong y)
    {
        long a = (long)x, b = (long)y;
        if (b == 0)
            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
        if (a == long.MinValue && b == -1)
            throw new WasmTrapException(WasmTrapCode.IntegerOverflow);
        return (ulong)(a / b);
    }

    public static ulong I64DivU(ulong x, ulong y)
    {
        if (y == 0)
            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
        return x / y;
    }

    public static ulong I64RemS(ulong x, ulong y)
    {
        long a = (long)x, b = (long)y;
        if (b == 0)
            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
        return b == -1 ? 0UL : (ulong)(a % b);
    }

    public static ulong I64RemU(ulong x, ulong y)
    {
        if (y == 0)
            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
        return x % y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Address(byte[] memory, ulong address, long offset, int size)
    {
        ulong effective = (uint)address + (ulong)offset;
        if (effective + (ulong)size > (ulong)memory.Length)
            throw new WasmTrapException(WasmTrapCode.OutOfBoundsMemoryAccess);
        return (int)effective;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I32Load(byte[] m, ulong a, long offset) => BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(Address(m, a, offset, 4), 4));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I64Load(byte[] m, ulong a, long offset) => BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(Address(m, a, offset, 8), 8));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I32Load8S(byte[] m, ulong a, long offset) => (uint)(int)(sbyte)m[Address(m, a, offset, 1)];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I32Load8U(byte[] m, ulong a, long offset) => m[Address(m, a, offset, 1)];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I32Load16S(byte[] m, ulong a, long offset) => (uint)(int)(short)BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(Address(m, a, offset, 2), 2));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I32Load16U(byte[] m, ulong a, long offset) => BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(Address(m, a, offset, 2), 2));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I64Load8S(byte[] m, ulong a, long offset) => (ulong)(long)(sbyte)m[Address(m, a, offset, 1)];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I64Load8U(byte[] m, ulong a, long offset) => m[Address(m, a, offset, 1)];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I64Load16S(byte[] m, ulong a, long offset) => (ulong)(long)(short)BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(Address(m, a, offset, 2), 2));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I64Load16U(byte[] m, ulong a, long offset) => BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(Address(m, a, offset, 2), 2));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I64Load32S(byte[] m, ulong a, long offset) => (ulong)(long)(int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(Address(m, a, offset, 4), 4));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong I64Load32U(byte[] m, ulong a, long offset) => BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(Address(m, a, offset, 4), 4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void I32Store(byte[] m, ulong a, long offset, ulong v) => BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(Address(m, a, offset, 4), 4), (uint)v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void I64Store(byte[] m, ulong a, long offset, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(Address(m, a, offset, 8), 8), v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store8(byte[] m, ulong a, long offset, ulong v) => m[Address(m, a, offset, 1)] = (byte)v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store16(byte[] m, ulong a, long offset, ulong v) => BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(Address(m, a, offset, 2), 2), (ushort)v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void I64Store32(byte[] m, ulong a, long offset, ulong v) => BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(Address(m, a, offset, 4), 4), (uint)v);

    public static Exception Trap(WasmTrapCode code) => new WasmTrapException(code);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void GlobalAddI32(WasmGlobal global, long imm) => global.Value = (uint)global.Value + (uint)imm;

    public static ulong TableGet(WasmInstance instance, int table, ulong index)
    {
        var t = instance.Tables[table];
        uint i = (uint)index;
        if (i >= t.Size)
            throw new WasmTrapException(WasmTrapCode.OutOfBoundsTableAccess);
        return t.Elements[i];
    }

    public static void TableSet(WasmInstance instance, int table, ulong index, ulong value)
    {
        var t = instance.Tables[table];
        uint i = (uint)index;
        if (i >= t.Size)
            throw new WasmTrapException(WasmTrapCode.OutOfBoundsTableAccess);
        t.Elements[i] = value;
    }

    public static ulong TableSize(WasmInstance instance, int table) => instance.Tables[table].Size;

    public static ulong TableGrow(WasmInstance instance, int table, ulong initial, ulong count, ref long fuel)
    {
        uint delta = (uint)count;
        int old = instance.Tables[table].Grow(delta, initial);
        if (old >= 0)
            fuel -= delta >> 4;
        return (uint)old;
    }

    public static void TableFill(WasmInstance instance, long table, ulong index, ulong value, ulong count, ref long fuel)
    {
        var t = instance.Tables[(int)table];
        uint i = (uint)index;
        uint n = (uint)count;
        if ((ulong)i + n > t.Size)
            throw new WasmTrapException(WasmTrapCode.OutOfBoundsTableAccess);
        fuel -= n >> 6;
        Array.Fill(t.Elements, value, (int)i, (int)n);
    }

    public static void TableCopy(WasmInstance instance, long tables, ulong to, ulong from, ulong count, ref long fuel)
    {
        var destination = instance.Tables[(int)(tables >> 32)];
        var source = instance.Tables[(int)(tables & 0xFFFFFFFF)];
        uint d = (uint)to;
        uint s = (uint)from;
        uint n = (uint)count;
        if ((ulong)d + n > destination.Size || (ulong)s + n > source.Size)
            throw new WasmTrapException(WasmTrapCode.OutOfBoundsTableAccess);
        fuel -= n >> 6;
        Array.Copy(source.Elements, s, destination.Elements, d, n);
    }

    public static void TableInit(WasmInstance instance, long which, ulong to, ulong from, ulong count, ref long fuel)
    {
        var table = instance.Tables[(int)(which >> 32)];
        var segment = instance.ElementSegments[(int)(which & 0xFFFFFFFF)];
        uint d = (uint)to;
        uint s = (uint)from;
        uint n = (uint)count;
        if ((ulong)d + n > table.Size || (ulong)s + n > (ulong)segment.Length)
            throw new WasmTrapException(WasmTrapCode.OutOfBoundsTableAccess);
        fuel -= n >> 6;
        Array.Copy(segment, s, table.Elements, d, n);
    }

    public static void ElemDrop(WasmInstance instance, int segment) => instance.ElementSegments[segment] = Array.Empty<ulong>();

    public static void DataDrop(WasmInstance instance, int segment) => instance.DataSegments[segment] = Array.Empty<byte>();

    public static ulong MemorySize(WasmInstance instance) => instance.Memory!.Pages;

    public static ulong MemoryGrow(WasmInstance instance, ulong pages, ref long fuel)
    {
        uint delta = (uint)pages;
        int old = instance.Memory!.Grow(delta);
        if (old >= 0)
            fuel -= (long)delta << 8;
        return (uint)old;
    }

    public static void MemoryFill(byte[] memory, ulong to, ulong value, ulong count, ref long fuel)
    {
        uint d = (uint)to;
        uint n = (uint)count;
        if ((ulong)d + n > (ulong)memory.Length)
            throw new WasmTrapException(WasmTrapCode.OutOfBoundsMemoryAccess);
        fuel -= n >> 6;
        memory.AsSpan((int)d, (int)n).Fill((byte)value);
    }

    public static void MemoryCopy(byte[] memory, ulong to, ulong from, ulong count, ref long fuel)
    {
        uint d = (uint)to;
        uint s = (uint)from;
        uint n = (uint)count;
        if ((ulong)d + n > (ulong)memory.Length || (ulong)s + n > (ulong)memory.Length)
            throw new WasmTrapException(WasmTrapCode.OutOfBoundsMemoryAccess);
        fuel -= n >> 6;
        memory.AsSpan((int)s, (int)n).CopyTo(memory.AsSpan((int)d, (int)n));
    }

    public static void MemoryInit(WasmInstance instance, byte[] memory, long segmentIndex, ulong to, ulong from, ulong count, ref long fuel)
    {
        var segment = instance.DataSegments[(int)segmentIndex];
        uint d = (uint)to;
        uint s = (uint)from;
        uint n = (uint)count;
        if ((ulong)d + n > (ulong)memory.Length || (ulong)s + n > (ulong)segment.Length)
            throw new WasmTrapException(WasmTrapCode.OutOfBoundsMemoryAccess);
        fuel -= n >> 6;
        segment.AsSpan((int)s, (int)n).CopyTo(memory.AsSpan((int)d, (int)n));
    }

    public static ulong RefFunc(WasmInstance instance, int function) => (ulong)instance.Functions[function].StoreId;
}
