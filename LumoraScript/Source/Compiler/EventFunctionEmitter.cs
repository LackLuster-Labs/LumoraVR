// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Wasm;

namespace Lumora.Script;

// Lowers the flow reachable from one event node into one exported function. A Delay's continuation is
// lowered the same way from the Delay, into a function of its own with no payload: a Delay ends its
// chain by asking the host for a timer, and the host calls the continuation when it is due. An await node
// calls its Start with its number ahead of the inputs and carries on through next; each exit its answer
// can run is lowered from the await into a function of its own whose parameters are the payload, the way
// an event's are, and the await's outputs are readable there and nowhere else.
//
// FLOW that is a tree, every node in it entered by one exit only, compiles to plain structured code: a
// loop node is a real loop, a branch an if, a Sequence one block per step, and every chain that runs out
// branches to what comes after it. Most graphs are trees, and a loop turn then costs a compare, a count
// and its body, nothing more.
//
// Any other flow is a dispatch loop: every flow node gets one or more numbered states, a br_table jumps to
// the current state's code, and each state ends by setting the next state and branching back to the top.
// A chain that runs out (an unwired exit) goes to the pop state, which resumes whatever is on the
// continuation stack in linear memory: a Sequence pushes its next step before running a step, a Repeat
// pushes its own check before running the body. An empty stack ends the call. It is correct for any
// flow graph, and fuel bounds whatever a graph does with it. A loop whose body is a tree still runs as a
// real loop inside its own state.
//
// A SCOPE runs its enter node, its body to its end, its leave node, then what follows: in place in a tree,
// and in the dispatch loop as a state that pushes the leave before the body runs.
//
// DATA is pulled: before a flow node runs, the inputs it needs are computed into locals. Pure and read
// nodes are computed once per flow step however many inputs share them; action outputs, repeat indexes
// and event payloads live in locals for the whole call; stored variables are globals. So a node can
// only read an output its own call produces: another event's payload, or anything from before a
// Delay, is a compile error rather than a silent default.
//
// DISPLAYS fed straight from a call's own data are copied into their globals as the call ends, from
// the locals the flow left; nothing is computed there, so taking them cannot fail the call. The show
// function computes the rest on its own.
//
// TRACE: every step out of a node by a wired exit bumps that exit's counter first, the way it goes
// whichever that is, so the editor can show flow moving along the very wire it took.
//
// A FUNCTION's body lowers the same way from its Function node, its arguments standing for a payload. Its
// last parameter is where its continuation stack starts: a call passes the top of its own, so a body in a
// dispatch loop stacks above its caller's and its pop state ends the call once it is back down to that start.
// A Return works out what it hands back, takes the Displays as a call's end would, and returns; a body that
// runs out without reaching one is a Function that gives nothing (the check refuses it for one that gives
// values), and gives every output's default should one ever get here. A Call Function is a step like an action's, its
// outputs kept for the whole call; an Evaluate Function is worked out like a pure node.
//
// A Function that WAITS (a Delay, an await, or a call of one that waits, anywhere in its body) gives nothing
// back from its wasm function. Its last parameter is the word of its call, a ticket the host keeps: every Delay
// and await its flows arm is armed in that call, the flows that carry on after them get the word back, and
// its Return hands what it gives to the host under the word. A call of it is an async call: it opens the
// ticket, runs the body's first stretch at once, tells the host that stretch is done, and ends the chain like
// a Delay; what follows it runs as an answer of its own with what the Function gave back. After a wait the
// body reads its arguments from what the host kept for the call. -xlinka
internal sealed class EventFunctionEmitter
{
    private enum StateKind : byte { Pop, Run, SequenceStep, RepeatCheck, ScopeLeave }

    private readonly record struct State(StateKind Kind, ResolvedNode? Node, int Step);

    private readonly record struct Location(int[]? Locals, int[]? Globals, ConstantValue? Constant);

    private readonly WasmFunctionBuilder _f;
    private readonly ResolvedNode? _root;
    private readonly IReadOnlyList<ResolvedNode> _displays;
    private readonly Func<string, int> _stringGlobal;
    private readonly RuntimeImports _imports;
    private readonly Action<string?, string> _error;
    private readonly int[][] _payload;

    // For an await's answer: which of its exits this function runs. 0 for anything else.
    private readonly int _answer;

    private readonly List<State> _states = new();
    private readonly Dictionary<ResolvedNode, int> _entry = new();
    private readonly Dictionary<ResolvedNode, int> _repeatCheck = new();
    private readonly Dictionary<ResolvedNode, int> _scopeLeave = new();
    private readonly Dictionary<(ResolvedNode, int), int> _sequenceStep = new();
    private readonly Dictionary<(ResolvedNode, int), int[]> _persistent = new();
    private readonly Dictionary<ResolvedNode, (int Counter, int Limit)> _repeatLocals = new();
    private readonly Dictionary<ResolvedNode, (int First, int Stride, int Wide)> _loopLocals = new();
    private readonly Dictionary<ResolvedNode, int> _scratch = new();
    private readonly Dictionary<ResolvedNode, int[][]> _memo = new();
    private readonly HashSet<ResolvedNode> _computing = new();

    // Nodes compiled in place as structured code, and the loop nodes whose bodies are.
    private readonly HashSet<ResolvedNode> _inline = new();
    private readonly HashSet<ResolvedNode> _structuredLoops = new();

    // How deep structured code may nest before the dispatch loop takes the flow instead: well inside the
    // validator's block depth, and inside the dispatch loop's own blocks for a loop in a state.
    private const int MaxTreeDepth = 256;
    private const int MaxLoopBodyDepth = 64;

    // Blocks, loops and ifs open in the structured code being written, for branch depths.
    private int _nest;

    private int _state;
    private int _sp;
    private int _pop;
    private int _loopDepth;
    private int _exitDepth;

    // Whether the flow runs as a dispatch loop, so _sp is the top of this call's continuation stack.
    private bool _dispatching;

    // A Function's body: the parameter holding where its continuation stack starts. -1 for anything else,
    // whose stack starts at the bottom.
    private readonly int _base = -1;

    // The parameter holding the word of the waiting Function's call these flows run in, or -1 for flows that
    // run in none.
    private readonly int _word = -1;

    // What follows an await's answer or an async call's return, its payload on the parameters.
    private readonly bool _resumes;

    // Root is the event node, the Delay whose continuation this is, the await whose answer exit answer
    // this is, or the Function whose body this is; null for the show function.
    public EventFunctionEmitter(WasmFunctionBuilder function, ResolvedNode? root, Func<string, int> stringGlobal,
        RuntimeImports imports, Action<string?, string> error, IReadOnlyList<ResolvedNode> displays, int answer = 0)
    {
        _f = function;
        _root = root;
        _stringGlobal = stringGlobal;
        _imports = imports;
        _error = error;
        _displays = displays;
        _answer = answer;

        _resumes = root != null && ((root.IsAwait && answer > 0) || root.IsAsyncCall);
        bool carriesPayload = root != null && (root.Kind is NodeKind.Event or NodeKind.Function || _resumes);
        _payload = new int[carriesPayload ? root!.Overload.Outputs.Count : 0][];
        int parameter = 0;
        for (int i = 0; i < _payload.Length; i++)
        {
            var lanes = new int[root!.Overload.Outputs[i].Type.LaneCount];
            for (int l = 0; l < lanes.Length; l++)
                lanes[l] = parameter++;
            _payload[i] = lanes;
        }
        if (root?.Kind == NodeKind.Function)
        {
            _base = parameter++;
            if (root.IsAsync)
                _word = parameter;
        }
        else if (root != null && root.CarriesWord && (root.IsDelay || _resumes))
        {
            _word = parameter;
        }
    }

