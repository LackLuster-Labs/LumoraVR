// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Wasm;

// Everything that can hold a function reference lives in one store: a reference is the function's id
// here, so tables and globals can be shared between instances linked into the same store. A store is
// used from one thread at a time. -xlinka
public sealed class WasmStore
{
    internal readonly List<WasmFunction?> Functions = new() { null };
    private readonly Dictionary<WasmFuncType, int> _typeIds = new();

    internal int InternType(WasmFuncType type)
    {
        if (!_typeIds.TryGetValue(type, out int id))
        {
            id = _typeIds.Count;
            _typeIds.Add(type, id);
        }
        return id;
    }

    internal void Register(WasmFunction function)
    {
        function.Store = this;
        function.TypeId = InternType(function.Type);
        function.StoreId = Functions.Count;
        Functions.Add(function);
    }

    // The register stack is per thread and shared by every store on it. Scripts never need more than
    // the default; tools running deep recursion can ask for more before they start.
    public static void ConfigureThreadStack(int slots) => WasmExecutor.ConfigureCurrentThread(slots);

    // Whether modules run as native code (WasmJit) where the platform compiles generated code; off, every
    // module runs in the interpreter. Both give the same results, so this is for measuring and for tests.
    public static bool NativeCode
    {
        get => _nativeCode;
        set => _nativeCode = value && WasmJit.Supported;
    }

    private static volatile bool _nativeCode = WasmJit.Supported;

    // Whether this platform can run modules as native code at all.
    public static bool NativeCodeSupported => WasmJit.Supported;

    public WasmHostFunction CreateHostFunction(WasmFuncType type, WasmHostCallback callback, int fuelCost = 10)
    {
        var function = new WasmHostFunction(type, callback, fuelCost);
        Register(function);
        return function;
    }

    // Runs a function to completion. Returns the fuel left over; throws WasmTrapException on a trap,
    // which leaves the store usable for the next call. The deadline is on Environment.TickCount64.
    public long Invoke(WasmFunction function, ReadOnlySpan<ulong> arguments, Span<ulong> results,
        long fuel = long.MaxValue, int maxCallDepth = 512, long deadline = long.MaxValue)
    {
        if (function.Store != this)
            throw new ArgumentException("function belongs to another store");
        if (arguments.Length != function.Type.Parameters.Length)
            throw new ArgumentException("wrong number of arguments");
        if (results.Length < function.Type.Results.Length)
            throw new ArgumentException("results span too small");
        return WasmExecutor.Current.Invoke(function, arguments, results, fuel, maxCallDepth, deadline);
    }

    public ulong[] Invoke(WasmFunction function, params ulong[] arguments)
    {
        var results = new ulong[function.Type.Results.Length];
        Invoke(function, arguments, results);
        return results;
    }
}

public abstract class WasmFunction
{
    public WasmFuncType Type { get; }
    internal WasmStore? Store;
    internal int TypeId;
    internal int StoreId;

    protected WasmFunction(WasmFuncType type)
    {
        Type = type;
    }
}

internal sealed class WasmModuleFunction : WasmFunction
{
    public readonly WasmInstance Instance;
    public readonly WasmCompiledFunction Compiled;

    public WasmModuleFunction(WasmInstance instance, WasmCompiledFunction compiled) : base(compiled.Type)
    {
        Instance = instance;
        Compiled = compiled;
    }
}

public delegate void WasmHostCallback(WasmHostCall call);

public sealed class WasmHostFunction : WasmFunction
{
    internal readonly WasmHostCallback Callback;
    public int FuelCost { get; }

    internal WasmHostFunction(WasmFuncType type, WasmHostCallback callback, int fuelCost) : base(type)
    {
        Callback = callback;
        FuelCost = Math.Max(0, fuelCost);
    }
}

