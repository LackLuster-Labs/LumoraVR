// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Math;

namespace Lumora.Core.Assets.Interop.Unity;

// A program compiled for playing: every animated property, parameter, clip, blend tree, state, transition
// and behaviour turned into an index, curves into flat key arrays, and per layer the properties it can
// write, filtered by its mask. Built once per program and shared by every avatar playing it.
//
// Properties are what the clips name: (object path, component class, attribute), with positions, scales
// and rotations whole. Euler curves become rotations. A class 95 curve named after a parameter animates
// that parameter; any other class 95 curve is a humanoid muscle, which only the body playable layers may
// move. -xlinka
public sealed class UnityAnimatorGraph
{
    public enum ValueKind : byte { Float, Vector, Rotation, Reference }

    public sealed class Binding
    {
        public UnityBinding Key;
        public ValueKind Kind;
        public int Parameter = -1;
        public bool Muscle;
        public int BodyPart = -1;
        public int Mirror = -1;
        public float MirrorSign = 1f;
    }

    public sealed class Track
    {
        public int Binding;
        public int Components;
        public bool Euler;
        public float[] Times = Array.Empty<float>();
        public float[] Values = Array.Empty<float>();
        public float[] In = Array.Empty<float>();
        public float[] Out = Array.Empty<float>();
        public bool Constant;
        public float4 First;
        public float4 Reference;
    }

    public sealed class ReferenceTrack
    {
        public int Binding;
        public float[] Times = Array.Empty<float>();
        public int[] Values = Array.Empty<int>();
    }

    public sealed class Clip
    {
        public string Key = "";
        public string Name = "";
        public float Length;
        public bool Loop;
        public Track[] Tracks = Array.Empty<Track>();
        public ReferenceTrack[] References = Array.Empty<ReferenceTrack>();
        public bool Constant;
        public int[] Set = Array.Empty<int>();
    }

    public sealed class Tree
    {
        public int Type;
        public int Parameter = -1, ParameterY = -1;
        public bool Normalized;
        public int[] Children = Array.Empty<int>();
        public float[] Thresholds = Array.Empty<float>();
        public float2[] Positions = Array.Empty<float2>();
        public int[] Direct = Array.Empty<int>();
        public float[] TimeScale = Array.Empty<float>();
        public float[] CycleOffset = Array.Empty<float>();
        public bool[] Mirror = Array.Empty<bool>();
        public int[] Order = Array.Empty<int>();
        public int[] Set = Array.Empty<int>();
    }

    public sealed record Condition(int Parameter, int Mode, float Threshold);

    public sealed class Transition
    {
        public int Destination = -1;
        public int DestinationMachine = -1;
        public bool Exit;
        public bool HasExitTime;
        public float ExitTime;
        public float Duration;
        public bool FixedDuration;
        public float Offset;
        public bool CanTransitionToSelf;
        public bool Muted, Solo;
        public int InterruptionSource;
        public bool OrderedInterruption;
        public Condition[] Conditions = Array.Empty<Condition>();
        public int[] Triggers = Array.Empty<int>();
    }

    public sealed class State
    {
        public string Name = "";
        public int Motion = -1;
        public bool WriteDefaults;
        public float Speed = 1f, CycleOffset;
        public bool Mirror;
        public int TimeParameter = -1, SpeedParameter = -1, MirrorParameter = -1, CycleOffsetParameter = -1;
        public int[] Transitions = Array.Empty<int>();
        public int[] Behaviours = Array.Empty<int>();
        public int Machine = -1;
    }

    public sealed class Machine
    {
        public int Parent = -1;
        public int DefaultState = -1;
        public int[] Entry = Array.Empty<int>();
        public readonly Dictionary<int, int[]> Exits = new();
    }

    public sealed class Layer
    {
        public string Name = "";
        public int Machine = -1;
        public float Weight;
        public bool Additive;
        public int Playable;
        public int PlayableIndex;
        public int[] Any = Array.Empty<int>();
        public int[] Set = Array.Empty<int>();
        public bool[]? Allowed;
        public int Synced = -1;
        public Dictionary<int, int>? SyncedMotions;
    }

