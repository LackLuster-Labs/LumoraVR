// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lumora.Wasm;

namespace Lumora.Script;

// graph.json to module.wasm. Every node resolves against the registry, every wire is type checked,
// flow cycles must pass through a loop node, and the module comes out with one export per event, a
// source map from code offset to node, and a manifest. The result is decoded by the engine's own
// validator before it is handed back: the compiler is trusted no more than any other module source.
//
// MODULE LAYOUT
//   import lumora:rt.str_utf8 (ptr, len) -> handle   makes the constant strings at start
//   import lumora:rt.url_utf8 (ptr, len) -> handle   makes the constant addresses at start, when there are any
//   import lumora:rt.bigint_utf8 (ptr, len) -> handle   makes the constant big integers at start, when there are any
//   import lumora.<overload>                         one per node overload that calls into C#
//   import lumora:rt.delay (timer, seconds)          asks for a timer, when the graph has a Delay
//   an await node's Start is a node import that takes the await's number ahead of its inputs
//   import lumora:rt.now () -> seconds               the world's clock, when the graph has a Cooldown
//   import lumora:rt.each_count/each_item            what a For Each walks, when the graph has one
//   import lumora:rt.each_query (source, floats) -> handle   what a searching For Each found
//   import lumora:rt.call_script.<node id> (await, word, target, inputs)   starts a Call Script Function's call
//   import lumora:rt.word (word)                     the waiting Function's call the next Delay or await is armed in
//   import lumora:rt.call_open (await, word) -> word, call_end (word)   an async call's ticket, its first stretch done
//   import lumora:rt.fn_arg (word, port, lane) -> i64   a waiting Function's argument read after a wait
//   import lumora:rt.fn_done.<name> (word, outputs), fn_keep.<name> (word, inputs)   its Return, its arguments kept
//   memory: [0, StackBytes) continuation stack, shared by a call and the Functions it calls, each one's
//           part above its caller's, then the constant string and address bytes
//   globals: one handle per constant string and address, then the lanes of every stored variable, Display, Pulse
//            and drive, then what each Watch, Follow and Frame Delta remembers
//   start: creates the constant strings and addresses
//   functions: one per Function node, taking its arguments' lanes and where its stack starts, giving its
//            outputs' lanes, exported as fn.<name> for whatever calls it from outside; one that waits takes
//            the word of its call last and gives nothing back
//   exports: on.<node id> per event, after.<node id> per Delay, await.<node id>.<exit> per exit an await's
//            answer can run, taking the payload lanes, var.<node id>.<lane> per lane of a
//            stored variable, Display or Pulse, show.<node id> per Display that needs no flow, each on
//            its own so one that fails leaves the rest showing, trace.<node id>.<exit> per wired flow
//            exit, counting the flow that leaves by it, mem.<node id> per Once, Toggle and Cooldown,
//            what it remembers, for a live swap to carry over, and drives, which works out every drive's
//            value into its globals, run every frame on every machine running the script -xlinka
public sealed class ScriptCompiler
{
    // What a script's module is read and checked against. The engine's own caps are for modules from
    // somewhere else; a script's module is only ever one this compiler just wrote, as big as its graph.
    public static readonly WasmLimits ModuleLimits = new()
    {
        MaxModuleBytes = int.MaxValue,
        MaxTypes = int.MaxValue,
        MaxFunctions = int.MaxValue,
        MaxImports = int.MaxValue,
        MaxExports = int.MaxValue,
        MaxGlobals = int.MaxValue,
        MaxElementSegments = int.MaxValue,
        MaxElementItems = int.MaxValue,
        MaxDataSegments = int.MaxValue,
        MaxTypeArity = int.MaxValue,
        MaxLocalsPerFunction = int.MaxValue,
        MaxFunctionBodyBytes = int.MaxValue,
        MaxBlockDepth = int.MaxValue,
        MaxBranchTableEntries = int.MaxValue,
        MaxOperandStack = int.MaxValue,
    };

    public const string NodeModule = "lumora";
    public const string RuntimeModule = "lumora:rt";
    public const string StringImport = "str_utf8";
    public const string UrlImport = "url_utf8";
    public const string BigIntImport = "bigint_utf8";
    public const string TimerImport = "delay";
    public const string NowImport = "now";
    public const string LiveImport = "live";
    public const string EachCountImport = "each_count";
    public const string EachItemImport = "each_item";
    public const string EachQueryImport = "each_query";
    public const float MinIntervalSeconds = 0.05f;
    public const string ManifestSection = "lumora.manifest";
    public const string EventExportPrefix = "on.";
    public const string VariableExportPrefix = "var.";
    public const string TimerExportPrefix = "after.";
    public const string AwaitExportPrefix = "await.";
    public const string DisplayExportPrefix = "show.";
    public const string TraceExportPrefix = "trace.";
    public const string MemoryExportPrefix = "mem.";
    public const string HandleRootPrefix = "held.";
    public const string FunctionExportPrefix = "fn.";
    public const string ScriptCallImportPrefix = "call_script.";
    public const string WordImport = "word";
    public const string CallOpenImport = "call_open";
    public const string CallEndImport = "call_end";
    public const string ArgumentImport = "fn_arg";
    public const string FunctionDoneImportPrefix = "fn_done.";
    public const string FunctionKeepImportPrefix = "fn_keep.";
    public const string FailClearImport = "fail_clear";
    public const string FailCheckImport = "fail_check";
    public const string DrivesExport = "drives";

    // The continuation stack. A Function's calls stack theirs above their caller's, so it is deep enough for
    // calls nested as deep as a script may go, and still leaves half of the first 64 KiB page for the
    // constant texts.
    internal const int StackBytes = 32768;

    // A search's source, then every float it can carry, unused ones zero.
    internal static readonly WasmFuncType EachQueryType = new(
        new[] { WasmValueType.I32 }.Concat(Enumerable.Repeat(WasmValueType.F32, FlowBuilder.MaxQueryLanes)).ToArray(),
        new[] { WasmValueType.I32 });

    private readonly ScriptGraph _graph;
    private readonly NodeRegistry _registry;
    private readonly List<CompileDiagnostic> _diagnostics = new();
    private readonly List<ResolvedNode> _nodes = new();
    private readonly List<string> _handleRoots = new();
    private readonly Dictionary<string, ResolvedNode> _byId = new(StringComparer.Ordinal);
    private readonly List<ScriptBindingEntry> _bindings = new();

    // Each exit that ran into a flow relay, and the relays it passed before reaching what runs.
    private readonly List<(ResolvedNode Node, int Exit, List<ResolvedNode> Relays)> _relayed = new();

    private ScriptCompiler(ScriptGraph graph, NodeRegistry registry)
    {
        _graph = graph;
        _registry = registry;
    }

    // Never throws: a compiler fault comes back as a failed compile that says so. Safe to run on any
    // thread, several at once: it reads the graph and the registry and writes only its own state.
    public static CompileResult Compile(ScriptGraph graph, NodeRegistry registry)
    {
        var compiler = new ScriptCompiler(graph, registry);
        try
        {
            return compiler.Run();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            compiler.Error(null, "internal: the compiler failed: " + ex.Message);
            string hash;
            try
            {
                hash = graph.ComputeHash();
            }
            catch (Exception)
            {
                hash = string.Empty;
            }
            return CompileResult.Failed(hash, compiler._diagnostics);
        }
    }

    private CompileResult Run()
    {
        var hash = _graph.ComputeHash();

        ResolveBindings();
        ResolveNodes();
        if (HasErrors)
            return CompileResult.Failed(hash, _diagnostics);

        ResolveWires();
        ResolveConstants();
        ResolveCalls();
        ResolveSteps();
        SpliceRelays();
        if (HasErrors)
            return CompileResult.Failed(hash, _diagnostics);

        MarkAsync();

        CheckFlowCycles();
        CheckDataCycles();
        if (HasErrors)
            return CompileResult.Failed(hash, _diagnostics);

        FoldConstants();

        CheckFunctions();
        if (HasErrors)
            return CompileResult.Failed(hash, _diagnostics);

        return Emit(hash);
    }

    private bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    // A node shared by several flows is lowered once per flow, so the same complaint can come up twice.
    private void Error(string? node, string message)
    {
        var diagnostic = new CompileDiagnostic(DiagnosticSeverity.Error, node, message);
        if (!_diagnostics.Contains(diagnostic))
            _diagnostics.Add(diagnostic);
    }

    // A node this build lacks or has only older: the graph is fine, this machine is behind.
    private void Behind(string node, string message)
        => _diagnostics.Add(new CompileDiagnostic(DiagnosticSeverity.Error, node, message) { NeedsNewerBuild = true });

    private void Warn(string? node, string message) => _diagnostics.Add(new CompileDiagnostic(DiagnosticSeverity.Warning, node, message));

    private void ResolveBindings()
    {
        foreach (var binding in _graph.Bindings)
        {
            if (!ScriptTypes.TryGet(binding.Type, out var type))
            {
                Error(null, $"binding '{binding.Name}' has unknown type '{binding.Type}'");
                continue;
            }
            if (!type.IsHandle || type == ScriptTypes.String)
            {
                Error(null, $"binding '{binding.Name}' is a {type.Name}; a binding holds a world object");
                continue;
            }
            _bindings.Add(new ScriptBindingEntry(binding.Name, type));
        }
    }

    private void ResolveNodes()
    {
        for (int i = 0; i < _graph.Nodes.Count; i++)
        {
            var node = _graph.Nodes[i];
            if (!_registry.TryGetDefinition(node.Type, out var definition))
            {
                Behind(node.Id, $"unknown node type '{node.Type}'");
                continue;
            }
            if (node.Version > definition.Version)
            {
                Behind(node.Id, $"was saved by a newer {definition.Name} (v{node.Version}, this build has v{definition.Version})");
                continue;
            }
            if (node.Version < definition.Version)
            {
                Error(node.Id, $"is {definition.Name} v{node.Version} and needs migrating to v{definition.Version}");
                continue;
            }
            if (!_registry.TryResolve(definition, node.Signature, out var overload, out var error))
            {
                Error(node.Id, error);
                continue;
            }

            var resolved = new ResolvedNode(node, i, overload);
            if (overload.Shape?.Kind == FlowShapeKind.ListEach && !ResolveListHelpers(resolved))
                continue;
            if (overload.Shape?.Kind == FlowShapeKind.Scope && !ResolveScopeHelpers(resolved))
                continue;
            if (overload.Shape?.Kind is FlowShapeKind.Change or FlowShapeKind.FrameDelta && !ResolveChangeHelper(resolved))
                continue;
            if (overload.Shape?.Kind == FlowShapeKind.Follow && !ResolveFollowHelper(resolved))
                continue;
            _nodes.Add(resolved);
            _byId.Add(node.Id, resolved);
        }
    }

