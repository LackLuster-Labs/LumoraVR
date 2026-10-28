// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Wasm;

namespace Lumora.Script;

// What a FlowNode's, a MemoryNode's or a DriveNode's Build writes into: its ports, and exactly one shape.
// The shapes are a closed set on purpose, each one lowered by the compiler, so a node cannot invent control
// flow the dispatch loop does not understand, or memory the instance does not keep. -xlinka
public sealed class FlowBuilder
{
    internal readonly List<NodePort> Inputs = new();
    internal readonly List<NodePort> Outputs = new();
    internal readonly List<string> Exits = new();
    internal FlowShape? Shape { get; private set; }

    // Floats a searching For Each may hand its host: enough for a box's centre, size and rotation.
    public const int MaxQueryLanes = 10;

    public FlowInput<T> Input<T>(string name)
    {
        Inputs.Add(new NodePort(NodePort.NormalizeName(name), ScriptTypes.Get(typeof(T)), null, hasDefault: false));
        return new FlowInput<T>(Inputs.Count - 1);
    }

    public FlowInput<T> Input<T>(string name, T defaultValue)
    {
        Inputs.Add(new NodePort(NodePort.NormalizeName(name), ScriptTypes.Get(typeof(T)), defaultValue, hasDefault: true));
        return new FlowInput<T>(Inputs.Count - 1);
    }

    public FlowInput<T> Setting<T>(string name)
    {
        Inputs.Add(new NodePort(NodePort.NormalizeName(name), ScriptTypes.Get(typeof(T)), null, hasDefault: false, isSetting: true));
        return new FlowInput<T>(Inputs.Count - 1);
    }

    public FlowOutput<T> Output<T>(string name)
    {
        Outputs.Add(new NodePort(NodePort.NormalizeName(name), ScriptTypes.Get(typeof(T)), null, hasDefault: false));
        return new FlowOutput<T>(Outputs.Count - 1);
    }

    public FlowExit Out(string name)
    {
        Exits.Add(NodePort.NormalizeName(name));
        return new FlowExit(Exits.Count - 1);
    }

    // How long a run of exits may be, either way of the count a node declares.
    public const int MinExits = 2;
    public const int MaxExits = 32;

    // The run's length the graph asked for, set by the registry; null builds the declared count.
    internal int? AskedExits { get; init; }

    // The count Outs was given, 0 until it is called.
    internal int DeclaredExits { get; private set; }

