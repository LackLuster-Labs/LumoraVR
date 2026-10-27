// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Core.Assets.Interop.Unity;

// One avatar's run of a compiled animator graph: parameter values, each layer's state, clock and
// transition, layer and playable weights, and the pose they make. The graph is shared; all of this is per
// avatar, and every peer runs its own from the parameters that reach it.
//
// The pose is a stream with one entry per animated property, kept from frame to frame. Each entry is a
// premultiplied value and an amount: the property shows value + rest * (1 - amount), so amount 0 is the
// property at rest and a weighted mix of entries is a plain weighted sum. Layers are laid over the stream
// in order. A state that writes defaults covers every property its layer animates, putting the ones its
// motion leaves alone back to rest; one that does not covers only what its motion animates, so the rest
// keep whatever they last showed. A transition covers the union of both sides, the side that does not
// touch a property standing in with the stream's own value. A blend tree adds its children by their
// weights, a direct tree by raw parameter values unless it normalises them. An additive layer adds each
// clip's change from its reference pose. A mask keeps a layer off transforms and body parts it does not
// name. Humanoid muscles go back to rest each frame instead of holding.
//
// A frame that would come out exactly as the last one did is not worked out again. After a frame in which
// nothing was entered, left, blended or moved, the runtime asks whether the frames after it would match as
// long as no parameter changes: no layer between states, every motion still at its time (flat curves, a
// clip that does not loop and has played through, a time parameter), no way out of a state that time alone
// opens. Until a parameter differs, Rest only runs the clocks on, the same sums Advance does. -xlinka
public sealed class UnityAnimatorRuntime
{
    public struct LayerState
    {
        public int Current, Previous, Transition;
        public double Time, Before, PreviousTime;
        public float Elapsed, Duration;
    }

    public enum EventKind { Tracking, AudioEnter, AudioExit }

    public readonly record struct Event(EventKind Kind, int Behaviour, int Layer);

    private struct Blend
    {
        public float From, Goal, Elapsed, Duration;
    }

    public UnityAnimatorGraph Graph { get; }
    public readonly float[] Values;
    public readonly LayerState[] Layers;
    public readonly float[] LayerWeights;
    public readonly float[] PlayableWeights = new float[6];
    public readonly List<Event> Events = new();
    // Parameters a driver wrote this frame, in the order it wrote them.
    public readonly List<int> Driven = new();
    // Parameters the animation itself wrote this frame.
    public readonly List<int> Animated = new();
    public bool Owner;

    private readonly Blend[] _layerBlends;
    private readonly Blend[] _playableBlends = new Blend[6];
    private readonly Random _random = new();

    // The stream.
    private readonly float4[] _q;
    private readonly float[] _a;
    private readonly int[] _ref;
    private readonly float4[] _extra;
    private readonly int[] _extraStamp;
    private readonly int[] _written;
    // What each property showed after the last frame, to tell which ones this frame moved.
    private readonly float4[] _shownQ, _shownExtra;
    private readonly float[] _shownA;
    private readonly int[] _shownRef;
    private readonly List<int> _touched = new(), _extras = new(), _lastExtras = new();
    // Properties whose value changed this frame.
    public readonly List<int> Changed = new();
    private int _frame = 1;

    // One layer's state output, sparse.
    private readonly float4[] _bq;
    private readonly float[] _ba, _bc, _brefWeight;
    private readonly int[] _bref, _bstamp, _btouched;
    private int _bgen = 1, _bcount;

    private readonly float[][] _weights = new float[34][];

    // The parameters as the last full frame started from, and each layer's clock while it rests.
    private readonly float[] _restValues;
    private readonly float[] _restSpeed, _restLength;
    private bool _stirred, _steady;
    // The last full frame moved nothing, and the next ones would not either while the parameters hold.
    public bool Resting { get; private set; }

    public UnityAnimatorRuntime(UnityAnimatorProgram program) : this(program.Graph) { }

    public UnityAnimatorRuntime(UnityAnimatorGraph graph)
    {
        Graph = graph;
        Values = graph.ParameterDefaults.ToArray();
        Layers = new LayerState[graph.Layers.Count];
        LayerWeights = new float[graph.Layers.Count];
        _layerBlends = new Blend[graph.Layers.Count];
        for (int i = 0; i < Layers.Length; i++)
        {
            Layers[i] = new LayerState { Current = -1, Previous = -1, Transition = -1 };
            LayerWeights[i] = graph.Layers[i].Weight;
        }
        for (int p = 0; p < PlayableWeights.Length; p++)
            PlayableWeights[p] = UnityAnimatorController.PlayableDefaultWeight(p);
        int n = graph.Bindings.Count;
        _q = new float4[n]; _a = new float[n]; _ref = new int[n]; _extra = new float4[n]; _extraStamp = new int[n]; _written = new int[n];
        _shownQ = new float4[n]; _shownExtra = new float4[n]; _shownA = new float[n]; _shownRef = new int[n];
        Array.Fill(_shownRef, -1);
        _bq = new float4[n]; _ba = new float[n]; _bc = new float[n]; _brefWeight = new float[n];
        _bref = new int[n]; _bstamp = new int[n]; _btouched = new int[n];
        Array.Fill(_ref, -1);
        for (int i = 0; i < _weights.Length; i++)
            _weights[i] = new float[System.Math.Max(1, graph.MaxTreeChildren)];
        _restValues = new float[Values.Length];
        _restSpeed = new float[Layers.Length];
        _restLength = new float[Layers.Length];
    }

