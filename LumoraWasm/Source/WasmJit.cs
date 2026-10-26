// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Wasm;

internal delegate void WasmNativeBody(WasmExecutor executor, WasmInstance instance, ulong[] stack, int fp);

// The second back end: a validated module's IR turned into .NET methods the runtime compiles to machine code,
// one per function, shared by every instance of the module. Nothing new is decided here. Each IR instruction
// becomes the interpreter's own work on that instruction (WasmIrMath), with the frame's slots in locals
// instead of the register stack and the branches as jumps instead of a dispatch loop, so a module gives the
// same bits, traps in the same place, and burns the same fuel whichever back end runs it.
//
// The code it makes reaches nothing a module could not reach in the interpreter: its slots, its instance's
// memory, globals and tables, and calls through the instance's own imports, which are only ever the host
// functions the linker was handed. No reflection, no types or members named by the module.
//
// Platforms that run generated code through an interpreter of their own, or not at all, keep the
// interpreter: Supported says which. A module whose code cannot be made for any reason runs interpreted
// too. -xlinka
internal static class WasmJit
{
    public const int None = 0;
    public const int Making = 1;
    public const int Made = 2;
    public const int Refused = 3;

    public static bool Supported { get; } = RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled;

    // A native frame keeps its slots on the .NET stack, and the stack check made as each one starts leaves room
    // for a frame of about this size; a function wider than it runs in the interpreter, whose frames live in an
    // array. Nothing a graph makes comes near it.
    public const int MaxFrameSlots = 4096;

    private static readonly Type[] BodyParameters =
        { typeof(WasmCompiledFunction), typeof(WasmExecutor), typeof(WasmInstance), typeof(ulong[]), typeof(int) };

    // A module first run before anyone made its code: made on a worker meanwhile, and taken up from the next
    // call on.
    public static void Request(WasmModule module)
    {
        if (!WasmStore.NativeCode || Volatile.Read(ref module.NativeState) != None)
            return;
        if (Interlocked.CompareExchange(ref module.NativeState, Making, None) != None)
            return;
        Task.Run(() => Make(module));
    }

    // On this thread. True once the module's code is in place, whoever made it.
    public static bool Compile(WasmModule module)
    {
        if (!WasmStore.NativeCode)
            return false;
        while (true)
        {
            int state = Volatile.Read(ref module.NativeState);
            if (state == Made)
                return true;
            if (state == Refused)
                return false;
            if (state == None && Interlocked.CompareExchange(ref module.NativeState, Making, None) == None)
                return Make(module);
            // Another thread is making it: it will not be long.
            Thread.Yield();
        }
    }

    private static bool Make(WasmModule module)
    {
        try
        {
            var functions = module.Compiled;
            var directs = Directs(module, out var costs);
            var methods = new DynamicMethod[functions.Length];
            for (int i = 0; i < functions.Length; i++)
            {
                if (functions[i].FrameSize > MaxFrameSlots)
                    throw new NotSupportedException($"function {functions[i].FunctionIndex} keeps {functions[i].FrameSize} slots, past the {MaxFrameSlots} native code takes");
                functions[i].Owner = module;
                functions[i].Peers = functions;
                functions[i].Directs = directs;
                methods[i] = new DynamicMethod("wasm_f" + functions[i].FunctionIndex, null, BodyParameters, typeof(WasmJit).Module, skipVisibility: true);
            }
            for (int i = 0; i < functions.Length; i++)
                new Emitter(module, functions[i], methods, costs).Emit(methods[i].GetILGenerator(64 + functions[i].Code.Length * 24));

            var bodies = new WasmNativeBody[functions.Length];
            for (int i = 0; i < functions.Length; i++)
                bodies[i] = (WasmNativeBody)methods[i].CreateDelegate(typeof(WasmNativeBody), functions[i]);
            // Each body returns at once when it is handed no executor, so this has the runtime compile it here
            // rather than on the thread that first runs it. The direct calls are pure, so one call each with zero
            // lanes does the same for them.
            foreach (var body in bodies)
                body(null!, null!, null!, 0);
            foreach (var direct in directs)
            {
                if (direct == null)
                    continue;
                try
                {
                    direct.DynamicInvoke(direct.GetType().GetMethod("Invoke")!.GetParameters().Select(_ => (object)0UL).ToArray());
                }
                catch (TargetInvocationException)
                {
                }
            }
            for (int i = 0; i < functions.Length; i++)
                functions[i].Native = bodies[i];
            Volatile.Write(ref module.NativeState, Made);
            return true;
        }
        catch (Exception ex)
        {
            module.NativeRefusal = ex.GetType().Name + ": " + ex.Message;
            Volatile.Write(ref module.NativeState, Refused);
            return false;
        }
    }