    public sealed record DriverEntry(UnityAnimatorController.DriverOperation Type, int Parameter, int Source, float Value, float Min, float Max,
        float Chance, bool ConvertRange, float SourceMin, float SourceMax, float DestMin, float DestMax);

    public sealed class Driver
    {
        public bool LocalOnly;
        public DriverEntry[] Entries = Array.Empty<DriverEntry>();
    }

    public sealed class LayerControl
    {
        public int Layer = -1;
        public float Goal, Duration;
    }

    public sealed class PlayableControl
    {
        public int Playable;
        public float Goal, Duration;
    }

    public sealed class PlayAudio
    {
        public UnityAnimatorController.PlayAudio Source = null!;
        public int Parameter = -1;
        // The audio object's path from the avatar's root.
        public string Path = "";
    }

    public readonly List<Binding> Bindings = new();
    public readonly Dictionary<UnityBinding, int> BindingIndex = new();
    public readonly List<UnityRef> References = new();
    private readonly Dictionary<UnityRef, int> _referenceIndex = new();
    public readonly List<string> ParameterNames = new();
    public readonly List<UnityAnimatorController.ParameterType> ParameterTypes = new();
    public readonly List<float> ParameterDefaults = new();
    public readonly Dictionary<string, int> ParameterIndex = new(StringComparer.Ordinal);
    public readonly List<Clip> Clips = new();
    public readonly List<Tree> Trees = new();
    public readonly List<State> States = new();
    public readonly List<Transition> Transitions = new();
    public readonly List<Machine> Machines = new();
    public readonly List<Layer> Layers = new();
    // Drivers, tracking controls, layer and playable controls and audio, by behaviour index.
    public readonly List<object> Behaviours = new();
    // Whether any state carries a parameter driver: what the owning machine sets and sends to everyone.
    public bool HasDrivers;
    public int[] MuscleBindings = Array.Empty<int>();
    public int[] ParameterBindings = Array.Empty<int>();
    public int[] Playables = Array.Empty<int>();
    public int MaxTreeChildren;
    // Per motion: whether anything in it loops, and its length when no blend can change it (NaN when one can).
    public bool[] MotionLoops = Array.Empty<bool>();
    public float[] MotionLength = Array.Empty<float>();
    // Per motion: whether every curve under it is flat, so playing it gives the same pose at any time.
    public bool[] MotionStatic = Array.Empty<bool>();

    private readonly Dictionary<string, int> _motions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, int> _stateIndex = new();
    private readonly Dictionary<long, int> _machineIndex = new();
    private readonly Dictionary<long, int> _transitionIndex = new();
    private readonly Dictionary<long, int> _behaviourIndex = new();
    private readonly UnityAnimatorProgram _program;
    private readonly UnityAnimatorController _controller;

    public int ClipCount => Clips.Count;
    public bool IsClip(int motion) => motion >= 0 && motion < Clips.Count;
    public Tree TreeOf(int motion) => Trees[motion - Clips.Count];