    // A list For Each counts and reads its list through the Count and Get At nodes for its item type.
    private bool ResolveListHelpers(ResolvedNode node)
    {
        var item = node.Overload.TypeArguments.Count == 1 ? node.Overload.TypeArguments[0].Name : null;
        var helpers = new NodeOverload[2];
        string[] names = { "ListCount", "ListGetAt" };
        for (int i = 0; i < names.Length; i++)
        {
            if (item == null || !_registry.TryGetDefinition(names[i], out var definition)
                || !_registry.TryResolve(definition, item, out helpers[i], out _) || !helpers[i].IsHostCall)
            {
                Error(node.Id, $"cannot walk a list of {item ?? "nothing"} on this build");
                return false;
            }
        }
        node.Helpers = helpers;
        return true;
    }

    // A scope opens and closes through two action nodes: enter takes the scope's own inputs in order, leave
    // takes none, and neither gives anything back.
    private bool ResolveScopeHelpers(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        string[] names = { shape.Enter ?? string.Empty, shape.Leave ?? string.Empty };
        var helpers = new NodeOverload[2];
        for (int i = 0; i < names.Length; i++)
        {
            if (!_registry.TryGetDefinition(names[i], out var definition) || definition.Kind != NodeKind.Action
                || !_registry.TryResolve(definition, null, out helpers[i], out _) || !helpers[i].IsHostCall || helpers[i].Outputs.Count > 0)
            {
                Error(node.Id, $"cannot open or close {node.Overload.Definition.Name} on this build");
                return false;
            }
        }
        var inputs = node.Overload.Inputs;
        var takes = helpers[0].Inputs;
        bool fits = takes.Count == inputs.Count && helpers[1].Inputs.Count == 0;
        for (int i = 0; fits && i < inputs.Count; i++)
            fits = takes[i].Type == inputs[i].Type;
        if (!fits)
        {
            Error(node.Id, $"{names[0]} does not take what {node.Overload.Definition.Name} is given");
            return false;
        }
        node.Helpers = helpers;
        return true;
    }

    // A Change works its difference out through a value node that takes the input type twice and gives it
    // back: a host call, or one instruction a lane.
    private bool ResolveChangeHelper(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        var type = node.Overload.Inputs[shape.Input].Type;
        if (!_registry.TryGetDefinition(shape.Helper ?? string.Empty, out var definition) || definition.Kind != NodeKind.Value
            || !_registry.TryResolve(definition, $"{type.Name},{type.Name}", out var helper, out _)
            || helper.Outputs.Count != 1 || helper.Outputs[0].Type != type || helper.Method == null || helper.InlineBody != null)
        {
            Error(node.Id, $"cannot work out a change of {type.Name} on this build");
            return false;
        }
        node.Helpers = new[] { helper };
        return true;
    }

    // A Follow moves through a value node that takes where it is and where it heads, both its own type, then
    // speed and the seconds since it last moved, and gives where it is now first.
    private bool ResolveFollowHelper(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        var type = node.Overload.Inputs[shape.Input].Type;
        if (!_registry.TryGetDefinition(shape.Helper ?? string.Empty, out var definition) || definition.Kind != NodeKind.Value
            || !_registry.TryResolve(definition, $"{type.Name},{type.Name},float,float", out var helper, out _)
            || helper.Outputs.Count == 0 || helper.Outputs[0].Type != type || helper.Method == null || helper.InlineOpcode != null
            || helper.InlineBody != null)
        {
            Error(node.Id, $"cannot follow a {type.Name} on this build");
            return false;
        }
        node.Helpers = new[] { helper };
        return true;
    }

    private void ResolveWires()
    {
        foreach (var wire in _graph.Flow)
        {
            if (!Ends(wire, out var from, out var to))
                continue;
            int exit = IndexOf(from.Overload.FlowOutputs, wire.FromPort);
            if (exit < 0)
            {
                Error(from.Id, $"has no flow output '{wire.FromPort}'");
                continue;
            }
            if (IndexOf(to.Overload.FlowInputs, wire.ToPort) < 0)
            {
                Error(to.Id, $"has no flow input '{wire.ToPort}'");
                continue;
            }
            if (from.Exits[exit] != null)
            {
                Error(from.Id, $"flow output '{wire.FromPort}' is wired twice; a Sequence runs several chains");
                continue;
            }
            from.Exits[exit] = to;
        }

        foreach (var wire in _graph.Data)
        {
            if (!Ends(wire, out var from, out var to))
                continue;
            int output = from.Overload.OutputIndex(wire.FromPort);
            if (output < 0)
            {
                Error(from.Id, $"has no output '{wire.FromPort}'");
                continue;
            }
            int input = to.Overload.InputIndex(wire.ToPort);
            if (input < 0)
            {
                Error(to.Id, $"has no input '{wire.ToPort}'");
                continue;
            }
            if (to.Overload.Inputs[input].IsSetting)
            {
                Error(to.Id, $"'{wire.ToPort}' is a setting: set it on the node instead of wiring it");
                continue;
            }
            var produced = from.Overload.Outputs[output].Type;
            var wanted = to.Overload.Inputs[input].Type;
            if (!ScriptTypes.Accepts(wanted, produced))
            {
                Error(to.Id, $"input '{wire.ToPort}' takes {wanted.Name}, {from.Id}.{wire.FromPort} gives {produced.Name}");
                continue;
            }
            if (to.Inputs[input] != null)
            {
                Error(to.Id, $"input '{wire.ToPort}' is wired twice");
                continue;
            }
            to.Inputs[input] = DataSource.Wired(from, output);
        }
    }

