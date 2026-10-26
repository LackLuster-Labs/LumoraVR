// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Wasm;

// The interpreter's instruction set. Every operand is a slot number relative to the frame: the validator
// knows the stack height at each instruction, so nothing is pushed or popped at run time. Numeric ops
// keep the WebAssembly numbering (NumericBase + opcode - 0x45) so the translator maps them by arithmetic.
internal enum WasmIrOp : ushort
{
    Unreachable,
    Fuel,
    Br,
    BrIf,
    BrIfZero,
    BrTable,
    Return,
    Call,
    CallImport,
    CallIndirect,
    Copy,
    Const,
    Select,
    GlobalGet,
    GlobalSet,
    TableGet,
    TableSet,
    TableSize,
    TableGrow,
    TableFill,
    TableCopy,
    TableInit,
    ElemDrop,
    MemorySize,
    MemoryGrow,
    MemoryFill,
    MemoryCopy,
    MemoryInit,
    DataDrop,
    RefIsNull,
    RefFunc,
    I32Load,
    I64Load,
    I32Load8S,
    I32Load8U,
    I32Load16S,
    I32Load16U,
    I64Load8S,
    I64Load8U,
    I64Load16S,
    I64Load16U,
    I64Load32S,
    I64Load32U,
    I32Store,
    I64Store,
    I32Store8,
    I32Store16,
    I64Store8,
    I64Store16,
    I64Store32,
    // A constant folded into the instruction that used it, and a counter global stepped in place.
    I32AddImm,
    F32MulImm,
    GlobalAddI32,
    // Copies one after another, in order, as one instruction: pairs (to, from) in the function's Moves.
    Moves,

    NumericBase = 128,
    I32Eqz = NumericBase,
    I32Eq,
    I32Ne,
    I32LtS,
    I32LtU,
    I32GtS,
    I32GtU,
    I32LeS,
    I32LeU,
    I32GeS,
    I32GeU,
    I64Eqz,
    I64Eq,
    I64Ne,
    I64LtS,
    I64LtU,
    I64GtS,
    I64GtU,
    I64LeS,
    I64LeU,
    I64GeS,
    I64GeU,
    F32Eq,
    F32Ne,
    F32Lt,
    F32Gt,
    F32Le,
    F32Ge,
    F64Eq,
    F64Ne,
    F64Lt,
    F64Gt,
    F64Le,
    F64Ge,
    I32Clz,
    I32Ctz,
    I32Popcnt,
    I32Add,
    I32Sub,
    I32Mul,
    I32DivS,
    I32DivU,
    I32RemS,
    I32RemU,
    I32And,
    I32Or,
    I32Xor,
    I32Shl,
    I32ShrS,
    I32ShrU,
    I32Rotl,
    I32Rotr,
    I64Clz,
    I64Ctz,
    I64Popcnt,
    I64Add,
    I64Sub,
    I64Mul,
    I64DivS,
    I64DivU,
    I64RemS,
    I64RemU,
    I64And,
    I64Or,
    I64Xor,
    I64Shl,
    I64ShrS,
    I64ShrU,
    I64Rotl,
    I64Rotr,
    F32Abs,
    F32Neg,
    F32Ceil,
    F32Floor,
    F32Trunc,
    F32Nearest,
    F32Sqrt,
    F32Add,
    F32Sub,
    F32Mul,
    F32Div,
    F32Min,
    F32Max,
    F32Copysign,
    F64Abs,
    F64Neg,
    F64Ceil,
    F64Floor,
    F64Trunc,
    F64Nearest,
    F64Sqrt,
    F64Add,
    F64Sub,
    F64Mul,
    F64Div,
    F64Min,
    F64Max,
    F64Copysign,
    I32WrapI64,
    I32TruncF32S,
    I32TruncF32U,
    I32TruncF64S,
    I32TruncF64U,
    I64ExtendI32S,
    I64ExtendI32U,
    I64TruncF32S,
    I64TruncF32U,
    I64TruncF64S,
    I64TruncF64U,
    F32ConvertI32S,
    F32ConvertI32U,
    F32ConvertI64S,
    F32ConvertI64U,
    F32DemoteF64,
    F64ConvertI32S,
    F64ConvertI32U,
    F64ConvertI64S,
    F64ConvertI64U,
    F64PromoteF32,
    I32ReinterpretF32,
    I64ReinterpretF64,
    F32ReinterpretI32,
    F64ReinterpretI64,
    I32Extend8S,
    I32Extend16S,
    I64Extend8S,
    I64Extend16S,
    I64Extend32S,

    SatBase = NumericBase + 0x80,
    I32TruncSatF32S = SatBase,
    I32TruncSatF32U,
    I32TruncSatF64S,
    I32TruncSatF64U,
    I64TruncSatF32S,
    I64TruncSatF32U,
    I64TruncSatF64S,
    I64TruncSatF64U,
}

internal struct WasmInstr
{
    public long Imm;
    public int A;
    public int B;
    public int C;
    public WasmIrOp Op;
}

internal sealed class WasmCompiledFunction
{
    public int FunctionIndex;
    public int[] Offsets = System.Array.Empty<int>();
    public WasmFuncType Type = WasmFuncType.Empty;
    public int ParamCount;
    public int LocalCount;
    public int FrameSize;
    public WasmInstr[] Code = System.Array.Empty<WasmInstr>();
    public int[] BranchTable = System.Array.Empty<int>();
    public int[] Moves = System.Array.Empty<int>();

    // The same code as machine code, once WasmJit has made it for the whole module, and the module's other
    // functions, which that code calls by index.
    public volatile WasmNativeBody? Native;
    public WasmModule? Owner;
    public WasmCompiledFunction[] Peers = System.Array.Empty<WasmCompiledFunction>();

    // Per function import of the module, the call native code makes straight instead, when there is one.
    public System.Delegate?[] Directs = System.Array.Empty<System.Delegate?>();
}

internal static class WasmOpcodes
{
    // Every single-byte opcode this version of the format defines, SIMD's prefix included, so a
    // constant expression can tell "not allowed here" (invalid) from "not an instruction" (malformed).
    public static bool IsKnown(byte op)
    {
        return op <= 0x05
            || (op >= 0x0B && op <= 0x11)
            || (op >= 0x1A && op <= 0x1C)
            || (op >= 0x20 && op <= 0x26)
            || (op >= 0x28 && op <= 0xC4)
            || (op >= 0xD0 && op <= 0xD2)
            || op == 0xFC
            || op == 0xFD;
    }
}