// What a host function sees: its arguments in, its results out, over the same slots, plus the instance
// that called it (null when the host called it directly). Floats written back are canonicalised the same
// way the engine's own arithmetic is.
public readonly ref struct WasmHostCall
{
    private readonly Span<ulong> _values;

    public WasmInstance? Caller { get; }
    public int ParameterCount { get; }
    public int ResultCount { get; }

    internal WasmHostCall(WasmInstance? caller, Span<ulong> values, int parameterCount, int resultCount)
    {
        Caller = caller;
        _values = values;
        ParameterCount = parameterCount;
        ResultCount = resultCount;
    }

    public int GetI32(int index) => (int)_values[index];
    public long GetI64(int index) => (long)_values[index];
    public float GetF32(int index) => WasmNumerics.F32(_values[index]);
    public double GetF64(int index) => WasmNumerics.F64(_values[index]);
    public ulong GetRaw(int index) => _values[index];

    public void SetI32(int index, int value) => _values[index] = (uint)value;
    public void SetI64(int index, long value) => _values[index] = (ulong)value;
    public void SetF32(int index, float value) => _values[index] = WasmNumerics.Bits(value);
    public void SetF64(int index, double value) => _values[index] = WasmNumerics.Bits(value);
    public void SetRaw(int index, ulong value) => _values[index] = value;

    public Span<byte> Memory => Caller?.Memory is WasmMemory memory ? memory.Data : Span<byte>.Empty;
}

public sealed class WasmMemory
{
    public const int PageSize = 65536;

    internal byte[] Data;
    public uint Pages { get; private set; }
    public uint? MaxPages { get; }
    private readonly uint _cap;

    public WasmMemory(WasmMemoryType type, uint pageCap)
    {
        uint limit = Math.Min(pageCap, 32767u);
        if (type.Pages.Min > limit)
            throw new WasmTrapException(WasmTrapCode.ResourceLimit, "memory larger than this instance may have");
        MaxPages = type.Pages.Max;
        _cap = type.Pages.Max is uint max ? Math.Min(max, limit) : limit;
        Pages = type.Pages.Min;
        Data = new byte[(long)Pages * PageSize];
    }

    public Span<byte> Span => Data;

    // Old size in pages, or -1 when the memory may not grow that far. Failing a grow is allowed by the
    // specification, so hitting our cap looks exactly like hitting the declared maximum.
    public int Grow(uint delta)
    {
        uint old = Pages;
        ulong wanted = (ulong)old + delta;
        if (wanted > _cap)
            return -1;
        if (delta > 0)
        {
            Array.Resize(ref Data, (int)(wanted * PageSize));
            Pages = (uint)wanted;
        }
        return (int)old;
    }
}

public sealed class WasmTable
{
    public WasmValueType ElementType { get; }
    internal ulong[] Elements;
    public uint? Max { get; }
    private readonly uint _cap;

    public uint Size => (uint)Elements.Length;

    public WasmTable(WasmTableType type, uint elementCap)
    {
        if (type.Size.Min > elementCap)
            throw new WasmTrapException(WasmTrapCode.ResourceLimit, "table larger than this instance may have");
        ElementType = type.ElementType;
        Max = type.Size.Max;
        _cap = type.Size.Max is uint max ? Math.Min(max, elementCap) : elementCap;
        Elements = new ulong[type.Size.Min];
    }

    public int Grow(uint delta, ulong initial)
    {
        uint old = Size;
        ulong wanted = (ulong)old + delta;
        if (wanted > _cap)
            return -1;
        if (delta > 0)
        {
            Array.Resize(ref Elements, (int)wanted);
            Array.Fill(Elements, initial, (int)old, (int)delta);
        }
        return (int)old;
    }

    public ulong GetRaw(uint index) => Elements[index];
}

public sealed class WasmGlobal
{
    public WasmGlobalType Type { get; }
    internal ulong Value;

    public WasmGlobal(WasmGlobalType type, ulong value)
    {
        Type = type;
        Value = value;
    }

    public ulong RawValue => Value;

    // The host writing a mutable global between calls, the way global.set would. An immutable one is
    // refused: a module may rely on it never changing.
    public void SetRawValue(ulong value)
    {
        if (!Type.Mutable)
            throw new InvalidOperationException("global is immutable");
        Value = value;
    }
}

public readonly struct WasmExtern
{
    public WasmExternKind Kind { get; }
    public object Value { get; }

    private WasmExtern(WasmExternKind kind, object value)
    {
        Kind = kind;
        Value = value;
    }

    public static WasmExtern Of(WasmFunction function) => new(WasmExternKind.Function, function);
    public static WasmExtern Of(WasmTable table) => new(WasmExternKind.Table, table);
    public static WasmExtern Of(WasmMemory memory) => new(WasmExternKind.Memory, memory);
    public static WasmExtern Of(WasmGlobal global) => new(WasmExternKind.Global, global);

    public WasmFunction? AsFunction => Value as WasmFunction;
    public WasmTable? AsTable => Value as WasmTable;
    public WasmMemory? AsMemory => Value as WasmMemory;
    public WasmGlobal? AsGlobal => Value as WasmGlobal;
}