    private bool Ends(GraphWire wire, out ResolvedNode from, out ResolvedNode to)
    {
        to = null!;
        if (!_byId.TryGetValue(wire.FromNode, out from!))
        {
            Error(wire.FromNode, $"wire {wire} starts at a node that does not exist");
            return false;
        }
        if (!_byId.TryGetValue(wire.ToNode, out to!))
        {
            Error(wire.ToNode, $"wire {wire} ends at a node that does not exist");
            return false;
        }
        return true;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
                return i;
        }
        return -1;
    }

    private void ResolveConstants()
    {
        foreach (var node in _nodes)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var inputs = node.Overload.Inputs;
            for (int i = 0; i < inputs.Count; i++)
            {
                var port = inputs[i];
                bool given = node.Source.Values.TryGetValue(port.Name, out var value);
                if (given)
                    used.Add(port.Name);

                if (node.Inputs[i] != null)
                {
                    if (given)
                        Warn(node.Id, $"input '{port.Name}' is wired, so its value is ignored");
                    continue;
                }

                if (given)
                {
                    if (TryConstant(node, port.Name, value!, port.Type, out var constant))
                        node.Inputs[i] = DataSource.Of(constant);
                }
                else if (port.HasDefault)
                {
                    node.Inputs[i] = DataSource.Of(ConstantValue.OfBoxed(port.Type, port.Default));
                }
                else
                {
                    node.Inputs[i] = DataSource.Of(ConstantValue.Default(port.Type));
                }
            }

            switch (node.Kind)
            {
                case NodeKind.StoredVariable:
                    ResolveStored(node, used);
                    break;
                case NodeKind.WriteVariable:
                    ResolveWrite(node, used);
                    break;
                case NodeKind.Event when node.Overload.Definition.TakesSource:
                    ResolveSource(node, used);
                    break;
                case NodeKind.Event when node.Overload.Definition.TakesInterval:
                    ResolveInterval(node, used);
                    break;
                case NodeKind.Drive:
                    ResolveDriveTarget(node);
                    break;
                case NodeKind.Function:
                    ResolveFunctionName(node, used);
                    break;
                case NodeKind.Return:
                case NodeKind.CallFunction:
                case NodeKind.EvaluateFunction:
                    // Its Function is found once every Function has its name.
                    used.Add("fn");
                    break;
                case NodeKind.Await when node.Overload.Definition.IsScriptCall:
                    ResolveCalledName(node, used);
                    break;
            }

            foreach (var key in node.Source.Values.Keys)
            {
                if (!used.Contains(key))
                    Error(node.Id, $"has no input or setting called '{key}'");
            }
        }
    }

    private bool TryConstant(ResolvedNode node, string port, GraphValue value, ScriptType type, out ConstantValue constant)
    {
        if (!ConstantValue.TryConvert(value, type, out constant, out var error))
        {
            Error(node.Id, $"'{port}': {error}");
            return false;
        }
        if (constant.Binding >= 0)
        {
            if (constant.Binding >= _bindings.Count)
            {
                Error(node.Id, $"'{port}' uses binding {constant.Binding}, the graph declares {_bindings.Count}");
                return false;
            }
            if (_bindings[constant.Binding].Type != type)
            {
                Error(node.Id, $"'{port}' takes {type.Name}, binding {constant.Binding} holds {_bindings[constant.Binding].Type.Name}");
                return false;
            }
        }
        return true;
    }

    private void ResolveStored(ResolvedNode node, HashSet<string> used)
    {
        var type = node.Overload.Outputs[0].Type;
        node.VariableName = node.Id;
        if (node.Source.Values.TryGetValue("name", out var name))
        {
            used.Add("name");
            if (name.Kind != GraphValueKind.String || !NodePort.IsValidName(name.String))
                Error(node.Id, "'name' is a plain identifier");
            else
                node.VariableName = name.String;
        }

        node.Initial = ConstantValue.Default(type);
        if (node.Source.Values.TryGetValue("initial", out var initial))
        {
            used.Add("initial");
            if (TryConstant(node, "initial", initial, type, out var constant))
                node.Initial = constant;
        }
    }

    private void ResolveWrite(ResolvedNode node, HashSet<string> used)
    {
        if (!node.Source.Values.TryGetValue("var", out var target) || target.Kind != GraphValueKind.String)
        {
            Error(node.Id, "'var' names the Stored Value node to write");
            return;
        }
        used.Add("var");
        if (!_byId.TryGetValue(target.String!, out var variable) || variable.Kind != NodeKind.StoredVariable)
        {
            Error(node.Id, $"'var' is '{target.String}', which is not a Stored Value node");
            return;
        }
        if (variable.Overload.Outputs[0].Type != node.Overload.Inputs[0].Type)
        {
            Error(node.Id, $"writes {node.Overload.Inputs[0].Type.Name} into {variable.Id}, which holds {variable.Overload.Outputs[0].Type.Name}");
            return;
        }
        node.Variable = variable;
    }

    // A Function's "name", the node's id when it has none: what it is called by and exported as.
    private void ResolveFunctionName(ResolvedNode node, HashSet<string> used)
    {
        node.FunctionName = node.Id;
        if (!node.Source.Values.TryGetValue("name", out var name))
            return;
        used.Add("name");
        if (name.Kind == GraphValueKind.String && name.String!.Length == 0)
            return;
        if (name.Kind != GraphValueKind.String || !NodePort.IsValidName(name.String))
            Error(node.Id, "'name' is a plain identifier");
        else
            node.FunctionName = name.String;
    }

    // A Call Script Function's "function": the name of the Function it calls in another script, which only that
    // script knows, so it is checked as the call is made.
    private void ResolveCalledName(ResolvedNode node, HashSet<string> used)
    {
        if (!node.Source.Values.TryGetValue("function", out var name) || name.Kind != GraphValueKind.String
            || string.IsNullOrEmpty(name.String))
        {
            Error(node.Id, "calls no Function yet; type its name in 'function'");
            used.Add("function");
            return;
        }
        used.Add("function");
        if (!NodePort.IsValidName(name.String))
            Error(node.Id, "'function' is a plain identifier, the name of a Function in the other script");
        else
            node.FunctionName = name.String;
    }

    // A Return, Call Function or Evaluate Function names its Function by node id in "fn", the way a Write Value
    // names its Stored Value, and carries that Function's ports, copied when it was pointed there. Ports that
    // differ mean the Function changed after, and wires made to the old ones cannot be trusted.
    private void ResolveCalls()
    {
        foreach (var node in _nodes)
        {
            if (!FunctionSignature.FollowsFunction(node.Kind))
                continue;
            string does = node.Kind == NodeKind.Return ? "returns from" : "calls";
            if (!node.Source.Values.TryGetValue("fn", out var target) || target.Kind != GraphValueKind.String)
            {
                Error(node.Id, $"{does} no Function yet; drag its fn onto one");
                continue;
            }
            if (!_byId.TryGetValue(target.String!, out var function) || function.Kind != NodeKind.Function)
            {
                Error(node.Id, $"'fn' is '{target.String}', which is not a Function node");
                continue;
            }
            if (function.Overload.Signature != node.Overload.Signature)
            {
                Error(node.Id, $"is out of date with {function.FunctionName}; drag its fn onto {function.FunctionName} again");
                continue;
            }
            node.Function = function;
        }
    }

    // Increment and Decrement Value write their stored value plus or minus "by": each is given an Add or
    // Subtract node of its own, fed by the stored value and the step, and writes what that gives. It reads the
    // variable as the write runs, so steps in a loop build on each other.
    private void ResolveSteps()
    {
        foreach (var node in _nodes.ToArray())
        {
            if (node.Kind != NodeKind.WriteVariable || !NodeRegistry.IsStep(node.Overload.Definition) || node.Variable == null)
                continue;
            bool up = node.Overload.Definition.ClrType == typeof(IncrementValue<>);
            var type = node.Overload.Inputs[0].Type;
            if (!_registry.TryGetDefinition(up ? "Add" : "Subtract", out var math)
                || !_registry.TryResolve(math, type.Name + "," + type.Name, out var step, out _) || step.Outputs.Count != 1 || step.Outputs[0].Type != type)
            {
                Error(node.Id, $"cannot step a {type.Name} on this build");
                continue;
            }
            string id = (node.Id.Length > GraphNode.MaxIdLength - 5 ? node.Id[..(GraphNode.MaxIdLength - 5)] : node.Id) + "-step";
            var stepped = new ResolvedNode(new GraphNode(id, math.Name), node.Tag, step);
            stepped.Inputs[0] = DataSource.Wired(node.Variable, 0);
            stepped.Inputs[1] = node.Inputs[0];
            node.Inputs[0] = DataSource.Wired(stepped, 0);
            _nodes.Add(stepped);
        }
    }

    // A pure node whose every input is a constant is worked out here, by its own C# body, and whatever reads it
    // reads the answer as a constant, so the module never works it out. Only over plain numbers and vectors,
    // never a NaN (a sign flip keeps a NaN's payload in the module where the body would make it canonical), and
    // only when the body answers: whatever would trap at run time is left to trap there. Folded answers feed the
    // next round, so a chain of them folds whole. -xlinka
    private void FoldConstants()
    {
        var folded = new HashSet<ResolvedNode>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in _nodes)
            {
                if (folded.Contains(node) || !Foldable(node) || !TryFold(node, out var answers))
                    continue;
                folded.Add(node);
                foreach (var reader in _nodes)
                {
                    for (int i = 0; i < reader.Inputs.Length; i++)
                    {
                        if (reader.Inputs[i] is { Node: { } from, Output: int output } && ReferenceEquals(from, node))
                        {
                            reader.Inputs[i] = DataSource.Of(answers[output]);
                            changed = true;
                        }
                    }
                }
            }
        }
    }

    private static bool Foldable(ResolvedNode node)
    {
        if (node.Kind != NodeKind.Value || node.Overload.Method == null || node.Overload.TakesContext || node.Overload.Folds != null)
            return false;
        foreach (var port in node.Overload.Inputs.Concat(node.Overload.Outputs))
        {
            if (port.Type.Kind is not (ScriptTypeKind.Bool or ScriptTypeKind.Int or ScriptTypeKind.Long or ScriptTypeKind.Float
                or ScriptTypeKind.Double or ScriptTypeKind.Vector))
                return false;
        }
        foreach (var input in node.Inputs)
        {
            if (input?.Constant is not { Binding: < 0, Value: { } value } || HasNaN(value))
                return false;
        }
        return true;
    }

    private static bool TryFold(ResolvedNode node, out ConstantValue[] answers)
    {
        answers = Array.Empty<ConstantValue>();
        var method = node.Overload.Method!;
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        int input = 0;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (!parameters[i].IsOut)
                arguments[i] = node.Inputs[input++]!.Constant!.Value;
        }
        object? returned;
        try
        {
            returned = method.Invoke(null, arguments);
        }
        catch (System.Reflection.TargetInvocationException)
        {
            return false;
        }
        var values = new List<object?>();
        if (method.ReturnType != typeof(void))
            values.Add(returned);
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].IsOut)
                values.Add(arguments[i]);
        }
        var outputs = node.Overload.Outputs;
        if (values.Count != outputs.Count)
            return false;
        answers = new ConstantValue[outputs.Count];
        for (int i = 0; i < outputs.Count; i++)
        {
            if (values[i] is not { } value || HasNaN(value))
                return false;
            answers[i] = ConstantValue.OfBoxed(outputs[i].Type, value);
        }
        return true;
    }

    private static bool HasNaN(object value) => value switch
    {
        float single => float.IsNaN(single),
        double precise => double.IsNaN(precise),
        _ => ScriptTypes.Get(value.GetType()) is { Kind: ScriptTypeKind.Vector } type
            && type.Leaves.Any(leaf => leaf.Get(value) is float f && float.IsNaN(f) || leaf.Get(value) is double v && double.IsNaN(v)),
    };

    // A relay is a point on a wire and nothing more. Every input it feeds reads what feeds it, every exit that
    // runs it runs what it runs, and it leaves before cycles are checked or anything is lowered, so the module
    // is the one the same graph makes without it. The exits that ran into one are kept, so its own exit can
    // show the flow they count. -xlinka
    private void SpliceRelays()
    {
        if (!_nodes.Any(n => n.Kind == NodeKind.Relay))
            return;
        foreach (var node in _nodes)
        {
            if (node.Kind == NodeKind.Relay)
                continue;
            for (int i = 0; i < node.Inputs.Length; i++)
            {
                if (node.Inputs[i]?.Node is { Kind: NodeKind.Relay } relay)
                    node.Inputs[i] = Through(relay);
            }
            for (int exit = 0; exit < node.Exits.Length; exit++)
            {
                if (node.Exits[exit] is not { Kind: NodeKind.Relay } relay)
                    continue;
                var passed = new List<ResolvedNode>();
                node.Exits[exit] = Onward(relay, passed);
                _relayed.Add((node, exit, passed));
            }
        }
        _nodes.RemoveAll(n => n.Kind == NodeKind.Relay);
    }

    // What a data relay passes on: what feeds the first relay along that no relay feeds.
    private DataSource? Through(ResolvedNode relay)
    {
        var seen = new HashSet<ResolvedNode> { relay };
        var source = relay.Inputs[0];
        while (source?.Node is { Kind: NodeKind.Relay } next)
        {
            if (!seen.Add(next))
            {
                Error(next.Id, $"is part of a data cycle through {relay.Id}");
                return null;
            }
            source = next.Inputs[0];
        }
        return source;
    }

    // Where a flow relay sends flow: the first node past it and any relays after it, or nowhere.
    private ResolvedNode? Onward(ResolvedNode relay, List<ResolvedNode> passed)
    {
        ResolvedNode? next = relay;
        while (next is { Kind: NodeKind.Relay })
        {
            if (passed.Contains(next))
            {
                Error(next.Id, "is on a flow cycle of relays alone, which never ends");
                return null;
            }
            passed.Add(next);
            next = next.Exits.Length > 0 ? next.Exits[0] : null;
        }
        return next;
    }

    // A sourced event's "source": a bound object, never a constant, never compiled into anything.
    private void ResolveSource(ResolvedNode node, HashSet<string> used)
    {
        if (!node.Source.Values.TryGetValue("source", out var source))
        {
            if (node.Overload.Definition.SourceType != "slot")
                Error(node.Id, $"'source' names the {node.Overload.Definition.SourceType} it listens to");
            return;
        }
        used.Add("source");
        if (source.Kind != GraphValueKind.Binding)
        {
            Error(node.Id, "'source' is an object bound to the script");
            return;
        }
        if (source.Binding >= _bindings.Count)
        {
            Error(node.Id, $"'source' uses binding {source.Binding}, the graph declares {_bindings.Count}");
            return;
        }
        node.SourceBinding = source.Binding;
    }

    // The field a drive holds: bound to the script and set on the node, never wired, so every machine finds
    // it in the bindings it writes into.
    private void ResolveDriveTarget(ResolvedNode node)
    {
        int target = node.Overload.Shape!.Target;
        var source = node.Inputs[target];
        if (source == null || source.Node != null || source.Constant is not { Binding: >= 0 } bound)
        {
            Error(node.Id, $"'{node.Overload.Inputs[target].Name}' is the field it holds, bound to the script");
            return;
        }
        node.SourceBinding = bound.Binding;
        node.HeardBinding = OptionalBinding(node, node.Overload.Shape.Heard);
        node.HeardCountBinding = OptionalBinding(node, node.Overload.Shape.HeardCount);
    }

    // A field input a drive may leave unset; set, it is a field bound to the script.
    private int OptionalBinding(ResolvedNode node, int input)
    {
        if (input < 0 || node.Inputs[input] is not { } source)
            return -1;
        if (source.Node == null && source.Constant is { Binding: >= 0 } bound)
            return bound.Binding;
        if (source.Node == null && source.Constant is { Binding: < 0 })
            return -1;
        Error(node.Id, $"'{node.Overload.Inputs[input].Name}' is a field bound to the script, or nothing");
        return -1;
    }

    // A timed event's "seconds": a number, one a second when unset, never faster than MinIntervalSeconds.
    private void ResolveInterval(ResolvedNode node, HashSet<string> used)
    {
        node.IntervalSeconds = 1f;
        if (!node.Source.Values.TryGetValue("seconds", out var seconds))
            return;
        used.Add("seconds");
        if (seconds.Kind != GraphValueKind.Number || !double.IsFinite(seconds.Number) || seconds.Number <= 0d)
        {
            Error(node.Id, "'seconds' is a number of seconds above zero");
            return;
        }
        node.IntervalSeconds = (float)Math.Max(seconds.Number, MinIntervalSeconds);
    }

    // A flow cycle has to pass through a loop node. Anything else is a chain that never ends, found
    // here rather than by the fuel meter at run time. A Delay's exit is not an edge at all: what follows
    // it runs in a later call, so a cycle through a Delay is a timer that re-arms itself. An await's answer
    // exits are the same.
    private void CheckFlowCycles()
    {
        var index = new Dictionary<ResolvedNode, int>();
        var low = new Dictionary<ResolvedNode, int>();
        var onStack = new HashSet<ResolvedNode>();
        var stack = new Stack<ResolvedNode>();
        int counter = 0;

        foreach (var root in _nodes)
        {
            if (index.ContainsKey(root))
                continue;

            var work = new Stack<(ResolvedNode Node, int Exit)>();
            work.Push((root, 0));
            index[root] = low[root] = counter++;
            stack.Push(root);
            onStack.Add(root);

            while (work.Count > 0)
            {
                var (node, exit) = work.Pop();
                if (exit < node.Exits.Length)
                {
                    work.Push((node, exit + 1));
                    var next = node.Exits[exit];
                    if (next == null || !node.RunsNow(exit))
                        continue;
                    if (!index.ContainsKey(next))
                    {
                        index[next] = low[next] = counter++;
                        stack.Push(next);
                        onStack.Add(next);
                        work.Push((next, 0));
                    }
                    else if (onStack.Contains(next))
                    {
                        low[node] = Math.Min(low[node], index[next]);
                    }
                    continue;
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }

                if (low[node] != index[node])
                    continue;

                var component = new List<ResolvedNode>();
                ResolvedNode member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    component.Add(member);
                } while (member != node);

                bool cyclic = component.Count > 1 || node.Exits.Where((_, exit) => node.RunsNow(exit)).Contains(node);
                if (cyclic && !component.Any(n => n.IsLoop))
                {
                    var ids = string.Join(", ", component.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal));
                    foreach (var n in component)
                        Error(n.Id, $"is on a flow cycle ({ids}) that passes through no loop node");
                }
            }
        }
    }

    private void CheckDataCycles()
    {
        var state = new Dictionary<ResolvedNode, int>();
        foreach (var node in _nodes)
        {
            if (IsComputed(node))
                Visit(node);
        }

        void Visit(ResolvedNode node)
        {
            var path = new Stack<(ResolvedNode Node, int Input)>();
            if (state.GetValueOrDefault(node) != 0)
                return;
            state[node] = 1;
            path.Push((node, 0));
            while (path.Count > 0)
            {
                var (current, input) = path.Pop();
                if (input < current.Inputs.Length)
                {
                    path.Push((current, input + 1));
                    var source = current.Inputs[input]?.Node;
                    if (source == null || !IsComputed(source))
                        continue;
                    int seen = state.GetValueOrDefault(source);
                    if (seen == 1)
                        Error(source.Id, $"is part of a data cycle through {current.Id}");
                    else if (seen == 0)
                    {
                        state[source] = 1;
                        path.Push((source, 0));
                    }
                    continue;
                }
                state[current] = 2;
            }
        }

        static bool IsComputed(ResolvedNode node)
            => node.Kind is NodeKind.Value or NodeKind.Read or NodeKind.Constant or NodeKind.Memory or NodeKind.EvaluateFunction;
    }

    // Which Functions wait: a Delay, an await or a call of a Function that waits anywhere in the body, every exit
    // followed, the ones that run later included. Worked to a fixed point, so Functions calling each other
    // settle. A call of one that waits waits for its answer like an await, and what follows it runs later.
    private void MarkAsync()
    {
        var functions = _nodes.Where(n => n.Kind == NodeKind.Function).ToList();
        if (functions.Count == 0)
            return;
        var bodies = new Dictionary<ResolvedNode, List<ResolvedNode>>();
        foreach (var function in functions)
            bodies[function] = LogicalFlow(function);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var function in functions)
            {
                if (function.IsAsync)
                    continue;
                foreach (var node in bodies[function])
                {
                    string? reason = node.IsDelay || node.IsAwait ? TitleOf(node)
                        : node.Kind == NodeKind.CallFunction && node.Function is { IsAsync: true } called ? $"it calls {called.FunctionName}, which waits"
                        : null;
                    if (reason == null)
                        continue;
                    function.IsAsync = true;
                    function.WaitReason = reason;
                    changed = true;
                    break;
                }
            }
        }
        foreach (var node in _nodes)
        {
            if (node.Kind == NodeKind.CallFunction && node.Function is { IsAsync: true })
                node.IsAsyncCall = true;
        }
    }

    // Every node a flow that starts at root runs, now or later: through every exit, a Delay's and an answer's too.
    private static List<ResolvedNode> LogicalFlow(ResolvedNode root)
    {
        var seen = new HashSet<ResolvedNode>();
        var order = new List<ResolvedNode>();
        var pending = new Stack<ResolvedNode>();
        foreach (var exit in root.Exits)
        {
            if (exit != null)
                pending.Push(exit);
        }
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node))
                continue;
            order.Add(node);
            foreach (var next in node.Exits)
            {
                if (next != null)
                    pending.Push(next);
            }
        }
        return order;
    }

    private static string TitleOf(ResolvedNode node)
    {
        var path = node.Overload.Definition.Path;
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    // What a chain that runs out goes on to, the way the body is lowered: the next step of a Sequence, the leave
    // of a scope and what follows it, or back to a loop, whose own exit is followed on its own. Null is the end of
    // the body.
    private enum ResumeKind : byte { Step, Then, Loop }

    private sealed record Resume(ResumeKind Kind, ResolvedNode Node, int Index, Resume? Next)
    {
        public string Key => Next == null ? $"{(int)Kind}{Node.Tag}:{Index}" : $"{(int)Kind}{Node.Tag}:{Index}|{Next.Key}";
    }

    private const int EndsAtReturnBudget = 20000;

    // Every way through a Function's body that ends without a Return, refused where it ends: an unwired exit, a
    // Once past its first run, or the Function's own next left unwired. Each branch side runs to its end, a
    // Sequence's steps one after another, a loop's body back to the loop and a scope's body to its leave. A shape
    // it does not know, or more work than the bound, and it says nothing rather than refuse a graph it could not
    // read. -xlinka
    private void CheckEndsAtReturn(ResolvedNode function)
    {
        var ends = new List<(ResolvedNode Node, int Exit)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(ResolvedNode? Node, Resume? Resume, ResolvedNode From, int Exit)>();
        pending.Push((function.Exits.Length > 0 ? function.Exits[0] : null, null, function, 0));
        int work = 0;
        while (pending.Count > 0)
        {
            if (++work > EndsAtReturnBudget)
                return;
            var (node, resume, from, exit) = pending.Pop();
            if (!seen.Add($"{node?.Tag ?? -1}/{from.Tag}:{exit}/{resume?.Key}"))
                continue;

            if (node == null)
            {
                if (resume == null)
                {
                    ends.Add((from, exit));
                    continue;
                }
                var back = resume.Node;
                var steps = back.Overload.Shape!.Exits;
                switch (resume.Kind)
                {
                    case ResumeKind.Step:
                        int at = steps[resume.Index];
                        pending.Push((back.Exits[at], resume.Index + 1 < steps.Length ? resume with { Index = resume.Index + 1 } : resume.Next, back, at));
                        break;
                    case ResumeKind.Then:
                        pending.Push((back.Exits[resume.Index], resume.Next, back, resume.Index));
                        break;
                }
                continue;
            }

            switch (node.Kind)
            {
                case NodeKind.Return:
                    continue;
                case NodeKind.Action when node.FailedExit >= 0:
                    pending.Push((node.Exits[0], resume, node, 0));
                    pending.Push((node.Exits[node.FailedExit], resume, node, node.FailedExit));
                    continue;
                case NodeKind.Action:
                case NodeKind.WriteVariable:
                case NodeKind.Pulse:
                case NodeKind.CallFunction when !node.IsAsyncCall:
                    pending.Push((node.Exits.Length > 0 ? node.Exits[0] : null, resume, node, 0));
                    continue;
            }

            var shape = node.Overload.Shape;
            if (shape == null || node.IsDelay || node.IsAwait || node.IsAsyncCall)
                return;
            var exits = shape.Exits;
            switch (shape.Kind)
            {
                case FlowShapeKind.Branch:
                case FlowShapeKind.Toggle:
                case FlowShapeKind.Watch:
                case FlowShapeKind.Cooldown:
                    pending.Push((node.Exits[exits[0]], resume, node, exits[0]));
                    pending.Push((node.Exits[exits[1]], resume, node, exits[1]));
                    break;
                case FlowShapeKind.Once:
                    pending.Push((node.Exits[exits[0]], resume, node, exits[0]));
                    pending.Push((null, resume, node, -1));
                    break;
                case FlowShapeKind.Change:
                    pending.Push((node.Exits[exits[0]], resume, node, exits[0]));
                    break;
                case FlowShapeKind.Scope:
                    pending.Push((node.Exits[exits[0]], new Resume(ResumeKind.Then, node, exits[1], resume), node, exits[0]));
                    break;
                case FlowShapeKind.Sequence:
                    if (exits.Length == 0)
                        pending.Push((null, resume, node, -1));
                    else
                        pending.Push((node.Exits[exits[0]], exits.Length > 1 ? new Resume(ResumeKind.Step, node, 1, resume) : resume, node, exits[0]));
                    break;
                case FlowShapeKind.Repeat:
                case FlowShapeKind.While:
                case FlowShapeKind.Each:
                case FlowShapeKind.Range:
                case FlowShapeKind.ListEach:
                    pending.Push((node.Exits[exits[0]], new Resume(ResumeKind.Loop, node, 0, null), node, exits[0]));
                    pending.Push((node.Exits[exits[1]], resume, node, exits[1]));
                    break;
                default:
                    return;
            }
        }

        string name = function.FunctionName ?? function.Id;
        foreach (var (node, exit) in ends)
        {
            if (node == function)
                Error(node.Id, "gives values back but its body never reaches a Return; run next to a Return, or have it give nothing back");
            else if (exit < 0)
                Error(node.Id, $"ends {name} without a Return after its first run, while {name} gives values back; wire it to a Return, or have {name} give nothing back");
            else
                Error(node.Id, $"'{node.Overload.FlowOutputs[exit]}' ends {name} without a Return, while {name} gives values back; wire it to a Return, or have {name} give nothing back");
        }
    }

    // Functions as a whole: one name each; a Return only ever reached from its own Function's body, whatever waits
    // come between; and an Evaluate only of a Function whose body is one Return of values that need no flow, which
    // never comes back round to evaluating itself, since a value is worked out at once and has no If to stop it.
    // On the way, which events and Functions reach each Delay, await and async call, so the flows that carry on
    // after one inside a Function carry the word of its call. -xlinka
    private void CheckFunctions()
    {
        var functions = _nodes.Where(n => n.Kind == NodeKind.Function).ToList();
        if (functions.Count == 0 && !_nodes.Any(n => n.Kind == NodeKind.Return))
            return;

        var named = new Dictionary<string, ResolvedNode>(StringComparer.Ordinal);
        foreach (var function in functions)
        {
            string name = function.FunctionName ?? function.Id;
            if (!named.TryAdd(name, function))
            {
                Error(function.Id, $"is called '{name}' like {named[name].Id}; two Functions never share a name");
                Error(named[name].Id, $"is called '{name}' like {function.Id}; two Functions never share a name");
            }
        }

        // Every flow from where it starts, an event or a Function, through every exit. Only nodes some flow reaches
        // are lowered, so a Return no flow reaches has to be found here.
        var reachedBy = new Dictionary<ResolvedNode, List<ResolvedNode>>();
        foreach (var root in _nodes)
        {
            if (root.Kind is not (NodeKind.Event or NodeKind.Function))
                continue;
            foreach (var node in LogicalFlow(root))
            {
                if (node.IsDelay || node.IsAwait || node.IsAsyncCall)
                    (node.Owners ??= new HashSet<ResolvedNode>()).Add(root);
                if (node.Kind != NodeKind.Return || node.Function is not { } owner)
                    continue;
                if (!reachedBy.TryGetValue(node, out var roots))
                    reachedBy[node] = roots = new List<ResolvedNode>();
                roots.Add(root);
                if (root != owner)
                {
                    Error(node.Id, root.Kind == NodeKind.Function
                        ? $"returns from {owner.FunctionName}, but runs in the body of {root.FunctionName}"
                        : $"returns from {owner.FunctionName}, but runs in {root.Id}'s flow; a Return only ends its own Function's body");
                }
            }
        }
        foreach (var node in _nodes)
        {
            if (node.Kind == NodeKind.Return && node.Function is { } owner && !reachedBy.ContainsKey(node))
                Error(node.Id, $"is never reached; run it from the body of {owner.FunctionName}");
            if (node.IsDelay || node.IsAwait || node.IsAsyncCall)
                node.CarriesWord = node.Owners?.Any(o => o.Kind == NodeKind.Function) == true;
        }

        // A Function that gives nothing back, no ports after its bar, is a void one: its body may end wherever its
        // flow does, and a Return in it only leaves early. One that gives values hands them over at a Return, so a
        // way through its body that ends anywhere else is refused where it ends, rather than the call quietly
        // coming back with every output's default. -xlinka
        foreach (var function in functions)
        {
            if (!function.IsAsync && function.Overload.FunctionSignature is { Outputs.Count: > 0 })
                CheckEndsAtReturn(function);
        }

        // Pure: next goes straight to a Return of this Function, fed by its arguments and what needs no flow.
        foreach (var function in functions)
        {
            if (function.Exits.Length > 0 && function.Exits[0] is { Kind: NodeKind.Return } only && only.Function == function
                && NeedsNoFlow(only, new HashSet<ResolvedNode>(), function))
                function.PureReturn = only;
        }

        var evaluates = new Dictionary<ResolvedNode, List<ResolvedNode>>();
        foreach (var node in _nodes)
        {
            if (node.Kind != NodeKind.EvaluateFunction || node.Function is not { } target)
                continue;
            if (target.IsAsync)
                Error(node.Id, $"{target.FunctionName} waits ({target.WaitReason}), so it cannot be evaluated as a value; use Call Function");
            else if (!target.IsPure)
                Error(node.Id, $"evaluates {target.FunctionName}, whose body runs flow; use Call Function, or have its next go straight to a Return");
        }
        foreach (var function in functions)
        {
            if (function.IsPure)
                evaluates[function] = EvaluatedIn(function.PureReturn!);
        }
        foreach (var (function, inside) in evaluates)
        {
            foreach (var evaluate in inside)
            {
                if (evaluate.Function is { IsPure: true } target && Reaches(target, function))
                    Error(evaluate.Id, $"makes {function.FunctionName} evaluate itself, which never ends; recurse with Call Function and an If");
            }
        }

        // The Evaluate Function nodes a Return's values are worked out through.
        static List<ResolvedNode> EvaluatedIn(ResolvedNode body)
        {
            var found = new List<ResolvedNode>();
            var seen = new HashSet<ResolvedNode>();
            var pending = new Stack<ResolvedNode>();
            foreach (var input in body.Inputs)
            {
                if (input?.Node is { } source)
                    pending.Push(source);
            }
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (!seen.Add(node) || node.Kind is not (NodeKind.Value or NodeKind.Read or NodeKind.Constant or NodeKind.Memory
                        or NodeKind.EvaluateFunction))
                    continue;
                if (node.Kind == NodeKind.EvaluateFunction)
                    found.Add(node);
                foreach (var input in node.Inputs)
                {
                    if (input?.Node is { } source)
                        pending.Push(source);
                }
            }
            return found;
        }

        // Whether evaluating from leads, through the pure Functions it evaluates, to evaluating to.
        bool Reaches(ResolvedNode from, ResolvedNode to)
        {
            var seen = new HashSet<ResolvedNode>();
            var pending = new Stack<ResolvedNode>();
            pending.Push(from);
            while (pending.Count > 0)
            {
                var function = pending.Pop();
                if (function == to)
                    return true;
                if (!seen.Add(function) || !evaluates.TryGetValue(function, out var inside))
                    continue;
                foreach (var evaluate in inside)
                {
                    if (evaluate.Function is { IsPure: true } next)
                        pending.Push(next);
                }
            }
            return false;
        }
    }

    private CompileResult Emit(string hash)
    {
        var module = new WasmModuleBuilder();

        int stringImport = module.ImportFunction(RuntimeModule, StringImport,
            new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }));
        int urlImport = AllConstants().Any(c => c.Value is Uri)
            ? module.ImportFunction(RuntimeModule, UrlImport, new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }))
            : -1;
        int bigImport = AllConstants().Any(c => c.Value is ScriptBigInt)
            ? module.ImportFunction(RuntimeModule, BigIntImport, new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }))
            : -1;

        var imports = new List<NodeOverload>();
        var importIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int Import(NodeOverload overload)
        {
            var linked = overload.Folds ?? overload;
            if (!importIndex.TryGetValue(linked.ImportName, out int function))
            {
                function = module.ImportFunction(NodeModule, linked.ImportName, linked.ImportType!);
                importIndex.Add(linked.ImportName, function);
                imports.Add(linked);
            }
            return function;
        }
        foreach (var node in _nodes)
        {
            if (node.Overload.IsHostCall)
                node.ImportIndex = Import(node.Overload);
            if (node.Helpers.Length > 0)
                node.HelperImports = node.Helpers.Select(h => h.IsHostCall ? Import(h) : -1).ToArray();
        }

        // Each Call Script Function starts its call through an import of its own, typed for its ports.
        var scriptCalls = new List<ScriptCallEntry>();
        foreach (var node in _nodes.Where(n => n.Overload.Definition.IsScriptCall))
        {
            var type = new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }.Concat(node.Overload.Inputs.SelectMany(p => p.Type.Lanes)).ToArray(),
                Array.Empty<WasmValueType>());
            var name = ScriptCallImportPrefix + node.Id;
            node.ImportIndex = module.ImportFunction(RuntimeModule, name, type);
            scriptCalls.Add(new ScriptCallEntry(node.Id, name, type, node.FunctionName!, node.Overload.FunctionSignature!, node.Overload));
        }

        int timerImport = -1;
        var delays = _nodes.Where(n => n.IsDelay).ToList();
        for (int i = 0; i < delays.Count; i++)
            delays[i].TimerIndex = i;
        if (delays.Count > 0)
        {
            timerImport = module.ImportFunction(RuntimeModule, TimerImport,
                new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.F32 }, Array.Empty<WasmValueType>()));
        }

        // An async call waits for its Function's answer the way an await waits for its work.
        var awaits = _nodes.Where(n => n.IsAwait || n.IsAsyncCall).ToList();
        for (int i = 0; i < awaits.Count; i++)
            awaits[i].AwaitIndex = i;

        int nowImport = -1;
        if (_nodes.Any(n => n.Overload.Shape?.Kind is FlowShapeKind.Cooldown or FlowShapeKind.Follow or FlowShapeKind.FrameDelta))
            nowImport = module.ImportFunction(RuntimeModule, NowImport, new WasmFuncType(Array.Empty<WasmValueType>(), new[] { WasmValueType.F64 }));
        int eachCount = -1, eachItem = -1;
        if (_nodes.Any(n => n.Overload.Shape?.Kind == FlowShapeKind.Each))
        {
            eachCount = module.ImportFunction(RuntimeModule, EachCountImport,
                new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }));
            eachItem = module.ImportFunction(RuntimeModule, EachItemImport,
                new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }));
        }
        int eachQuery = -1;
        if (_nodes.Any(n => n.Overload.Shape is { Kind: FlowShapeKind.Each, Query.Length: > 0 }))
            eachQuery = module.ImportFunction(RuntimeModule, EachQueryImport, EachQueryType);
        int liveImport = -1;
        if (_nodes.Any(n => n.Overload.Shape is { Kind: FlowShapeKind.Watch } watch && n.Overload.Inputs[watch.Input].Type.IsHandle))
            liveImport = module.ImportFunction(RuntimeModule, LiveImport, new WasmFuncType(new[] { WasmValueType.I32 }, new[] { WasmValueType.I32 }));

        // A waiting Function's calls: the word of the call the next Delay or await starts in, an async call's
        // ticket and its first stretch run out, an argument read after a wait, and per Function its arguments
        // kept and what its Return gives.
        int wordImport = -1, callOpen = -1, callEnd = -1, argument = -1;
        var waiting = _nodes.Where(n => n.Kind == NodeKind.Function && n.IsAsync).ToList();
        if (waiting.Count > 0)
            wordImport = module.ImportFunction(RuntimeModule, WordImport, new WasmFuncType(new[] { WasmValueType.I32 }, Array.Empty<WasmValueType>()));
        if (_nodes.Any(n => n.IsAsyncCall))
        {
            callOpen = module.ImportFunction(RuntimeModule, CallOpenImport,
                new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I32 }));
            callEnd = module.ImportFunction(RuntimeModule, CallEndImport, new WasmFuncType(new[] { WasmValueType.I32 }, Array.Empty<WasmValueType>()));
        }
        if (waiting.Any(n => n.Overload.FunctionSignature!.Inputs.Count > 0))
        {
            argument = module.ImportFunction(RuntimeModule, ArgumentImport,
                new WasmFuncType(new[] { WasmValueType.I32, WasmValueType.I32, WasmValueType.I32 }, new[] { WasmValueType.I64 }));
        }
        foreach (var node in waiting)
        {
            var signature = node.Overload.FunctionSignature!;
            node.DoneImport = module.ImportFunction(RuntimeModule, FunctionDoneImportPrefix + node.FunctionName,
                new WasmFuncType(new[] { WasmValueType.I32 }.Concat(signature.Outputs.SelectMany(p => p.Type.Lanes)).ToArray(), Array.Empty<WasmValueType>()));
            if (signature.Inputs.Count > 0)
                node.KeepImport = module.ImportFunction(RuntimeModule, FunctionKeepImportPrefix + node.FunctionName,
                    new WasmFuncType(new[] { WasmValueType.I32 }.Concat(signature.Inputs.SelectMany(p => p.Type.Lanes)).ToArray(), Array.Empty<WasmValueType>()));
        }
        // An action with its failed exit wired: the module clears the call's failure before it and asks after it.
        int failClear = -1, failCheck = -1;
        if (_nodes.Any(n => n.FailedExit >= 0))
        {
            failClear = module.ImportFunction(RuntimeModule, FailClearImport, new WasmFuncType(Array.Empty<WasmValueType>(), Array.Empty<WasmValueType>()));
            failCheck = module.ImportFunction(RuntimeModule, FailCheckImport, new WasmFuncType(Array.Empty<WasmValueType>(), new[] { WasmValueType.I32 }));
        }
        var runtime = new RuntimeImports(timerImport, nowImport, eachCount, eachItem, eachQuery, liveImport, wordImport, callOpen, callEnd, argument,
            failClear, failCheck);

        // Every constant string, deduplicated, laid out after the continuation stack.
        var strings = new Dictionary<string, (int Offset, int Length, int Global)>(StringComparer.Ordinal);
        var data = new List<byte>();
        foreach (var constant in AllConstants())
        {
            if (constant.Value is not string text || constant.Type.Kind != ScriptTypeKind.Handle || strings.ContainsKey(text))
                continue;
            var utf8 = Encoding.UTF8.GetBytes(text);
            int global = module.AddGlobal(new WasmGlobalType(WasmValueType.I32, true), WasmConstInit.I32(0));
            strings.Add(text, (StackBytes + data.Count, utf8.Length, global));
            data.AddRange(utf8);
        }
        // Every constant address after them, by its text, each read from the global its handle is made into.
        var urls = new Dictionary<string, (int Offset, int Length, int Global)>(StringComparer.Ordinal);
        foreach (var constant in AllConstants())
        {
            if (constant.Value is not Uri url)
                continue;
            if (!urls.TryGetValue(url.OriginalString, out var made))
            {
                var utf8 = Encoding.UTF8.GetBytes(url.OriginalString);
                made = (StackBytes + data.Count, utf8.Length, module.AddGlobal(new WasmGlobalType(WasmValueType.I32, true), WasmConstInit.I32(0)));
                urls.Add(url.OriginalString, made);
                data.AddRange(utf8);
            }
            constant.Global = made.Global;
        }
        // And every constant big integer, by its digits.
        var bigs = new Dictionary<string, (int Offset, int Length, int Global)>(StringComparer.Ordinal);
        foreach (var constant in AllConstants())
        {
            if (constant.Value is not ScriptBigInt big)
                continue;
            string digits = big.ToString();
            if (!bigs.TryGetValue(digits, out var made))
            {
                var utf8 = Encoding.UTF8.GetBytes(digits);
                made = (StackBytes + data.Count, utf8.Length, module.AddGlobal(new WasmGlobalType(WasmValueType.I32, true), WasmConstInit.I32(0)));
                bigs.Add(digits, made);
                data.AddRange(utf8);
            }
            constant.Global = made.Global;
        }

        uint pages = (uint)((StackBytes + data.Count + 65535) / 65536);
        module.AddMemory(pages, pages);
        if (data.Count > 0)
            module.AddActiveData(WasmConstInit.I32(StackBytes), data.ToArray());

        var variables = new List<ScriptVariableEntry>();
        var stringVariables = new List<(int Global, string Text)>();
        var urlVariables = new List<(int Global, int From)>();
        var bigVariables = new List<(int Global, int From)>();
        foreach (var node in _nodes.Where(n => n.Kind == NodeKind.StoredVariable))
        {
            var type = node.Overload.Outputs[0].Type;
            var initial = node.Initial ?? ConstantValue.Default(type);
            var raw = new ulong[type.LaneCount];
            if (initial.Binding >= 0)
                raw[0] = LaneCodec.FromInt(initial.Binding + 1);
            else if (initial.Value is not string)
                LaneCodec.Write(type, initial.Value, raw, null);

            node.Globals = new int[type.LaneCount];
            var exports = new string[type.LaneCount];
            for (int lane = 0; lane < type.LaneCount; lane++)
            {
                node.Globals[lane] = module.AddGlobal(new WasmGlobalType(type.Lanes[lane], true), ConstOf(type.Lanes[lane], raw[lane]));
                exports[lane] = $"{VariableExportPrefix}{node.Id}.{lane}";
                module.Export(exports[lane], WasmExternKind.Global, node.Globals[lane]);
            }
            if (initial.Value is string text)
                stringVariables.Add((node.Globals[0], text));
            else if (initial.Value is Uri)
                urlVariables.Add((node.Globals[0], initial.Global));
            else if (initial.Value is ScriptBigInt)
                bigVariables.Add((node.Globals[0], initial.Global));
            variables.Add(new ScriptVariableEntry(node.Id, node.VariableName ?? node.Id, type, exports));
        }

        // A Display's value and a Pulse's count are variables too: the host reads them the same way, and a
        // handle on show stays a root for as long as it is shown. A Display that can be worked out at any
        // time is refreshed by the show export; one fed straight from a flow's own data is taken as that
        // flow's call ends; any other would have to compute from data that is gone, so it shows nothing.
        // What a Once, a Toggle or a Cooldown remembers between runs: fresh with every instance, and carried
        // to the same node in a live graph swap.
        var memory = new List<ScriptMemoryEntry>();
        foreach (var node in _nodes)
        {
            var kind = node.Overload.Shape?.Kind;
            switch (kind)
            {
                case FlowShapeKind.Once:
                case FlowShapeKind.Toggle:
                    node.Globals = new[] { module.AddGlobal(new WasmGlobalType(WasmValueType.I32, true), WasmConstInit.I32(0)) };
                    break;
                case FlowShapeKind.Cooldown:
                    node.Globals = new[] { module.AddGlobal(new WasmGlobalType(WasmValueType.F64, true), WasmConstInit.F64(-1e300)) };
                    break;
                case FlowShapeKind.Change:
                case FlowShapeKind.Watch:
                {
                    // Whether it has run, then the lanes of what came in last. Not carried in a swap: an
                    // edited graph's Change or Watch starts over.
                    var watched = node.Overload.Inputs[node.Overload.Shape!.Input].Type;
                    var lanes = watched.Lanes;
                    node.Globals = new int[lanes.Length + 1];
                    node.Globals[0] = module.AddGlobal(new WasmGlobalType(WasmValueType.I32, true), WasmConstInit.I32(0));
                    for (int lane = 0; lane < lanes.Length; lane++)
                        node.Globals[lane + 1] = module.AddGlobal(new WasmGlobalType(lanes[lane], true), ConstOf(lanes[lane], 0ul));
                    // A kept thing stays held between calls, or its number is handed to another.
                    if (watched.IsHandle)
                    {
                        string held = HandleRootPrefix + node.Id;
                        module.Export(held, WasmExternKind.Global, node.Globals[1]);
                        _handleRoots.Add(held);
                    }
                    continue;
                }
                case FlowShapeKind.Follow:
                case FlowShapeKind.FrameDelta:
                {
                    // Whether it has run, the lanes of where it is (a Follow) or of the value last frame (a Frame
                    // Delta), the clock when that was, then for a Frame Delta the change it gave then, and for a
                    // two-stage Follow where its middle is.
                    bool delta = kind == FlowShapeKind.FrameDelta || node.Overload.Shape!.Stages == 2;
                    var lanes = node.Overload.Inputs[node.Overload.Shape!.Input].Type.Lanes;
                    node.Globals = new int[2 + lanes.Length * (delta ? 2 : 1)];
                    node.Globals[0] = module.AddGlobal(new WasmGlobalType(WasmValueType.I32, true), WasmConstInit.I32(0));
                    for (int lane = 0; lane < lanes.Length; lane++)
                        node.Globals[lane + 1] = module.AddGlobal(new WasmGlobalType(lanes[lane], true), ConstOf(lanes[lane], 0ul));
                    node.Globals[lanes.Length + 1] = module.AddGlobal(new WasmGlobalType(WasmValueType.F64, true), WasmConstInit.F64(-1e300));
                    for (int lane = 0; delta && lane < lanes.Length; lane++)
                        node.Globals[lanes.Length + 2 + lane] = module.AddGlobal(new WasmGlobalType(lanes[lane], true), ConstOf(lanes[lane], 0ul));
                    continue;
                }
                default:
                    continue;
            }
            string export = MemoryExportPrefix + node.Id;
            module.Export(export, WasmExternKind.Global, node.Globals[0]);
            memory.Add(new ScriptMemoryEntry(node.Id, kind!.Value, export));
        }

        // A counter on every wired flow exit, for the editor's live view: how many times flow has left that
        // node that way. Four instructions a step, whichever way the flow goes.
        var traces = new List<ScriptTraceEntry>();
        foreach (var node in _nodes)
        {
            if (node.Exits.Length == 0)
                continue;
            node.TraceGlobals = new int[node.Exits.Length];
            for (int exit = 0; exit < node.Exits.Length; exit++)
            {
                if (node.Exits[exit] == null)
                {
                    node.TraceGlobals[exit] = -1;
                    continue;
                }
                int global = module.AddGlobal(new WasmGlobalType(WasmValueType.I32, true), WasmConstInit.I32(0));
                string export = $"{TraceExportPrefix}{node.Id}.{exit}";
                module.Export(export, WasmExternKind.Global, global);
                node.TraceGlobals[exit] = global;
                traces.Add(new ScriptTraceEntry(node.Id, node.Overload.FlowOutputs[exit], export));
            }
        }
        // A flow relay's exit counts the flow that reaches it, from the counters of the exits that run it; nothing
        // of its own runs.
        var relayed = new Dictionary<ResolvedNode, List<string>>();
        foreach (var (node, exit, passed) in _relayed)
        {
            if (exit >= node.TraceGlobals.Length || node.TraceGlobals[exit] < 0)
                continue;
            string export = $"{TraceExportPrefix}{node.Id}.{exit}";
            foreach (var relay in passed)
            {
                if (!relayed.TryGetValue(relay, out var exports))
                    relayed[relay] = exports = new List<string>();
                exports.Add(export);
            }
        }
        foreach (var (relay, exports) in relayed)
            traces.Add(new ScriptTraceEntry(relay.Id, relay.Overload.FlowOutputs[0], exports[0]) { Also = exports.Skip(1).ToArray() });

        var displays = new List<ScriptDisplayEntry>();
        var free = new List<ResolvedNode>();
        var direct = new List<ResolvedNode>();
        var shown = new Dictionary<ResolvedNode, int>();
        foreach (var node in _nodes.Where(n => n.Kind is NodeKind.Display or NodeKind.Pulse))
        {
            bool counts = node.Kind == NodeKind.Pulse;
            var type = counts ? ScriptTypes.Int : node.Overload.Inputs[0].Type;
            node.Globals = new int[type.LaneCount];
            var exports = new string[type.LaneCount];
            for (int lane = 0; lane < type.LaneCount; lane++)
            {
                node.Globals[lane] = module.AddGlobal(new WasmGlobalType(type.Lanes[lane], true), ConstOf(type.Lanes[lane], 0));
                exports[lane] = $"{VariableExportPrefix}{node.Id}.{lane}";
                module.Export(exports[lane], WasmExternKind.Global, node.Globals[lane]);
            }
            variables.Add(new ScriptVariableEntry(node.Id, (counts ? "pulse." : "display.") + node.Id, type, exports));
            bool refreshed = !counts && NeedsNoFlow(node, new HashSet<ResolvedNode>());
            bool captured = !counts && !refreshed && FedByFlow(node);
            shown[node] = displays.Count;
            displays.Add(new ScriptDisplayEntry(node.Id, counts, refreshed ? DisplayExportPrefix + node.Id : null, captured));
            if (refreshed)
                free.Add(node);
            else if (captured)
                direct.Add(node);
            else if (!counts)
                Warn(node.Id, "shows a value worked out from a flow's data after that flow is done; wire it straight to the output to show");
        }

        // What each drive holds its field at, read the way a stored variable is, so a handle it holds stays
        // alive until the next frame's drives replace it.
        var drives = new List<ScriptDriveEntry>();
        var driven = new List<ResolvedNode>();
        foreach (var node in _nodes.Where(n => n.Kind == NodeKind.Drive))
        {
            var shape = node.Overload.Shape!;
            var type = node.Overload.Inputs[shape.Input].Type;
            if (!NeedsNoFlow(node, new HashSet<ResolvedNode>()))
            {
                Error(node.Id, "is worked out every frame on every machine, so its value can only come from what needs no event");
                continue;
            }
            node.Globals = new int[type.LaneCount];
            var exports = new string[type.LaneCount];
            for (int lane = 0; lane < type.LaneCount; lane++)
            {
                node.Globals[lane] = module.AddGlobal(new WasmGlobalType(type.Lanes[lane], true), ConstOf(type.Lanes[lane], 0));
                exports[lane] = $"{VariableExportPrefix}{node.Id}.{lane}";
                module.Export(exports[lane], WasmExternKind.Global, node.Globals[lane]);
            }
            variables.Add(new ScriptVariableEntry(node.Id, "drive." + node.Id, type, exports));
            string? onExport = null;
            if (shape.Switch >= 0)
            {
                node.SwitchGlobal = module.AddGlobal(new WasmGlobalType(WasmValueType.I32, true), WasmConstInit.I32(1));
                onExport = $"{VariableExportPrefix}{node.Id}.on";
                module.Export(onExport, WasmExternKind.Global, node.SwitchGlobal);
            }
            drives.Add(new ScriptDriveEntry(node.Id, node.SourceBinding, node.Overload.Inputs[shape.Target].Type, type, exports, onExport,
                node.HeardBinding, node.HeardCountBinding));
            driven.Add(node);
        }

        List<string>? driveSources = driven.Count > 0 ? new List<string>() : null;
        bool drivesRead = false;
        foreach (var node in driven)
        {
            var shape = node.Overload.Shape!;
            var stored = new List<ResolvedNode>();
            var seen = new HashSet<ResolvedNode>();
            if (!SettlesOn(node.Inputs[shape.Input], seen, stored, ref drivesRead)
                || (shape.Switch >= 0 && !SettlesOn(node.Inputs[shape.Switch], seen, stored, ref drivesRead)))
            {
                driveSources = null;
                drivesRead = false;
                break;
            }
            foreach (var variable in stored)
            {
                if (!driveSources!.Contains(variable.Id))
                    driveSources.Add(variable.Id);
            }
        }

        if (strings.Count > 0 || urls.Count > 0 || bigs.Count > 0)
        {
            var start = module.AddFunction(WasmFuncType.Empty);
            foreach (var (_, (offset, length, global)) in strings)
                start.I32Const(offset).I32Const(length).Call(stringImport).GlobalSet(global);
            foreach (var (_, (offset, length, global)) in urls)
                start.I32Const(offset).I32Const(length).Call(urlImport).GlobalSet(global);
            foreach (var (global, text) in stringVariables)
                start.GlobalGet(strings[text].Global).GlobalSet(global);
            foreach (var (_, (offset, length, global)) in bigs)
                start.I32Const(offset).I32Const(length).Call(bigImport).GlobalSet(global);
            foreach (var (global, from) in urlVariables)
                start.GlobalGet(from).GlobalSet(global);
            foreach (var (global, from) in bigVariables)
                start.GlobalGet(from).GlobalSet(global);
            module.SetStart(start.Index);
        }

        // Every Function's place in the module first, so a call anywhere, a Function calling itself included,
        // knows what it calls before any body is written. One that waits takes the word of its call last and gives
        // nothing back: its Return hands what it gives to the call's ticket.
        var functions = new List<ScriptFunctionEntry>();
        var bodies = new List<(ResolvedNode Node, WasmFunctionBuilder Function)>();
        foreach (var node in _nodes.Where(n => n.Kind == NodeKind.Function))
        {
            var signature = node.Overload.FunctionSignature!;
            var parameters = signature.Inputs.SelectMany(p => p.Type.Lanes).Append(WasmValueType.I32);
            if (node.IsAsync)
                parameters = parameters.Append(WasmValueType.I32);
            var results = node.IsAsync ? Array.Empty<WasmValueType>() : signature.Outputs.SelectMany(p => p.Type.Lanes).ToArray();
            var function = module.AddFunction(new WasmFuncType(parameters.ToArray(), results));
            node.FunctionIndex = function.Index;
            bodies.Add((node, function));
            var export = FunctionExportPrefix + node.FunctionName;
            module.Export(export, WasmExternKind.Function, function.Index);
            functions.Add(new ScriptFunctionEntry(node.Id, node.FunctionName!, export, signature, node.IsPure, node.IsAsync));
        }

        var events = new List<ScriptEventEntry>();
        foreach (var node in _nodes.Where(n => n.Kind == NodeKind.Event))
        {
            var parameters = node.Overload.Outputs.SelectMany(o => o.Type.Lanes).ToArray();
            var function = module.AddFunction(new WasmFuncType(parameters, Array.Empty<WasmValueType>()));
            new EventFunctionEmitter(function, node, text => strings[text].Global, runtime, Error, direct).Emit();
            var export = EventExportPrefix + node.Id;
            module.Export(export, WasmExternKind.Function, function.Index);
            events.Add(new ScriptEventEntry(node.Id, node.Overload.Definition.Name, export, node.Overload.PayloadType!, node.Overload,
                node.SourceBinding, node.IntervalSeconds));
        }

        var timers = new List<ScriptTimerEntry>();
        foreach (var node in delays)
        {
            var function = module.AddFunction(node.CarriesWord
                ? new WasmFuncType(new[] { WasmValueType.I32 }, Array.Empty<WasmValueType>())
                : WasmFuncType.Empty);
            new EventFunctionEmitter(function, node, text => strings[text].Global, runtime, Error, direct).Emit();
            var export = TimerExportPrefix + node.Id;
            module.Export(export, WasmExternKind.Function, function.Index);
            timers.Add(new ScriptTimerEntry(node.Id, export, node.CarriesWord));
        }

        // Each exit an await's answer can run is a function of its own, with the payload as its parameters,
        // the way an event's is, and an async call's next one with what its Function gave back. Inside a
        // Function's flows the word of its call comes after them.
        var answered = new List<ScriptAwaitEntry>();
        foreach (var node in awaits)
        {
            var parameters = node.Overload.Outputs.SelectMany(o => o.Type.Lanes);
            if (node.CarriesWord)
                parameters = parameters.Append(WasmValueType.I32);
            var exits = new List<string>();
            var exports = new List<string>();
            for (int exit = node.IsAsyncCall ? 0 : 1; exit < (node.IsAsyncCall ? 1 : node.Exits.Length); exit++)
            {
                var function = module.AddFunction(new WasmFuncType(parameters.ToArray(), Array.Empty<WasmValueType>()));
                new EventFunctionEmitter(function, node, text => strings[text].Global, runtime, Error, direct, exit).Emit();
                var name = node.Overload.FlowOutputs[exit];
                var export = $"{AwaitExportPrefix}{node.Id}.{name}";
                module.Export(export, WasmExternKind.Function, function.Index);
                exits.Add(name);
                exports.Add(export);
            }
            answered.Add(new ScriptAwaitEntry(node.Id, exits, exports, node.IsAsyncCall ? typeof(CallAnswer) : node.Overload.PayloadType!,
                node.Overload, node.CarriesWord));
        }

        // A Function's body lowers the way an event's flow does, its arguments in place of a payload.
        foreach (var (node, function) in bodies)
            new EventFunctionEmitter(function, node, text => strings[text].Global, runtime, Error, direct).Emit();

        foreach (var node in free)
        {
            var function = module.AddFunction(WasmFuncType.Empty);
            EventFunctionEmitter.EmitDisplays(function, new[] { node }, text => strings[text].Global, runtime, Error);
            module.Export(displays[shown[node]].Export!, WasmExternKind.Function, function.Index);
        }

        // Every drive in one function, so what several share is worked out once a frame.
        if (driven.Count > 0)
        {
            var function = module.AddFunction(WasmFuncType.Empty);
            EventFunctionEmitter.EmitDisplays(function, driven, text => strings[text].Global, runtime, Error);
            module.Export(DrivesExport, WasmExternKind.Function, function.Index);
        }

        if (HasErrors)
            return CompileResult.Failed(hash, _diagnostics);

        var manifest = ManifestWriter.Write(hash, events, timers, answered, imports, _bindings, variables, displays, functions, scriptCalls);
        module.AddCustomSection(ManifestSection, manifest);
        var bytes = module.ToBytes();

        WasmModule decoded;
        try
        {
            decoded = WasmModule.Decode(bytes, ModuleLimits);
        }
        catch (WasmException ex)
        {
            Error(null, $"internal: the compiler produced a module the engine rejects ({ex.Message})");
            return CompileResult.Failed(hash, _diagnostics);
        }
        decoded.DirectImports = DirectImports(imports);

        var tags = _graph.Nodes.Select(n => n.Id).ToArray();
        return new CompileResult(hash, bytes, manifest, _diagnostics, imports, events, timers, variables, _bindings, tags,
            displays) { Decoded = decoded, Traces = traces, FlowMemory = memory, Awaits = answered, Drives = drives, HandleRoots = _handleRoots.ToArray(), DriveSources = driveSources, DrivesRead = drivesRead, Functions = functions, ScriptCalls = scriptCalls };
    }

    // What native code may call straight: a node import linked to a pure overload over plain numbers, which the
    // linker binds to that same overload's adapter and nothing else, so the straight call is the same work
    // without the host call around it.
    private static Func<WasmImport, WasmFuncType, WasmDirectImport?> DirectImports(IReadOnlyList<NodeOverload> imports)
    {
        var byName = new Dictionary<string, NodeOverload>(StringComparer.Ordinal);
        foreach (var overload in imports)
            byName[overload.ImportName] = overload;
        return (import, _) => import.Module == NodeModule && byName.TryGetValue(import.Name, out var overload)
            && (overload.Folds ?? overload).DirectLanes is { } call
                ? new WasmDirectImport(call, NodeOverload.HostFuel)
                : null;
    }

    // Whether a value can be worked out at any time: from constants, stored values, and pure and read
    // nodes over them, evaluated Functions among them, with no event's data and no action's output anywhere
    // in it. Within a Function's body that Function's arguments count too, since every call brings them.
    private static bool NeedsNoFlow(ResolvedNode node, HashSet<ResolvedNode> seen, ResolvedNode? within = null)
    {
        foreach (var source in node.Inputs)
        {
            if (source?.Node is not { } from || !seen.Add(from))
                continue;
            switch (from.Kind)
            {
                case NodeKind.Function when from == within:
                    break;
                case NodeKind.Event:
                case NodeKind.Action:
                case NodeKind.Flow:
                case NodeKind.Await:
                case NodeKind.Function:
                case NodeKind.CallFunction:
                    return false;
                case NodeKind.Value:
                case NodeKind.Read:
                case NodeKind.Constant:
                case NodeKind.Memory:
                case NodeKind.EvaluateFunction:
                    if (!NeedsNoFlow(from, seen, within))
                        return false;
                    break;
            }
        }
        return true;
    }

    // Whether a value comes from constants and stored values through pure nodes over plain numbers alone, with
    // the stored values it reads. A pure node with a handle in or out is not counted: what a handle names can
    // go away while the lanes stay the same. An evaluated Function counts as what its Return's values come
    // from, its arguments standing for the evaluation's own inputs.
    private static bool SettlesOn(DataSource? source, HashSet<ResolvedNode> seen, List<ResolvedNode> stored, ref bool reads)
    {
        if (source?.Node is not { } from || !seen.Add(from))
            return true;
        switch (from.Kind)
        {
            case NodeKind.Constant:
                break;
            case NodeKind.StoredVariable:
                stored.Add(from);
                return true;
            case NodeKind.Function:
                return true;
            case NodeKind.EvaluateFunction:
                if (from.Function?.PureReturn is not { } body)
                    return false;
                foreach (var input in body.Inputs)
                {
                    if (!SettlesOn(input, seen, stored, ref reads))
                        return false;
                }
                goto case NodeKind.Value;
            case NodeKind.Value:
                foreach (var port in from.Overload.Inputs.Concat(from.Overload.Outputs))
                {
                    if (!port.Type.IsHandle)
                        continue;
                    if (!HoldsStill(port.Type))
                        return false;
                    // Whether it is gone, or what it is called, moves with the tree.
                    if (port.Type.Name is not ("string" or "uri" or "bigint" or "jsontoken" or "jsonobject" or "jsonarray"))
                        reads = true;
                }
                break;
            // A read whose answer moves only with the tree or with the field it reads: the drives then note at
            // run time what they read, and are left as they are while none of it moves.
            case NodeKind.Read when from.Overload.Definition.Evaluation is Evaluation.Fixed or Evaluation.Tree or Evaluation.Field:
                reads = true;
                break;
            default:
                return false;
        }
        foreach (var input in from.Inputs)
        {
            if (!SettlesOn(input, seen, stored, ref reads))
                return false;
        }
        return true;
    }

    // A handle a pure node can take or give without its answer moving under it: text, addresses and big integers
    // never change, a JSON value never changes, and what a pure node can tell of a world object (which one it is,
    // whether it is gone, what it is called) moves only with the tree, which the drives check. Lists and
    // dictionaries change in place, so a pure node over one answers differently with the same handle.
    private static bool HoldsStill(ScriptType type)
        => type.Name is "string" or "uri" or "bigint" or "slot" or "user" or "component" or "anyfield" or "reference"
            or "jsontoken" or "jsonobject" or "jsonarray"
            || type.Name.StartsWith("field<", StringComparison.Ordinal);

    // Whether a Display reads an event's payload, a Function's argument or an action's or call's output
    // directly, through any Constants.
    private static bool FedByFlow(ResolvedNode display)
    {
        var source = display.Inputs[0];
        while (source?.Node is { Kind: NodeKind.Constant } pass)
            source = pass.Inputs[0];
        return source?.Node?.Kind is NodeKind.Event or NodeKind.Action or NodeKind.Flow or NodeKind.Await or NodeKind.Function
            or NodeKind.CallFunction;
    }

    private IEnumerable<ConstantValue> AllConstants()
    {
        foreach (var node in _nodes)
        {
            foreach (var input in node.Inputs)
            {
                if (input?.Constant != null)
                    yield return input.Constant;
            }
            if (node.Initial != null)
                yield return node.Initial;
        }
    }

    private static WasmConstInit ConstOf(WasmValueType lane, ulong raw) => lane switch
    {
        WasmValueType.I64 => WasmConstInit.I64(LaneCodec.ToLong(raw)),
        WasmValueType.F32 => WasmConstInit.F32(LaneCodec.ToFloat(raw)),
        WasmValueType.F64 => WasmConstInit.F64(LaneCodec.ToDouble(raw)),
        _ => WasmConstInit.I32(LaneCodec.ToInt(raw)),
    };
}