    // A run of exits named name0, name1 and on, count of them unless the graph asks for more or fewer: the
    // card's options grow and shrink it, and a graph whose run is not count long says how long in "sig".
    // One run a node.
    public FlowExit[] Outs(string name, int count)
    {
        if (DeclaredExits != 0)
            throw new InvalidOperationException("a flow node has one run of exits");
        if (count < MinExits || count > MaxExits)
            throw new ArgumentOutOfRangeException(nameof(count), $"a run of exits is {MinExits} to {MaxExits} long");
        DeclaredExits = count;
        var exits = new FlowExit[AskedExits ?? count];
        for (int i = 0; i < exits.Length; i++)
            exits[i] = Out(name + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return exits;
    }

    // Runs one exit or the other on a condition read when the node runs.
    public void Branch(FlowInput<bool> condition, FlowExit whenTrue, FlowExit whenFalse)
        => SetShape(new FlowShape(FlowShapeKind.Branch, condition.Index, new[] { whenTrue.Index, whenFalse.Index }, -1));

    // Runs each exit's chain to its end, in order.
    public void Sequence(params FlowExit[] steps)
    {
        if (steps.Length == 0)
            throw new ArgumentException("a sequence needs at least one step");
        var exits = new int[steps.Length];
        for (int i = 0; i < steps.Length; i++)
            exits[i] = steps[i].Index;
        SetShape(new FlowShape(FlowShapeKind.Sequence, -1, exits, -1));
    }

    // Runs the body count times with the iteration on index, then completed. Count is read once, when
    // the node is entered.
    public void Repeat(FlowInput<int> count, FlowExit body, FlowOutput<int> index, FlowExit completed)
        => SetShape(new FlowShape(FlowShapeKind.Repeat, count.Index, new[] { body.Index, completed.Index }, index.Index));

    // The same, counting index down from count - 1 to 0 while reverse holds. Reverse is read once, with
    // the count.
    public void Repeat(FlowInput<int> count, FlowInput<bool> reverse, FlowExit body, FlowOutput<int> index, FlowExit completed)
        => SetShape(new FlowShape(FlowShapeKind.Repeat, count.Index, new[] { body.Index, completed.Index }, index.Index)
        {
            Reverse = reverse.Index,
        });

    // Runs the body with index going from start to end, both included, in steps of step, then
    // completed. A step pointing away from end, or a step of 0, runs nothing. All three are read once,
    // when the node is entered.
    public void Range(FlowInput<int> start, FlowInput<int> end, FlowInput<int> step, FlowExit body, FlowOutput<int> index,
        FlowExit completed)
        => SetShape(new FlowShape(FlowShapeKind.Range, -1, new[] { body.Index, completed.Index }, index.Index)
        {
            Start = start.Index,
            End = end.Index,
            Step = step.Index,
        });

    // Runs the body once per item of a list, with the item and its index, then completed. The count is
    // taken when the node is entered; an item removed meanwhile reads as the type's default.
    public void ListEach<TItem>(FlowInput<ScriptList<TItem>> list, FlowExit body, FlowOutput<TItem> item, FlowOutput<int> index,
        FlowExit completed)
        => SetShape(new FlowShape(FlowShapeKind.ListEach, list.Index, new[] { body.Index, completed.Index }, index.Index, item.Index));

    // Ends this chain here and runs then as a call of its own, seconds later. Nothing the chain computed
    // survives the wait: event data and action outputs have to be kept in a stored value to cross it.
    public void Delay(FlowInput<float> seconds, FlowExit then)
        => SetShape(new FlowShape(FlowShapeKind.Delay, seconds.Index, new[] { then.Index }, -1));

    // Runs the body for as long as the condition holds, reading it again before every turn, then
    // completed. Fuel is what stops a condition that never turns false.
    public void While(FlowInput<bool> condition, FlowExit body, FlowExit completed)
        => SetShape(new FlowShape(FlowShapeKind.While, condition.Index, new[] { body.Index, completed.Index }, -1));

    // Runs the body once per item the host lists for source, with the item and its index, then
    // completed. The count is taken when the node is entered.
    public void Each<TItem>(EachSource source, FlowExit body, FlowOutput<TItem> item, FlowOutput<int> index, FlowExit completed)
        => SetShape(new FlowShape(FlowShapeKind.Each, -1, new[] { body.Index, completed.Index }, index.Index, item.Index, source));

    // The same, over the items of one thing: "of" names whose.
    public void Each<TItem, TOf>(EachSource source, FlowInput<TOf> of, FlowExit body, FlowOutput<TItem> item, FlowOutput<int> index,
        FlowExit completed)
        => SetShape(new FlowShape(FlowShapeKind.Each, of.Index, new[] { body.Index, completed.Index }, index.Index, item.Index, source));

    // Runs the body once per item the host finds for source, with the item and its index, then
    // completed. The host searches once, when the node is entered, with the query inputs' floats in
    // order, and the loop walks what that found.
    public void Query<TItem, TA, TB>(EachSource source, FlowInput<TA> a, FlowInput<TB> b, FlowExit body, FlowOutput<TItem> item,
        FlowOutput<int> index, FlowExit completed)
        => Query(source, new[] { a.Index, b.Index }, body, item.Index, index, completed);

    public void Query<TItem, TA, TB, TC>(EachSource source, FlowInput<TA> a, FlowInput<TB> b, FlowInput<TC> c, FlowExit body,
        FlowOutput<TItem> item, FlowOutput<int> index, FlowExit completed)
        => Query(source, new[] { a.Index, b.Index, c.Index }, body, item.Index, index, completed);

    private void Query(EachSource source, int[] query, FlowExit body, int item, FlowOutput<int> index, FlowExit completed)
    {
        int lanes = 0;
        foreach (int input in query)
        {
            var port = Inputs[input];
            foreach (var lane in port.Type.Lanes)
            {
                if (lane != WasmValueType.F32)
                    throw new InvalidOperationException($"a search hands its host floats only; '{port.Name}' is {port.Type.Name}");
            }
            lanes += port.Type.LaneCount;
        }
        if (lanes > MaxQueryLanes)
            throw new InvalidOperationException($"a search hands its host at most {MaxQueryLanes} floats");
        SetShape(new FlowShape(FlowShapeKind.Each, -1, new[] { body.Index, completed.Index }, index.Index, item, source, query));
    }

    // Runs the action node named enter with this node's inputs, the body to its end, the action node named
    // leave, then next. A body that faults skips leave: the host closes what a call left open as it ends.
    public void Scope(string enter, string leave, FlowExit body, FlowExit next)
        => SetShape(new FlowShape(FlowShapeKind.Scope, -1, new[] { body.Index, next.Index }, -1) { Enter = enter, Leave = leave });

    // Runs next the first time flow arrives after the script starts, and never again.
    public void Once(FlowExit next)
        => SetShape(new FlowShape(FlowShapeKind.Once, -1, new[] { next.Index }, -1));

    // Runs on and off by turns, on first; state says which ran last.
    public void Toggle(FlowExit on, FlowExit off, FlowOutput<bool> state)
        => SetShape(new FlowShape(FlowShapeKind.Toggle, -1, new[] { on.Index, off.Index }, state.Index));

    // Runs next at most once every seconds, by the world's clock; flow that comes sooner runs cooling.
    public void Cooldown(FlowInput<float> seconds, FlowExit next, FlowExit cooling)
        => SetShape(new FlowShape(FlowShapeKind.Cooldown, seconds.Index, new[] { next.Index, cooling.Index }, -1));

    // Hands change the difference between value this time and value the last time flow came through, then
    // runs next. The value node named helper, taking the input type twice and giving it back, works it out;
    // with previousFirst it is given the earlier value first. The first time through there is no last time,
    // and value is compared with itself.
    public void Change<T>(FlowInput<T> value, FlowOutput<T> change, FlowExit next, string helper, bool previousFirst)
        => SetShape(new FlowShape(FlowShapeKind.Change, value.Index, new[] { next.Index }, change.Index)
        {
            Helper = helper,
            PreviousFirst = previousFirst,
        });

    // Runs changed when value differs from the last time flow came through, else same; previous is the value
    // from that last time. The first time through there is nothing to differ from: it only remembers, and
    // runs same.
    public void Watch<T>(FlowInput<T> value, FlowExit changed, FlowExit same, FlowOutput<T> previous)
        => SetShape(new FlowShape(FlowShapeKind.Watch, value.Index, new[] { changed.Index, same.Index }, previous.Index));

    // A value that follows value over time. The value node named helper takes where it is, where it heads,
    // speed times speedScale and the seconds since it last moved, and gives where it is now. With two stages
    // a hidden middle follows value and the result follows the middle, which eases in as well as out. The
    // first time it starts where value is; worked out again in the same frame it does not move twice.
    public void Follow<T>(FlowInput<T> value, FlowInput<float> speed, FlowOutput<T> result, string helper, int stages = 1,
        float speedScale = 1f)
    {
        if (stages is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(stages), "a follow eases in one stage or two");
        SetShape(new FlowShape(FlowShapeKind.Follow, value.Index, Array.Empty<int>(), result.Index)
        {
            Speed = speed.Index,
            Helper = helper,
            Stages = stages,
            SpeedScale = speedScale,
        });
    }

    // How much value has changed since the frame it was last worked out, through the helper the way a Change
    // works it out. Worked out again in the same frame it gives the same answer; the first time, none.
    public void FrameDelta<T>(FlowInput<T> value, FlowOutput<T> delta, string helper, bool previousFirst)
        => SetShape(new FlowShape(FlowShapeKind.FrameDelta, value.Index, Array.Empty<int>(), delta.Index)
        {
            Helper = helper,
            PreviousFirst = previousFirst,
        });

    // Holds the field on target at value, on every machine, every frame the script runs.
    public void Drive<TField, T>(FlowInput<TField> target, FlowInput<T> value)
        => SetShape(new FlowShape(FlowShapeKind.Drive, value.Index, Array.Empty<int>(), -1) { Target = target.Index });

    // A drive that holds its field only while on is true, and lets it go while it is false.
    public void Drive<TField, T>(FlowInput<TField> target, FlowInput<T> value, FlowInput<bool> on)
        => SetShape(new FlowShape(FlowShapeKind.Drive, value.Index, Array.Empty<int>(), -1) { Target = target.Index, Switch = on.Index });

    // The same, and with heard bound it refuses every other write, telling heard what was tried and counting
    // the tries in heardCount.
    public void Drive<TField, T, TCount>(FlowInput<TField> target, FlowInput<T> value, FlowInput<bool> on, FlowInput<TField> heard,
        FlowInput<TCount> heardCount)
        => SetShape(new FlowShape(FlowShapeKind.Drive, value.Index, Array.Empty<int>(), -1)
        {
            Target = target.Index,
            Switch = on.Index,
            Heard = heard.Index,
            HeardCount = heardCount.Index,
        });

    private void SetShape(FlowShape shape)
    {
        if (Shape != null)
            throw new InvalidOperationException("a flow node has exactly one shape");
        Shape = shape;
    }
}