    // What the module's resolver offers for each function import, kept only where it has exactly the shape the
    // import's lanes need.
    private static Delegate?[] Directs(WasmModule module, out int[] costs)
    {
        var directs = new Delegate?[module.ImportedFunctionCount];
        costs = new int[directs.Length];
        if (module.DirectImports is not { } resolve)
            return directs;
        int index = 0;
        foreach (var import in module.ImportList)
        {
            if (import.Kind != WasmExternKind.Function)
                continue;
            var type = module.Types[import.TypeIndex];
            if (resolve(import, type) is { } direct && DirectType(type) is { } shape && direct.Call.GetType() == shape)
            {
                directs[index] = direct.Call;
                costs[index] = Math.Max(0, direct.FuelCost);
            }
            index++;
        }
        return directs;
    }

    // Func<ulong, ..., ulong>: every lane a ulong, one result.
    internal static Type? DirectType(WasmFuncType type)
    {
        if (type.Results.Length != 1 || type.Parameters.Length > 16)
            return null;
        var lanes = new Type[type.Parameters.Length + 1];
        Array.Fill(lanes, typeof(ulong));
        return typeof(Func<>).Assembly.GetType("System.Func`" + lanes.Length)?.MakeGenericType(lanes);
    }

    // A trap out of native code gets the place the interpreter would give it: the function that was running and
    // the instruction it was at.
    public static void Locate(WasmTrapException trap, WasmCompiledFunction function, int at)
        => trap.SetLocation(function.Owner!, function.FunctionIndex, at >= 0 && at < function.Offsets.Length ? function.Offsets[at] : -1);

    private sealed class Emitter
    {
        private static readonly FieldInfo FuelField = Field(typeof(WasmExecutor), "_fuel");
        private static readonly FieldInfo GlobalsField = Field(typeof(WasmInstance), nameof(WasmInstance.Globals));
        private static readonly FieldInfo ValueField = Field(typeof(WasmGlobal), nameof(WasmGlobal.Value));
        private static readonly FieldInfo PeersField = Field(typeof(WasmCompiledFunction), nameof(WasmCompiledFunction.Peers));
        private static readonly FieldInfo DirectsField = Field(typeof(WasmCompiledFunction), nameof(WasmCompiledFunction.Directs));
        private static readonly MethodInfo ChargeDirect = Method(typeof(WasmExecutor), nameof(WasmExecutor.ChargeDirect));
        private static readonly MethodInfo Burn = Method(typeof(WasmExecutor), nameof(WasmExecutor.Burn));
        private static readonly FieldInfo NativeFnField = Field(typeof(WasmExecutor), nameof(WasmExecutor._nativeFn));
        private static readonly FieldInfo NativeAtField = Field(typeof(WasmExecutor), nameof(WasmExecutor._nativeAt));
        private static readonly MethodInfo MemoryBytes = typeof(WasmInstance).GetProperty("MemoryBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!;
        private static readonly MethodInfo EnterNative = Method(typeof(WasmExecutor), nameof(WasmExecutor.EnterNative));
        private static readonly MethodInfo PushNative = Method(typeof(WasmExecutor), nameof(WasmExecutor.PushNative));
        private static readonly MethodInfo PopNative = Method(typeof(WasmExecutor), nameof(WasmExecutor.PopNative));
        private static readonly MethodInfo CallImport = Method(typeof(WasmExecutor), nameof(WasmExecutor.CallImportNative));
        private static readonly MethodInfo CallIndirect = Method(typeof(WasmExecutor), nameof(WasmExecutor.CallIndirectNative));
        private static readonly Dictionary<string, MethodInfo> Math = MathMethods();
        private static readonly Dictionary<WasmIrOp, MethodInfo> MathOf = MathByOp();

        private readonly WasmModule _module;
        private readonly WasmCompiledFunction _f;
        private readonly DynamicMethod[] _methods;
        private readonly int[] _costs;
        private ILGenerator _il = null!;
        private LocalBuilder[] _slots = Array.Empty<LocalBuilder>();
        private LocalBuilder _memory = null!;
        private LocalBuilder _globals = null!;
        private Label[] _labels = Array.Empty<Label>();

        public Emitter(WasmModule module, WasmCompiledFunction function, DynamicMethod[] methods, int[] costs)
        {
            _module = module;
            _f = function;
            _methods = methods;
            _costs = costs;
        }

        private static FieldInfo Field(Type type, string name)
            => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(type.Name, name);

        private static MethodInfo Method(Type type, string name)
            => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(type.Name, name);

        private static Dictionary<string, MethodInfo> MathMethods()
        {
            var methods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
            foreach (var method in typeof(WasmIrMath).GetMethods(BindingFlags.Static | BindingFlags.Public))
                methods[method.Name] = method;
            return methods;
        }

        // By instruction rather than by its name: the first numeric and the first saturating instruction share
        // their values with NumericBase and SatBase, which is the name ToString gives them.
        private static Dictionary<WasmIrOp, MethodInfo> MathByOp()
        {
            var methods = MathMethods();
            var byOp = new Dictionary<WasmIrOp, MethodInfo>();
            foreach (var name in Enum.GetNames<WasmIrOp>())
            {
                if (methods.TryGetValue(name, out var method))
                    byOp[Enum.Parse<WasmIrOp>(name)] = method;
            }
            return byOp;
        }

        public void Emit(ILGenerator il)
        {
            _il = il;
            var code = _f.Code;
            _slots = new LocalBuilder[_f.FrameSize];
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = il.DeclareLocal(typeof(ulong));
            _memory = il.DeclareLocal(typeof(byte[]));
            _globals = il.DeclareLocal(typeof(WasmGlobal[]));
            _labels = new Label[code.Length];
            for (int i = 0; i < code.Length; i++)
                _labels[i] = il.DefineLabel();

            // No executor: the warm-up call, which only wants this compiled.
            var run = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Brtrue, run);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(run);

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_S, (byte)4);
            Int(_f.FrameSize);
            il.Emit(OpCodes.Call, EnterNative);
            Running();
            for (int i = 0; i < _f.ParamCount; i++)
            {
                StackAddress(i);
                il.Emit(OpCodes.Ldelem_I8);
                il.Emit(OpCodes.Stloc, _slots[i]);
            }
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldfld, GlobalsField);
            il.Emit(OpCodes.Stloc, _globals);
            ReloadMemory();