    public int Parameter(string name) => Graph.ParameterIndex.GetValueOrDefault(name, -1);

    public string StateName(int layer) => Layers[layer].Current >= 0 ? Graph.States[Layers[layer].Current].Name : "";

    // ---- Stepping ----

    public void Advance(float delta)
    {
        Events.Clear();
        Driven.Clear();
        Resting = false;
        _stirred = false;
        // Settling is only looked into once the parameters have held from one frame to the next, so an avatar
        // whose parameters move every frame never pays for the look.
        _steady = Unmoved();
        Values.AsSpan().CopyTo(_restValues);
        for (int p = 0; p < PlayableWeights.Length; p++)
        {
            _stirred |= _playableBlends[p].Duration > 0f;
            Step(ref _playableBlends[p], ref PlayableWeights[p], delta);
        }
        for (int i = 0; i < Layers.Length; i++)
        {
            _stirred |= _layerBlends[i].Duration > 0f;
            Step(ref _layerBlends[i], ref LayerWeights[i], delta);
        }
        for (int i = 0; i < Layers.Length; i++)
        {
            var layer = Graph.Layers[i];
            if (layer.Synced >= 0 && layer.Synced < Layers.Length)
            {
                _stirred |= Layers[i].Current != Layers[layer.Synced].Current || Layers[i].Previous != Layers[layer.Synced].Previous;
                Layers[i] = Layers[layer.Synced];
                continue;
            }
            ref var state = ref Layers[i];
            if (state.Current < 0)
            {
                int start = Enter(layer.Machine, 0);
                if (start < 0)
                    continue;
                _stirred = true;
                state.Current = start;
                state.Time = state.Before = 0;
                OnEnter(i, start);
            }
            state.Before = state.Time;
            // The same sum as Advance(state, time, delta), with the speed and length kept for Settles: a tree's
            // length can mean weighing it, which is the dearest part of a frame.
            var playing = Graph.States[state.Current];
            if (playing.TimeParameter >= 0)
                state.Time = Values[playing.TimeParameter];
            else
            {
                _restSpeed[i] = Speed(playing);
                _restLength[i] = Length(playing.Motion, 0);
                state.Time = state.Time + delta * _restSpeed[i] / _restLength[i];
            }
            if (state.Previous >= 0)
            {
                _stirred = true;
                state.PreviousTime = Advance(Graph.States[state.Previous], state.PreviousTime, delta);
                state.Elapsed += delta;
                if (state.Elapsed >= state.Duration)
                {
                    int left = state.Previous;
                    state.Previous = -1;
                    state.Transition = -1;
                    OnExit(i, left);
                }
            }
            int taken = Pick(i, state);
            if (taken >= 0)
            {
                _stirred = true;
                Start(i, ref state, taken);
            }
        }
        _stirred |= Driven.Count > 0 || Events.Count > 0;
    }

    // A frame the last one already showed: the clocks run on as Advance runs them and nothing else moves.
    // False when the runtime is not resting or a parameter changed since, and the frame has to be worked out.
    public bool Rest(float delta)
    {
        if (!Resting)
            return false;
        if (!Unmoved())
        {
            Resting = false;
            return false;
        }
        Events.Clear();
        Driven.Clear();
        for (int i = 0; i < Layers.Length; i++)
        {
            var layer = Graph.Layers[i];
            if (layer.Synced >= 0 && layer.Synced < Layers.Length)
            {
                Layers[i] = Layers[layer.Synced];
                continue;
            }
            ref var state = ref Layers[i];
            if (state.Current < 0)
                continue;
            var current = Graph.States[state.Current];
            state.Before = state.Time;
            state.Time = current.TimeParameter >= 0 ? Values[current.TimeParameter] : state.Time + delta * _restSpeed[i] / _restLength[i];
        }
        return true;
    }

    private bool Unmoved()
        => MemoryMarshal.Cast<float, int>(Values.AsSpan()).SequenceEqual(MemoryMarshal.Cast<float, int>(_restValues.AsSpan()));

    // Whether the frames after this one come out the same while no parameter changes: no layer between two
    // states, every motion that shows holding still where its clock is going, and no way out of a state that
    // the clock alone opens. Each layer's speed and length are the ones Advance just used, which hold while
    // the parameters do.
    private bool Settles()
    {
        for (int i = 0; i < Layers.Length; i++)
        {
            var layer = Graph.Layers[i];
            var state = Layers[i];
            if (state.Previous >= 0)
                return false;
            if (state.Current < 0)
                continue;
            var current = Graph.States[state.Current];
            bool synced = layer.Synced >= 0 && layer.Synced < Layers.Length;
            double rate = current.TimeParameter >= 0 ? 0
                : synced ? (double)Speed(current) / Length(current.Motion, 0)
                : (double)_restSpeed[i] / _restLength[i];
            if (!double.IsFinite(rate))
                return false;
            float weight = LayerWeights[i] * PlayableWeights[System.Math.Clamp(layer.Playable, 0, PlayableWeights.Length - 1)];
            if (weight > 0f && rate != 0)
            {
                int motion = current.Motion;
                if (layer.SyncedMotions != null)
                    motion = layer.SyncedMotions.TryGetValue(state.Current, out int syncedMotion) ? syncedMotion : -1;
                float offset = current.CycleOffsetParameter >= 0 ? Values[current.CycleOffsetParameter] : current.CycleOffset;
                if (!Still(motion, state.Time + offset, rate, 0))
                    return false;
            }
            if (synced)
                continue;
            bool loops = Loops(current.Motion, 0);
            if (Opens(layer.Any, state, rate, loops, any: true) || Opens(current.Transitions, state, rate, loops, any: false))
                return false;
        }
        return true;
    }