    public UnityAnimatorGraph(UnityAnimatorProgram program)
    {
        _program = program;
        _controller = program.Controller;
        foreach (var p in _controller.Parameters)
            AddParameter(p.Name, p.Type, program.Defaults.TryGetValue(p.Name, out var d) ? d : p.Default);
        foreach (var name in _controller.ReferencedParameters())
            if (name.Length > 0 && !ParameterIndex.ContainsKey(name))
                AddParameter(name, UnityAnimatorController.ParameterType.Float, program.Defaults.GetValueOrDefault(name));

        // Clips first so a motion index below Clips.Count is a clip.
        foreach (var (key, clip) in program.Clips)
            AddClip(key, clip);

        foreach (var id in _controller.Machines.Keys)
            _machineIndex[id] = _machineIndex.Count;
        foreach (var id in _controller.States.Keys)
            _stateIndex[id] = _stateIndex.Count;
        foreach (var id in _controller.Transitions.Keys)
            _transitionIndex[id] = _transitionIndex.Count;
        foreach (var (id, behaviour) in _controller.Behaviours)
        {
            _behaviourIndex[id] = Behaviours.Count;
            Behaviours.Add(behaviour);
        }

        foreach (var (id, m) in _controller.Machines)
        {
            var machine = new Machine
            {
                DefaultState = _stateIndex.GetValueOrDefault(m.DefaultState, -1),
                Entry = m.EntryTransitions.Where(_transitionIndex.ContainsKey).Select(t => _transitionIndex[t]).ToArray(),
            };
            foreach (var (from, list) in m.MachineTransitions)
                if (_machineIndex.TryGetValue(from, out int child))
                    machine.Exits[child] = list.Where(_transitionIndex.ContainsKey).Select(t => _transitionIndex[t]).ToArray();
            Machines.Add(machine);
        }
        foreach (var (id, m) in _controller.Machines)
            foreach (var child in m.Machines)
                if (_machineIndex.TryGetValue(child, out int c))
                    Machines[c].Parent = _machineIndex[id];

        foreach (var (id, s) in _controller.States)
        {
            States.Add(new State
            {
                Name = s.Name, Motion = Motion(s.Motion, 0), WriteDefaults = s.WriteDefaults, Speed = s.Speed, CycleOffset = s.CycleOffset,
                Mirror = s.Mirror, TimeParameter = Param(s.TimeParameter), SpeedParameter = Param(s.SpeedParameter),
                MirrorParameter = Param(s.MirrorParameter), CycleOffsetParameter = Param(s.CycleOffsetParameter),
                Transitions = s.Transitions.Where(_transitionIndex.ContainsKey).Select(t => _transitionIndex[t]).ToArray(),
            });
        }
        foreach (var (id, m) in _controller.Machines)
            foreach (var state in m.States)
                if (_stateIndex.TryGetValue(state, out int s))
                    States[s].Machine = _machineIndex[id];
        // A machine's behaviours run for every state inside it.
        var machineIds = _machineIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var (id, s) in _controller.States)
        {
            var list = new List<int>();
            foreach (var b in s.Behaviours)
                if (_behaviourIndex.TryGetValue(b, out int at)) list.Add(at);
            for (int m = States[_stateIndex[id]].Machine, depth = 0; m >= 0 && depth < 32; m = Machines[m].Parent, depth++)
                foreach (var b in _controller.Machines[machineIds[m]].Behaviours)
                    if (_behaviourIndex.TryGetValue(b, out int at)) list.Add(at);
            States[_stateIndex[id]].Behaviours = list.ToArray();
        }

        foreach (var (_, t) in _controller.Transitions)
        {
            var conditions = t.Conditions.Where(c => c.Parameter.Length > 0)
                .Select(c => new Condition(Param(c.Parameter), c.Mode, c.Threshold)).ToArray();
            Transitions.Add(new Transition
            {
                Destination = _stateIndex.GetValueOrDefault(t.DstState, -1),
                DestinationMachine = _machineIndex.GetValueOrDefault(t.DstMachine, -1),
                Exit = t.IsExit, HasExitTime = t.HasExitTime, ExitTime = t.ExitTime, Duration = t.Duration, FixedDuration = t.FixedDuration,
                Offset = t.Offset, CanTransitionToSelf = t.CanTransitionToSelf, Muted = t.Muted, Solo = t.Solo,
                InterruptionSource = t.InterruptionSource, OrderedInterruption = t.OrderedInterruption, Conditions = conditions,
                Triggers = conditions.Where(c => c.Parameter >= 0 && ParameterTypes[c.Parameter] == UnityAnimatorController.ParameterType.Trigger)
                    .Select(c => c.Parameter).Distinct().ToArray(),
            });
        }

        foreach (var layer in _controller.Layers)
            Layers.Add(CompileLayer(layer));
        for (int i = 0; i < Behaviours.Count; i++)
        {
            Behaviours[i] = CompileBehaviour(Behaviours[i]);
            if (Behaviours[i] is Driver)
                HasDrivers = true;
        }