    // A show function or the drives function: Displays or drives that need no flow, computed and stored, one
    // after another, sharing what they have in common.
    public static void EmitDisplays(WasmFunctionBuilder function, IReadOnlyList<ResolvedNode> displays, Func<string, int> stringGlobal,
        RuntimeImports imports, Action<string?, string> error)
    {
        var emitter = new EventFunctionEmitter(function, null, stringGlobal, imports, error, Array.Empty<ResolvedNode>());
        foreach (var display in displays)
        {
            var value = emitter.Materialize(display, display.Kind == NodeKind.Drive ? display.Overload.Shape!.Input : 0);
            function.Mark(display.Tag);
            emitter.Store(value, display.Globals!);
            if (display.Kind == NodeKind.Drive && display.SwitchGlobal >= 0)
                emitter.Store(emitter.Materialize(display, display.Overload.Shape!.Switch), new[] { display.SwitchGlobal });
        }
    }

    public void Emit()
    {
        var root = _root!;
        int rootExit = root.IsDelay ? root.Overload.Shape!.Exits[0] : root.IsAwait ? _answer : 0;
        ResolvedNode? first = rootExit < root.Exits.Length ? root.Exits[rootExit] : null;

        if (TreeFrom(first, MaxTreeDepth) is { } tree)
        {
            _inline.UnionWith(tree);
            _f.Mark(root.Tag);
            Trace(root, rootExit);
            KeepArguments();
            _nest = 0;
            int end = OpenBlock();
            Chain(first, end);
            Close();
            StoreDisplays();
            ReturnDefaults();
            return;
        }

        _state = _f.AddLocal(WasmValueType.I32);
        _sp = _f.AddLocal(WasmValueType.I32);
        _dispatching = true;

        _pop = AddState(StateKind.Pop, null, 0);
        PlanFrom(first);

        _f.Mark(root.Tag);
        Trace(root, rootExit);
        KeepArguments();
        if (_base >= 0)
            _f.LocalGet(_base).LocalSet(_sp);
        _f.I32Const(Target(first)).LocalSet(_state);

        int count = _states.Count;
        _f.Block();
        _f.Loop();
        for (int i = 0; i < count; i++)
            _f.Block();

        var depths = new int[count];
        for (int i = 0; i < count; i++)
            depths[i] = i;
        _f.LocalGet(_state).BrTable(depths, count + 1);

        for (int i = 0; i < count; i++)
        {
            _f.End();
            _loopDepth = count - 1 - i;
            _exitDepth = count - i;
            _memo.Clear();
            EmitState(_states[i]);
        }

        _f.End();
        _f.End();
        StoreDisplays();
        ReturnDefaults();
    }

    // A Function's body that ran out without reaching a Return gives every output's default, the value an
    // unwired input of that type reads. A waiting one's call ends with the host, once nothing is under way.
    private void ReturnDefaults()
    {
        if (_root?.Kind != NodeKind.Function || _root.IsAsync)
            return;
        foreach (var output in _root.Overload.FunctionSignature!.Outputs)
            Load(new Location(null, null, ConstantValue.Default(output.Type)));
    }

    // A waiting Function's arguments, kept by the host for the flows of its call that run after a wait.
    private void KeepArguments()
    {
        if (_root is not { Kind: NodeKind.Function, KeepImport: >= 0 } root)
            return;
        _f.LocalGet(_word);
        foreach (var lanes in _payload)
        {
            foreach (int lane in lanes)
                _f.LocalGet(lane);
        }
        _f.Call(root.KeepImport);
    }

    // The word of the waiting Function's call these flows run in, 0 for none.
    private void LoadWord()
    {
        if (_word >= 0)
            _f.LocalGet(_word);
        else
            _f.I32Const(0);
    }

    // Tells the host the next Delay or await it arms is armed in the call these flows run in.
    private void PassWord()
    {
        if (_word >= 0)
            _f.LocalGet(_word).Call(_imports.Word);
    }

    // Where a call made from here may start its continuation stack: just above this call's own.
    private void LoadStackTop()
    {
        if (_dispatching)
            _f.LocalGet(_sp);
        else if (_base >= 0)
            _f.LocalGet(_base);
        else
            _f.I32Const(0);
    }

    private void StoreDisplays()
    {
        foreach (var display in _displays)
        {
            if (FlowValue(display) is { } value)
            {
                _f.Mark(display.Tag);
                Store(value, display.Globals!);
            }
        }
    }