    // A motion that gives the same pose from here on while its clock runs at this rate: flat curves, a clip
    // that does not loop and has played through, a tree whose weighted children all hold.
    private bool Still(int motion, double time, double rate, int depth)
    {
        if (motion < 0 || depth > 32 || rate == 0 || Graph.MotionStatic[motion])
            return true;
        if (Graph.IsClip(motion))
            return !Graph.Clips[motion].Loop && (rate > 0 ? time >= 1.0 : time <= 0.0);
        var tree = Graph.TreeOf(motion);
        var weights = _weights[depth];
        Weigh(tree, weights);
        for (int i = 0; i < tree.Children.Length; i++)
            if (weights[i] != 0f && tree.Children[i] >= 0
                && !Still(tree.Children[i], time * tree.TimeScale[i] + tree.CycleOffset[i], rate * tree.TimeScale[i], depth + 1))
                return false;
        return true;
    }

    // Whether a transition in this list could be taken on a later frame with the parameters as they are, read
    // the way First reads them.
    private bool Opens(int[] list, LayerState state, double rate, bool loops, bool any)
    {
        bool solo = false;
        foreach (int id in list)
        {
            var t = Graph.Transitions[id];
            if (t.Solo && !t.Muted) { solo = true; break; }
        }
        foreach (int id in list)
        {
            var t = Graph.Transitions[id];
            if (t.Muted || solo && !t.Solo)
                continue;
            if (any && !t.CanTransitionToSelf && t.Destination == state.Current)
                continue;
            if (t.Conditions.Length == 0 && !t.HasExitTime)
                continue;
            if (!Conditions(t))
                continue;
            if (!t.HasExitTime)
                return true;
            // A clock standing still meets the exit time every frame or never; one running forward meets it
            // while it is still ahead, or each time round a loop.
            if (rate == 0 ? ExitReady(t.ExitTime, state.Time, state.Time, loops)
                : rate < 0 || loops && t.ExitTime < 1f || state.Time < t.ExitTime || state.Time == 0 && t.ExitTime == 0)
                return true;
        }
        return false;
    }

    private static void Step(ref Blend blend, ref float weight, float delta)
    {
        if (blend.Duration <= 0f)
            return;
        blend.Elapsed += delta;
        float t = System.Math.Clamp(blend.Elapsed / blend.Duration, 0f, 1f);
        weight = blend.From + (blend.Goal - blend.From) * t;
        if (t >= 1f)
            blend.Duration = 0f;
    }

    private double Advance(UnityAnimatorGraph.State state, double time, float delta)
    {
        if (state.TimeParameter >= 0)
            return Values[state.TimeParameter];
        return time + delta * Speed(state) / Length(state.Motion, 0);
    }

    private float Speed(UnityAnimatorGraph.State state)
        => state.Speed * (state.SpeedParameter >= 0 ? Values[state.SpeedParameter] : 1f);

    // The first transition that may be taken now: any state's first, then the current state's, or while a
    // transition runs, the ones its interruption source lets in.
    private int Pick(int layerIndex, LayerState state)
    {
        var layer = Graph.Layers[layerIndex];
        var current = Graph.States[state.Current];
        bool loops = Loops(current.Motion, 0);
        int taken = First(layer.Any, -1, state, current, loops, any: true);
        if (taken >= 0)
            return taken;
        if (state.Previous < 0 || state.Transition < 0)
            return First(current.Transitions, -1, state, current, loops, any: false);
        var active = Graph.Transitions[state.Transition];
        var source = Graph.States[state.Previous];
        int Source()
        {
            // Ordered: only transitions ahead of the running one in its source's list may cut in.
            int stop = active.OrderedInterruption ? Array.IndexOf(source.Transitions, state.Transition) : -1;
            var sourceState = state;
            sourceState.Before = state.PreviousTime;
            sourceState.Time = state.PreviousTime;
            return First(source.Transitions, stop < 0 ? source.Transitions.Length : stop, sourceState, source, Loops(source.Motion, 0), any: false, skip: state.Transition);
        }
        int Destination() => First(current.Transitions, -1, state, current, loops, any: false);
        return active.InterruptionSource switch
        {
            1 => Source(),
            2 => Destination(),
            3 => Source() is int s and >= 0 ? s : Destination(),
            4 => Destination() is int d and >= 0 ? d : Source(),
            _ => -1,
        };
    }

