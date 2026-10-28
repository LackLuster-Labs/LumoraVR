// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Lumora.Wasm;

namespace Lumora.Script;

// One compiled graph, linked and running. Knows nothing about worlds: the context it is given decides
// whose authority node calls run under and which capabilities link at all. The module it runs is one
// this process's compiler made and the engine's validator decoded, once per graph however many
// instances run it.
//
// Must be driven from one thread at a time, the thread that owns whatever the nodes touch. -xlinka
public sealed class ScriptInstance
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _payloadProperties = new();

    private readonly WasmStore _store;
    private readonly WasmModule _module;
    private readonly WasmInstance _instance;
    private readonly ScriptInstanceOptions _options;
    private readonly Dictionary<string, (ScriptEventEntry Entry, WasmFunction Function)> _events = new(StringComparer.Ordinal);
    private readonly List<WasmGlobal> _handleRoots = new();
    private readonly Dictionary<string, WasmGlobal[]> _variables = new(StringComparer.Ordinal);
    private readonly WasmFunction[] _timerFunctions;
    private readonly Dictionary<(string NodeId, string Exit), (ScriptAwaitEntry Entry, WasmFunction Function)> _answers = new();
    private readonly Dictionary<string, WasmFunction> _showFunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ScriptFunctionEntry Entry, WasmFunction Function)> _functions = new(StringComparer.Ordinal);
    private readonly WasmGlobal[][] _traces;
    private readonly Dictionary<string, (FlowShapeKind Kind, WasmGlobal Global)> _memory = new(StringComparer.Ordinal);
    private readonly PriorityQueue<(int Timer, int Word), (double Due, long Order)> _timers;
    private readonly WasmFunction? _drivesFunction;
    private readonly WasmGlobal[][] _driveGlobals;
    // The stored values every drive is worked out from, when that is all the drives read, and the lanes those
    // held when the drives last ran clean.
    private readonly WasmGlobal[]? _driveSources;
    private readonly ulong[]? _driveSeen;
    private bool _drivesRan;
    private readonly WasmGlobal?[] _driveOn;

    public CompileResult Compiled { get; }
    public ScriptContextBase Context { get; }
    public int Faults { get; private set; }
    public ScriptCallResult LastResult { get; private set; }

    private ScriptInstance(CompileResult compiled, ScriptContextBase context, ScriptInstanceOptions options,
        WasmStore store, WasmModule module, WasmInstance instance, PriorityQueue<(int Timer, int Word), (double Due, long Order)> timers)
    {
        _timers = timers;
        Compiled = compiled;
        Context = context;
        _options = options;
        _store = store;
        _module = module;
        _instance = instance;

        foreach (var entry in compiled.Events)
        {
            var function = instance.GetFunction(entry.Export)
                ?? throw new ScriptLinkException($"module has no export {entry.Export}");
            _events.Add(entry.NodeId, (entry, function));
        }

        foreach (var variable in compiled.Variables)
        {
            var globals = new WasmGlobal[variable.Exports.Count];
            for (int i = 0; i < globals.Length; i++)
            {
                globals[i] = instance.GetGlobal(variable.Exports[i])
                    ?? throw new ScriptLinkException($"module has no export {variable.Exports[i]}");
            }
            _variables.Add(variable.NodeId, globals);
            if (variable.Type.IsHandle)
                _handleRoots.Add(globals[0]);
        }

        foreach (var held in compiled.HandleRoots)
        {
            _handleRoots.Add(instance.GetGlobal(held)
                ?? throw new ScriptLinkException($"module has no export {held}"));
        }

        foreach (var entry in compiled.Functions)
        {
            _functions[entry.Name] = (entry, instance.GetFunction(entry.Export)
                ?? throw new ScriptLinkException($"module has no export {entry.Export}"));
        }

        foreach (var display in compiled.Displays)
        {
            if (display.Export == null)
                continue;
            _showFunctions.Add(display.NodeId, instance.GetFunction(display.Export)
                ?? throw new ScriptLinkException($"module has no export {display.Export}"));
        }

        _driveGlobals = new WasmGlobal[compiled.Drives.Count][];
        _driveOn = new WasmGlobal?[compiled.Drives.Count];
        for (int i = 0; i < _driveGlobals.Length; i++)
        {
            _driveGlobals[i] = _variables[compiled.Drives[i].NodeId];
            if (compiled.Drives[i].OnExport is { } on)
                _driveOn[i] = instance.GetGlobal(on) ?? throw new ScriptLinkException($"module has no export {on}");
        }
        if (compiled.Drives.Count > 0)
            _drivesFunction = instance.GetFunction(ScriptCompiler.DrivesExport)
                ?? throw new ScriptLinkException($"module has no export {ScriptCompiler.DrivesExport}");
        if (_drivesFunction != null && compiled.DriveSources is { } sources)
        {
            var lanes = new List<WasmGlobal>();
            foreach (var id in sources)
            {
                if (!_variables.TryGetValue(id, out var held))
                {
                    lanes = null;
                    break;
                }
                lanes.AddRange(held);
            }
            if (lanes != null)
            {
                _driveSources = lanes.ToArray();
                _driveSeen = new ulong[_driveSources.Length];
            }
        }

        _traces = new WasmGlobal[compiled.Traces.Count][];
        for (int i = 0; i < _traces.Length; i++)
        {
            var entry = compiled.Traces[i];
            var counters = new WasmGlobal[1 + entry.Also.Count];
            for (int c = 0; c < counters.Length; c++)
            {
                string export = c == 0 ? entry.Export : entry.Also[c - 1];
                counters[c] = instance.GetGlobal(export) ?? throw new ScriptLinkException($"module has no export {export}");
            }
            _traces[i] = counters;
        }

        foreach (var entry in compiled.FlowMemory)
        {
            _memory[entry.NodeId] = (entry.Kind, instance.GetGlobal(entry.Export)
                ?? throw new ScriptLinkException($"module has no export {entry.Export}"));
        }

        _timerFunctions = new WasmFunction[compiled.Timers.Count];
        for (int i = 0; i < _timerFunctions.Length; i++)
        {
            _timerFunctions[i] = instance.GetFunction(compiled.Timers[i].Export)
                ?? throw new ScriptLinkException($"module has no export {compiled.Timers[i].Export}");
        }

        foreach (var entry in compiled.Awaits)
        {
            for (int i = 0; i < entry.Exits.Count; i++)
            {
                _answers[(entry.NodeId, entry.Exits[i])] = (entry, instance.GetFunction(entry.Exports[i])
                    ?? throw new ScriptLinkException($"module has no export {entry.Exports[i]}"));
            }
        }
    }

    public static ScriptInstance Create(CompileResult compiled, ScriptContextBase context, IReadOnlyList<object?> bindings,
        ScriptInstanceOptions? options = null)
    {
        options ??= ScriptInstanceOptions.Default;
        if (!compiled.Success || compiled.Module == null)
            throw new ScriptLinkException("the graph did not compile");
        if (bindings.Count != compiled.Bindings.Count)
            throw new ScriptLinkException($"the graph declares {compiled.Bindings.Count} binding(s), {bindings.Count} given");
        for (int i = 0; i < bindings.Count; i++)
        {
            var value = bindings[i];
            var type = compiled.Bindings[i].Type;
            if (value != null && !type.ClrType.IsInstanceOfType(value))
                throw new ScriptLinkException($"binding '{compiled.Bindings[i].Name}' takes {type.Name}, got {value.GetType().Name}");
        }

        context.PinBindings(bindings);
        context.Awaits = new ScriptAwaits(compiled.Awaits, options.WarnAwaits, context);

        var module = compiled.Decoded ?? WasmModule.Decode(compiled.Module, ScriptCompiler.ModuleLimits);
        var store = new WasmStore();
        var linker = new WasmLinker(store);

        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.StringImport,
            new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }),
            call => MakeString(call, context));
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.UrlImport,
            new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }),
            call => MakeUrl(call, context));
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.BigIntImport,
            new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }),
            call => MakeBigInt(call, context));

        // The queue exists before the instance does, because the timer import is bound at link time.
        var timers = new PriorityQueue<(int Timer, int Word), (double Due, long Order)>();
        long order = 0;
        int timerCount = compiled.Timers.Count;
        if (timerCount > 0)
        {
            linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.TimerImport,
                new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.F32 }, Array.Empty<WasmValueType>()),
                call => ArmTimer(call, context, options, timers, timerCount, ref order));
        }

        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.NowImport,
            new WasmFuncType(Array.Empty<WasmValueType>(), new[] { WasmValueType.F64 }),
            call => call.SetF64(0, context.Now));
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.LiveImport,
            new WasmFuncType(new[] { WasmValueType.I32 }, new[] { WasmValueType.I32 }),
            call => call.SetI32(0, context.GetLive(call.GetI32(0)) != null ? call.GetI32(0) : 0));
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.EachCountImport,
            new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }),
            call => call.SetI32(0, Math.Max(0, context.EachCount((EachSource)call.GetI32(0), context.GetHandle(call.GetI32(1))))));
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.EachItemImport,
            new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }),
            call => call.SetI32(0, context.AddHandle(context.EachItem((EachSource)call.GetI32(0), context.GetHandle(call.GetI32(1)), call.GetI32(2)))));
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.EachQueryImport, ScriptCompiler.EachQueryType,
            call =>
            {
                Span<float> query = stackalloc float[FlowBuilder.MaxQueryLanes];
                for (int i = 0; i < query.Length; i++)
                    query[i] = call.GetF32(i + 1);
                call.SetI32(0, context.AddHandle(context.EachQuery((EachSource)call.GetI32(0), query)));
            });
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.FailClearImport, WasmFuncType.Empty, _ => context.ClearFailure());
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.FailCheckImport,
            new WasmFuncType(Array.Empty<WasmValueType>(), new[] { WasmValueType.I32 }),
            call => call.SetI32(0, context.TakeFailure() ? 1 : 0));

        foreach (var overload in compiled.Imports)
        {
            if (!context.AllowsCapability(overload.Definition.Capability))
                throw new ScriptLinkException($"{overload.ImportName} needs {overload.Definition.Capability}, which this script may not use");
            linker.DefineFunction(ScriptCompiler.NodeModule, overload.ImportName, overload.ImportType!, overload.Invoker.Bind(context),
                NodeOverload.HostFuel);
        }

        foreach (var entry in compiled.ScriptCalls)
        {
            var capability = entry.Overload.Definition.Capability;
            if (!context.AllowsCapability(capability))
                throw new ScriptLinkException($"calling another script needs {capability}, which this script may not use");
            var call = entry;
            linker.DefineFunction(ScriptCompiler.RuntimeModule, call.Import, call.ImportType, host => StartScriptCall(host, context, call));
        }

        // A waiting Function's calls: a word for the next Delay or await, a call begun and its first stretch run
        // out, an argument read after a wait, and per Function its arguments kept and what its Return gives.
        var begun = new List<int>();
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.WordImport,
            new WasmFuncType(new[] { WasmValueType.I32 }, Array.Empty<WasmValueType>()), host => context.SetWord(host.GetI32(0)));
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.CallOpenImport,
            new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }),
            host => host.SetI32(0, OpenCall(host, context, begun)));
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.CallEndImport,
            new WasmFuncType(new[] { WasmValueType.I32 }, Array.Empty<WasmValueType>()), host => context.Awaits?.Settle(host.GetI32(0)));
        linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.ArgumentImport,
            new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I64 }),
            host => ReadArgument(host, context));
        foreach (var function in compiled.Functions)
        {
            if (!function.Async)
                continue;
            var signature = function.Signature;
            linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.FunctionDoneImportPrefix + function.Name,
                new WasmFuncType(new[] { WasmValueType.I32 }.Concat(signature.Outputs.SelectMany(p => p.Type.Lanes)).ToArray(), Array.Empty<WasmValueType>()),
                host => context.Awaits?.Finish(host.GetI32(0), ReadPorts(host, signature.Outputs, context)));
            linker.DefineFunction(ScriptCompiler.RuntimeModule, ScriptCompiler.FunctionKeepImportPrefix + function.Name,
                new WasmFuncType(new[] { WasmValueType.I32 }.Concat(signature.Inputs.SelectMany(p => p.Type.Lanes)).ToArray(), Array.Empty<WasmValueType>()),
                host => context.Awaits?.Keep(host.GetI32(0), ReadPorts(host, signature.Inputs, context)));
        }

        var instance = linker.Instantiate(module, new WasmInstanceLimits
        {
            MaxMemoryPages = options.MaxMemoryPages,
            MaxCallDepth = options.MaxCallDepth,
        });
        context.EndPinning();

        return new ScriptInstance(compiled, context, options, store, module, instance, timers) { _begun = begun };
    }

    // Working memory the instance holds, fixed when it starts: a graph's module never grows its memory.
    public long MemorySize => _instance.MemorySize;

    public IEnumerable<ScriptEventEntry> Events
    {
        get
        {
            foreach (var (entry, _) in _events.Values)
                yield return entry;
        }
    }

    // Invokes one event node with its payload already in lanes.
    public ScriptCallResult Raise(string nodeId, ReadOnlySpan<ulong> payload)
    {
        if (!_events.TryGetValue(nodeId, out var target))
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, nodeId, "no such event node", 0);
        return LastResult = Invoke(target.Function, payload);
    }

    // Invokes one event node with a payload struct, turned into lanes in the payload's declared order.
    public ScriptCallResult Raise<T>(string nodeId, in T payload) where T : struct
    {
        if (!_events.TryGetValue(nodeId, out var target))
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, nodeId, "no such event node", 0);
        if (target.Entry.PayloadType != typeof(T))
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, nodeId, $"{nodeId} takes {target.Entry.PayloadType.Name}", 0);
        return LastResult = Invoke(target.Function, Encode(target.Entry, payload));
    }

    // Every event node of one payload type, in graph order, found once per type: the frame's update event is
    // raised on every script that has one, every frame.
    private readonly Dictionary<Type, (ScriptEventEntry Entry, WasmFunction Function)[]> _eventsByType = new();

    private (ScriptEventEntry Entry, WasmFunction Function)[] EventsOf(Type payloadType)
    {
        if (_eventsByType.TryGetValue(payloadType, out var found))
            return found;
        var list = new List<(ScriptEventEntry, WasmFunction)>();
        foreach (var pair in _events.Values)
        {
            if (pair.Entry.PayloadType == payloadType)
                list.Add(pair);
        }
        found = list.ToArray();
        _eventsByType[payloadType] = found;
        return found;
    }

    // Every event node of this payload type in graph order, stopping at the first that does not run clean, the
    // way raising them one by one does. Each node takes the payload turned into lanes afresh: a call lets go of
    // the handles it did not keep, so a user or slot in the payload has to be handed in again for the next.
    // The result is the last node's, with the fuel all of them used.
    public ScriptCallResult RaiseEach<T>(in T payload, ScriptCallResult none) where T : struct
    {
        var last = none;
        long fuel = 0;
        foreach (var (entry, function) in EventsOf(typeof(T)))
        {
            last = LastResult = Invoke(function, Encode(entry, payload));
            fuel += last.FuelUsed;
            if (!last.Ok)
                break;
        }
        return fuel == last.FuelUsed ? last : last with { FuelUsed = fuel };
    }

    // Every event node of this payload type, in graph order. Returns how many ran without a fault.
    public int RaiseAll<T>(in T payload) where T : struct
    {
        int ok = 0;
        foreach (var (entry, function) in _events.Values)
        {
            if (entry.PayloadType != typeof(T))
                continue;
            LastResult = Invoke(function, Encode(entry, payload));
            if (LastResult.Ok)
                ok++;
        }
        return ok;
    }

    public int PendingTimers => _timers.Count;

    // Moves every timer due by now into the list, earliest first, each with the call of a waiting Function it
    // was armed in. Anything they arm when they run waits for the next take, so a Delay of zero is one frame,
    // never a loop inside one.
    public void TakeDueTimers(double now, List<(int Timer, int Word)> due)
    {
        while (_timers.TryPeek(out _, out var when) && when.Due <= now)
            due.Add(_timers.Dequeue());
    }

    // Runs what follows the Delay a timer belongs to, on in the call it was armed in. One armed in a call that
    // is gone runs nothing.
    public ScriptCallResult RunTimer(int timer, int word = 0)
    {
        if ((uint)timer >= (uint)_timerFunctions.Length)
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, null, $"no timer {timer}", 0);
        if (Context.Awaits is { } awaits && !awaits.Resume(word))
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, Compiled.Timers[timer].NodeId, "its call is over", 0);
        var lanes = Compiled.Timers[timer].CarriesWord ? new[] { LaneCodec.FromInt(word) } : Array.Empty<ulong>();
        return LastResult = RunOn(word, Invoke(_timerFunctions[timer], lanes, word));
    }

    // After a flow of a waiting Function's call: the call ends when that was its last, or when it trapped.
    private ScriptCallResult RunOn(int word, ScriptCallResult result)
    {
        if (word != 0 && Context.Awaits is { } awaits)
        {
            if (result.Status is ScriptCallStatus.Trap or ScriptCallStatus.OutOfFuel or ScriptCallStatus.OutOfTime)
                awaits.Fault(word, result.Message ?? result.Status.ToString());
            else
                awaits.Settle(word);
        }
        return result;
    }

    // Answers to this instance's awaits that came back since the last take, in the order they came.
    public void TakeAnswers(List<ScriptAnswer> into) => Context.Awaits?.Take(into);

    // The oldest answer that came back, taken only when it is about to run, so one still waiting at a live
    // graph swap goes to the new instance with the rest.
    public bool TryTakeAnswer(out ScriptAnswer answer)
    {
        answer = default;
        return Context.Awaits?.TryTake(out answer) == true;
    }

    public int WaitingAnswers => Context.Awaits?.Waiting ?? 0;

    // Work the awaits have out and answers not yet taken.
    public int PendingAwaits => Context.Awaits is { } awaits ? awaits.Out + awaits.Waiting : 0;

    // Runs the exit an answer names, with its payload on the await's outputs. An answer for an await the
    // graph no longer has, or an exit it lacks, runs nothing.
    public ScriptCallResult RunAnswer(in ScriptAnswer answer)
    {
        if (!_answers.TryGetValue((answer.NodeId, answer.Exit), out var target))
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, answer.NodeId, $"no answer {answer.Exit} on {answer.NodeId}", 0);
        if (answer.Payload.GetType() != target.Entry.PayloadType)
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, answer.NodeId, $"{answer.NodeId} answers with {target.Entry.PayloadType.Name}", 0);
        // A graph swapped in since the call was made may give the node other ports; that answer is not its.
        if (answer.Payload is CallAnswer values && target.Entry.Overload.FunctionSignature?.Canonical != values.Signature)
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, answer.NodeId, $"{answer.NodeId} was called with other ports", 0);
        if (Context.Awaits is { } awaits && !awaits.Resume(answer.Word))
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, answer.NodeId, "its call is over", 0);
        var lanes = answer.Payload is CallAnswer called
            ? EncodeValues(target.Entry.Overload.Outputs, called.Values, target.Entry.CarriesWord ? 1 : 0)
            : Encode(target.Entry.Overload, target.Entry.PayloadType, answer.Payload, target.Entry.CarriesWord ? 1 : 0);
        if (target.Entry.CarriesWord)
            lanes[^1] = LaneCodec.FromInt(answer.Word);
        return LastResult = RunOn(answer.Word, Invoke(target.Function, lanes, answer.Word));
    }

    // Runs one of this graph's Functions for a call from another script, answering its ticket: at once with what
    // it gave back, or, for a Function that waits, when its call returns. Described is how failures name it.
    public ScriptCallResult CallFunction(string name, IReadOnlyList<object?> arguments, CallTicket answer, string described)
    {
        if (!_functions.TryGetValue(name, out var target))
        {
            answer.Fail($"no function {name}");
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, null, $"no function {name}", 0);
        }
        if (!target.Entry.Async)
        {
            var result = CallFunction(name, arguments, out var outputs);
            if (result.Ok)
                answer.Return(outputs);
            else
                answer.Fail($"{described} failed: {result.Message ?? result.Status.ToString()}");
            return result;
        }
        var signature = target.Entry.Signature;
        if (arguments.Count != signature.Inputs.Count || Context.Awaits is not { } awaits)
        {
            answer.Fail($"{described} could not be called");
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, target.Entry.NodeId, $"{name} could not be called", 0);
        }
        int word = awaits.Begin(answer, signature, described);
        var lanes = EncodeValues(signature.Inputs, arguments, extra: 2);
        lanes[^1] = LaneCodec.FromInt(word);
        return LastResult = RunOn(word, Invoke(target.Function, lanes, word));
    }

    // Runs one of this graph's Functions for another script, as this script: the arguments in the order its
    // ports are declared, in, and what it gave back, out. Objects cross as themselves, each given a handle in
    // the table of the script it reaches, so a list or a dictionary passed either way is then shared by
    // reference between the two scripts. What it gave back is read before this call's handles are let go.
    public ScriptCallResult CallFunction(string name, IReadOnlyList<object?> arguments, out object?[] outputs)
    {
        outputs = Array.Empty<object?>();
        if (!_functions.TryGetValue(name, out var target))
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, null, $"no function {name}", 0);
        if (target.Entry.Async)
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, target.Entry.NodeId, $"{name} waits, so it answers a ticket later", 0);
        var signature = target.Entry.Signature;
        if (arguments.Count != signature.Inputs.Count)
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, target.Entry.NodeId,
                $"{name} takes {signature.Inputs.Count} argument(s), {arguments.Count} given", 0);
        var lanes = EncodeValues(signature.Inputs, arguments, extra: 1);
        var results = new ulong[LaneCount(signature.Outputs)];
        long fuel = _options.FuelPerCall;
        Context.ResetFailures();
        try
        {
            long deadline = Environment.TickCount64 + _options.MaxCallMilliseconds;
            long left = _store.Invoke(target.Function, lanes, results, fuel, _options.MaxCallDepth, deadline);
            outputs = DecodeValues(signature.Outputs, results);
            return LastResult = new ScriptCallResult(ScriptCallStatus.Ok, null, null, fuel - left);
        }
        catch (WasmTrapException trap)
        {
            return LastResult = Faulted(trap, fuel);
        }
        finally
        {
            CollectHandles();
        }
    }

    // The script is stopping for good: whatever the awaits have out is cancelled and what came back dropped.
    public void StopAwaits() => Context.Awaits?.Stop();

    // Works out one Display that needs no flow, into its globals.
    public ScriptCallResult RunDisplay(string nodeId)
    {
        if (!_showFunctions.TryGetValue(nodeId, out var function))
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, nodeId, $"no display {nodeId}", 0);
        return LastResult = Invoke(function, ReadOnlySpan<ulong>.Empty);
    }

    public bool HasDrives => _drivesFunction != null;

    // Works out what every drive holds its field at, into its globals.
    public ScriptCallResult RunDrives()
    {
        if (_drivesFunction == null)
            return LastResult = new ScriptCallResult(ScriptCallStatus.NoSuchEvent, null, "no drives", 0);
        if (_driveSources != null)
        {
            for (int i = 0; i < _driveSources.Length; i++)
                _driveSeen![i] = _driveSources[i].RawValue;
        }
        bool record = _driveSources != null && Compiled.DrivesRead;
        if (record)
            Context.BeginRecording();
        ScriptCallResult result;
        bool settled;
        try
        {
            result = LastResult = Invoke(_drivesFunction, ReadOnlySpan<ulong>.Empty);
        }
        finally
        {
            settled = !record || Context.EndRecording();
        }
        _drivesRan = result.Ok && settled;
        return result;
    }

    // Whether running the drives again would only work out what they hold already: they read nothing but
    // stored values, pure nodes and reads that said what they hang on, ran clean last time, every one of those
    // values holds the lanes it held then, and nothing those reads noted has moved since.
    public bool DrivesCurrent
    {
        get
        {
            if (_driveSources == null || !_drivesRan)
                return false;
            for (int i = 0; i < _driveSources.Length; i++)
            {
                if (_driveSources[i].RawValue != _driveSeen![i])
                    return false;
            }
            return !Compiled.DrivesRead || Context.ReadsCurrent();
        }
    }

    // The lanes Compiled.Drives[index] holds since the drives function last ran, and what they make.
    // Whether a drive holds its field now; one with no on input always does.
    public bool DriveOn(int index) => _driveOn[index] is not { } on || LaneCodec.ToInt(on.RawValue) != 0;

    public void ReadDriveLanes(int index, Span<ulong> into)
    {
        var globals = _driveGlobals[index];
        for (int i = 0; i < globals.Length && i < into.Length; i++)
            into[i] = globals[i].RawValue;
    }

    public object? DecodeDrive(int index, ReadOnlySpan<ulong> lanes) => LaneCodec.Read(Compiled.Drives[index].ValueType, lanes, Context);

    // How many times flow has left by the wired exit Compiled.Traces[index] names, since this instance began.
    public int ReadTrace(int index)
    {
        if ((uint)index >= (uint)_traces.Length)
            return 0;
        int count = 0;
        foreach (var counter in _traces[index])
            count += LaneCodec.ToInt(counter.RawValue);
        return count;
    }

    // A stored variable's current value, read straight from its globals.
    public object? ReadVariable(string nodeId)
    {
        if (!_variables.TryGetValue(nodeId, out var globals))
            return null;
        var type = Compiled.Variables.First(v => v.NodeId == nodeId).Type;
        var lanes = new ulong[globals.Length];
        for (int i = 0; i < lanes.Length; i++)
            lanes[i] = globals[i].RawValue;
        return LaneCodec.Read(type, lanes, Context);
    }

    public bool WriteVariable(string nodeId, object? value)
    {
        if (!_variables.TryGetValue(nodeId, out var globals))
            return false;
        var type = Compiled.Variables.First(v => v.NodeId == nodeId).Type;
        var lanes = new ulong[globals.Length];
        LaneCodec.Write(type, value, lanes, Context);
        for (int i = 0; i < lanes.Length; i++)
            globals[i].SetRawValue(lanes[i]);
        return true;
    }

    // A live graph swap: every stored variable whose name and type both still match takes the old
    // instance's value. Handles are carried as the objects behind them, since each instance numbers
    // its own. Answers how many carried over.
    public int AdoptStoredValues(ScriptInstance previous)
    {
        int adopted = 0;
        foreach (var variable in Compiled.Variables)
        {
            ScriptVariableEntry? match = null;
            foreach (var old in previous.Compiled.Variables)
            {
                if (old.Name == variable.Name && old.Type == variable.Type)
                {
                    match = old;
                    break;
                }
            }
            if (match != null && WriteVariable(variable.NodeId, previous.ReadVariable(match.NodeId)))
                adopted++;
        }
        return adopted;
    }

    // A live graph swap, past stored values: what each Once, Toggle and Cooldown remembers goes to the node
    // of the same id and kind, every Delay still waiting whose node is still there waits on, due when it
    // was, and the awaits' work out answers here instead. Answers how many carried over.
    public int AdoptFlowState(ScriptInstance previous)
    {
        int adopted = 0;
        if (Context.Awaits is { } awaits && previous.Context.Awaits is { } before)
        {
            adopted += before.Out + before.Waiting;
            awaits.Absorb(before);
        }
        foreach (var (nodeId, (kind, global)) in _memory)
        {
            if (previous._memory.TryGetValue(nodeId, out var old) && old.Kind == kind)
            {
                global.SetRawValue(old.Global.RawValue);
                adopted++;
            }
        }

        if (previous._timers.Count == 0 || Compiled.Timers.Count == 0)
            return adopted;
        var byNode = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Compiled.Timers.Count; i++)
            byNode[Compiled.Timers[i].NodeId] = i;
        var waiting = new List<(int Timer, int Word, double Due, long Order)>();
        foreach (var ((timer, word), when) in previous._timers.UnorderedItems)
        {
            if ((uint)timer < (uint)previous.Compiled.Timers.Count && byNode.TryGetValue(previous.Compiled.Timers[timer].NodeId, out int mine))
                waiting.Add((mine, word, when.Due, when.Order));
        }
        waiting.Sort((a, b) => a.Due != b.Due ? a.Due.CompareTo(b.Due) : a.Order.CompareTo(b.Order));
        long order = long.MinValue;
        foreach (var (timer, word, due, _) in waiting)
        {
            _timers.Enqueue((timer, word), (due, order++));
            adopted++;
        }
        return adopted;
    }

    // Whether any event node takes this payload.
    public bool Handles(Type payloadType)
    {
        foreach (var (entry, _) in _events.Values)
        {
            if (entry.PayloadType == payloadType)
                return true;
        }
        return false;
    }

    // Word is the waiting Function's call the function runs in, which a trap ends before anything else it began.
    private ScriptCallResult Invoke(WasmFunction function, ReadOnlySpan<ulong> payload, int word = 0)
    {
        long fuel = _options.FuelPerCall;
        _begun.Clear();
        Context.TakeWord();
        Context.ResetFailures();
        try
        {
            long deadline = Environment.TickCount64 + _options.MaxCallMilliseconds;
            long left = _store.Invoke(function, payload, Span<ulong>.Empty, fuel, _options.MaxCallDepth, deadline);
            return new ScriptCallResult(ScriptCallStatus.Ok, null, null, fuel - left);
        }
        catch (WasmTrapException trap)
        {
            var result = Faulted(trap, fuel);
            string reason = result.Message ?? result.Status.ToString();
            Context.Awaits?.Fault(word, reason);
            // A waiting Function's call begun in a call that trapped, with nothing under way, never returns.
            foreach (int begun in _begun)
                Context.Awaits?.Fault(begun, reason, onlyIfIdle: true);
            return result;
        }
        finally
        {
            _begun.Clear();
            CollectHandles();
        }
    }

    // The words of the waiting Functions' calls begun in the call running now.
    private List<int> _begun = new();

    // A Call Function of a waiting Function: its await's ticket, in the call it is made from, and a call begun
    // for the Function that answers it.
    private static int OpenCall(WasmHostCall call, ScriptContextBase context, List<int> begun)
    {
        var awaits = context.Awaits ?? throw new ScriptLimitException("this script cannot wait on anything");
        var ticket = (CallTicket)awaits.Open(call.GetI32(0), call.GetI32(1));
        int word = awaits.Begin(ticket, ticket.Signature, ticket.NodeId);
        begun.Add(word);
        return word;
    }

    // An argument of a waiting Function's call, read after a wait: the lane asked for, in this call's own table.
    private static void ReadArgument(WasmHostCall call, ScriptContextBase context)
    {
        ScriptType? type = null;
        var value = context.Awaits?.Argument(call.GetI32(0), call.GetI32(1), out type);
        ulong raw = 0;
        if (type != null)
        {
            Span<ulong> lanes = stackalloc ulong[16];
            int lane = call.GetI32(2);
            LaneCodec.Write(type, value, lanes[..type.LaneCount], context);
            raw = (uint)lane < (uint)type.LaneCount ? lanes[lane] : 0;
        }
        call.SetRaw(0, raw);
    }

    // Ports' values from an import's lanes after its word, in this instance's table.
    private static object?[] ReadPorts(WasmHostCall call, IReadOnlyList<NodePort> ports, ScriptContextBase context)
    {
        var values = new object?[ports.Count];
        Span<ulong> raw = stackalloc ulong[16];
        int lane = 1;
        for (int i = 0; i < ports.Count; i++)
        {
            var type = ports[i].Type;
            for (int l = 0; l < type.LaneCount; l++)
                raw[l] = call.GetRaw(lane + l);
            values[i] = LaneCodec.Read(type, raw[..type.LaneCount], context);
            lane += type.LaneCount;
        }
        return values;
    }

    // A trap as a call's result, pinned to the node the source map puts it in.
    private ScriptCallResult Faulted(WasmTrapException trap, long fuel)
    {
        Faults++;
        string? node = null;
        if (trap.ByteOffset >= 0 && WasmSourceMap.TryFindTag(_module, trap.ByteOffset, out int tag)
            && tag >= 0 && tag < Compiled.TagNodes.Count)
            node = Compiled.TagNodes[tag];
        long used = fuel - trap.FuelLeft;
        if (trap.Code == WasmTrapCode.OutOfTime)
            return new ScriptCallResult(ScriptCallStatus.OutOfTime, node, $"stopped after {_options.MaxCallMilliseconds / 1000.0:0.#} s in one call", used);
        var status = trap.Code == WasmTrapCode.OutOfFuel ? ScriptCallStatus.OutOfFuel : ScriptCallStatus.Trap;
        return new ScriptCallResult(status, node, trap.Message, used);
    }

    private static int LaneCount(IReadOnlyList<NodePort> ports)
    {
        int lanes = 0;
        foreach (var port in ports)
            lanes += port.Type.LaneCount;
        return lanes;
    }

    // Values for ports, in order, as lanes in this instance, with room for extra lanes after them that stay 0.
    private ulong[] EncodeValues(IReadOnlyList<NodePort> ports, IReadOnlyList<object?> values, int extra = 0)
    {
        var lanes = new ulong[LaneCount(ports) + extra];
        int lane = 0;
        for (int i = 0; i < ports.Count; i++)
        {
            var type = ports[i].Type;
            LaneCodec.Write(type, i < values.Count ? values[i] : null, lanes.AsSpan(lane, type.LaneCount), Context);
            lane += type.LaneCount;
        }
        return lanes;
    }

    private object?[] DecodeValues(IReadOnlyList<NodePort> ports, ReadOnlySpan<ulong> lanes)
    {
        var values = new object?[ports.Count];
        int lane = 0;
        for (int i = 0; i < ports.Count; i++)
        {
            var type = ports[i].Type;
            values[i] = LaneCodec.Read(type, lanes.Slice(lane, type.LaneCount), Context);
            lane += type.LaneCount;
        }
        return values;
    }

    // A Call Script Function reaching its import: its ticket, the target and the arguments read in this script's
    // own handle table while the call that made them is still running, then handed to the host to place.
    private static void StartScriptCall(WasmHostCall call, ScriptContextBase context, ScriptCallEntry entry)
    {
        var awaits = context.Awaits ?? throw new ScriptLimitException("this script cannot wait on anything");
        var ticket = (CallTicket)awaits.Open(call.GetI32(0), call.GetI32(1));
        var inputs = entry.Overload.Inputs;
        var arguments = new object?[inputs.Count - 1];
        object? target = null;
        Span<ulong> raw = stackalloc ulong[16];
        int lane = 2;
        for (int i = 0; i < inputs.Count; i++)
        {
            var type = inputs[i].Type;
            for (int l = 0; l < type.LaneCount; l++)
                raw[l] = call.GetRaw(lane + l);
            var value = LaneCodec.Read(type, raw[..type.LaneCount], context);
            lane += type.LaneCount;
            if (i == 0)
                target = value;
            else
                arguments[i - 1] = value;
        }
        context.CallScriptFunction(ticket, target, entry.Function, entry.Signature, arguments);
    }

    private void CollectHandles()
    {
        if (!Context.HasLiveHandles)
            return;
        Span<int> roots = _handleRoots.Count <= 64 ? stackalloc int[_handleRoots.Count] : new int[_handleRoots.Count];
        for (int i = 0; i < _handleRoots.Count; i++)
            roots[i] = LaneCodec.ToInt(_handleRoots[i].RawValue);
        Context.Collect(roots);
    }

    // A payload's lanes in a buffer the instance keeps: the executor copies them in before anything runs.
    private ulong[] _payload = new ulong[8];

    private ReadOnlySpan<ulong> Encode<T>(ScriptEventEntry entry, in T payload) where T : struct
    {
        if (PayloadLanes<T>.Write is not { } write)
            return Encode(entry.Overload, typeof(T), payload);
        int count = PayloadLanes<T>.Count;
        if (_payload.Length < count)
            _payload = new ulong[count];
        write(payload, _payload, Context);
        return _payload.AsSpan(0, count);
    }

    private ulong[] Encode(NodeOverload overload, Type payloadType, object boxed, int extra = 0)
    {
        var properties = _payloadProperties.GetOrAdd(payloadType, NodeRegistry.PayloadProperties);
        var outputs = overload.Outputs;
        int laneCount = 0;
        foreach (var port in outputs)
            laneCount += port.Type.LaneCount;

        var lanes = new ulong[laneCount + extra];
        int lane = 0;
        for (int i = 0; i < outputs.Count; i++)
        {
            var type = outputs[i].Type;
            LaneCodec.Write(type, properties[i].GetValue(boxed), lanes.AsSpan(lane, type.LaneCount), Context);
            lane += type.LaneCount;
        }
        return lanes;
    }

    private static void ArmTimer(WasmHostCall call, ScriptContextBase context, ScriptInstanceOptions options,
        PriorityQueue<(int Timer, int Word), (double Due, long Order)> timers, int timerCount, ref long order)
    {
        int timer = call.GetI32(0);
        float seconds = call.GetF32(1);
        if ((uint)timer >= (uint)timerCount)
            throw new ScriptLimitException($"no timer {timer}");
        if (timers.Count == options.WarnDelays)
            context.Warn(ScriptWarning.Delays, $"more than {options.WarnDelays:N0} delays waiting");
        if (!(seconds > 0f))
            seconds = 0f;
        // Armed in a waiting Function's call, it is one more wait under way there.
        int word = context.TakeWord();
        context.Awaits?.Wait(word);
        timers.Enqueue((timer, word), (context.Now + seconds, order++));
    }

    private static void MakeString(WasmHostCall call, ScriptContextBase context)
        => call.SetI32(0, context.AddHandle(ReadText(call)));

    // The compiler only writes whole addresses; read back by the same rule, anything else is nothing.
    private static void MakeUrl(WasmHostCall call, ScriptContextBase context)
        => call.SetI32(0, TextToUrl.Run(ReadText(call), out _) is { } url ? context.AddHandle(url) : 0);

    private static void MakeBigInt(WasmHostCall call, ScriptContextBase context)
        => call.SetI32(0, ScriptBigInt.TryParse(ReadText(call), out var number) ? context.AddHandle(number) : 0);

    private static string ReadText(WasmHostCall call)
    {
        int pointer = call.GetI32(0);
        int length = call.GetI32(1);
        var memory = call.Memory;
        if (pointer < 0 || length < 0 || (long)pointer + length > memory.Length)
            throw new ScriptLimitException("string out of bounds");
        return Encoding.UTF8.GetString(memory.Slice(pointer, length));
    }
}