        Measure();
        MuscleBindings = Enumerable.Range(0, Bindings.Count).Where(i => Bindings[i].Muscle).ToArray();
        ParameterBindings = Enumerable.Range(0, Bindings.Count).Where(i => Bindings[i].Parameter >= 0).ToArray();
        Playables = Layers.Select(l => l.Playable).Distinct().OrderBy(p => p).ToArray();
        // Masks are read last, once every property is known.
        for (int i = 0; i < _controller.Layers.Count; i++)
            Layers[i].Allowed = Allowed(_controller.Layers[i], Layers[i]);
    }

    private void AddParameter(string name, UnityAnimatorController.ParameterType type, float value)
    {
        ParameterIndex[name] = ParameterNames.Count;
        ParameterNames.Add(name);
        ParameterTypes.Add(type);
        ParameterDefaults.Add(value);
    }

    private int Param(string? name) => string.IsNullOrEmpty(name) ? -1 : ParameterIndex.GetValueOrDefault(name, -1);

    public int Reference(UnityRef value)
    {
        if (value.IsNull)
            return -1;
        if (_referenceIndex.TryGetValue(value, out int at))
            return at;
        _referenceIndex[value] = References.Count;
        References.Add(value);
        return References.Count - 1;
    }

    private int Bind(UnityBinding key, ValueKind kind)
    {
        if (BindingIndex.TryGetValue(key, out int at))
            return at;
        var binding = new Binding { Key = key, Kind = kind };
        if (key.ClassId == 95)
        {
            if (key.Attribute.Length > 0 && ParameterIndex.TryGetValue(key.Attribute, out int p))
                binding.Parameter = p;
            else
            {
                binding.Muscle = true;
                binding.BodyPart = BodyPart(key.Attribute);
            }
        }
        at = Bindings.Count;
        Bindings.Add(binding);
        BindingIndex[key] = at;
        if (binding.Muscle && MirrorName(key.Attribute) is var (name, sign))
        {
            binding.MirrorSign = sign;
            if (name == key.Attribute)
                binding.Mirror = at;
            else
            {
                binding.Mirror = Bind(key with { Attribute = name }, kind);
                Bindings[binding.Mirror].Mirror = at;
                Bindings[binding.Mirror].MirrorSign = sign;
            }
        }
        return at;
    }

    // A muscle's left-right twin, and the sign its value takes across the mirror.
    private static (string Name, float Sign) MirrorName(string attribute)
    {
        if (attribute.StartsWith("Left", StringComparison.Ordinal))
            return ("Right" + attribute[4..], 1f);
        if (attribute.StartsWith("Right", StringComparison.Ordinal))
            return ("Left" + attribute[5..], 1f);
        if (attribute.EndsWith("Left-Right", StringComparison.Ordinal) || attribute is "RootT.x" or "RootQ.y" or "RootQ.z")
            return (attribute, -1f);
        return (attribute, 1f);
    }

    // Unity's avatar mask body parts: root, body, head, legs, arms, fingers, then the four IK goals.
    private static int BodyPart(string attribute)
    {
        if (attribute.StartsWith("Root", StringComparison.Ordinal)) return 0;
        if (attribute.StartsWith("LeftFoot", StringComparison.Ordinal) && attribute.Length > 8 && attribute[8] is 'T' or 'Q') return 9;
        if (attribute.StartsWith("RightFoot", StringComparison.Ordinal) && attribute.Length > 9 && attribute[9] is 'T' or 'Q') return 10;
        if (attribute.StartsWith("LeftHand", StringComparison.Ordinal) && attribute.Length > 8 && attribute[8] is 'T' or 'Q') return 11;
        if (attribute.StartsWith("RightHand", StringComparison.Ordinal) && attribute.Length > 9 && attribute[9] is 'T' or 'Q') return 12;
        bool left = attribute.StartsWith("Left", StringComparison.Ordinal);
        bool right = attribute.StartsWith("Right", StringComparison.Ordinal);
        if (left || right)
        {
            string rest = attribute[(left ? 4 : 5)..].TrimStart('.', ' ');
            if (rest.StartsWith("Hand.", StringComparison.Ordinal) || rest.StartsWith("Thumb", StringComparison.Ordinal)
                || rest.StartsWith("Index", StringComparison.Ordinal) || rest.StartsWith("Middle", StringComparison.Ordinal)
                || rest.StartsWith("Ring", StringComparison.Ordinal) || rest.StartsWith("Little", StringComparison.Ordinal))
                return left ? 7 : 8;
            if (rest.StartsWith("Eye", StringComparison.Ordinal))
                return 2;
            if (rest.StartsWith("Upper Leg", StringComparison.Ordinal) || rest.StartsWith("Lower Leg", StringComparison.Ordinal)
                || rest.StartsWith("Foot", StringComparison.Ordinal) || rest.StartsWith("Toes", StringComparison.Ordinal))
                return left ? 3 : 4;
            return left ? 5 : 6;
        }
        if (attribute.StartsWith("Neck", StringComparison.Ordinal) || attribute.StartsWith("Head", StringComparison.Ordinal)
            || attribute.StartsWith("Jaw", StringComparison.Ordinal))
            return 2;
        return 1;
    }

    private int AddClip(string key, UnityAnimationClip source)
    {
        var clip = new Clip { Key = key, Name = source.Name, Length = source.Length, Loop = source.Loop };
        var tracks = new List<Track>();
        float referenceTime = source.AdditiveReferenceTime;
        foreach (var c in source.Floats)
        {
            var track = new Track
            {
                Binding = Bind(new UnityBinding(c.Path, c.ClassId, c.Attribute, c.Script), ValueKind.Float), Components = 1,
                Times = c.Keys.Select(k => k.Time).ToArray(), Values = c.Keys.Select(k => k.Value).ToArray(),
                In = c.Keys.Select(k => k.InSlope).ToArray(), Out = c.Keys.Select(k => k.OutSlope).ToArray(),
                Constant = c.IsConstant,
            };
            tracks.Add(Finish(track));
        }
        void Vectors(List<UnityAnimationClip.VectorCurve> curves, string attribute, bool euler)
        {
            foreach (var c in curves)
            {
                var track = new Track
                {
                    Binding = Bind(new UnityBinding(c.Path, 4, attribute, null), euler ? ValueKind.Rotation : ValueKind.Vector),
                    Components = 3, Euler = euler, Times = c.Keys.Select(k => k.Time).ToArray(),
                    Values = c.Keys.SelectMany(k => new[] { k.Value.x, k.Value.y, k.Value.z }).ToArray(),
                    In = c.Keys.SelectMany(k => new[] { k.InSlope.x, k.InSlope.y, k.InSlope.z }).ToArray(),
                    Out = c.Keys.SelectMany(k => new[] { k.OutSlope.x, k.OutSlope.y, k.OutSlope.z }).ToArray(),
                    Constant = c.IsConstant,
                };
                tracks.Add(Finish(track));
            }
        }
        Vectors(source.Positions, "position", false);
        Vectors(source.Scales, "scale", false);
        Vectors(source.Eulers, "rotation", true);
        foreach (var c in source.Rotations)
        {
            var track = new Track
            {
                Binding = Bind(new UnityBinding(c.Path, 4, "rotation", null), ValueKind.Rotation), Components = 4,
                Times = c.Keys.Select(k => k.Time).ToArray(),
                Values = c.Keys.SelectMany(k => new[] { k.Value.x, k.Value.y, k.Value.z, k.Value.w }).ToArray(),
                In = c.Keys.SelectMany(k => new[] { k.InSlope.x, k.InSlope.y, k.InSlope.z, k.InSlope.w }).ToArray(),
                Out = c.Keys.SelectMany(k => new[] { k.OutSlope.x, k.OutSlope.y, k.OutSlope.z, k.OutSlope.w }).ToArray(),
                Constant = c.IsConstant,
            };
            tracks.Add(Finish(track));
        }
        Track Finish(Track track)
        {
            track.First = Sample(track, 0f);
            track.Reference = track.Constant ? track.First : Sample(track, referenceTime);
            return track;
        }
        clip.Tracks = tracks.ToArray();
        clip.References = source.References.Select(c => new ReferenceTrack
        {
            Binding = Bind(new UnityBinding(c.Path, c.ClassId, c.Attribute, c.Script), ValueKind.Reference),
            Times = c.Keys.Select(k => k.Time).ToArray(), Values = c.Keys.Select(k => Reference(k.Value)).ToArray(),
        }).ToArray();
        clip.Constant = clip.Tracks.All(t => t.Constant) && clip.References.All(r => r.Values.Distinct().Count() <= 1);
        clip.Set = clip.Tracks.Select(t => t.Binding).Concat(clip.References.Select(r => r.Binding)).Distinct().OrderBy(b => b).ToArray();
        _motions["c:" + key] = Clips.Count;
        Clips.Add(clip);
        return Clips.Count - 1;
    }

    // A track's value at a time, as a float4: x for a number, xyz for a vector, xyzw for a rotation, an
    // euler track already turned into its rotation.
    public static float4 Sample(Track track, float time)
    {
        var times = track.Times;
        int n = times.Length;
        int c = track.Components;
        if (n == 0)
            return c == 4 ? new float4(0, 0, 0, 1) : float4.Zero;
        float x, y = 0f, z = 0f, w = 0f;
        if (track.Constant || n == 1 || time <= times[0])
            Read(track, 0, out x, out y, out z, out w);
        else if (time >= times[n - 1])
            Read(track, n - 1, out x, out y, out z, out w);
        else
        {
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (times[mid] <= time) lo = mid;
                else hi = mid;
            }
            float span = times[hi] - times[lo];
            float t = span > 0f ? (time - times[lo]) / span : 1f;
            var v = track.Values;
            int a = lo * c, b = hi * c;
            x = UnityAnimationClip.Hermite(v[a], track.Out[a], v[b], track.In[b], span, t);
            if (c > 1)
            {
                y = UnityAnimationClip.Hermite(v[a + 1], track.Out[a + 1], v[b + 1], track.In[b + 1], span, t);
                z = UnityAnimationClip.Hermite(v[a + 2], track.Out[a + 2], v[b + 2], track.In[b + 2], span, t);
            }
            if (c > 3)
                w = UnityAnimationClip.Hermite(v[a + 3], track.Out[a + 3], v[b + 3], track.In[b + 3], span, t);
        }
        if (track.Euler)
        {
            var q = EulerDegrees(x, y, z);
            return new float4(q.x, q.y, q.z, q.w);
        }
        if (c == 4)
        {
            float length = MathF.Sqrt(x * x + y * y + z * z + w * w);
            return length > 1e-8f ? new float4(x / length, y / length, z / length, w / length) : new float4(0, 0, 0, 1);
        }
        return new float4(x, y, z, w);
    }

    private static void Read(Track track, int key, out float x, out float y, out float z, out float w)
    {
        int c = track.Components, at = key * c;
        x = track.Values[at];
        y = c > 1 ? track.Values[at + 1] : 0f;
        z = c > 2 ? track.Values[at + 2] : 0f;
        w = c > 3 ? track.Values[at + 3] : 0f;
    }

    // Unity's euler order: Z, then X, then Y.
    public static floatQ EulerDegrees(float x, float y, float z)
    {
        const float toRadians = MathF.PI / 180f;
        return floatQ.AxisAngle(new float3(0, 1, 0), y * toRadians) * floatQ.AxisAngle(new float3(1, 0, 0), x * toRadians)
            * floatQ.AxisAngle(new float3(0, 0, 1), z * toRadians);
    }

    public static int SampleReference(ReferenceTrack track, float time)
    {
        var times = track.Times;
        if (times.Length == 0)
            return -1;
        int value = track.Values[0];
        for (int i = 0; i < times.Length && times[i] <= time; i++)
            value = track.Values[i];
        return value;
    }

    // A motion's index: a clip, or a blend tree compiled the first time it is reached. -1 for a reference
    // to nothing the package carries.
    private int Motion(UnityRef motion, int depth)
    {
        if (motion.IsNull || depth > 32)
            return -1;
        string key = motion.Guid is { } guid ? (_program.TryGetTree(motion, out _) ? "e:" + guid + ":" + motion.FileId : "c:" + guid) : "t:" + motion.FileId;
        if (_motions.TryGetValue(key, out int known))
            return known;
        if (!_program.TryGetTree(motion, out var source))
            return -1;
        var tree = new Tree
        {
            Type = source.Type, Parameter = Param(source.Parameter), ParameterY = Param(source.ParameterY), Normalized = source.Normalized,
        };
        int index = Clips.Count + Trees.Count;
        _motions[key] = index;
        Trees.Add(tree);
        int count = source.Children.Count;
        var children = new int[count];
        for (int i = 0; i < count; i++)
            children[i] = Motion(UnityAnimatorProgram.Retag(source.Children[i].Motion, motion), depth + 1);
        tree.Children = children;
        tree.Thresholds = source.Children.Select(c => c.Threshold).ToArray();
        tree.Direct = source.Children.Select(c => Param(c.DirectParameter)).ToArray();
        tree.Positions = Enumerable.Range(0, count).Select(i => i < source.Positions.Count ? new float2(source.Positions[i].X, source.Positions[i].Y) : float2.Zero).ToArray();
        tree.TimeScale = Enumerable.Range(0, count).Select(i => i < source.Playback.Count ? source.Playback[i].Speed : 1f).ToArray();
        tree.CycleOffset = Enumerable.Range(0, count).Select(i => i < source.Playback.Count ? source.Playback[i].Offset : 0f).ToArray();
        tree.Mirror = Enumerable.Range(0, count).Select(i => i < source.Playback.Count && source.Playback[i].Mirror).ToArray();
        tree.Order = Enumerable.Range(0, count).OrderBy(i => tree.Thresholds[i]).ToArray();
        tree.Set = children.Where(c => c >= 0).SelectMany(SetOf).Distinct().OrderBy(b => b).ToArray();
        MaxTreeChildren = System.Math.Max(MaxTreeChildren, count);
        return index;
    }

    // A tree whose children all last the same time lasts that long whatever its weights; a missing child counts
    // as a second, as an empty state does.
    private void Measure()
    {
        int count = Clips.Count + Trees.Count;
        MotionLoops = new bool[count];
        MotionLength = new float[count];
        MotionStatic = new bool[count];
        for (int i = 0; i < Clips.Count; i++)
        {
            MotionLoops[i] = Clips[i].Loop;
            MotionLength[i] = MathF.Max(Clips[i].Length, 1f / 60f);
            MotionStatic[i] = Clips[i].Constant || Clips[i].Length <= 0f;
        }
        var done = new bool[Trees.Count];
        for (int t = 0; t < Trees.Count; t++)
            MeasureTree(t, done, 0);
    }

    private void MeasureTree(int t, bool[] done, int depth)
    {
        if (done[t] || depth > 32)
            return;
        done[t] = true;
        int index = Clips.Count + t;
        var tree = Trees[t];
        bool loops = false, still = true;
        float length = float.NaN;
        bool fixedLength = true;
        for (int i = 0; i < tree.Children.Length; i++)
        {
            int child = tree.Children[i];
            float childLength = 1f;
            if (child >= 0)
            {
                if (!IsClip(child))
                    MeasureTree(child - Clips.Count, done, depth + 1);
                loops |= MotionLoops[child];
                still &= MotionStatic[child];
                childLength = MotionLength[child];
            }
            float scale = MathF.Abs(tree.TimeScale[i]) > 1e-4f ? MathF.Abs(tree.TimeScale[i]) : 1f;
            childLength /= scale;
            if (float.IsNaN(childLength) || !float.IsNaN(length) && MathF.Abs(childLength - length) > 1e-6f)
                fixedLength = false;
            else
                length = childLength;
        }
        MotionLoops[index] = loops;
        MotionStatic[index] = still;
        MotionLength[index] = fixedLength && !float.IsNaN(length) ? MathF.Max(length, 1f / 60f) : tree.Children.Length == 0 ? 1f : float.NaN;
    }

    public int[] SetOf(int motion) => motion < 0 ? Array.Empty<int>() : IsClip(motion) ? Clips[motion].Set : TreeOf(motion).Set;

    private Layer CompileLayer(UnityAnimatorController.Layer source)
    {
        var layer = new Layer
        {
            Name = source.Name, Machine = _machineIndex.GetValueOrDefault(source.Machine, -1), Weight = source.Weight, Additive = source.Additive,
            Playable = source.Playable, PlayableIndex = source.PlayableIndex, Synced = source.SyncedLayer,
        };
        var any = new List<int>();
        var set = new HashSet<int>();
        var machineSource = source.SyncedLayer >= 0 && source.SyncedLayer < _controller.Layers.Count ? _controller.Layers[source.SyncedLayer].Machine : source.Machine;
        foreach (var machine in _controller.MachinesUnder(machineSource))
        {
            foreach (var t in machine.AnyTransitions)
                if (_transitionIndex.TryGetValue(t, out int at)) any.Add(at);
            foreach (var s in machine.States)
                if (_stateIndex.TryGetValue(s, out int state) && source.SyncedLayer < 0)
                    AddSet(States[state]);
        }
        if (source.SyncedLayer >= 0)
        {
            layer.Machine = _machineIndex.GetValueOrDefault(machineSource, -1);
            layer.SyncedMotions = new Dictionary<int, int>();
            foreach (var (state, motion) in source.SyncedMotions)
                if (_stateIndex.TryGetValue(state, out int s))
                {
                    int m = Motion(motion, 0);
                    layer.SyncedMotions[s] = m;
                    set.UnionWith(SetOf(m));
                }
        }
        layer.Any = any.ToArray();
        layer.Set = set.OrderBy(b => b).ToArray();
        return layer;

        void AddSet(State state)
        {
            set.UnionWith(SetOf(state.Motion));
            if (state.Mirror || state.MirrorParameter >= 0)
                foreach (var b in SetOf(state.Motion))
                    if (Bindings[b].Mirror >= 0) set.Add(Bindings[b].Mirror);
        }
    }

    // Which properties a layer may write: transforms its mask leaves on, muscles of body parts its mask
    // leaves on and only on the body playable layers, everything else always. Null when that is all of them.
    private bool[]? Allowed(UnityAnimatorController.Layer source, Layer layer)
    {
        var mask = _program.Mask(source);
        bool muscles = layer.Playable is 0 or 2 or 4;
        bool any = false;
        var allowed = new bool[Bindings.Count];
        for (int i = 0; i < allowed.Length; i++)
        {
            var b = Bindings[i];
            bool on = true;
            if (b.Muscle)
                on = muscles && (mask == null || b.BodyPart < 0 || mask.Body[b.BodyPart]);
            else if (b.Key.ClassId == 4 && mask != null)
                on = mask.AllowsTransform(b.Key.Path);
            allowed[i] = on;
            any |= !on;
        }
        return any ? allowed : null;
    }

    private object CompileBehaviour(object behaviour) => behaviour switch
    {
        UnityAnimatorController.ParameterDriver d => new Driver
        {
            LocalOnly = d.LocalOnly,
            Entries = d.Entries.Select(e => new DriverEntry(e.Type, Param(e.Name), Param(e.Source), e.Value, e.Min, e.Max, e.Chance,
                e.ConvertRange, e.SourceMin, e.SourceMax, e.DestMin, e.DestMax)).Where(e => e.Parameter >= 0).ToArray(),
        },
        // A merged controller's layer control names its own layers; the descriptor's controllers name the
        // descriptor's.
        UnityAnimatorController.LayerControl l => new LayerControl
        {
            Layer = _controller.Layers.FindIndex(x => x.Playable == l.Playable && x.PlayableIndex == l.Layer
                && x.Tag == (l.Playable == l.SourcePlayable ? l.Tag : "")), Goal = l.Goal, Duration = l.Duration,
        },
        UnityAnimatorController.PlayableControl p => new PlayableControl { Playable = p.Playable, Goal = p.Goal, Duration = p.Duration },
        UnityAnimatorController.PlayAudio a => new PlayAudio { Source = a, Parameter = Param(a.Parameter), Path = _program.PathOf(a.Tag, a.SourcePath) },
        _ => behaviour,
    };
}