            for (int k = 0; k < code.Length; k++)
            {
                il.MarkLabel(_labels[k]);
                Step(k, code[k]);
            }
            // The IR never runs off its end; the guard keeps the method well formed if it did.
            Int((int)WasmTrapCode.Unreachable);
            il.Emit(OpCodes.Call, Math[nameof(WasmIrMath.Trap)]);
            il.Emit(OpCodes.Throw);
        }

        // Which function is running, for whoever places a trap: set as it starts and again whenever a call it made
        // returns. A trap is placed once, where it is caught, from this and the instruction At last named, so no
        // native frame has a handler of its own and a trap leaves a deep recursion without nesting a rethrow
        // per frame.
        private void Running()
        {
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Stfld, NativeFnField);
        }

        // The fuel field, for the bulk instructions that charge as they go.
        private void FuelAddress()
        {
            _il.Emit(OpCodes.Ldarg_1);
            _il.Emit(OpCodes.Ldflda, FuelField);
        }

        private void Int(int value) => _il.Emit(OpCodes.Ldc_I4, value);

        private void Long(long value) => _il.Emit(OpCodes.Ldc_I8, value);

        private void Load(int slot) => _il.Emit(OpCodes.Ldloc, _slots[slot]);

        private void Store(int slot) => _il.Emit(OpCodes.Stloc, _slots[slot]);

        private void Call(string name) => _il.Emit(OpCodes.Call, Math[name]);

        // The instruction a trap from here on belongs to.
        private void At(int k)
        {
            _il.Emit(OpCodes.Ldarg_1);
            Int(k);
            _il.Emit(OpCodes.Stfld, NativeAtField);
        }

        // stack, fp + offset: the element address pair for ldelem/stelem.
        private void StackAddress(int offset)
        {
            _il.Emit(OpCodes.Ldarg_3);
            _il.Emit(OpCodes.Ldarg_S, (byte)4);
            if (offset != 0)
            {
                Int(offset);
                _il.Emit(OpCodes.Add);
            }
        }

        private void ReloadMemory()
        {
            _il.Emit(OpCodes.Ldarg_2);
            _il.Emit(OpCodes.Call, MemoryBytes);
            _il.Emit(OpCodes.Stloc, _memory);
        }

        private void Spill(int from, int count)
        {
            for (int i = 0; i < count; i++)
            {
                StackAddress(from + i);
                Load(from + i);
                _il.Emit(OpCodes.Stelem_I8);
            }
        }

        private void Fill(int from, int count)
        {
            for (int i = 0; i < count; i++)
            {
                StackAddress(from + i);
                _il.Emit(OpCodes.Ldelem_I8);
                Store(from + i);
            }
        }

        private void Step(int k, in WasmInstr ins)
        {
            var il = _il;
            switch (ins.Op)
            {
                case WasmIrOp.Fuel:
                    At(k);
                    il.Emit(OpCodes.Ldarg_1);
                    Int(ins.A);
                    il.Emit(OpCodes.Call, Burn);
                    return;
                case WasmIrOp.Unreachable:
                    At(k);
                    Int((int)WasmTrapCode.Unreachable);
                    Call(nameof(WasmIrMath.Trap));
                    il.Emit(OpCodes.Throw);
                    return;
                case WasmIrOp.Br:
                    il.Emit(OpCodes.Br, _labels[ins.A]);
                    return;
                case WasmIrOp.BrIf:
                    Load(ins.A);
                    il.Emit(OpCodes.Conv_U4);
                    il.Emit(OpCodes.Brtrue, _labels[ins.B]);
                    return;
                case WasmIrOp.BrIfZero:
                    Load(ins.A);
                    il.Emit(OpCodes.Conv_U4);
                    il.Emit(OpCodes.Brfalse, _labels[ins.B]);
                    return;
                case WasmIrOp.BrTable:
                {
                    // Anything at or past the last entry takes the last, as the interpreter clamps it.
                    var targets = new Label[ins.C];
                    for (int i = 0; i < ins.C; i++)
                        targets[i] = _labels[_f.BranchTable[ins.B + i]];
                    Load(ins.A);
                    il.Emit(OpCodes.Conv_U4);
                    il.Emit(OpCodes.Switch, targets);
                    il.Emit(OpCodes.Br, _labels[_f.BranchTable[ins.B + ins.C]]);
                    return;
                }
                case WasmIrOp.Return:
                    Spill(0, _f.Type.Results.Length);
                    il.Emit(OpCodes.Ret);
                    return;
                case WasmIrOp.Call:
                {
                    var callee = _module.Compiled[ins.A];
                    Spill(ins.B, callee.ParamCount);
                    At(k);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_S, (byte)4);
                    Int(ins.B);
                    il.Emit(OpCodes.Add);
                    Int(callee.FrameSize);
                    il.Emit(OpCodes.Call, PushNative);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, PeersField);
                    Int(ins.A);
                    il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Ldarg_S, (byte)4);
                    Int(ins.B);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Call, _methods[ins.A]);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, PopNative);
                    Running();
                    ReloadMemory();
                    Fill(ins.B, callee.Type.Results.Length);
                    return;
                }
                case WasmIrOp.CallImport when _f.Directs[ins.A] is { } direct:
                {
                    // Charged and timed like the host call it stands for, then made straight from the slots. The charge
                    // lands once the call has answered, so a call that throws leaves the fuel as a host call that
                    // throws does. The call turns its own throws into traps.
                    var type = _module.FunctionType((uint)ins.A);
                    var shape = direct.GetType();
                    var charged = il.DeclareLocal(typeof(long));
                    At(k);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldfld, FuelField);
                    Int(_costs[ins.A]);
                    il.Emit(OpCodes.Call, ChargeDirect);
                    il.Emit(OpCodes.Stloc, charged);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, DirectsField);
                    Int(ins.A);
                    il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(OpCodes.Castclass, shape);
                    for (int i = 0; i < type.Parameters.Length; i++)
                        Load(ins.B + i);
                    il.Emit(OpCodes.Callvirt, shape.GetMethod("Invoke")!);
                    Store(ins.B);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldloc, charged);
                    il.Emit(OpCodes.Stfld, FuelField);
                    return;
                }
                case WasmIrOp.CallImport:
                {
                    var type = _module.FunctionType((uint)ins.A);
                    Spill(ins.B, type.Parameters.Length);
                    At(k);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    Int(ins.A);
                    il.Emit(OpCodes.Ldarg_S, (byte)4);
                    Int(ins.B);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Ldarg_S, (byte)4);
                    Int(_f.FrameSize);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Call, CallImport);
                    ReloadMemory();
                    Fill(ins.B, type.Results.Length);
                    return;
                }
                case WasmIrOp.CallIndirect:
                {
                    var type = _module.Types[ins.Imm];
                    Spill(ins.B, type.Parameters.Length);
                    At(k);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    Int(ins.A);
                    Long(ins.Imm);
                    Load(ins.C);
                    il.Emit(OpCodes.Ldarg_S, (byte)4);
                    Int(ins.B);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Ldarg_S, (byte)4);
                    Int(_f.FrameSize);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Call, CallIndirect);
                    ReloadMemory();
                    Fill(ins.B, type.Results.Length);
                    return;
                }
                case WasmIrOp.Copy:
                    Load(ins.B);
                    Store(ins.A);
                    return;
                case WasmIrOp.Moves:
                {
                    int end = ins.B + ins.C * 2;
                    for (int m = ins.B; m < end; m += 2)
                    {
                        Load(_f.Moves[m + 1]);
                        Store(_f.Moves[m]);
                    }
                    return;
                }
                case WasmIrOp.Const:
                    Long(ins.Imm);
                    Store(ins.A);
                    return;
                case WasmIrOp.Select:
                {
                    var keep = il.DefineLabel();
                    Load(ins.C);
                    il.Emit(OpCodes.Conv_U4);
                    il.Emit(OpCodes.Brtrue, keep);
                    Load(ins.B);
                    Store(ins.A);
                    il.MarkLabel(keep);
                    return;
                }
                case WasmIrOp.GlobalGet:
                    il.Emit(OpCodes.Ldloc, _globals);
                    Int(ins.B);
                    il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(OpCodes.Ldfld, ValueField);
                    Store(ins.A);
                    return;
                case WasmIrOp.GlobalSet:
                    il.Emit(OpCodes.Ldloc, _globals);
                    Int(ins.A);
                    il.Emit(OpCodes.Ldelem_Ref);
                    Load(ins.B);
                    il.Emit(OpCodes.Stfld, ValueField);
                    return;
                case WasmIrOp.GlobalAddI32:
                    il.Emit(OpCodes.Ldloc, _globals);
                    Int(ins.A);
                    il.Emit(OpCodes.Ldelem_Ref);
                    Long(ins.Imm);
                    Call(nameof(WasmIrMath.GlobalAddI32));
                    return;
                case WasmIrOp.TableGet:
                    At(k);
                    il.Emit(OpCodes.Ldarg_2);
                    Int(ins.B);
                    Load(ins.A);
                    Call(nameof(WasmIrMath.TableGet));
                    Store(ins.A);
                    return;
                case WasmIrOp.TableSet:
                    At(k);
                    il.Emit(OpCodes.Ldarg_2);
                    Int(ins.C);
                    Load(ins.A);
                    Load(ins.B);
                    Call(nameof(WasmIrMath.TableSet));
                    return;
                case WasmIrOp.TableSize:
                    il.Emit(OpCodes.Ldarg_2);
                    Int(ins.B);
                    Call(nameof(WasmIrMath.TableSize));
                    Store(ins.A);
                    return;
                case WasmIrOp.TableGrow:
                    il.Emit(OpCodes.Ldarg_2);
                    Int(ins.C);
                    Load(ins.A);
                    Load(ins.B);
                    FuelAddress();
                    Call(nameof(WasmIrMath.TableGrow));
                    Store(ins.A);
                    return;
                case WasmIrOp.TableFill:
                case WasmIrOp.TableCopy:
                case WasmIrOp.TableInit:
                    At(k);
                    il.Emit(OpCodes.Ldarg_2);
                    Long(ins.Imm);
                    Load(ins.A);
                    Load(ins.B);
                    Load(ins.C);
                    FuelAddress();
                    Call(ins.Op.ToString());
                    return;
                case WasmIrOp.ElemDrop:
                case WasmIrOp.DataDrop:
                    il.Emit(OpCodes.Ldarg_2);
                    Int(ins.A);
                    Call(ins.Op.ToString());
                    return;
                case WasmIrOp.MemorySize:
                    il.Emit(OpCodes.Ldarg_2);
                    Call(nameof(WasmIrMath.MemorySize));
                    Store(ins.A);
                    return;
                case WasmIrOp.MemoryGrow:
                    il.Emit(OpCodes.Ldarg_2);
                    Load(ins.A);
                    FuelAddress();
                    Call(nameof(WasmIrMath.MemoryGrow));
                    Store(ins.A);
                    ReloadMemory();
                    return;
                case WasmIrOp.MemoryFill:
                case WasmIrOp.MemoryCopy:
                    At(k);
                    il.Emit(OpCodes.Ldloc, _memory);
                    Load(ins.A);
                    Load(ins.B);
                    Load(ins.C);
                    FuelAddress();
                    Call(ins.Op.ToString());
                    return;
                case WasmIrOp.MemoryInit:
                    At(k);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldloc, _memory);
                    Long(ins.Imm);
                    Load(ins.A);
                    Load(ins.B);
                    Load(ins.C);
                    FuelAddress();
                    Call(nameof(WasmIrMath.MemoryInit));
                    return;
                case WasmIrOp.RefIsNull:
                    Load(ins.B);
                    Long(0);
                    il.Emit(OpCodes.Ceq);
                    il.Emit(OpCodes.Conv_U8);
                    Store(ins.A);
                    return;
                case WasmIrOp.RefFunc:
                    il.Emit(OpCodes.Ldarg_2);
                    Int(ins.B);
                    Call(nameof(WasmIrMath.RefFunc));
                    Store(ins.A);
                    return;
                case WasmIrOp.I32Load:
                case WasmIrOp.I64Load:
                case WasmIrOp.I32Load8S:
                case WasmIrOp.I32Load8U:
                case WasmIrOp.I32Load16S:
                case WasmIrOp.I32Load16U:
                case WasmIrOp.I64Load8S:
                case WasmIrOp.I64Load8U:
                case WasmIrOp.I64Load16S:
                case WasmIrOp.I64Load16U:
                case WasmIrOp.I64Load32S:
                case WasmIrOp.I64Load32U:
                    At(k);
                    il.Emit(OpCodes.Ldloc, _memory);
                    Load(ins.A);
                    Long(ins.Imm);
                    Call(ins.Op.ToString());
                    Store(ins.A);
                    return;
                case WasmIrOp.I32Store:
                case WasmIrOp.I64Store:
                case WasmIrOp.I32Store8:
                case WasmIrOp.I64Store8:
                case WasmIrOp.I32Store16:
                case WasmIrOp.I64Store16:
                case WasmIrOp.I64Store32:
                    At(k);
                    il.Emit(OpCodes.Ldloc, _memory);
                    Load(ins.A);
                    Long(ins.Imm);
                    Load(ins.B);
                    Call(ins.Op switch
                    {
                        WasmIrOp.I32Store8 or WasmIrOp.I64Store8 => nameof(WasmIrMath.Store8),
                        WasmIrOp.I32Store16 or WasmIrOp.I64Store16 => nameof(WasmIrMath.Store16),
                        _ => ins.Op.ToString(),
                    });
                    return;
                case WasmIrOp.I32AddImm:
                case WasmIrOp.F32MulImm:
                    Load(ins.B);
                    Long(ins.Imm);
                    Call(ins.Op.ToString());
                    Store(ins.A);
                    return;
            }

            // Every other instruction is arithmetic on one or two slots.
            var math = MathOf[ins.Op];
            if (Traps(ins.Op))
                At(k);
            Load(ins.B);
            if (math.GetParameters().Length == 2)
                Load(ins.C);
            il.Emit(OpCodes.Call, math);
            Store(ins.A);
        }

        private static bool Traps(WasmIrOp op) => op is WasmIrOp.I32DivS or WasmIrOp.I32DivU or WasmIrOp.I32RemS or WasmIrOp.I32RemU
            or WasmIrOp.I64DivS or WasmIrOp.I64DivU or WasmIrOp.I64RemS or WasmIrOp.I64RemU
            or WasmIrOp.I32TruncF32S or WasmIrOp.I32TruncF32U or WasmIrOp.I32TruncF64S or WasmIrOp.I32TruncF64U
            or WasmIrOp.I64TruncF32S or WasmIrOp.I64TruncF32U or WasmIrOp.I64TruncF64S or WasmIrOp.I64TruncF64U;
    }
}
