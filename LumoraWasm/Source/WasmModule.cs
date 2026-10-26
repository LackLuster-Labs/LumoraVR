// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Lumora.Wasm;

public enum WasmConstKind : byte
{
    I32,
    I64,
    F32,
    F64,
    RefNull,
    RefFunc,
    GlobalGet,
}

// A constant expression is one instruction in this version of the format: a literal, a null or function
// reference, or a read of an imported immutable global.
public readonly struct WasmConstExpr
{
    public readonly WasmConstKind Kind;
    public readonly WasmValueType Type;
    public readonly ulong Value;
    public readonly uint Index;

    public WasmConstExpr(WasmConstKind kind, WasmValueType type, ulong value, uint index)
    {
        Kind = kind;
        Type = type;
        Value = value;
        Index = index;
    }
}

public sealed class WasmImport
{
    public string Module { get; }
    public string Name { get; }
    public WasmExternKind Kind { get; }
    public uint TypeIndex { get; init; }
    public WasmTableType Table { get; init; }
    public WasmMemoryType Memory { get; init; }
    public WasmGlobalType Global { get; init; }

    public WasmImport(string module, string name, WasmExternKind kind)
    {
        Module = module;
        Name = name;
        Kind = kind;
    }
}

public sealed class WasmExport
{
    public string Name { get; }
    public WasmExternKind Kind { get; }
    public uint Index { get; }

    public WasmExport(string name, WasmExternKind kind, uint index)
    {
        Name = name;
        Kind = kind;
        Index = index;
    }
}

public sealed class WasmCustomSection
{
    public string Name { get; }
    public byte[] Bytes { get; }

    public WasmCustomSection(string name, byte[] bytes)
    {
        Name = name;
        Bytes = bytes;
    }
}

public enum WasmSegmentMode : byte
{
    Active,
    Passive,
    Declarative,
}

internal sealed class WasmGlobalDefinition
{
    public WasmGlobalType Type;
    public WasmConstExpr Init;
}

internal sealed class WasmElementSegment
{
    public WasmSegmentMode Mode;
    public uint Table;
    public WasmConstExpr Offset;
    public WasmValueType ElementType;
    public WasmConstExpr[] Items = Array.Empty<WasmConstExpr>();
}

internal sealed class WasmDataSegment
{
    public WasmSegmentMode Mode;
    public uint Memory;
    public WasmConstExpr Offset;
    public byte[] Bytes = Array.Empty<byte>();
}

internal sealed class WasmFunctionBody
{
    public int Start;
    public int End;
    public WasmValueType[] Locals = Array.Empty<WasmValueType>();
}

public sealed class WasmModule
{
    internal byte[] Bytes = Array.Empty<byte>();
    internal WasmFuncType[] Types = Array.Empty<WasmFuncType>();
    internal WasmImport[] ImportList = Array.Empty<WasmImport>();
    internal uint[] DefinedFunctionTypeIndices = Array.Empty<uint>();
    internal WasmTableType[] DefinedTables = Array.Empty<WasmTableType>();
    internal WasmMemoryType[] DefinedMemories = Array.Empty<WasmMemoryType>();
    internal WasmGlobalDefinition[] DefinedGlobals = Array.Empty<WasmGlobalDefinition>();
    internal WasmExport[] ExportList = Array.Empty<WasmExport>();
    internal uint? Start;
    internal WasmElementSegment[] Elements = Array.Empty<WasmElementSegment>();
    internal WasmDataSegment[] Data = Array.Empty<WasmDataSegment>();
    internal uint? DataCount;
    internal WasmFunctionBody[] Bodies = Array.Empty<WasmFunctionBody>();
    internal readonly List<WasmCustomSection> Customs = new();

    // Whole index spaces, imports first, filled once decoding is done.
    internal uint[] FunctionTypeIndices = Array.Empty<uint>();
    internal WasmTableType[] TableTypes = Array.Empty<WasmTableType>();
    internal WasmMemoryType[] MemoryTypes = Array.Empty<WasmMemoryType>();
    internal WasmGlobalType[] GlobalTypes = Array.Empty<WasmGlobalType>();
    internal int ImportedFunctionCount;
    internal int ImportedTableCount;
    internal int ImportedMemoryCount;
    internal int ImportedGlobalCount;
    internal HashSet<uint> DeclaredFunctionReferences = new();

    internal WasmCompiledFunction[] Compiled = Array.Empty<WasmCompiledFunction>();

    // Whether this module's functions have native code: WasmJit.None, Making, Made or Refused.
    internal int NativeState;

    // Makes the module's native code on this thread, so whatever runs it first does not wait for it. False
    // when the platform runs no generated code, the switch is off, or the code could not be made; the module
    // then runs in the interpreter, as it always could.
    public bool CompileNative() => WasmJit.Compile(this);

    // Whether the module runs as native code now.
    public bool IsNative => Volatile.Read(ref NativeState) == WasmJit.Made;

    // Why its native code could not be made, when it could not.
    public string? NativeRefusal { get; internal set; }

    // Imports the native code may call straight instead of through the host function linked for them: asked
    // once per function import when the code is made. Only for an import whose linked host function is always
    // that same computation, so skipping the host call changes nothing but the time it takes.
    public Func<WasmImport, WasmFuncType, WasmDirectImport?>? DirectImports { get; set; }

    public IReadOnlyList<WasmFuncType> FunctionSignatureTypes => Types;
    public IReadOnlyList<WasmImport> Imports => ImportList;
    public IReadOnlyList<WasmExport> Exports => ExportList;
    public IReadOnlyList<WasmCustomSection> CustomSections => Customs;

    internal WasmModule()
    {
    }

    internal WasmFuncType FunctionType(uint functionIndex) => Types[FunctionTypeIndices[functionIndex]];

    public static WasmModule Decode(byte[] bytes, WasmLimits? limits = null)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        limits ??= WasmLimits.Default;
        if (bytes.Length > limits.MaxModuleBytes)
            throw new WasmValidationException("module too large");
        var module = WasmDecoder.Decode(bytes, limits);
        WasmValidator.Validate(module, limits);
        return module;
    }
}
