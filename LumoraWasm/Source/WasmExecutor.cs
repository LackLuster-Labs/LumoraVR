// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using static Lumora.Wasm.WasmNumerics;

namespace Lumora.Wasm;

// One per thread. All registers of every active frame live in one ulong array, frames overlap so a call
// copies nothing, and WebAssembly calls never recurse on the .NET stack: a module that recurses a
// million times runs out of frames here and traps, it cannot take the process down. -xlinka
internal sealed class WasmExecutor
{
    public const int DefaultStackSlots = 1 << 18;

    [ThreadStatic]
    private static WasmExecutor? _current;

    public static WasmExecutor Current => _current ??= new WasmExecutor(DefaultStackSlots);

    // Tests and tools may need more room than scripts ever get.
    public static void ConfigureCurrentThread(int stackSlots)
    {
        if (_current != null && _current._depth > 0)
            throw new InvalidOperationException("cannot resize while running");
        _current = new WasmExecutor(stackSlots);
    }

    private struct CallFrame
    {
        public WasmCompiledFunction Function;
        public WasmInstance Instance;
        public int Pc;
        public int Fp;
    }

    private readonly ulong[] _stack;
    private CallFrame[] _frames = new CallFrame[256];
    private int _depth;
    private int _top;
    // How deep native code may call before it traps, as the interpreter's maxDepth.
    private int _nativeLimit;
    // The native function running and the instruction it last said it was at, which place a trap it raises.
    internal WasmCompiledFunction? _nativeFn;
    internal int _nativeAt;
    // Running code is handed its fuel a slice at a time with the rest held back, and the clock is read
    // between slices as well as at every host call, so a call past its deadline stops even in a loop that
    // never leaves the module. -xlinka
    public const long FuelSlice = 1 << 20;

    internal long _fuel;
    private long _reserve;
    private long _deadline = long.MaxValue;
    private int _invocations;

    private WasmExecutor(int stackSlots)
    {
        _stack = new ulong[stackSlots];
    }

    public long Invoke(WasmFunction function, ReadOnlySpan<ulong> arguments, Span<ulong> results, long fuel, int maxCallDepth,
        long deadline = long.MaxValue)
    {
        var type = function.Type;
        int savedTop = _top;
        int savedDepth = _depth;
        long savedFuel = _fuel + _reserve;
        bool outermost = _invocations == 0;
        if (outermost)
        {
            _fuel = Math.Min(fuel, FuelSlice);
            _reserve = fuel - _fuel;
            _deadline = deadline;
        }
        _invocations++;
        try
        {
            int fp = _top;
            int width = Math.Max(type.Parameters.Length, type.Results.Length);
            if (function is WasmHostFunction host)
            {
                if (fp + width > _stack.Length)
                    throw new WasmTrapException(WasmTrapCode.CallStackExhausted);
                arguments.CopyTo(_stack.AsSpan(fp));
                _top = fp + width;
                ChargeHost(host);
                CallHost(host, null, _stack.AsSpan(fp, width), type);
            }
            else
            {
                var target = (WasmModuleFunction)function;
                var compiled = target.Compiled;
                if (fp + compiled.FrameSize > _stack.Length)
                    throw new WasmTrapException(WasmTrapCode.CallStackExhausted);
                arguments.CopyTo(_stack.AsSpan(fp));
                if (compiled.Native is { } native && WasmStore.NativeCode)
                {
                    int limit = _nativeLimit;
                    var running = _nativeFn;
                    int at = _nativeAt;
                    _nativeLimit = _depth + Math.Max(1, maxCallDepth);
                    _nativeFn = null;
                    try
                    {
                        native(this, target.Instance, _stack, fp);
                    }
                    catch (WasmTrapException trap) when (!trap.HasLocation && _nativeFn != null)
                    {
                        WasmJit.Locate(trap, _nativeFn, _nativeAt);
                        throw;
                    }
                    finally
                    {
                        _nativeLimit = limit;
                        _nativeFn = running;
                        _nativeAt = at;
                    }
                }
                else
                {
                    WasmJit.Request(target.Instance.Module);
                    Array.Clear(_stack, fp + compiled.ParamCount, compiled.LocalCount - compiled.ParamCount);
                    _top = fp + compiled.FrameSize;
                    Run(target.Instance, compiled, fp, _depth, _depth + Math.Max(1, maxCallDepth));
                }
            }
            _stack.AsSpan(fp, type.Results.Length).CopyTo(results);
            return _fuel + _reserve;
        }
        catch (WasmTrapException trap) when (outermost)
        {
            trap.FuelLeft = Math.Max(0, _fuel + _reserve);
            throw;
        }
        finally
        {
            _invocations--;
            _top = savedTop;
            _depth = savedDepth;
            if (outermost)
            {
                _fuel = 0;
                _reserve = 0;
                _deadline = long.MaxValue;
            }
            else if (_fuel + _reserve > savedFuel)
            {
                _fuel = savedFuel;
                _reserve = 0;
            }
        }
    }