    private int First(int[] list, int count, LayerState state, UnityAnimatorGraph.State from, bool loops, bool any, int skip = -1)
    {
        if (count < 0 || count > list.Length)
            count = list.Length;
        bool solo = false;
        for (int i = 0; i < count; i++)
        {
            var t = Graph.Transitions[list[i]];
            if (t.Solo && !t.Muted) { solo = true; break; }
        }
        for (int i = 0; i < count; i++)
        {
            int id = list[i];
            if (id == skip)
                continue;
            var t = Graph.Transitions[id];
            if (t.Muted || solo && !t.Solo)
                continue;
            if (any && !t.CanTransitionToSelf && t.Destination == state.Current)
                continue;
            if (t.Conditions.Length == 0 && !t.HasExitTime)
                continue;
            if (!Conditions(t))
                continue;
            if (t.HasExitTime && !ExitReady(t.ExitTime, state.Before, state.Time, loops))
                continue;
            return id;
        }
        return -1;
    }

    // Exit time is a moment, not a floor: it is met on the frame the state's normalised time passes it.
    // Below one on a looping motion it comes round every loop.
    private static bool ExitReady(float exitTime, double before, double after, bool loops)
    {
        double exit = exitTime;
        if (exit < 1.0 && loops)
            exit += System.Math.Floor(before);
        return after >= exit && (before < exit || before == 0 && exit == 0);
    }

    private bool Conditions(UnityAnimatorGraph.Transition t)
    {
        foreach (var c in t.Conditions)
            if (c.Parameter < 0 || !UnityAnimatorController.Test(c.Mode, Values[c.Parameter], c.Threshold))
                return false;
        return true;
    }

    private void Consume(UnityAnimatorGraph.Transition t)
    {
        foreach (int trigger in t.Triggers)
            Values[trigger] = 0f;
    }

    private void Start(int layerIndex, ref LayerState state, int id)
    {
        var t = Graph.Transitions[id];
        Consume(t);
        var layer = Graph.Layers[layerIndex];
        int next = t.Exit ? ExitFrom(layer, Graph.States[state.Current].Machine)
            : t.Destination >= 0 ? t.Destination
            : t.DestinationMachine >= 0 ? Enter(t.DestinationMachine, 0) : -1;
        if (next < 0)
            return;
        var current = Graph.States[state.Current];
        float speed = MathF.Abs(Speed(current));
        float duration = t.FixedDuration ? t.Duration : t.Duration * Length(current.Motion, 0) / (speed > 1e-4f ? speed : 1f);
        int leaving = state.Current;
        // An interrupted transition hands over from where its destination had got to.
        if (state.Previous >= 0)
        {
            int dropped = state.Previous;
            state.Previous = -1;
            OnExit(layerIndex, dropped);
        }
        double offset = t.Offset;
        if (duration <= 0f)
        {
            OnExit(layerIndex, leaving);
            state.Transition = -1;
        }
        else
        {
            state.Previous = leaving;
            state.PreviousTime = state.Time;
            state.Elapsed = 0f;
            state.Duration = duration;
            state.Transition = id;
        }
        state.Current = next;
        state.Time = state.Before = offset;
        OnEnter(layerIndex, next);
    }

    // A state machine's way in: the first entry transition that passes, else its default state.
    private int Enter(int machine, int depth)
    {
        if (machine < 0 || depth > 32)
            return -1;
        var m = Graph.Machines[machine];
        foreach (int id in m.Entry)
        {
            var t = Graph.Transitions[id];
            if (t.Muted || !Conditions(t))
                continue;
            Consume(t);
            if (t.Destination >= 0)
                return t.Destination;
            if (t.DestinationMachine >= 0)
                return Enter(t.DestinationMachine, depth + 1);
        }
        return m.DefaultState;
    }