    // The flow reachable from entry when it is a tree: every node in it entered by one exit, none of them
    // more than depth constructs deep. What follows a Delay, or an await's answer, belongs to a function of
    // its own and is left out. Null when some node is entered twice, cannot stand in flow, or sits too deep.
    private static HashSet<ResolvedNode>? TreeFrom(ResolvedNode? entry, int maxDepth)
    {
        var tree = new HashSet<ResolvedNode>();
        if (entry == null)
            return tree;
        var pending = new Stack<(ResolvedNode Node, int Depth)>();
        pending.Push((entry, 0));
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (depth > maxDepth || !tree.Add(node)
                || node.Kind is not (NodeKind.Action or NodeKind.WriteVariable or NodeKind.Pulse or NodeKind.Flow or NodeKind.Await
                    or NodeKind.CallFunction or NodeKind.Return))
                return null;
            if (node.IsDelay || node.IsAsyncCall)
                continue;
            if (node.IsAwait)
            {
                if (node.Exits[0] is { } next)
                    pending.Push((next, depth));
                continue;
            }
            int deeper = node.Kind == NodeKind.Flow || node.FailedExit >= 0 ? depth + 2 : depth;
            foreach (var next in node.Exits)
            {
                if (next != null)
                    pending.Push((next, deeper));
            }
        }
        return tree;
    }

    private int OpenBlock()
    {
        _f.Block();
        return _nest++;
    }

    private int OpenLoop()
    {
        _f.Loop();
        return _nest++;
    }

    private void OpenIf()
    {
        _f.If();
        _nest++;
    }

    private void Close()
    {
        _f.End();
        _nest--;
    }

    private void BranchTo(int label) => _f.Br(_nest - 1 - label);

    private static ResolvedNode? ExitOf(ResolvedNode node, int exit) => exit < node.Exits.Length ? node.Exits[exit] : null;

    // Runs node and everything after it in place, then branches to label wherever a chain runs out. Only
    // for a tree, so each node's code sits once, right where it runs. Plain steps follow one another in a
    // loop; only what branches nests.
    private void Chain(ResolvedNode? node, int label)
    {
        while (true)
        {
            if (node == null)
            {
                BranchTo(label);
                return;
            }
            _memo.Clear();
            switch (node.Kind)
            {
                case NodeKind.Action when node.FailedExit >= 0:
                    RunAction(node);
                    _f.Call(_imports.FailCheck);
                    Either(node, node.FailedExit, 0, label);
                    return;
                case NodeKind.Action:
                    RunAction(node);
                    Trace(node, 0);
                    node = ExitOf(node, 0);
                    continue;
                case NodeKind.WriteVariable:
                    RunWrite(node);
                    Trace(node, 0);
                    node = ExitOf(node, 0);
                    continue;
                case NodeKind.Pulse:
                    RunPulse(node);
                    Trace(node, 0);
                    node = ExitOf(node, 0);
                    continue;
                case NodeKind.Await:
                    ArmAwait(node);
                    Trace(node, 0);
                    node = ExitOf(node, 0);
                    continue;
                case NodeKind.CallFunction when node.IsAsyncCall:
                    ArmCall(node);
                    BranchTo(label);
                    return;
                case NodeKind.CallFunction:
                    RunCall(node);
                    Trace(node, 0);
                    node = ExitOf(node, 0);
                    continue;
                case NodeKind.Return:
                    RunReturn(node);
                    return;
            }

            var shape = node.Overload.Shape!;
            switch (shape.Kind)
            {
                case FlowShapeKind.Delay:
                    ArmDelay(node);
                    BranchTo(label);
                    return;
                case FlowShapeKind.Branch:
                {
                    var condition = Materialize(node, shape.Input);
                    _f.Mark(node.Tag);
                    Load(condition);
                    Either(node, shape.Exits[0], shape.Exits[1], label);
                    return;
                }
                case FlowShapeKind.Toggle:
                {
                    int flag = node.Globals[0];
                    var state = Persistent(node, shape.Output);
                    _f.Mark(node.Tag);
                    _f.GlobalGet(flag).Op(WasmOp.I32Eqz).LocalTee(state[0]).GlobalSet(flag);
                    _f.LocalGet(state[0]);
                    Either(node, shape.Exits[0], shape.Exits[1], label);
                    return;
                }
                case FlowShapeKind.Once:
                {
                    int flag = node.Globals[0];
                    _f.Mark(node.Tag);
                    _f.GlobalGet(flag).Op(WasmOp.I32Eqz);
                    OpenIf();
                    _f.I32Const(1).GlobalSet(flag);
                    Trace(node, shape.Exits[0]);
                    Chain(node.Exits[shape.Exits[0]], label);
                    Close();
                    BranchTo(label);
                    return;
                }
                case FlowShapeKind.Cooldown:
                {
                    var seconds = Materialize(node, shape.Input);
                    int last = node.Globals[0];
                    int now = Scratch(node, WasmValueType.F64);
                    _f.Mark(node.Tag);
                    _f.Call(_imports.Now).LocalSet(now);
                    _f.LocalGet(now).GlobalGet(last).Op(WasmOp.F64Sub);
                    Load(seconds);
                    _f.Op(WasmOp.F64PromoteF32).Op(WasmOp.F64Ge);
                    OpenIf();
                    _f.LocalGet(now).GlobalSet(last);
                    Trace(node, shape.Exits[0]);
                    Chain(node.Exits[shape.Exits[0]], label);
                    _f.Else();
                    Trace(node, shape.Exits[1]);
                    Chain(node.Exits[shape.Exits[1]], label);
                    Close();
                    return;
                }
                case FlowShapeKind.Scope:
                {
                    Enter(node);
                    int after = OpenBlock();
                    Trace(node, shape.Exits[0]);
                    Chain(node.Exits[shape.Exits[0]], after);
                    Close();
                    Leave(node);
                    Trace(node, shape.Exits[1]);
                    node = node.Exits[shape.Exits[1]];
                    continue;
                }
                case FlowShapeKind.Change:
                    RunChange(node);
                    Trace(node, shape.Exits[0]);
                    node = node.Exits[shape.Exits[0]];
                    continue;
                case FlowShapeKind.Watch:
                    _f.LocalGet(RunWatch(node));
                    Either(node, shape.Exits[0], shape.Exits[1], label);
                    return;
                case FlowShapeKind.Sequence:
                {
                    _f.Mark(node.Tag);
                    var exits = shape.Exits;
                    for (int step = 0; step < exits.Length - 1; step++)
                    {
                        int next = OpenBlock();
                        Trace(node, exits[step]);
                        Chain(node.Exits[exits[step]], next);
                        Close();
                    }
                    Trace(node, exits[^1]);
                    node = node.Exits[exits[^1]];
                    continue;
                }
                default:
                    Loop(node);
                    Trace(node, shape.Exits[1]);
                    node = node.Exits[shape.Exits[1]];
                    continue;
            }
        }
    }

    // The condition is on the stack: the first exit when it is set, else the second, each run to its end.
    private void Either(ResolvedNode node, int first, int second, int label)
    {
        OpenIf();
        Trace(node, first);
        Chain(node.Exits[first], label);
        _f.Else();
        Trace(node, second);
        Chain(node.Exits[second], label);
        Close();
    }

    // A For, While or For Each as a real loop whose body is a tree: each turn runs the body to its end and
    // comes round again. Falls out of the bottom once it is done; the caller runs what comes after.
    private void Loop(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        int body = shape.Exits[0];
        if (shape.Kind == FlowShapeKind.While)
        {
            _f.Mark(node.Tag);
            int whileDone = OpenBlock();
            int whileTurn = OpenLoop();
            _memo.Clear();
            var condition = Materialize(node, shape.Input);
            _f.Mark(node.Tag);
            Load(condition);
            _f.Op(WasmOp.I32Eqz).BrIf(_nest - 1 - whileDone);
            Trace(node, body);
            Chain(node.Exits[body], whileTurn);
            Close();
            Close();
            _memo.Clear();
            return;
        }

        var (counter, limit) = RepeatLocals(node);
        EnterCounted(node);
        int done = OpenBlock();
        int turn = OpenLoop();
        _f.LocalGet(counter).LocalGet(limit).Op(WasmOp.I32GeS).BrIf(_nest - 1 - done);
        Turn(node);
        Trace(node, body);
        Chain(node.Exits[body], turn);
        Close();
        Close();
        _memo.Clear();
    }

    // A counted loop's entry: its limit, and whatever each turn works its index or item out from. The
    // counter starts at 0; Turn moves it on.
    private void EnterCounted(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        var (counter, limit) = RepeatLocals(node);
        switch (shape.Kind)
        {
            case FlowShapeKind.Each:
            {
                int of = Scratch(node, WasmValueType.I32);
                EachSourceOf(node, shape);
                _f.LocalSet(of);
                _f.I32Const((int)shape.Source).LocalGet(of).Call(_imports.EachCount).LocalSet(limit);
                break;
            }
            case FlowShapeKind.ListEach:
            {
                int of = Scratch(node, WasmValueType.I32);
                var list = Materialize(node, shape.Input);
                _f.Mark(node.Tag);
                Load(list);
                _f.LocalSet(of);
                _f.LocalGet(of).Call(node.HelperImports[0]).LocalSet(limit);
                break;
            }
            case FlowShapeKind.Range:
            {
                var start = Materialize(node, shape.Start);
                var end = Materialize(node, shape.End);
                var step = Materialize(node, shape.Step);
                var (first, stride, wide) = LoopLocals(node);
                _f.Mark(node.Tag);
                Load(start);
                _f.LocalSet(first);
                Load(step);
                _f.LocalSet(stride);
                // Turns: (end - start) / step + 1 when that is not negative, worked in 64 bits so no
                // difference of two ints overflows; none for a step of 0.
                _f.I32Const(0).LocalSet(limit);
                _f.LocalGet(stride);
                _f.If();
                Load(end);
                _f.Op(WasmOp.I64ExtendI32S).LocalGet(first).Op(WasmOp.I64ExtendI32S).Op(WasmOp.I64Sub);
                _f.LocalGet(stride).Op(WasmOp.I64ExtendI32S).Op(WasmOp.I64DivS).LocalSet(wide);
                _f.LocalGet(wide).I64Const(0).Op(WasmOp.I64GeS);
                _f.If();
                _f.LocalGet(wide).I64Const(int.MaxValue - 1).Op(WasmOp.I64GtS);
                _f.If().I64Const(int.MaxValue - 1).LocalSet(wide).End();
                _f.LocalGet(wide).Op(WasmOp.I32WrapI64).I32Const(1).Op(WasmOp.I32Add).LocalSet(limit);
                _f.End();
                _f.End();
                break;
            }
            default:
            {
                var count = Materialize(node, shape.Input);
                Location? reverse = Reversing(node) ? Materialize(node, shape.Reverse) : null;
                _f.Mark(node.Tag);
                Load(count);
                _f.LocalSet(limit);
                if (reverse is { } flag)
                {
                    Load(flag);
                    _f.LocalSet(LoopLocals(node).First);
                }
                break;
            }
        }
        _f.I32Const(0).LocalSet(counter);
    }

    // One turn of a counted loop, once the counter is known to be under the limit: the index and item
    // from the counter, then the counter moved on.
    private void Turn(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        var (counter, limit) = RepeatLocals(node);
        var index = Persistent(node, shape.Output);
        switch (shape.Kind)
        {
            case FlowShapeKind.Range:
            {
                var (first, stride, _) = LoopLocals(node);
                _f.LocalGet(first).LocalGet(counter).LocalGet(stride).Op(WasmOp.I32Mul).Op(WasmOp.I32Add).LocalSet(index[0]);
                break;
            }
            case FlowShapeKind.Repeat when Reversing(node):
                _f.LocalGet(limit).I32Const(1).Op(WasmOp.I32Sub).LocalGet(counter).Op(WasmOp.I32Sub);
                _f.LocalGet(counter).LocalGet(LoopLocals(node).First).Select().LocalSet(index[0]);
                break;
            default:
                _f.LocalGet(counter).LocalSet(index[0]);
                break;
        }
        if (shape.Kind == FlowShapeKind.Each)
        {
            var item = Persistent(node, shape.Item);
            _f.I32Const((int)shape.Source).LocalGet(Scratch(node, WasmValueType.I32)).LocalGet(counter).Call(_imports.EachItem)
                .LocalSet(item[0]);
        }
        else if (shape.Kind == FlowShapeKind.ListEach)
        {
            var item = Persistent(node, shape.Item);
            _f.LocalGet(Scratch(node, WasmValueType.I32)).LocalGet(counter).Call(node.HelperImports[1]);
            for (int lane = item.Length - 1; lane >= 0; lane--)
                _f.LocalSet(item[lane]);
        }
        _f.LocalGet(counter).I32Const(1).Op(WasmOp.I32Add).LocalSet(counter);
    }

    // A For counts down only when its reverse input can be on: wired, or set on.
    private static bool Reversing(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        if (shape.Kind != FlowShapeKind.Repeat || shape.Reverse < 0)
            return false;
        var source = node.Inputs[shape.Reverse];
        return source != null && (source.Constant == null || source.Constant.Value is true);
    }

    // A range loop's start, step and 64-bit turn count; a reversible For keeps its flag in First.
    private (int First, int Stride, int Wide) LoopLocals(ResolvedNode node)
    {
        if (!_loopLocals.TryGetValue(node, out var locals))
        {
            locals = (_f.AddLocal(WasmValueType.I32), _f.AddLocal(WasmValueType.I32), _f.AddLocal(WasmValueType.I64));
            _loopLocals[node] = locals;
        }
        return locals;
    }

    // What a For Each walks, on the stack: a search's result, the object it lists the children of, or
    // nothing for the session's people.
    private void EachSourceOf(ResolvedNode node, FlowShape shape)
    {
        if (shape.Query.Length > 0)
        {
            var query = new Location[shape.Query.Length];
            for (int i = 0; i < query.Length; i++)
                query[i] = Materialize(node, shape.Query[i]);
            _f.Mark(node.Tag);
            _f.I32Const((int)shape.Source);
            int lanes = 0;
            for (int i = 0; i < query.Length; i++)
            {
                Load(query[i]);
                lanes += node.Overload.Inputs[shape.Query[i]].Type.LaneCount;
            }
            for (; lanes < FlowBuilder.MaxQueryLanes; lanes++)
                _f.F32Const(0f);
            _f.Call(_imports.EachQuery);
        }
        else if (shape.Input >= 0)
        {
            var owner = Materialize(node, shape.Input);
            _f.Mark(node.Tag);
            Load(owner);
        }
        else
        {
            _f.Mark(node.Tag);
            _f.I32Const(0);
        }
    }

    private void RunAction(ResolvedNode node)
    {
        var inputs = MaterializeAll(node);
        _f.Mark(node.Tag);
        if (node.FailedExit >= 0)
            _f.Call(_imports.FailClear);
        foreach (var input in inputs)
            Load(input);
        _f.Call(node.ImportIndex);
        for (int o = node.Overload.Outputs.Count - 1; o >= 0; o--)
        {
            var locals = Persistent(node, o);
            for (int lane = locals.Length - 1; lane >= 0; lane--)
                _f.LocalSet(locals[lane]);
        }
    }

    // A Call Function: its arguments and where its stack may start, then what it gives back kept for the
    // whole call, the way an action's outputs are.
    private void RunCall(ResolvedNode node)
    {
        var inputs = MaterializeAll(node);
        _f.Mark(node.Tag);
        foreach (var input in inputs)
            Load(input);
        LoadStackTop();
        _f.Call(node.Function!.FunctionIndex);
        for (int o = node.Overload.Outputs.Count - 1; o >= 0; o--)
        {
            var locals = Persistent(node, o);
            for (int lane = locals.Length - 1; lane >= 0; lane--)
                _f.LocalSet(locals[lane]);
        }
    }

    // A Return: what it hands back worked out, the Displays taken as a call's end takes them, and the call over.
    // A waiting Function's Return hands it to the host for the call's ticket.
    private void RunReturn(ResolvedNode node)
    {
        var inputs = MaterializeAll(node);
        StoreDisplays();
        _f.Mark(node.Tag);
        if (node.Function is { IsAsync: true } function)
        {
            if (_word < 0)
            {
                _error(node.Id, $"returns from {function.FunctionName} in a flow that is not its call's");
                return;
            }
            _f.LocalGet(_word);
            foreach (var input in inputs)
                Load(input);
            _f.Call(function.DoneImport);
        }
        else
        {
            foreach (var input in inputs)
                Load(input);
        }
        _f.Return();
    }

    // An async call: its ticket opened in the call these flows run in, the Function's first stretch run at once
    // with the ticket's word, then the host told that stretch is done. What follows runs when it returns.
    private void ArmCall(ResolvedNode node)
    {
        var inputs = MaterializeAll(node);
        int word = Scratch(node, WasmValueType.I32);
        _f.Mark(node.Tag);
        _f.I32Const(node.AwaitIndex);
        LoadWord();
        _f.Call(_imports.CallOpen).LocalSet(word);
        foreach (var input in inputs)
            Load(input);
        LoadStackTop();
        _f.LocalGet(word);
        _f.Call(node.Function!.FunctionIndex);
        _f.LocalGet(word).Call(_imports.CallEnd);
    }

    // A Change: this value against the one kept from last time, through the helper into the output, and
    // then this value kept in its place. The first time nothing is kept yet, so this value is kept first
    // and the change is none.
    private void RunChange(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        var value = Materialize(node, shape.Input);
        var change = Persistent(node, shape.Output);
        int ran = node.Globals[0];
        var kept = node.Globals[1..];
        var before = new Location(null, kept, null);
        var helper = node.Helpers[0];
        var (first, second) = shape.PreviousFirst ? (before, value) : (value, before);

        _f.Mark(node.Tag);
        _f.GlobalGet(ran).Op(WasmOp.I32Eqz);
        _f.If();
        Store(value, kept);
        _f.I32Const(1).GlobalSet(ran);
        _f.End();

        if (helper.InlineOpcode is byte opcode)
        {
            for (int lane = 0; lane < change.Length; lane++)
            {
                LoadLane(first, lane);
                LoadLane(second, lane);
                _f.Op(opcode).LocalSet(change[lane]);
            }
        }
        else
        {
            Load(first);
            Load(second);
            _f.Call(node.HelperImports[0]);
            for (int lane = change.Length - 1; lane >= 0; lane--)
                _f.LocalSet(change[lane]);
        }
        Store(value, kept);
    }

    // A Watch: previous is what was kept last time, changed whether any lane of this value differs from it,
    // and then this value is kept in its place. The first time nothing is kept yet, so this value is kept
    // first and nothing has changed. Answers the local holding changed.
    private int RunWatch(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        var value = Materialize(node, shape.Input);
        if (node.Overload.Inputs[shape.Input].Type.IsHandle)
        {
            // A thing that has left the world is watched as nothing.
            int live = _f.AddLocal(WasmValueType.I32);
            LoadLane(value, 0);
            _f.Call(_imports.Live).LocalSet(live);
            value = new Location(new[] { live }, null, null);
        }
        var previous = Persistent(node, shape.Output);
        int ran = node.Globals[0];
        var kept = node.Globals[1..];
        var lanes = node.Overload.Inputs[shape.Input].Type.Lanes;
        int changed = Scratch(node, WasmValueType.I32);

        _f.Mark(node.Tag);
        _f.GlobalGet(ran).Op(WasmOp.I32Eqz);
        _f.If();
        Store(value, kept);
        _f.I32Const(1).GlobalSet(ran);
        _f.End();

        _f.I32Const(0);
        for (int lane = 0; lane < kept.Length; lane++)
        {
            _f.GlobalGet(kept[lane]).LocalSet(previous[lane]);
            LoadLane(value, lane);
            _f.GlobalGet(kept[lane]);
            _f.Op(lanes[lane] switch
            {
                WasmValueType.I64 => WasmOp.I64Ne,
                WasmValueType.F32 => WasmOp.F32Ne,
                WasmValueType.F64 => WasmOp.F64Ne,
                _ => WasmOp.I32Ne,
            });
            _f.Op(WasmOp.I32Or);
        }
        _f.LocalSet(changed);
        Store(value, kept);
        return changed;
    }

    // A Follow: where it is moves toward the value through the helper by the seconds since it last moved,
    // once a frame however often it is worked out; in two stages a middle moves toward the value first and
    // where it is follows the middle. The first time it starts where the value is.
    private void EmitFollow(ResolvedNode node, Location[] inputs, int[][] outputs)
    {
        var shape = node.Overload.Shape!;
        var value = inputs[shape.Input];
        var speed = inputs[shape.Speed];
        int lanes = node.Overload.Inputs[shape.Input].Type.LaneCount;
        int ran = node.Globals[0];
        var at = node.Globals[1..(1 + lanes)];
        int moved = node.Globals[1 + lanes];
        var middle = shape.Stages == 2 ? node.Globals[(2 + lanes)..] : null;
        var helper = node.Helpers[0];
        int now = _f.AddLocal(WasmValueType.F64);

        _f.Mark(node.Tag);
        _f.Call(_imports.Now).LocalSet(now);
        _f.GlobalGet(ran).Op(WasmOp.I32Eqz);
        _f.If();
        Store(value, at);
        if (middle != null)
            Store(value, middle);
        _f.LocalGet(now).GlobalSet(moved);
        _f.I32Const(1).GlobalSet(ran);
        _f.End();

        _f.LocalGet(now).GlobalGet(moved).Op(WasmOp.F64Ne);
        _f.If();
        if (middle != null)
        {
            Step(new Location(null, middle, null), value, middle);
            Step(new Location(null, at, null), new Location(null, middle, null), at);
        }
        else
        {
            Step(new Location(null, at, null), value, at);
        }
        _f.LocalGet(now).GlobalSet(moved);
        _f.End();

        void Step(Location from, Location toward, int[] into)
        {
            Load(from);
            Load(toward);
            Load(speed);
            if (shape.SpeedScale != 1f)
                _f.F32Const(shape.SpeedScale).Op(WasmOp.F32Mul);
            _f.LocalGet(now).GlobalGet(moved).Op(WasmOp.F64Sub).Op(WasmOp.F32DemoteF64);
            _f.Call(node.HelperImports[0]);
            PopInto(helper, into);
        }

        var result = outputs[shape.Output];
        for (int lane = 0; lane < lanes; lane++)
            _f.GlobalGet(at[lane]).LocalSet(result[lane]);
    }

    // A Frame Delta: the change since the value last frame, through the helper the way a Change works it
    // out, worked out once a frame and given again for the rest of it. The first time the value is kept
    // first, so the change is none.
    private void EmitFrameDelta(ResolvedNode node, Location[] inputs, int[][] outputs)
    {
        var shape = node.Overload.Shape!;
        var value = inputs[shape.Input];
        int lanes = node.Overload.Inputs[shape.Input].Type.LaneCount;
        int ran = node.Globals[0];
        var last = node.Globals[1..(1 + lanes)];
        int seen = node.Globals[1 + lanes];
        var change = node.Globals[(2 + lanes)..];
        var helper = node.Helpers[0];
        var before = new Location(null, last, null);
        var (first, second) = shape.PreviousFirst ? (before, value) : (value, before);
        int now = _f.AddLocal(WasmValueType.F64);

        _f.Mark(node.Tag);
        _f.Call(_imports.Now).LocalSet(now);
        _f.GlobalGet(ran).Op(WasmOp.I32Eqz);
        _f.If();
        Store(value, last);
        _f.I32Const(1).GlobalSet(ran);
        _f.End();

        _f.LocalGet(now).GlobalGet(seen).Op(WasmOp.F64Ne);
        _f.If();
        if (helper.InlineOpcode is byte opcode)
        {
            for (int lane = 0; lane < lanes; lane++)
            {
                LoadLane(first, lane);
                LoadLane(second, lane);
                _f.Op(opcode).GlobalSet(change[lane]);
            }
        }
        else
        {
            Load(first);
            Load(second);
            _f.Call(node.HelperImports[0]);
            PopInto(helper, change);
        }
        Store(value, last);
        _f.LocalGet(now).GlobalSet(seen);
        _f.End();

        var result = outputs[shape.Output];
        for (int lane = 0; lane < lanes; lane++)
            _f.GlobalGet(change[lane]).LocalSet(result[lane]);
    }

    // A helper's results off the stack: its first output into globals, anything after it dropped.
    private void PopInto(NodeOverload helper, int[] globals)
    {
        for (int o = helper.Outputs.Count - 1; o >= 0; o--)
        {
            var type = helper.Outputs[o].Type;
            for (int lane = type.LaneCount - 1; lane >= 0; lane--)
            {
                if (o == 0)
                    _f.GlobalSet(globals[lane]);
                else
                    _f.Op(WasmOp.Drop);
            }
        }
    }

    // A scope's opening, its enter node called with the scope's own inputs, and its closing.
    private void Enter(ResolvedNode node)
    {
        var inputs = MaterializeAll(node);
        _f.Mark(node.Tag);
        foreach (var input in inputs)
            Load(input);
        _f.Call(node.HelperImports[0]);
    }

    private void Leave(ResolvedNode node)
    {
        _f.Mark(node.Tag);
        _f.Call(node.HelperImports[1]);
    }

    private void RunWrite(ResolvedNode node)
    {
        var value = Materialize(node, 0);
        _f.Mark(node.Tag);
        Load(value);
        var globals = node.Variable!.Globals;
        for (int lane = globals.Length - 1; lane >= 0; lane--)
            _f.GlobalSet(globals[lane]);
    }

    private void RunPulse(ResolvedNode node)
    {
        int counter = node.Globals![0];
        _f.Mark(node.Tag);
        _f.GlobalGet(counter).I32Const(1).Op(WasmOp.I32Add).GlobalSet(counter);
    }

    // Starts an await's work: its number, then its inputs, to its Start. A call to another script's Function
    // passes the word of the flow it runs in between the two.
    private void ArmAwait(ResolvedNode node)
    {
        var inputs = MaterializeAll(node);
        _f.Mark(node.Tag);
        if (!node.Overload.Definition.IsScriptCall)
            PassWord();
        _f.I32Const(node.AwaitIndex);
        if (node.Overload.Definition.IsScriptCall)
            LoadWord();
        foreach (var input in inputs)
            Load(input);
        _f.Call(node.ImportIndex);
    }

    private void ArmDelay(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        var seconds = Materialize(node, shape.Input);
        _f.Mark(node.Tag);
        PassWord();
        _f.I32Const(node.TimerIndex);
        Load(seconds);
        _f.Call(_imports.Timer);
    }

    // A Display's value as this call's flow left it: this event's payload, or the output of an action or
    // flow node this call plans, through any Constants between. Null when it comes from somewhere else.
    private Location? FlowValue(ResolvedNode display)
    {
        var source = display.Inputs[0];
        while (source?.Node is { Kind: NodeKind.Constant } pass)
            source = pass.Inputs[0];
        if (source?.Node is not { } from)
            return null;
        if (from.Kind == NodeKind.Event)
            return from == _root ? new Location(_payload[source.Output], null, null) : null;
        if (from.Kind == NodeKind.Await)
            return from == _root && _answer > 0 ? new Location(_payload[source.Output], null, null) : null;
        if (from.Kind == NodeKind.Function || from.IsAsyncCall)
            return from == _root ? new Location(_payload[source.Output], null, null) : null;
        if (from.Kind is NodeKind.Action or NodeKind.Flow or NodeKind.CallFunction && (_entry.ContainsKey(from) || _inline.Contains(from)))
            return new Location(Persistent(from, source.Output), null, null);
        return null;
    }

    private void Store(Location value, int[] globals)
    {
        Load(value);
        for (int lane = globals.Length - 1; lane >= 0; lane--)
            _f.GlobalSet(globals[lane]);
    }

    private int AddState(StateKind kind, ResolvedNode? node, int step)
    {
        _states.Add(new State(kind, node, step));
        return _states.Count - 1;
    }

    private void PlanFrom(ResolvedNode? first)
    {
        var queue = new Queue<ResolvedNode>();
        if (first != null)
            queue.Enqueue(first);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (_entry.ContainsKey(node))
                continue;

            switch (node.Kind)
            {
                case NodeKind.CallFunction when node.IsAsyncCall:
                    // What follows an async call is its answer's business.
                    _entry[node] = AddState(StateKind.Run, node, 0);
                    continue;
                case NodeKind.Action:
                case NodeKind.WriteVariable:
                case NodeKind.Pulse:
                case NodeKind.CallFunction:
                case NodeKind.Return:
                    _entry[node] = AddState(StateKind.Run, node, 0);
                    break;
                case NodeKind.Flow when node.Overload.Shape!.Kind == FlowShapeKind.Sequence:
                    for (int i = 0; i < node.Overload.Shape.Exits.Length; i++)
                        _sequenceStep[(node, i)] = AddState(StateKind.SequenceStep, node, i);
                    _entry[node] = _sequenceStep[(node, 0)];
                    break;
                case NodeKind.Flow when node.IsLoop:
                    _entry[node] = AddState(StateKind.Run, node, 0);
                    if (TreeFrom(node.Exits[node.Overload.Shape!.Exits[0]], MaxLoopBodyDepth) is { } body)
                    {
                        _structuredLoops.Add(node);
                        _inline.UnionWith(body);
                        if (node.Exits[node.Overload.Shape.Exits[1]] is { } completed)
                            queue.Enqueue(completed);
                        continue;
                    }
                    _repeatCheck[node] = AddState(StateKind.RepeatCheck, node, 0);
                    break;
                case NodeKind.Flow when node.Overload.Shape!.Kind == FlowShapeKind.Scope:
                    _entry[node] = AddState(StateKind.Run, node, 0);
                    _scopeLeave[node] = AddState(StateKind.ScopeLeave, node, 0);
                    break;
                case NodeKind.Flow when node.IsDelay:
                    // What follows a Delay is its own function's business.
                    _entry[node] = AddState(StateKind.Run, node, 0);
                    continue;
                case NodeKind.Await:
                    // So is what follows an await's answer; next carries on here.
                    _entry[node] = AddState(StateKind.Run, node, 0);
                    if (node.Exits[0] is { } next)
                        queue.Enqueue(next);
                    continue;
                case NodeKind.Flow:
                    _entry[node] = AddState(StateKind.Run, node, 0);
                    break;
                default:
                    _error(node.Id, "only action, flow and variable-write nodes can be wired into flow");
                    _entry[node] = _pop;
                    continue;
            }

            foreach (var exit in node.Exits)
            {
                if (exit != null)
                    queue.Enqueue(exit);
            }
        }
    }

    private int Target(ResolvedNode? node) => node != null && _entry.TryGetValue(node, out int state) ? state : _pop;

    private void EmitState(State state)
    {
        switch (state.Kind)
        {
            case StateKind.Pop:
                // Back down to where this call's stack started: the call is done.
                if (_base >= 0)
                    _f.LocalGet(_sp).LocalGet(_base).Op(WasmOp.I32Eq).BrIf(_exitDepth);
                else
                    _f.LocalGet(_sp).Op(WasmOp.I32Eqz).BrIf(_exitDepth);
                _f.LocalGet(_sp).I32Const(4).Op(WasmOp.I32Sub).LocalTee(_sp);
                _f.Memory(WasmOp.I32Load, 2, 0).LocalSet(_state);
                _f.Br(_loopDepth);
                break;

            case StateKind.SequenceStep:
            {
                var node = state.Node!;
                _f.Mark(node.Tag);
                var exits = node.Overload.Shape!.Exits;
                if (state.Step + 1 < exits.Length)
                    Push(_sequenceStep[(node, state.Step + 1)]);
                Trace(node, exits[state.Step]);
                Goto(node.Exits[exits[state.Step]], 0);
                break;
            }

            case StateKind.RepeatCheck:
                EmitRepeatCheck(state.Node!);
                break;

            case StateKind.ScopeLeave:
            {
                var node = state.Node!;
                var exits = node.Overload.Shape!.Exits;
                Leave(node);
                Trace(node, exits[1]);
                Goto(node.Exits[exits[1]], 0);
                break;
            }

            default:
                EmitRun(state.Node!);
                break;
        }
    }

    private void EmitRun(ResolvedNode node)
    {
        switch (node.Kind)
        {
            case NodeKind.Action when node.FailedExit >= 0:
            {
                RunAction(node);
                int failed = Scratch(node, WasmValueType.I32);
                _f.Call(_imports.FailCheck).LocalSet(failed);
                _f.I32Const(Target(node.Exits[node.FailedExit]));
                _f.I32Const(Target(ExitOf(node, 0)));
                _f.LocalGet(failed);
                _f.Select().LocalSet(_state);
                TraceEither(node, node.FailedExit, 0, failed);
                _f.Br(_loopDepth);
                break;
            }

            case NodeKind.Action:
                RunAction(node);
                Trace(node, 0);
                Goto(ExitOf(node, 0), 0);
                break;

            case NodeKind.WriteVariable:
                RunWrite(node);
                Trace(node, 0);
                Goto(ExitOf(node, 0), 0);
                break;

            case NodeKind.Pulse:
                RunPulse(node);
                Trace(node, 0);
                Goto(ExitOf(node, 0), 0);
                break;

            case NodeKind.Await:
                ArmAwait(node);
                Trace(node, 0);
                Goto(ExitOf(node, 0), 0);
                break;

            case NodeKind.CallFunction when node.IsAsyncCall:
                ArmCall(node);
                Goto(null, 0);
                break;

            case NodeKind.CallFunction:
                RunCall(node);
                Trace(node, 0);
                Goto(ExitOf(node, 0), 0);
                break;

            case NodeKind.Return:
                RunReturn(node);
                break;

            default:
            {
                var shape = node.Overload.Shape!;
                if (_structuredLoops.Contains(node))
                {
                    _nest = 0;
                    Loop(node);
                    Trace(node, shape.Exits[1]);
                    Goto(node.Exits[shape.Exits[1]], 0);
                }
                else if (shape.Kind == FlowShapeKind.Delay)
                {
                    ArmDelay(node);
                    Goto(null, 0);
                }
                else if (shape.Kind == FlowShapeKind.Scope)
                {
                    Enter(node);
                    Push(_scopeLeave[node]);
                    Trace(node, shape.Exits[0]);
                    Goto(node.Exits[shape.Exits[0]], 0);
                }
                else if (shape.Kind == FlowShapeKind.Branch)
                {
                    var condition = Materialize(node, shape.Input);
                    int taken = Scratch(node, WasmValueType.I32);
                    _f.Mark(node.Tag);
                    Load(condition);
                    _f.LocalSet(taken);
                    _f.I32Const(Target(node.Exits[shape.Exits[0]]));
                    _f.I32Const(Target(node.Exits[shape.Exits[1]]));
                    _f.LocalGet(taken);
                    _f.Select().LocalSet(_state);
                    TraceEither(node, shape.Exits[0], shape.Exits[1], taken);
                    _f.Br(_loopDepth);
                }
                else if (shape.Kind == FlowShapeKind.While)
                {
                    _f.Mark(node.Tag);
                    _f.I32Const(_repeatCheck[node]).LocalSet(_state);
                    _f.Br(_loopDepth);
                }
                else if (shape.Kind is FlowShapeKind.Each or FlowShapeKind.ListEach or FlowShapeKind.Range)
                {
                    EnterCounted(node);
                    _f.I32Const(_repeatCheck[node]).LocalSet(_state);
                    _f.Br(_loopDepth);
                }
                else if (shape.Kind == FlowShapeKind.Once)
                {
                    int flag = node.Globals[0];
                    _f.Mark(node.Tag);
                    _f.GlobalGet(flag).Op(WasmOp.I32Eqz);
                    _f.If();
                    _f.I32Const(1).GlobalSet(flag);
                    Trace(node, shape.Exits[0]);
                    Goto(node.Exits[shape.Exits[0]], 1);
                    _f.End();
                    Goto(null, 0);
                }
                else if (shape.Kind == FlowShapeKind.Toggle)
                {
                    int flag = node.Globals[0];
                    var state = Persistent(node, shape.Output);
                    _f.Mark(node.Tag);
                    _f.GlobalGet(flag).Op(WasmOp.I32Eqz).LocalTee(state[0]).GlobalSet(flag);
                    _f.I32Const(Target(node.Exits[shape.Exits[0]]));
                    _f.I32Const(Target(node.Exits[shape.Exits[1]]));
                    _f.LocalGet(state[0]);
                    _f.Select().LocalSet(_state);
                    TraceEither(node, shape.Exits[0], shape.Exits[1], state[0]);
                    _f.Br(_loopDepth);
                }
                else if (shape.Kind == FlowShapeKind.Cooldown)
                {
                    var seconds = Materialize(node, shape.Input);
                    int last = node.Globals[0];
                    int now = Scratch(node, WasmValueType.F64);
                    _f.Mark(node.Tag);
                    _f.Call(_imports.Now).LocalSet(now);
                    _f.LocalGet(now).GlobalGet(last).Op(WasmOp.F64Sub);
                    Load(seconds);
                    _f.Op(WasmOp.F64PromoteF32).Op(WasmOp.F64Ge);
                    _f.If();
                    _f.LocalGet(now).GlobalSet(last);
                    Trace(node, shape.Exits[0]);
                    Goto(node.Exits[shape.Exits[0]], 1);
                    _f.End();
                    Trace(node, shape.Exits[1]);
                    Goto(node.Exits[shape.Exits[1]], 0);
                }
                else if (shape.Kind == FlowShapeKind.Change)
                {
                    RunChange(node);
                    Trace(node, shape.Exits[0]);
                    Goto(node.Exits[shape.Exits[0]], 0);
                }
                else if (shape.Kind == FlowShapeKind.Watch)
                {
                    int changed = RunWatch(node);
                    _f.I32Const(Target(node.Exits[shape.Exits[0]]));
                    _f.I32Const(Target(node.Exits[shape.Exits[1]]));
                    _f.LocalGet(changed);
                    _f.Select().LocalSet(_state);
                    TraceEither(node, shape.Exits[0], shape.Exits[1], changed);
                    _f.Br(_loopDepth);
                }
                else
                {
                    EnterCounted(node);
                    _f.I32Const(_repeatCheck[node]).LocalSet(_state);
                    _f.Br(_loopDepth);
                }
                break;
            }
        }
    }

    private void EmitRepeatCheck(ResolvedNode node)
    {
        var shape = node.Overload.Shape!;
        if (shape.Kind == FlowShapeKind.While)
        {
            var condition = Materialize(node, shape.Input);
            _f.Mark(node.Tag);
            Load(condition);
            _f.If();
            Push(_repeatCheck[node]);
            Trace(node, shape.Exits[0]);
            Goto(node.Exits[shape.Exits[0]], 1);
            _f.End();
            Trace(node, shape.Exits[1]);
            Goto(node.Exits[shape.Exits[1]], 0);
            return;
        }

        var (counter, limit) = RepeatLocals(node);
        _f.Mark(node.Tag);
        _f.LocalGet(counter).LocalGet(limit).Op(WasmOp.I32LtS);
        _f.If();
        Turn(node);
        Push(_repeatCheck[node]);
        Trace(node, shape.Exits[0]);
        Goto(node.Exits[shape.Exits[0]], 1);
        _f.End();
        Trace(node, shape.Exits[1]);
        Goto(node.Exits[shape.Exits[1]], 0);
    }

    private static bool Traced(ResolvedNode node, int exit)
        => (uint)exit < (uint)node.TraceGlobals.Length && node.TraceGlobals[exit] >= 0;

    // Counts flow leaving a node by one of its exits. Nothing for an exit with no wire.
    private void Trace(ResolvedNode node, int exit)
    {
        if (!Traced(node, exit))
            return;
        int counter = node.TraceGlobals[exit];
        _f.GlobalGet(counter).I32Const(1).Op(WasmOp.I32Add).GlobalSet(counter);
    }

    // The first exit when a condition already in a local is set, else the second.
    private void TraceEither(ResolvedNode node, int first, int second, int condition)
    {
        bool one = Traced(node, first);
        bool other = Traced(node, second);
        if (!one && !other)
            return;
        _f.LocalGet(condition);
        _f.If();
        Trace(node, first);
        if (other)
        {
            _f.Else();
            Trace(node, second);
        }
        _f.End();
    }

    private void Goto(ResolvedNode? target, int extraDepth)
    {
        _f.I32Const(Target(target)).LocalSet(_state);
        _f.Br(_loopDepth + extraDepth);
    }

    private void Push(int state)
    {
        _f.LocalGet(_sp).I32Const(ScriptCompiler.StackBytes).Op(WasmOp.I32GeU);
        _f.If().Op(WasmOp.Unreachable).End();
        _f.LocalGet(_sp).I32Const(state).Memory(WasmOp.I32Store, 2, 0);
        _f.LocalGet(_sp).I32Const(4).Op(WasmOp.I32Add).LocalSet(_sp);
    }

    private (int Counter, int Limit) RepeatLocals(ResolvedNode node)
    {
        if (!_repeatLocals.TryGetValue(node, out var locals))
        {
            locals = (_f.AddLocal(WasmValueType.I32), _f.AddLocal(WasmValueType.I32));
            _repeatLocals[node] = locals;
        }
        return locals;
    }

    // One more local a node keeps for the whole call: what a For Each walks, a Cooldown's clock reading.
    private int Scratch(ResolvedNode node, WasmValueType type)
    {
        if (!_scratch.TryGetValue(node, out int local))
        {
            local = _f.AddLocal(type);
            _scratch[node] = local;
        }
        return local;
    }

    private int[] Persistent(ResolvedNode node, int output)
    {
        if (!_persistent.TryGetValue((node, output), out var locals))
        {
            locals = AllocateLanes(node.Overload.Outputs[output].Type);
            _persistent[(node, output)] = locals;
        }
        return locals;
    }

    private int[] AllocateLanes(ScriptType type)
    {
        var locals = new int[type.LaneCount];
        for (int i = 0; i < locals.Length; i++)
            locals[i] = _f.AddLocal(type.Lanes[i]);
        return locals;
    }

    private Location[] MaterializeAll(ResolvedNode node)
    {
        var inputs = new Location[node.Inputs.Length];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = Materialize(node, i);
        return inputs;
    }

    private Location Materialize(ResolvedNode consumer, int input)
    {
        var source = consumer.Inputs[input];
        if (source == null)
            return new Location(null, null, ConstantValue.Default(consumer.Overload.Inputs[input].Type));
        if (source.Constant != null)
            return new Location(null, null, source.Constant);

        var from = source.Node!;
        switch (from.Kind)
        {
            case NodeKind.StoredVariable:
                return new Location(null, from.Globals, null);
            case NodeKind.Constant:
                return Materialize(from, 0);
            case NodeKind.Event:
                if (from != _root)
                {
                    _error(consumer.Id, Later() is { } later
                        ? $"reads {from.Id}'s event data {later}; keep it in a Stored Value to carry it across"
                        : $"reads {from.Id}'s event data outside {from.Id}'s own flow");
                    return new Location(null, null, ConstantValue.Default(consumer.Overload.Inputs[input].Type));
                }
                return new Location(_payload[source.Output], null, null);
            case NodeKind.Await:
                if (from != _root || _answer == 0)
                {
                    _error(consumer.Id, $"reads {from.Id}'s answer outside the flows its answer runs; keep it in a Stored Value to carry it on");
                    return new Location(null, null, ConstantValue.Default(consumer.Overload.Inputs[input].Type));
                }
                return new Location(_payload[source.Output], null, null);
            case NodeKind.Function:
                if (from == _root)
                    return new Location(_payload[source.Output], null, null);
                // After a wait in the Function's own call, from what the host kept for that call.
                if (_word >= 0 && from.KeepImport >= 0 && _root?.Owners is { Count: 1 } owners && owners.Contains(from))
                    return Argument(from, source.Output);
                _error(consumer.Id, _word >= 0 && from.IsAsync
                    ? $"reads {from.FunctionName}'s arguments after a wait that other flows reach too; keep them in a Stored Value"
                    : $"reads {from.FunctionName}'s arguments outside its body; only the flow its next runs has them");
                return new Location(null, null, ConstantValue.Default(consumer.Overload.Inputs[input].Type));
            case NodeKind.CallFunction when from.IsAsyncCall:
                if (from != _root)
                {
                    _error(consumer.Id, $"reads what {from.Id}'s call gives back outside what runs after it returns");
                    return new Location(null, null, ConstantValue.Default(consumer.Overload.Inputs[input].Type));
                }
                return new Location(_payload[source.Output], null, null);
            case NodeKind.Action:
            case NodeKind.Flow:
            case NodeKind.CallFunction:
                if (!_entry.ContainsKey(from) && !_inline.Contains(from))
                {
                    _error(consumer.Id, Later() is { } later
                        ? $"reads {from.Id}'s output {later}; keep it in a Stored Value to carry it across"
                        : $"reads {from.Id}'s output, which never runs in {_root?.Id}'s flow");
                    return new Location(null, null, ConstantValue.Default(consumer.Overload.Inputs[input].Type));
                }
                return new Location(Persistent(from, source.Output), null, null);
            default:
                return new Location(Compute(from)[source.Output], null, null);
        }
    }

    // Where this function runs, when it runs after the call that set it up: after a Delay, in an answer, or after
    // an async call returned.
    private string? Later()
    {
        if (_root?.IsDelay == true)
            return $"after the Delay {_root.Id}";
        if (_root?.IsAwait == true && _answer > 0)
            return $"in {_root.Id}'s answer";
        if (_root?.IsAsyncCall == true)
            return $"after the call {_root.Id} returned";
        return null;
    }

    // One of a waiting Function's arguments, lane by lane from what the host kept for the call.
    private Location Argument(ResolvedNode function, int port)
    {
        var type = function.Overload.Outputs[port].Type;
        var locals = AllocateLanes(type);
        for (int lane = 0; lane < locals.Length; lane++)
        {
            _f.LocalGet(_word).I32Const(port).I32Const(lane).Call(_imports.Argument);
            switch (type.Lanes[lane])
            {
                case WasmValueType.I32:
                    _f.Op(WasmOp.I32WrapI64);
                    break;
                case WasmValueType.F32:
                    _f.Op(WasmOp.I32WrapI64).Op(WasmOp.F32ReinterpretI32);
                    break;
                case WasmValueType.F64:
                    _f.Op(WasmOp.F64ReinterpretI64);
                    break;
            }
            _f.LocalSet(locals[lane]);
        }
        return new Location(locals, null, null);
    }

    private int[][] Compute(ResolvedNode node)
    {
        if (_memo.TryGetValue(node, out var done))
            return done;

        var outputs = new int[node.Overload.Outputs.Count][];
        for (int o = 0; o < outputs.Length; o++)
            outputs[o] = AllocateLanes(node.Overload.Outputs[o].Type);

        if (!_computing.Add(node))
        {
            _error(node.Id, "is part of a data cycle");
            return outputs;
        }

        var inputs = MaterializeAll(node);
        if (node.Kind == NodeKind.Memory)
        {
            if (node.Overload.Shape!.Kind == FlowShapeKind.Follow)
                EmitFollow(node, inputs, outputs);
            else
                EmitFrameDelta(node, inputs, outputs);
            _computing.Remove(node);
            _memo[node] = outputs;
            return outputs;
        }
        _f.Mark(node.Tag);
        bool folds = node.Overload.Folds != null;
        if (node.Kind == NodeKind.EvaluateFunction)
        {
            foreach (var input in inputs)
                Load(input);
            LoadStackTop();
            _f.Call(node.Function!.FunctionIndex);
            for (int o = outputs.Length - 1; o >= 0; o--)
            {
                for (int lane = outputs[o].Length - 1; lane >= 0; lane--)
                    _f.LocalSet(outputs[o][lane]);
            }
        }
        else if (node.Overload.InlineOpcode is byte opcode)
        {
            int lanes = node.Overload.Inputs[0].Type.LaneCount;
            for (int lane = 0; lane < lanes; lane++)
            {
                if (folds)
                {
                    LoadLane(inputs[0], lane);
                    for (int i = 1; i < inputs.Length; i++)
                    {
                        LoadLane(inputs[i], lane);
                        _f.Op(opcode);
                    }
                    _f.LocalSet(outputs[0][lane]);
                    continue;
                }
                foreach (var input in inputs)
                    LoadLane(input, lane);
                _f.Op(opcode).LocalSet(outputs[0][lane]);
            }
        }
        else if (node.Overload.InlineBody is { } body)
        {
            body(new WasmBodyWriter(_f, (input, lane) => LoadLane(inputs[input], lane), outputs));
        }
        else if (folds)
        {
            // The pair's result stays on the stack as the next call's first input.
            Load(inputs[0]);
            for (int i = 1; i < inputs.Length; i++)
            {
                Load(inputs[i]);
                _f.Call(node.ImportIndex);
            }
            for (int lane = outputs[0].Length - 1; lane >= 0; lane--)
                _f.LocalSet(outputs[0][lane]);
        }
        else
        {
            foreach (var input in inputs)
                Load(input);
            _f.Call(node.ImportIndex);
            for (int o = outputs.Length - 1; o >= 0; o--)
            {
                for (int lane = outputs[o].Length - 1; lane >= 0; lane--)
                    _f.LocalSet(outputs[o][lane]);
            }
        }

        _computing.Remove(node);
        _memo[node] = outputs;
        return outputs;
    }

    private void Load(Location location)
    {
        int lanes = location.Locals?.Length ?? location.Globals?.Length ?? location.Constant!.Type.LaneCount;
        for (int lane = 0; lane < lanes; lane++)
            LoadLane(location, lane);
    }

    private void LoadLane(Location location, int lane)
    {
        if (location.Locals != null)
        {
            _f.LocalGet(location.Locals[lane]);
            return;
        }
        if (location.Globals != null)
        {
            _f.GlobalGet(location.Globals[lane]);
            return;
        }

        var constant = location.Constant!;
        var type = constant.Type;
        switch (type.Kind)
        {
            case ScriptTypeKind.Bool:
                _f.I32Const(constant.Value is true ? 1 : 0);
                break;
            case ScriptTypeKind.Int:
                _f.I32Const(constant.Value is int integer ? integer : 0);
                break;
            case ScriptTypeKind.Long:
                _f.I64Const(constant.Value is long wide ? wide : 0);
                break;
            case ScriptTypeKind.Float:
                _f.F32Const(constant.Value is float single ? single : 0f);
                break;
            case ScriptTypeKind.Double:
                _f.F64Const(constant.Value is double precise ? precise : 0d);
                break;
            case ScriptTypeKind.Vector:
            case ScriptTypeKind.Coded:
            {
                ulong raw = constant.RawLanes[lane];
                switch (type.Lanes[lane])
                {
                    case WasmValueType.I32: _f.I32Const(LaneCodec.ToInt(raw)); break;
                    case WasmValueType.I64: _f.I64Const(LaneCodec.ToLong(raw)); break;
                    case WasmValueType.F32: _f.F32Const(LaneCodec.ToFloat(raw)); break;
                    default: _f.F64Const(LaneCodec.ToDouble(raw)); break;
                }
                break;
            }
            default:
                if (constant.Binding >= 0)
                    _f.I32Const(constant.Binding + 1);
                else if (constant.Value is string text)
                    _f.GlobalGet(_stringGlobal(text));
                else if (constant.Value is Uri or ScriptBigInt)
                    _f.GlobalGet(constant.Global);
                else
                    _f.I32Const(0);
                break;
        }
    }
}