    // A host function is our own code, but a bug in it must not escape into the frame that called the
    // script: anything it throws that is not already a trap becomes one.
    private static void CallHost(WasmHostFunction host, WasmInstance? caller, Span<ulong> values, WasmFuncType type)
    {
        try
        {
            host.Callback(new WasmHostCall(caller, values, type.Parameters.Length, type.Results.Length));
        }
        catch (WasmException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new WasmTrapException(WasmTrapCode.HostTrap, e.GetType().Name + ": " + e.Message);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ChargeHost(WasmHostFunction host)
    {
        _fuel -= host.FuelCost;
        if (_fuel < 0)
            _fuel = Refuel(_fuel);
        if (Environment.TickCount64 >= _deadline)
            throw new WasmTrapException(WasmTrapCode.OutOfTime);
    }

    // The slice in hand is spent: the next comes out of what the call has left, once the clock says there
    // is time for it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal long Refuel(long fuel)
    {
        while (fuel < 0)
        {
            if (_reserve <= 0)
                throw new WasmTrapException(WasmTrapCode.OutOfFuel);
            long take = Math.Min(_reserve, FuelSlice);
            _reserve -= take;
            fuel += take;
        }
        if (Environment.TickCount64 >= _deadline)
            throw new WasmTrapException(WasmTrapCode.OutOfTime);
        return fuel;
    }

    // What native code (WasmJit) asks of the executor: the room each function needs, the frames it calls into,
    // and the calls it makes through the instance's imports and tables. The checks and the traps are the
    // interpreter's own. A native frame lives on the .NET stack as well, so one also stops short of using up
    // that stack, which the interpreter, keeping its frames in an array, never has to.
    internal void EnterNative(int fp, int frameSize)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new WasmTrapException(WasmTrapCode.CallStackExhausted);
        _top = fp + frameSize;
    }

    internal void PushNative(int calleeFp, int frameSize)
    {
        if (_depth >= _nativeLimit)
            throw new WasmTrapException(WasmTrapCode.CallStackExhausted);
        if (calleeFp + frameSize > _stack.Length)
            throw new WasmTrapException(WasmTrapCode.CallStackExhausted);
        _depth++;
    }

    internal void PopNative() => _depth--;

    // A block's fuel, the way the interpreter charges it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Burn(int amount)
    {
        _fuel -= amount;
        if (_fuel < 0)
            _fuel = Refuel(_fuel);
    }

    // A direct call's charge, the same a host call is charged: its fuel, then the clock.
    internal long ChargeDirect(long fuel, int cost)
    {
        fuel -= cost;
        if (fuel < 0)
            fuel = Refuel(fuel);
        if (Environment.TickCount64 >= _deadline)
            throw new WasmTrapException(WasmTrapCode.OutOfTime);
        return fuel;
    }

    internal void CallImportNative(WasmInstance instance, int index, int at, int frameEnd)
        => CallFromNative(instance, instance.Functions[index], at, frameEnd);

    internal void CallIndirectNative(WasmInstance instance, int tableIndex, long typeIndex, ulong element, int at, int frameEnd)
    {
        var table = instance.Tables[tableIndex];
        uint index = (uint)element;
        if (index >= table.Size)
            throw new WasmTrapException(WasmTrapCode.UndefinedElement);
        ulong reference = table.Elements[index];
        if (reference == 0)
            throw new WasmTrapException(WasmTrapCode.UninitializedElement);
        var target = instance.Store.Functions[(int)reference]!;
        if (target.TypeId != instance.TypeIds[typeIndex])
            throw new WasmTrapException(WasmTrapCode.IndirectCallTypeMismatch);
        CallFromNative(instance, target, at, frameEnd);
    }

    // A function of another instance runs natively when its module has the code, else in the interpreter from
    // here; a host function is charged and called the way the interpreter calls it.
    // The caller's place is kept across the call: what runs inside it may name places of its own, and a trap
    // from the host function itself belongs to the caller's call. A host call that traps leaves the fuel as it
    // was before its charge, as the interpreter's does.
    private void CallFromNative(WasmInstance caller, WasmFunction target, int at, int frameEnd)
    {
        var running = _nativeFn;
        int place = _nativeAt;
        if (target is WasmModuleFunction module)
        {
            var callee = module.Compiled;
            PushNative(at, callee.FrameSize);
            if (callee.Native is { } native && WasmStore.NativeCode)
            {
                native(this, module.Instance, _stack, at);
            }
            else
            {
                Array.Clear(_stack, at + callee.ParamCount, callee.LocalCount - callee.ParamCount);
                _top = at + callee.FrameSize;
                Run(module.Instance, callee, at, _depth, _nativeLimit);
            }
            _depth--;
            _nativeFn = running;
            _nativeAt = place;
            return;
        }
        var host = (WasmHostFunction)target;
        var type = host.Type;
        int width = Math.Max(type.Parameters.Length, type.Results.Length);
        _top = frameEnd;
        long before = _fuel;
        try
        {
            ChargeHost(host);
            CallHost(host, caller, _stack.AsSpan(at, width), type);
        }
        catch (WasmException)
        {
            _fuel = before;
            _nativeFn = running;
            _nativeAt = place;
            throw;
        }
        _nativeFn = running;
        _nativeAt = place;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PushFrame(WasmCompiledFunction function, WasmInstance instance, int pc, int fp, int maxDepth)
    {
        if (_depth >= maxDepth)
            throw new WasmTrapException(WasmTrapCode.CallStackExhausted);
        if (_depth == _frames.Length)
            Array.Resize(ref _frames, _frames.Length * 2);
        ref var frame = ref _frames[_depth++];
        frame.Function = function;
        frame.Instance = instance;
        frame.Pc = pc;
        frame.Fp = fp;
    }

    private static WasmTrapException MemoryTrap() => new(WasmTrapCode.OutOfBoundsMemoryAccess);
    private static WasmTrapException TableTrap() => new(WasmTrapCode.OutOfBoundsTableAccess);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Address(byte[] memory, ulong address, long offset, int size)
    {
        ulong effective = (uint)address + (ulong)offset;
        if (effective + (ulong)size > (ulong)memory.Length)
            throw MemoryTrap();
        return (int)effective;
    }

    private void Run(WasmInstance instance, WasmCompiledFunction function, int fp, int baseDepth, int maxDepth)
    {
        ulong[] s = _stack;
        WasmInstr[] code = function.Code;
        byte[] memory = instance.MemoryBytes;
        long fuel = _fuel;
        int pc = 0;

        try
        {
            while (true)
            {
                ref readonly WasmInstr ins = ref code[pc++];
                switch (ins.Op)
                {
                    case WasmIrOp.Fuel:
                        fuel -= ins.A;
                        if (fuel < 0)
                            fuel = Refuel(fuel);
                        break;
                    case WasmIrOp.Unreachable:
                        throw new WasmTrapException(WasmTrapCode.Unreachable);
                    case WasmIrOp.Br:
                        pc = ins.A;
                        break;
                    case WasmIrOp.BrIf:
                        if ((uint)s[fp + ins.A] != 0)
                            pc = ins.B;
                        break;
                    case WasmIrOp.BrIfZero:
                        if ((uint)s[fp + ins.A] == 0)
                            pc = ins.B;
                        break;
                    case WasmIrOp.BrTable:
                    {
                        uint index = (uint)s[fp + ins.A];
                        if (index > (uint)ins.C)
                            index = (uint)ins.C;
                        pc = function.BranchTable[ins.B + (int)index];
                        break;
                    }
                    case WasmIrOp.Return:
                    {
                        if (_depth == baseDepth)
                            return;
                        ref var frame = ref _frames[--_depth];
                        function = frame.Function;
                        instance = frame.Instance;
                        pc = frame.Pc;
                        fp = frame.Fp;
                        frame.Function = null!;
                        frame.Instance = null!;
                        code = function.Code;
                        memory = instance.MemoryBytes;
                        _top = fp + function.FrameSize;
                        break;
                    }
                    case WasmIrOp.Call:
                    {
                        var callee = instance.DefinedFunctions[ins.A].Compiled;
                        int calleeFp = fp + ins.B;
                        PushFrame(function, instance, pc, fp, maxDepth);
                        if (calleeFp + callee.FrameSize > s.Length)
                            throw new WasmTrapException(WasmTrapCode.CallStackExhausted);
                        Array.Clear(s, calleeFp + callee.ParamCount, callee.LocalCount - callee.ParamCount);
                        function = callee;
                        code = callee.Code;
                        pc = 0;
                        fp = calleeFp;
                        _top = fp + function.FrameSize;
                        break;
                    }
                    case WasmIrOp.CallImport:
                    case WasmIrOp.CallIndirect:
                    {
                        WasmFunction target;
                        int argumentBase;
                        if (ins.Op == WasmIrOp.CallImport)
                        {
                            target = instance.Functions[ins.A];
                            argumentBase = ins.B;
                        }
                        else
                        {
                            var table = instance.Tables[ins.A];
                            uint index = (uint)s[fp + ins.C];
                            if (index >= table.Size)
                                throw new WasmTrapException(WasmTrapCode.UndefinedElement);
                            ulong reference = table.Elements[index];
                            if (reference == 0)
                                throw new WasmTrapException(WasmTrapCode.UninitializedElement);
                            target = instance.Store.Functions[(int)reference]!;
                            if (target.TypeId != instance.TypeIds[ins.Imm])
                                throw new WasmTrapException(WasmTrapCode.IndirectCallTypeMismatch);
                            argumentBase = ins.B;
                        }

                        if (target is WasmModuleFunction module)
                        {
                            var callee = module.Compiled;
                            int calleeFp = fp + argumentBase;
                            PushFrame(function, instance, pc, fp, maxDepth);
                            if (calleeFp + callee.FrameSize > s.Length)
                                throw new WasmTrapException(WasmTrapCode.CallStackExhausted);
                            Array.Clear(s, calleeFp + callee.ParamCount, callee.LocalCount - callee.ParamCount);
                            instance = module.Instance;
                            memory = instance.MemoryBytes;
                            function = callee;
                            code = callee.Code;
                            pc = 0;
                            fp = calleeFp;
                            _top = fp + function.FrameSize;
                        }
                        else
                        {
                            var host = (WasmHostFunction)target;
                            var type = host.Type;
                            int width = Math.Max(type.Parameters.Length, type.Results.Length);
                            _fuel = fuel;
                            ChargeHost(host);
                            CallHost(host, instance, s.AsSpan(fp + argumentBase, width), type);
                            fuel = _fuel;
                            memory = instance.MemoryBytes;
                        }
                        break;
                    }
                    case WasmIrOp.Copy:
                        s[fp + ins.A] = s[fp + ins.B];
                        break;
                    case WasmIrOp.Moves:
                    {
                        int[] moves = function.Moves;
                        int end = ins.B + ins.C * 2;
                        for (int k = ins.B; k < end; k += 2)
                            s[fp + moves[k]] = s[fp + moves[k + 1]];
                        break;
                    }
                    case WasmIrOp.Const:
                        s[fp + ins.A] = (ulong)ins.Imm;
                        break;
                    case WasmIrOp.Select:
                        if ((uint)s[fp + ins.C] == 0)
                            s[fp + ins.A] = s[fp + ins.B];
                        break;
                    case WasmIrOp.GlobalGet:
                        s[fp + ins.A] = instance.Globals[ins.B].Value;
                        break;
                    case WasmIrOp.GlobalSet:
                        instance.Globals[ins.A].Value = s[fp + ins.B];
                        break;
                    case WasmIrOp.TableGet:
                    {
                        var table = instance.Tables[ins.B];
                        uint index = (uint)s[fp + ins.A];
                        if (index >= table.Size)
                            throw TableTrap();
                        s[fp + ins.A] = table.Elements[index];
                        break;
                    }
                    case WasmIrOp.TableSet:
                    {
                        var table = instance.Tables[ins.C];
                        uint index = (uint)s[fp + ins.A];
                        if (index >= table.Size)
                            throw TableTrap();
                        table.Elements[index] = s[fp + ins.B];
                        break;
                    }
                    case WasmIrOp.TableSize:
                        s[fp + ins.A] = instance.Tables[ins.B].Size;
                        break;
                    case WasmIrOp.TableGrow:
                    {
                        uint delta = (uint)s[fp + ins.B];
                        int old = instance.Tables[ins.C].Grow(delta, s[fp + ins.A]);
                        if (old >= 0)
                            fuel -= delta >> 4;
                        s[fp + ins.A] = (uint)old;
                        break;
                    }
                    case WasmIrOp.TableFill:
                    {
                        var table = instance.Tables[(int)ins.Imm];
                        uint index = (uint)s[fp + ins.A];
                        uint count = (uint)s[fp + ins.C];
                        if ((ulong)index + count > table.Size)
                            throw TableTrap();
                        fuel -= count >> 6;
                        Array.Fill(table.Elements, s[fp + ins.B], (int)index, (int)count);
                        break;
                    }
                    case WasmIrOp.TableCopy:
                    {
                        var destination = instance.Tables[(int)(ins.Imm >> 32)];
                        var source = instance.Tables[(int)(ins.Imm & 0xFFFFFFFF)];
                        uint d = (uint)s[fp + ins.A];
                        uint from = (uint)s[fp + ins.B];
                        uint count = (uint)s[fp + ins.C];
                        if ((ulong)d + count > destination.Size || (ulong)from + count > source.Size)
                            throw TableTrap();
                        fuel -= count >> 6;
                        Array.Copy(source.Elements, from, destination.Elements, d, count);
                        break;
                    }
                    case WasmIrOp.TableInit:
                    {
                        var table = instance.Tables[(int)(ins.Imm >> 32)];
                        var segment = instance.ElementSegments[(int)(ins.Imm & 0xFFFFFFFF)];
                        uint d = (uint)s[fp + ins.A];
                        uint from = (uint)s[fp + ins.B];
                        uint count = (uint)s[fp + ins.C];
                        if ((ulong)d + count > table.Size || (ulong)from + count > (ulong)segment.Length)
                            throw TableTrap();
                        fuel -= count >> 6;
                        Array.Copy(segment, from, table.Elements, d, count);
                        break;
                    }
                    case WasmIrOp.ElemDrop:
                        instance.ElementSegments[ins.A] = Array.Empty<ulong>();
                        break;
                    case WasmIrOp.MemorySize:
                        s[fp + ins.A] = instance.Memory!.Pages;
                        break;
                    case WasmIrOp.MemoryGrow:
                    {
                        uint delta = (uint)s[fp + ins.A];
                        int old = instance.Memory!.Grow(delta);
                        if (old >= 0)
                            fuel -= (long)delta << 8;
                        s[fp + ins.A] = (uint)old;
                        memory = instance.MemoryBytes;
                        break;
                    }
                    case WasmIrOp.MemoryFill:
                    {
                        uint d = (uint)s[fp + ins.A];
                        uint count = (uint)s[fp + ins.C];
                        if ((ulong)d + count > (ulong)memory.Length)
                            throw MemoryTrap();
                        fuel -= count >> 6;
                        memory.AsSpan((int)d, (int)count).Fill((byte)s[fp + ins.B]);
                        break;
                    }
                    case WasmIrOp.MemoryCopy:
                    {
                        uint d = (uint)s[fp + ins.A];
                        uint from = (uint)s[fp + ins.B];
                        uint count = (uint)s[fp + ins.C];
                        if ((ulong)d + count > (ulong)memory.Length || (ulong)from + count > (ulong)memory.Length)
                            throw MemoryTrap();
                        fuel -= count >> 6;
                        memory.AsSpan((int)from, (int)count).CopyTo(memory.AsSpan((int)d, (int)count));
                        break;
                    }
                    case WasmIrOp.MemoryInit:
                    {
                        var segment = instance.DataSegments[(int)ins.Imm];
                        uint d = (uint)s[fp + ins.A];
                        uint from = (uint)s[fp + ins.B];
                        uint count = (uint)s[fp + ins.C];
                        if ((ulong)d + count > (ulong)memory.Length || (ulong)from + count > (ulong)segment.Length)
                            throw MemoryTrap();
                        fuel -= count >> 6;
                        segment.AsSpan((int)from, (int)count).CopyTo(memory.AsSpan((int)d, (int)count));
                        break;
                    }
                    case WasmIrOp.DataDrop:
                        instance.DataSegments[ins.A] = Array.Empty<byte>();
                        break;
                    case WasmIrOp.RefIsNull:
                        s[fp + ins.A] = s[fp + ins.B] == 0 ? 1UL : 0UL;
                        break;
                    case WasmIrOp.RefFunc:
                        s[fp + ins.A] = (ulong)instance.Functions[ins.B].StoreId;
                        break;

                    case WasmIrOp.I32Load:
                        s[fp + ins.A] = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 4), 4));
                        break;
                    case WasmIrOp.I64Load:
                        s[fp + ins.A] = BinaryPrimitives.ReadUInt64LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 8), 8));
                        break;
                    case WasmIrOp.I32Load8S:
                        s[fp + ins.A] = (uint)(int)(sbyte)memory[Address(memory, s[fp + ins.A], ins.Imm, 1)];
                        break;
                    case WasmIrOp.I32Load8U:
                        s[fp + ins.A] = memory[Address(memory, s[fp + ins.A], ins.Imm, 1)];
                        break;
                    case WasmIrOp.I32Load16S:
                        s[fp + ins.A] = (uint)(int)(short)BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 2), 2));
                        break;
                    case WasmIrOp.I32Load16U:
                        s[fp + ins.A] = BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 2), 2));
                        break;
                    case WasmIrOp.I64Load8S:
                        s[fp + ins.A] = (ulong)(long)(sbyte)memory[Address(memory, s[fp + ins.A], ins.Imm, 1)];
                        break;
                    case WasmIrOp.I64Load8U:
                        s[fp + ins.A] = memory[Address(memory, s[fp + ins.A], ins.Imm, 1)];
                        break;
                    case WasmIrOp.I64Load16S:
                        s[fp + ins.A] = (ulong)(long)(short)BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 2), 2));
                        break;
                    case WasmIrOp.I64Load16U:
                        s[fp + ins.A] = BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 2), 2));
                        break;
                    case WasmIrOp.I64Load32S:
                        s[fp + ins.A] = (ulong)(long)(int)BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 4), 4));
                        break;
                    case WasmIrOp.I64Load32U:
                        s[fp + ins.A] = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 4), 4));
                        break;
                    case WasmIrOp.I32Store:
                        BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 4), 4), (uint)s[fp + ins.B]);
                        break;
                    case WasmIrOp.I64Store:
                        BinaryPrimitives.WriteUInt64LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 8), 8), s[fp + ins.B]);
                        break;
                    case WasmIrOp.I32Store8:
                    case WasmIrOp.I64Store8:
                        memory[Address(memory, s[fp + ins.A], ins.Imm, 1)] = (byte)s[fp + ins.B];
                        break;
                    case WasmIrOp.I32Store16:
                    case WasmIrOp.I64Store16:
                        BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 2), 2), (ushort)s[fp + ins.B]);
                        break;
                    case WasmIrOp.I64Store32:
                        BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(Address(memory, s[fp + ins.A], ins.Imm, 4), 4), (uint)s[fp + ins.B]);
                        break;

                    case WasmIrOp.I32AddImm: s[fp + ins.A] = (uint)s[fp + ins.B] + (uint)ins.Imm; break;
                    case WasmIrOp.F32MulImm: s[fp + ins.A] = Bits(F32(s[fp + ins.B]) * F32((ulong)ins.Imm)); break;
                    case WasmIrOp.GlobalAddI32:
                    {
                        var global = instance.Globals[ins.A];
                        global.Value = (uint)global.Value + (uint)ins.Imm;
                        break;
                    }

                    case WasmIrOp.I32Eqz: s[fp + ins.A] = (uint)s[fp + ins.B] == 0 ? 1UL : 0UL; break;
                    case WasmIrOp.I32Eq: s[fp + ins.A] = (uint)s[fp + ins.B] == (uint)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I32Ne: s[fp + ins.A] = (uint)s[fp + ins.B] != (uint)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I32LtS: s[fp + ins.A] = (int)s[fp + ins.B] < (int)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I32LtU: s[fp + ins.A] = (uint)s[fp + ins.B] < (uint)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I32GtS: s[fp + ins.A] = (int)s[fp + ins.B] > (int)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I32GtU: s[fp + ins.A] = (uint)s[fp + ins.B] > (uint)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I32LeS: s[fp + ins.A] = (int)s[fp + ins.B] <= (int)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I32LeU: s[fp + ins.A] = (uint)s[fp + ins.B] <= (uint)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I32GeS: s[fp + ins.A] = (int)s[fp + ins.B] >= (int)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I32GeU: s[fp + ins.A] = (uint)s[fp + ins.B] >= (uint)s[fp + ins.C] ? 1UL : 0UL; break;

                    case WasmIrOp.I64Eqz: s[fp + ins.A] = s[fp + ins.B] == 0 ? 1UL : 0UL; break;
                    case WasmIrOp.I64Eq: s[fp + ins.A] = s[fp + ins.B] == s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I64Ne: s[fp + ins.A] = s[fp + ins.B] != s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I64LtS: s[fp + ins.A] = (long)s[fp + ins.B] < (long)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I64LtU: s[fp + ins.A] = s[fp + ins.B] < s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I64GtS: s[fp + ins.A] = (long)s[fp + ins.B] > (long)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I64GtU: s[fp + ins.A] = s[fp + ins.B] > s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I64LeS: s[fp + ins.A] = (long)s[fp + ins.B] <= (long)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I64LeU: s[fp + ins.A] = s[fp + ins.B] <= s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I64GeS: s[fp + ins.A] = (long)s[fp + ins.B] >= (long)s[fp + ins.C] ? 1UL : 0UL; break;
                    case WasmIrOp.I64GeU: s[fp + ins.A] = s[fp + ins.B] >= s[fp + ins.C] ? 1UL : 0UL; break;

                    case WasmIrOp.F32Eq: s[fp + ins.A] = F32(s[fp + ins.B]) == F32(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F32Ne: s[fp + ins.A] = F32(s[fp + ins.B]) != F32(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F32Lt: s[fp + ins.A] = F32(s[fp + ins.B]) < F32(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F32Gt: s[fp + ins.A] = F32(s[fp + ins.B]) > F32(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F32Le: s[fp + ins.A] = F32(s[fp + ins.B]) <= F32(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F32Ge: s[fp + ins.A] = F32(s[fp + ins.B]) >= F32(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F64Eq: s[fp + ins.A] = F64(s[fp + ins.B]) == F64(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F64Ne: s[fp + ins.A] = F64(s[fp + ins.B]) != F64(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F64Lt: s[fp + ins.A] = F64(s[fp + ins.B]) < F64(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F64Gt: s[fp + ins.A] = F64(s[fp + ins.B]) > F64(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F64Le: s[fp + ins.A] = F64(s[fp + ins.B]) <= F64(s[fp + ins.C]) ? 1UL : 0UL; break;
                    case WasmIrOp.F64Ge: s[fp + ins.A] = F64(s[fp + ins.B]) >= F64(s[fp + ins.C]) ? 1UL : 0UL; break;

                    case WasmIrOp.I32Clz: s[fp + ins.A] = (uint)BitOperations.LeadingZeroCount((uint)s[fp + ins.B]); break;
                    case WasmIrOp.I32Ctz: s[fp + ins.A] = (uint)BitOperations.TrailingZeroCount((uint)s[fp + ins.B]); break;
                    case WasmIrOp.I32Popcnt: s[fp + ins.A] = (uint)BitOperations.PopCount((uint)s[fp + ins.B]); break;
                    case WasmIrOp.I32Add: s[fp + ins.A] = (uint)s[fp + ins.B] + (uint)s[fp + ins.C]; break;
                    case WasmIrOp.I32Sub: s[fp + ins.A] = (uint)s[fp + ins.B] - (uint)s[fp + ins.C]; break;
                    case WasmIrOp.I32Mul: s[fp + ins.A] = (uint)s[fp + ins.B] * (uint)s[fp + ins.C]; break;
                    case WasmIrOp.I32DivS:
                    {
                        int a = (int)s[fp + ins.B], b = (int)s[fp + ins.C];
                        if (b == 0)
                            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
                        if (a == int.MinValue && b == -1)
                            throw new WasmTrapException(WasmTrapCode.IntegerOverflow);
                        s[fp + ins.A] = (uint)(a / b);
                        break;
                    }
                    case WasmIrOp.I32DivU:
                    {
                        uint b = (uint)s[fp + ins.C];
                        if (b == 0)
                            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
                        s[fp + ins.A] = (uint)s[fp + ins.B] / b;
                        break;
                    }
                    case WasmIrOp.I32RemS:
                    {
                        int a = (int)s[fp + ins.B], b = (int)s[fp + ins.C];
                        if (b == 0)
                            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
                        s[fp + ins.A] = b == -1 ? 0UL : (uint)(a % b);
                        break;
                    }
                    case WasmIrOp.I32RemU:
                    {
                        uint b = (uint)s[fp + ins.C];
                        if (b == 0)
                            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
                        s[fp + ins.A] = (uint)s[fp + ins.B] % b;
                        break;
                    }
                    case WasmIrOp.I32And: s[fp + ins.A] = (uint)s[fp + ins.B] & (uint)s[fp + ins.C]; break;
                    case WasmIrOp.I32Or: s[fp + ins.A] = (uint)s[fp + ins.B] | (uint)s[fp + ins.C]; break;
                    case WasmIrOp.I32Xor: s[fp + ins.A] = (uint)s[fp + ins.B] ^ (uint)s[fp + ins.C]; break;
                    case WasmIrOp.I32Shl: s[fp + ins.A] = (uint)s[fp + ins.B] << ((int)s[fp + ins.C] & 31); break;
                    case WasmIrOp.I32ShrS: s[fp + ins.A] = (uint)((int)s[fp + ins.B] >> ((int)s[fp + ins.C] & 31)); break;
                    case WasmIrOp.I32ShrU: s[fp + ins.A] = (uint)s[fp + ins.B] >> ((int)s[fp + ins.C] & 31); break;
                    case WasmIrOp.I32Rotl: s[fp + ins.A] = BitOperations.RotateLeft((uint)s[fp + ins.B], (int)s[fp + ins.C] & 31); break;
                    case WasmIrOp.I32Rotr: s[fp + ins.A] = BitOperations.RotateRight((uint)s[fp + ins.B], (int)s[fp + ins.C] & 31); break;

                    case WasmIrOp.I64Clz: s[fp + ins.A] = (ulong)BitOperations.LeadingZeroCount(s[fp + ins.B]); break;
                    case WasmIrOp.I64Ctz: s[fp + ins.A] = (ulong)BitOperations.TrailingZeroCount(s[fp + ins.B]); break;
                    case WasmIrOp.I64Popcnt: s[fp + ins.A] = (ulong)BitOperations.PopCount(s[fp + ins.B]); break;
                    case WasmIrOp.I64Add: s[fp + ins.A] = s[fp + ins.B] + s[fp + ins.C]; break;
                    case WasmIrOp.I64Sub: s[fp + ins.A] = s[fp + ins.B] - s[fp + ins.C]; break;
                    case WasmIrOp.I64Mul: s[fp + ins.A] = s[fp + ins.B] * s[fp + ins.C]; break;
                    case WasmIrOp.I64DivS:
                    {
                        long a = (long)s[fp + ins.B], b = (long)s[fp + ins.C];
                        if (b == 0)
                            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
                        if (a == long.MinValue && b == -1)
                            throw new WasmTrapException(WasmTrapCode.IntegerOverflow);
                        s[fp + ins.A] = (ulong)(a / b);
                        break;
                    }
                    case WasmIrOp.I64DivU:
                    {
                        ulong b = s[fp + ins.C];
                        if (b == 0)
                            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
                        s[fp + ins.A] = s[fp + ins.B] / b;
                        break;
                    }
                    case WasmIrOp.I64RemS:
                    {
                        long a = (long)s[fp + ins.B], b = (long)s[fp + ins.C];
                        if (b == 0)
                            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
                        s[fp + ins.A] = b == -1 ? 0UL : (ulong)(a % b);
                        break;
                    }
                    case WasmIrOp.I64RemU:
                    {
                        ulong b = s[fp + ins.C];
                        if (b == 0)
                            throw new WasmTrapException(WasmTrapCode.IntegerDivideByZero);
                        s[fp + ins.A] = s[fp + ins.B] % b;
                        break;
                    }
                    case WasmIrOp.I64And: s[fp + ins.A] = s[fp + ins.B] & s[fp + ins.C]; break;
                    case WasmIrOp.I64Or: s[fp + ins.A] = s[fp + ins.B] | s[fp + ins.C]; break;
                    case WasmIrOp.I64Xor: s[fp + ins.A] = s[fp + ins.B] ^ s[fp + ins.C]; break;
                    case WasmIrOp.I64Shl: s[fp + ins.A] = s[fp + ins.B] << ((int)s[fp + ins.C] & 63); break;
                    case WasmIrOp.I64ShrS: s[fp + ins.A] = (ulong)((long)s[fp + ins.B] >> ((int)s[fp + ins.C] & 63)); break;
                    case WasmIrOp.I64ShrU: s[fp + ins.A] = s[fp + ins.B] >> ((int)s[fp + ins.C] & 63); break;
                    case WasmIrOp.I64Rotl: s[fp + ins.A] = BitOperations.RotateLeft(s[fp + ins.B], (int)s[fp + ins.C] & 63); break;
                    case WasmIrOp.I64Rotr: s[fp + ins.A] = BitOperations.RotateRight(s[fp + ins.B], (int)s[fp + ins.C] & 63); break;

                    case WasmIrOp.F32Abs: s[fp + ins.A] = (uint)s[fp + ins.B] & 0x7FFFFFFFu; break;
                    case WasmIrOp.F32Neg: s[fp + ins.A] = (uint)s[fp + ins.B] ^ 0x80000000u; break;
                    case WasmIrOp.F32Ceil: s[fp + ins.A] = Bits(Ceil(F32(s[fp + ins.B]))); break;
                    case WasmIrOp.F32Floor: s[fp + ins.A] = Bits(Floor(F32(s[fp + ins.B]))); break;
                    case WasmIrOp.F32Trunc: s[fp + ins.A] = Bits(Trunc(F32(s[fp + ins.B]))); break;
                    case WasmIrOp.F32Nearest: s[fp + ins.A] = Bits(Nearest(F32(s[fp + ins.B]))); break;
                    case WasmIrOp.F32Sqrt: s[fp + ins.A] = Bits(MathF.Sqrt(F32(s[fp + ins.B]))); break;
                    case WasmIrOp.F32Add: s[fp + ins.A] = Bits(F32(s[fp + ins.B]) + F32(s[fp + ins.C])); break;
                    case WasmIrOp.F32Sub: s[fp + ins.A] = Bits(F32(s[fp + ins.B]) - F32(s[fp + ins.C])); break;
                    case WasmIrOp.F32Mul: s[fp + ins.A] = Bits(F32(s[fp + ins.B]) * F32(s[fp + ins.C])); break;
                    case WasmIrOp.F32Div: s[fp + ins.A] = Bits(F32(s[fp + ins.B]) / F32(s[fp + ins.C])); break;
                    case WasmIrOp.F32Min: s[fp + ins.A] = Bits(Min(F32(s[fp + ins.B]), F32(s[fp + ins.C]))); break;
                    case WasmIrOp.F32Max: s[fp + ins.A] = Bits(Max(F32(s[fp + ins.B]), F32(s[fp + ins.C]))); break;
                    case WasmIrOp.F32Copysign: s[fp + ins.A] = ((uint)s[fp + ins.B] & 0x7FFFFFFFu) | ((uint)s[fp + ins.C] & 0x80000000u); break;

                    case WasmIrOp.F64Abs: s[fp + ins.A] = s[fp + ins.B] & 0x7FFFFFFFFFFFFFFFUL; break;
                    case WasmIrOp.F64Neg: s[fp + ins.A] = s[fp + ins.B] ^ 0x8000000000000000UL; break;
                    case WasmIrOp.F64Ceil: s[fp + ins.A] = Bits(Ceil(F64(s[fp + ins.B]))); break;
                    case WasmIrOp.F64Floor: s[fp + ins.A] = Bits(Floor(F64(s[fp + ins.B]))); break;
                    case WasmIrOp.F64Trunc: s[fp + ins.A] = Bits(Trunc(F64(s[fp + ins.B]))); break;
                    case WasmIrOp.F64Nearest: s[fp + ins.A] = Bits(Nearest(F64(s[fp + ins.B]))); break;
                    case WasmIrOp.F64Sqrt: s[fp + ins.A] = Bits(Math.Sqrt(F64(s[fp + ins.B]))); break;
                    case WasmIrOp.F64Add: s[fp + ins.A] = Bits(F64(s[fp + ins.B]) + F64(s[fp + ins.C])); break;
                    case WasmIrOp.F64Sub: s[fp + ins.A] = Bits(F64(s[fp + ins.B]) - F64(s[fp + ins.C])); break;
                    case WasmIrOp.F64Mul: s[fp + ins.A] = Bits(F64(s[fp + ins.B]) * F64(s[fp + ins.C])); break;
                    case WasmIrOp.F64Div: s[fp + ins.A] = Bits(F64(s[fp + ins.B]) / F64(s[fp + ins.C])); break;
                    case WasmIrOp.F64Min: s[fp + ins.A] = Bits(Min(F64(s[fp + ins.B]), F64(s[fp + ins.C]))); break;
                    case WasmIrOp.F64Max: s[fp + ins.A] = Bits(Max(F64(s[fp + ins.B]), F64(s[fp + ins.C]))); break;
                    case WasmIrOp.F64Copysign: s[fp + ins.A] = (s[fp + ins.B] & 0x7FFFFFFFFFFFFFFFUL) | (s[fp + ins.C] & 0x8000000000000000UL); break;

                    case WasmIrOp.I32WrapI64: s[fp + ins.A] = (uint)s[fp + ins.B]; break;
                    case WasmIrOp.I32TruncF32S: s[fp + ins.A] = (uint)TruncF32ToI32(F32(s[fp + ins.B])); break;
                    case WasmIrOp.I32TruncF32U: s[fp + ins.A] = TruncF32ToU32(F32(s[fp + ins.B])); break;
                    case WasmIrOp.I32TruncF64S: s[fp + ins.A] = (uint)TruncF64ToI32(F64(s[fp + ins.B])); break;
                    case WasmIrOp.I32TruncF64U: s[fp + ins.A] = TruncF64ToU32(F64(s[fp + ins.B])); break;
                    case WasmIrOp.I64ExtendI32S: s[fp + ins.A] = (ulong)(long)(int)s[fp + ins.B]; break;
                    case WasmIrOp.I64ExtendI32U: s[fp + ins.A] = (uint)s[fp + ins.B]; break;
                    case WasmIrOp.I64TruncF32S: s[fp + ins.A] = (ulong)TruncF32ToI64(F32(s[fp + ins.B])); break;
                    case WasmIrOp.I64TruncF32U: s[fp + ins.A] = TruncF32ToU64(F32(s[fp + ins.B])); break;
                    case WasmIrOp.I64TruncF64S: s[fp + ins.A] = (ulong)TruncF64ToI64(F64(s[fp + ins.B])); break;
                    case WasmIrOp.I64TruncF64U: s[fp + ins.A] = TruncF64ToU64(F64(s[fp + ins.B])); break;
                    case WasmIrOp.F32ConvertI32S: s[fp + ins.A] = Bits((float)(int)s[fp + ins.B]); break;
                    case WasmIrOp.F32ConvertI32U: s[fp + ins.A] = Bits((float)(uint)s[fp + ins.B]); break;
                    case WasmIrOp.F32ConvertI64S: s[fp + ins.A] = Bits((float)(long)s[fp + ins.B]); break;
                    case WasmIrOp.F32ConvertI64U: s[fp + ins.A] = Bits(U64ToF32(s[fp + ins.B])); break;
                    case WasmIrOp.F32DemoteF64: s[fp + ins.A] = Bits((float)F64(s[fp + ins.B])); break;
                    case WasmIrOp.F64ConvertI32S: s[fp + ins.A] = Bits((double)(int)s[fp + ins.B]); break;
                    case WasmIrOp.F64ConvertI32U: s[fp + ins.A] = Bits((double)(uint)s[fp + ins.B]); break;
                    case WasmIrOp.F64ConvertI64S: s[fp + ins.A] = Bits((double)(long)s[fp + ins.B]); break;
                    case WasmIrOp.F64ConvertI64U: s[fp + ins.A] = Bits(U64ToF64(s[fp + ins.B])); break;
                    case WasmIrOp.F64PromoteF32: s[fp + ins.A] = Bits((double)F32(s[fp + ins.B])); break;
                    case WasmIrOp.I32Extend8S: s[fp + ins.A] = (uint)(int)(sbyte)s[fp + ins.B]; break;
                    case WasmIrOp.I32Extend16S: s[fp + ins.A] = (uint)(int)(short)s[fp + ins.B]; break;
                    case WasmIrOp.I64Extend8S: s[fp + ins.A] = (ulong)(long)(sbyte)s[fp + ins.B]; break;
                    case WasmIrOp.I64Extend16S: s[fp + ins.A] = (ulong)(long)(short)s[fp + ins.B]; break;
                    case WasmIrOp.I64Extend32S: s[fp + ins.A] = (ulong)(long)(int)s[fp + ins.B]; break;

                    case WasmIrOp.I32TruncSatF32S: s[fp + ins.A] = (uint)SatF32ToI32(F32(s[fp + ins.B])); break;
                    case WasmIrOp.I32TruncSatF32U: s[fp + ins.A] = SatF32ToU32(F32(s[fp + ins.B])); break;
                    case WasmIrOp.I32TruncSatF64S: s[fp + ins.A] = (uint)SatF64ToI32(F64(s[fp + ins.B])); break;
                    case WasmIrOp.I32TruncSatF64U: s[fp + ins.A] = SatF64ToU32(F64(s[fp + ins.B])); break;
                    case WasmIrOp.I64TruncSatF32S: s[fp + ins.A] = (ulong)SatF32ToI64(F32(s[fp + ins.B])); break;
                    case WasmIrOp.I64TruncSatF32U: s[fp + ins.A] = SatF32ToU64(F32(s[fp + ins.B])); break;
                    case WasmIrOp.I64TruncSatF64S: s[fp + ins.A] = (ulong)SatF64ToI64(F64(s[fp + ins.B])); break;
                    case WasmIrOp.I64TruncSatF64U: s[fp + ins.A] = SatF64ToU64(F64(s[fp + ins.B])); break;

                    default:
                        throw new InvalidOperationException("unknown instruction " + ins.Op);
                }
            }
        }
        catch (WasmTrapException trap) when (!trap.HasLocation)
        {
            int at = pc - 1;
            trap.SetLocation(instance.Module, function.FunctionIndex, at >= 0 && at < function.Offsets.Length ? function.Offsets[at] : -1);
            throw;
        }
        finally
        {
            _fuel = fuel;
        }
    }
}