    // Leaving a machine through its exit: the parent's transitions out of that machine's node decide where
    // to, or the parent is left in turn; leaving the layer's own machine starts it over from its entry.
    private int ExitFrom(UnityAnimatorGraph.Layer layer, int machine)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            if (machine < 0 || machine == layer.Machine)
                return Enter(layer.Machine, 0);
            int parent = Graph.Machines[machine].Parent;
            if (parent < 0)
                return Enter(layer.Machine, 0);
            if (Graph.Machines[parent].Exits.TryGetValue(machine, out var list))
            {
                foreach (int id in list)
                {
                    var t = Graph.Transitions[id];
                    if (t.Muted || t.Conditions.Length > 0 && !Conditions(t))
                        continue;
                    Consume(t);
                    if (t.Exit)
                        break;
                    if (t.Destination >= 0)
                        return t.Destination;
                    if (t.DestinationMachine >= 0)
                        return Enter(t.DestinationMachine, 0);
                }
            }
            machine = parent;
        }
        return -1;
    }

    // ---- Behaviours ----

    private void OnEnter(int layer, int state)
    {
        if (Graph.Layers[layer].Synced >= 0)
            return;
        foreach (int b in Graph.States[state].Behaviours)
        {
            switch (Graph.Behaviours[b])
            {
                case UnityAnimatorGraph.Driver driver:
                    if (!driver.LocalOnly || Owner)
                        Drive(driver);
                    break;
                case UnityAnimatorController.TrackingControl:
                    Events.Add(new Event(EventKind.Tracking, b, layer));
                    break;
                case UnityAnimatorGraph.LayerControl control when control.Layer >= 0 && control.Layer < Layers.Length:
                    BlendTo(ref _layerBlends[control.Layer], ref LayerWeights[control.Layer], control.Goal, control.Duration);
                    break;
                case UnityAnimatorGraph.PlayableControl control when control.Playable >= 0 && control.Playable < PlayableWeights.Length:
                    BlendTo(ref _playableBlends[control.Playable], ref PlayableWeights[control.Playable], control.Goal, control.Duration);
                    break;
                case UnityAnimatorGraph.PlayAudio:
                    Events.Add(new Event(EventKind.AudioEnter, b, layer));
                    break;
            }
        }
    }

    private void OnExit(int layer, int state)
    {
        if (Graph.Layers[layer].Synced >= 0)
            return;
        foreach (int b in Graph.States[state].Behaviours)
            if (Graph.Behaviours[b] is UnityAnimatorGraph.PlayAudio)
                Events.Add(new Event(EventKind.AudioExit, b, layer));
    }

    private static void BlendTo(ref Blend blend, ref float weight, float goal, float duration)
    {
        if (duration <= 0f)
        {
            weight = goal;
            blend.Duration = 0f;
            return;
        }
        blend = new Blend { From = weight, Goal = goal, Elapsed = 0f, Duration = duration };
    }

    private void Drive(UnityAnimatorGraph.Driver driver)
    {
        foreach (var e in driver.Entries)
        {
            var type = Graph.ParameterTypes[e.Parameter];
            float current = Values[e.Parameter];
            float value;
            bool truncate = false;
            switch (e.Type)
            {
                case UnityAnimatorController.DriverOperation.Add:
                    value = current + e.Value;
                    break;
                case UnityAnimatorController.DriverOperation.Random:
                    if (type is UnityAnimatorController.ParameterType.Bool or UnityAnimatorController.ParameterType.Trigger)
                        value = _random.NextDouble() < e.Chance ? 1f : 0f;
                    else if (type == UnityAnimatorController.ParameterType.Int)
                        value = _random.Next((int)MathF.Round(MathF.Min(e.Min, e.Max)), (int)MathF.Round(MathF.Max(e.Min, e.Max)) + 1);
                    else
                        value = e.Min + (float)_random.NextDouble() * (e.Max - e.Min);
                    break;
                case UnityAnimatorController.DriverOperation.Copy:
                {
                    value = e.Source >= 0 ? Values[e.Source] : 0f;
                    if (e.ConvertRange)
                    {
                        float span = e.SourceMax - e.SourceMin;
                        float at = MathF.Abs(span) > 1e-8f ? (value - e.SourceMin) / span : 0f;
                        value = e.DestMin + at * (e.DestMax - e.DestMin);
                    }
                    truncate = true;
                    break;
                }
                default:
                    value = e.Value;
                    break;
            }
            Values[e.Parameter] = type switch
            {
                UnityAnimatorController.ParameterType.Bool or UnityAnimatorController.ParameterType.Trigger => value != 0f ? 1f : 0f,
                UnityAnimatorController.ParameterType.Int => System.Math.Clamp(truncate ? MathF.Truncate(value) : MathF.Round(value), 0f, 255f),
                _ => value,
            };
            Driven.Add(e.Parameter);
        }
    }

    // ---- Lengths ----

    private float Length(int motion, int depth)
    {
        if (motion < 0 || depth > 32)
            return 1f;
        float known = Graph.MotionLength[motion];
        if (!float.IsNaN(known))
            return known;
        var tree = Graph.TreeOf(motion);
        var weights = _weights[depth];
        Weigh(tree, weights);
        float total = 0f, sum = 0f;
        for (int i = 0; i < tree.Children.Length; i++)
        {
            float w = MathF.Abs(weights[i]);
            if (w <= 0f || tree.Children[i] < 0)
                continue;
            float scale = MathF.Abs(tree.TimeScale[i]) > 1e-4f ? MathF.Abs(tree.TimeScale[i]) : 1f;
            sum += w * Length(tree.Children[i], depth + 1) / scale;
            total += w;
        }
        return total > 1e-6f ? MathF.Max(sum / total, 1f / 60f) : 1f;
    }

    private bool Loops(int motion, int depth) => motion >= 0 && Graph.MotionLoops[motion];

    // ---- Blend tree weights ----

    private void Weigh(UnityAnimatorGraph.Tree tree, float[] weights)
    {
        int n = tree.Children.Length;
        Array.Clear(weights, 0, n);
        if (n == 0)
            return;
        switch (tree.Type)
        {
            case 0:
            {
                float v = tree.Parameter >= 0 ? Values[tree.Parameter] : 0f;
                var order = tree.Order;
                if (v <= tree.Thresholds[order[0]]) { weights[order[0]] = 1f; return; }
                for (int k = 1; k < order.Length; k++)
                {
                    float a = tree.Thresholds[order[k - 1]], b = tree.Thresholds[order[k]];
                    if (v > b)
                        continue;
                    float t = b > a ? (v - a) / (b - a) : 1f;
                    weights[order[k - 1]] = 1f - t;
                    weights[order[k]] += t;
                    return;
                }
                weights[order[^1]] = 1f;
                return;
            }
            case 1:
                SimpleDirectional(tree, weights);
                return;
            case 2:
            case 3:
                Freeform(tree, weights);
                return;
            case 4:
            {
                float total = 0f;
                for (int i = 0; i < n; i++)
                {
                    weights[i] = tree.Direct[i] >= 0 ? Values[tree.Direct[i]] : 0f;
                    total += weights[i];
                }
                if (tree.Normalized && MathF.Abs(total) > 1e-6f)
                    for (int i = 0; i < n; i++)
                        weights[i] /= total;
                return;
            }
        }
    }

    // One motion per direction round a centre, and optionally one at the centre. The point falls between the
    // two directions either side of it and is weighed by where it sits in their triangle with the centre.
    private void SimpleDirectional(UnityAnimatorGraph.Tree tree, float[] weights)
    {
        int n = tree.Children.Length;
        float x = tree.Parameter >= 0 ? Values[tree.Parameter] : 0f;
        float y = tree.ParameterY >= 0 ? Values[tree.ParameterY] : 0f;
        int centre = -1;
        for (int i = 0; i < n; i++)
            if (tree.Positions[i].x * tree.Positions[i].x + tree.Positions[i].y * tree.Positions[i].y < 1e-8f) { centre = i; break; }
        if (x * x + y * y < 1e-10f)
        {
            if (centre >= 0)
                weights[centre] = 1f;
            else
                for (int i = 0; i < n; i++) weights[i] = 1f / n;
            return;
        }
        float angle = MathF.Atan2(y, x);
        int before = -1, after = -1;
        float bestBefore = float.NegativeInfinity, bestAfter = float.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            if (i == centre)
                continue;
            float d = MathF.Atan2(tree.Positions[i].y, tree.Positions[i].x) - angle;
            while (d > MathF.PI) d -= 2f * MathF.PI;
            while (d <= -MathF.PI) d += 2f * MathF.PI;
            if (d <= 0f && d > bestBefore) { bestBefore = d; before = i; }
            if (d > 0f && d < bestAfter) { bestAfter = d; after = i; }
        }
        if (before < 0) before = after;
        if (after < 0) after = before;
        if (before < 0)
        {
            if (centre >= 0) weights[centre] = 1f;
            return;
        }
        var p = tree.Positions[before];
        var q = tree.Positions[after];
        float det = p.x * q.y - p.y * q.x;
        float a, b;
        if (before == after || MathF.Abs(det) < 1e-8f || bestAfter - bestBefore >= MathF.PI)
        {
            // One direction, or two that do not enclose the point: the nearest by angle, scaled by distance.
            int nearest = -bestBefore <= bestAfter ? before : after;
            var at = tree.Positions[nearest];
            float along = (x * at.x + y * at.y) / MathF.Max(at.x * at.x + at.y * at.y, 1e-8f);
            a = System.Math.Clamp(along, 0f, 1f);
            if (centre < 0) a = 1f;
            weights[nearest] += a;
            if (centre >= 0) weights[centre] += 1f - a;
            return;
        }
        a = (x * q.y - y * q.x) / det;
        b = (p.x * y - p.y * x) / det;
        a = MathF.Max(a, 0f);
        b = MathF.Max(b, 0f);
        float sum = a + b;
        if (centre < 0 || sum > 1f)
        {
            if (sum > 1e-8f) { a /= sum; b /= sum; }
            else { a = 1f; b = 0f; }
            sum = 1f;
        }
        weights[before] += a;
        weights[after] += b;
        if (centre >= 0)
            weights[centre] += 1f - sum;
    }

    // Gradient bands: each motion's weight falls off across every line to another motion. Directional bands
    // measure a direction and a relative distance from the centre, cartesian ones plain displacement.
    private void Freeform(UnityAnimatorGraph.Tree tree, float[] weights)
    {
        int n = tree.Children.Length;
        float x = tree.Parameter >= 0 ? Values[tree.Parameter] : 0f;
        float y = tree.ParameterY >= 0 ? Values[tree.ParameterY] : 0f;
        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            var a = tree.Positions[i];
            float weight = 1f;
            for (int j = 0; j < n; j++)
            {
                if (i == j)
                    continue;
                var b = tree.Positions[j];
                float dx = b.x - a.x, dy = b.y - a.y, px = x - a.x, py = y - a.y;
                if (tree.Type == 2)
                {
                    float ra = MathF.Sqrt(a.x * a.x + a.y * a.y), rb = MathF.Sqrt(b.x * b.x + b.y * b.y);
                    float rp = MathF.Sqrt(x * x + y * y), average = (ra + rb) * 0.5f;
                    if (average > 1e-6f)
                    {
                        dx = (rb - ra) / average;
                        px = (rp - ra) / average;
                        dy = ra < 1e-6f || rb < 1e-6f ? 0f : MathF.Atan2(a.x * b.y - a.y * b.x, a.x * b.x + a.y * b.y);
                        py = ra < 1e-6f || rp < 1e-6f ? 0f : MathF.Atan2(a.x * y - a.y * x, a.x * x + a.y * y);
                    }
                }
                float length = dx * dx + dy * dy;
                if (length < 1e-8f)
                    continue;
                weight = MathF.Min(weight, System.Math.Clamp(1f - (px * dx + py * dy) / length, 0f, 1f));
            }
            weights[i] = weight;
            total += weight;
        }
        if (total > 1e-8f)
            for (int i = 0; i < n; i++)
                weights[i] /= total;
    }

    // ---- Evaluation ----

    public void Evaluate()
    {
        _frame++;
        Animated.Clear();
        Changed.Clear();
        _touched.Clear();
        _lastExtras.Clear();
        _lastExtras.AddRange(_extras);
        _extras.Clear();
        foreach (int b in Graph.MuscleBindings)
        {
            _q[b] = float4.Zero;
            _a[b] = 0f;
        }
        for (int i = 0; i < Layers.Length; i++)
        {
            var layer = Graph.Layers[i];
            ref var state = ref Layers[i];
            float weight = LayerWeights[i] * PlayableWeights[System.Math.Clamp(layer.Playable, 0, PlayableWeights.Length - 1)];
            if (weight <= 0f || state.Current < 0)
                continue;
            _bgen++;
            _bcount = 0;
            if (state.Previous >= 0 && state.Duration > 0f)
            {
                float t = System.Math.Clamp(state.Elapsed / state.Duration, 0f, 1f);
                AddState(layer, state.Previous, state.PreviousTime, 1f - t);
                AddState(layer, state.Current, state.Time, t);
            }
            else
                AddState(layer, state.Current, state.Time, 1f);
            Compose(layer, MathF.Min(weight, 1f));
        }
        foreach (int b in Graph.ParameterBindings)
        {
            if (_written[b] != _frame)
                continue;
            int p = Graph.Bindings[b].Parameter;
            Values[p] = _q[b].x + Graph.ParameterDefaults[p] * (1f - _a[b]) + (_extraStamp[b] == _frame ? _extra[b].x : 0f);
            Animated.Add(p);
        }
        foreach (int b in _touched)
            Compare(b);
        foreach (int b in _extras)
            if (_written[b] != _frame)
                Compare(b);
        foreach (int b in _lastExtras)
            if (_written[b] != _frame && _extraStamp[b] != _frame)
                Compare(b);
        Resting = _steady && !_stirred && Changed.Count == 0 && Unmoved() && Settles();
    }

    private void Compare(int b)
    {
        var extra = _extraStamp[b] == _frame ? _extra[b] : float4.Zero;
        if (_shownQ[b] == _q[b] && _shownA[b] == _a[b] && _shownRef[b] == _ref[b] && _shownExtra[b] == extra)
            return;
        _shownQ[b] = _q[b];
        _shownA[b] = _a[b];
        _shownRef[b] = _ref[b];
        _shownExtra[b] = extra;
        Changed.Add(b);
    }

    private void AddState(UnityAnimatorGraph.Layer layer, int stateIndex, double time, float weight)
    {
        if (weight <= 0f)
            return;
        var state = Graph.States[stateIndex];
        int motion = state.Motion;
        if (layer.SyncedMotions != null)
            motion = layer.SyncedMotions.TryGetValue(stateIndex, out int synced) ? synced : -1;
        bool mirror = state.MirrorParameter >= 0 ? Values[state.MirrorParameter] > 0.5f : state.Mirror;
        var cover = state.WriteDefaults ? layer.Set : Graph.SetOf(motion);
        foreach (int b in cover)
        {
            int at = mirror && Graph.Bindings[b].Mirror >= 0 && !state.WriteDefaults ? Graph.Bindings[b].Mirror : b;
            Touch(at);
            _bc[at] += weight;
        }
        if (motion < 0)
            return;
        float offset = state.CycleOffsetParameter >= 0 ? Values[state.CycleOffsetParameter] : state.CycleOffset;
        Motion(motion, time + offset, weight, mirror, layer.Additive, 0);
    }

    private void Motion(int motion, double time, float weight, bool mirror, bool additive, int depth)
    {
        if (motion < 0 || depth > 32)
            return;
        if (Graph.IsClip(motion))
        {
            Clip(Graph.Clips[motion], time, weight, mirror, additive);
            return;
        }
        var tree = Graph.TreeOf(motion);
        var weights = _weights[depth];
        Weigh(tree, weights);
        for (int i = 0; i < tree.Children.Length; i++)
        {
            float w = weights[i];
            if (w == 0f || tree.Children[i] < 0)
                continue;
            Motion(tree.Children[i], time * tree.TimeScale[i] + tree.CycleOffset[i], weight * w, mirror ^ tree.Mirror[i], additive, depth + 1);
        }
    }

    private void Clip(UnityAnimatorGraph.Clip clip, double normalized, float weight, bool mirror, bool additive)
    {
        float length = clip.Length;
        float time = length <= 0f ? 0f
            : clip.Loop ? (float)((normalized - System.Math.Floor(normalized)) * length)
            : (float)System.Math.Clamp(normalized, 0.0, 1.0) * length;
        foreach (var track in clip.Tracks)
        {
            if (additive && track.Constant)
                continue;
            int b = track.Binding;
            var binding = Graph.Bindings[b];
            var value = track.Constant ? track.First : UnityAnimatorGraph.Sample(track, time);
            if (additive)
                value = binding.Kind == UnityAnimatorGraph.ValueKind.Rotation ? Difference(value, track.Reference) : value - track.Reference;
            if (mirror && binding.Muscle && binding.Mirror >= 0)
            {
                if (binding.MirrorSign < 0f)
                    value.x = -value.x;
                b = binding.Mirror;
            }
            Touch(b);
            if (binding.Kind == UnityAnimatorGraph.ValueKind.Rotation && Dot(_bq[b], value) < 0f)
                value = -value;
            _bq[b] += value * weight;
            _ba[b] += weight;
        }
        if (additive)
            return;
        foreach (var track in clip.References)
        {
            int b = track.Binding;
            int value = UnityAnimatorGraph.SampleReference(track, time);
            Touch(b);
            _ba[b] += weight;
            if (weight > _brefWeight[b])
            {
                _brefWeight[b] = weight;
                _bref[b] = value;
            }
        }
    }

    private static float4 Difference(float4 rotation, float4 reference)
    {
        var q = new floatQ(rotation.x, rotation.y, rotation.z, rotation.w) * new floatQ(reference.x, reference.y, reference.z, reference.w).Inverse;
        return new float4(q.x, q.y, q.z, q.w);
    }

    private static float Dot(float4 a, float4 b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

    private void Touch(int b)
    {
        if (_bstamp[b] == _bgen)
            return;
        _bstamp[b] = _bgen;
        _btouched[_bcount++] = b;
        _bq[b] = float4.Zero;
        _ba[b] = 0f;
        _bc[b] = 0f;
        _bref[b] = -1;
        _brefWeight[b] = 0f;
    }

    private void Compose(UnityAnimatorGraph.Layer layer, float weight)
    {
        var allowed = layer.Allowed;
        for (int k = 0; k < _bcount; k++)
        {
            int b = _btouched[k];
            if (allowed != null && !allowed[b])
                continue;
            var kind = Graph.Bindings[b].Kind;
            if (layer.Additive)
            {
                if (_ba[b] == 0f || kind == UnityAnimatorGraph.ValueKind.Reference)
                    continue;
                if (_extraStamp[b] != _frame)
                {
                    _extraStamp[b] = _frame;
                    _extras.Add(b);
                    _extra[b] = kind == UnityAnimatorGraph.ValueKind.Rotation ? new float4(0, 0, 0, 1) : float4.Zero;
                }
                if (kind == UnityAnimatorGraph.ValueKind.Rotation)
                {
                    var d = _bq[b] + new float4(0, 0, 0, 1) * (1f - _ba[b]);
                    var delta = floatQ.Slerp(floatQ.Identity, Normal(d), weight);
                    var q = delta * new floatQ(_extra[b].x, _extra[b].y, _extra[b].z, _extra[b].w);
                    _extra[b] = new float4(q.x, q.y, q.z, q.w);
                }
                else
                    _extra[b] += _bq[b] * weight;
                continue;
            }
            float c = _bc[b];
            if (c <= 0f)
                continue;
            if (_written[b] != _frame)
            {
                _written[b] = _frame;
                _touched.Add(b);
            }
            if (kind == UnityAnimatorGraph.ValueKind.Reference)
            {
                if (weight * c >= 0.5f)
                {
                    _ref[b] = _brefWeight[b] >= 0.5f * c ? _bref[b] : -1;
                    _a[b] = _ref[b] >= 0 ? 1f : 0f;
                }
                continue;
            }
            var sq = _q[b];
            var bq = _bq[b];
            if (kind == UnityAnimatorGraph.ValueKind.Rotation && Dot(sq, bq) < 0f)
                bq = -bq;
            _q[b] = sq + (bq - sq * c) * weight;
            _a[b] += (_ba[b] - _a[b] * c) * weight;
        }
    }

    private static floatQ Normal(float4 v)
    {
        float length = MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z + v.w * v.w);
        return length > 1e-8f ? new floatQ(v.x / length, v.y / length, v.z / length, v.w / length) : floatQ.Identity;
    }

    // ---- Output ----

    // How much of a property the animation holds: 0 at rest.
    public float Amount(int binding) => _a[binding];

    public bool HasExtra(int binding) => _extraStamp[binding] == _frame;

    // A number or vector property's value over this rest.
    public float4 Value(int binding, float4 rest)
    {
        var v = _q[binding] + rest * (1f - _a[binding]);
        return _extraStamp[binding] == _frame ? v + _extra[binding] : v;
    }

    public floatQ Rotation(int binding, floatQ rest)
    {
        var q = _q[binding];
        var r = new float4(rest.x, rest.y, rest.z, rest.w);
        if (Dot(q, r) < 0f)
            r = -r;
        var result = Normal(q + r * (1f - _a[binding]));
        if (_extraStamp[binding] == _frame)
            result = new floatQ(_extra[binding].x, _extra[binding].y, _extra[binding].z, _extra[binding].w) * result;
        return result;
    }

    public int Reference(int binding) => _ref[binding];

    // A muscle: the animated value and how much of the pose it holds, and an additive change on top.
    public (float Value, float Weight, float Extra) Muscle(int binding)
    {
        float a = _a[binding];
        float extra = _extraStamp[binding] == _frame ? _extra[binding].x : 0f;
        return (a > 1e-6f ? _q[binding].x / a : 0f, System.Math.Clamp(a, 0f, 1f), extra);
    }

    public void SetParameter(int index, float value)
    {
        if (index >= 0 && index < Values.Length)
            Values[index] = value;
    }
}
