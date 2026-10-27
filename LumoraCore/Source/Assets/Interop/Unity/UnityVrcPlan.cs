// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Components.Avatar;

namespace Lumora.Core.Assets.Interop.Unity;

// A package avatar's animator and menu, read before anything is built. Pure data, worked out off the world
// thread.
//
// The avatar's playable layers (base, additive, gesture, action, FX) and whatever a build tool would merge
// into them at upload become one program, played live. A menu control only sets a parameter, so the menu
// keeps every control whose parameter the program reads.
//
// For when no live player can be kept, the FX layers are also read as poses: each layer is settled with a
// control's parameter set and with it unset, and the poses of the two states it lands in are the switch's
// on and off. The poses every layer settles into with nothing touched are how the avatar stands at rest. A
// radial becomes a slider read all along its range; the hand layers become one gesture per hand shape that
// changes what they play. -xlinka
public sealed class UnityVrcPlan
{
    public sealed class Switch
    {
        public string Path = "";
        public bool DefaultOn;
        public string Group = "";
        public string Parameter = "";
        public float OnValue = 1f, OffValue;
        // Shows nothing itself but unlocks contacts, so it stays in the menu.
        public bool Gate;
        // A menu button: on only while held.
        public bool Momentary;
        public bool Saved = true;
        public string? Icon;
        // The live program reads its parameter.
        public bool Live;
        public readonly UnityPose On = new();
        public readonly UnityPose Off = new();
    }

    // A value turned from 0 to 1: what the layers settle into at points along the range, in order.
    public sealed class Slider
    {
        public string Path = "";
        public string Parameter = "";
        public float Default;
        // A layer outside the FX reads it too, so it matters even when no pose here changes along it.
        public bool Body;
        public bool Saved = true;
        public string? Icon;
        public bool Live;
        public readonly List<(float At, UnityPose Pose)> Samples = new();
    }

    public sealed class Gesture
    {
        public GestureHand Hand;
        public HandGesture Sign;
        public bool FistWeight;
        public bool BlockBlinking;
        public readonly UnityPose On = new();
        public readonly UnityPose Off = new();
    }

    // Controllers, menus and parameters a build tool merges into the avatar at upload. Controllers carry the
    // descriptor's playable layer number (0 base, 2 additive, 3 gesture, 4 action, 5 FX); their clips name
    // objects below PathPrefix, after Rewrites; Parameters renames what they use. Controls are menu entries
    // the tool makes itself: a switch (the parameter 1 against 0) or a slider.
    public sealed class Merge
    {
        public string Name = "";
        public string PathPrefix = "";
        public bool RootBindingsApplyToAvatar;
        public readonly List<(int Type, string ControllerGuid)> Controllers = new();
        public readonly List<(string MenuGuid, string Prefix)> Menus = new();
        public readonly List<string> ParameterSets = new();
        public readonly Dictionary<string, string> Parameters = new(StringComparer.Ordinal);
        public readonly List<(string From, string To, bool Delete)> Rewrites = new();
        public readonly List<(string Path, string Parameter, bool Slider, float Default)> Controls = new();

        public string Rename(string name) => Parameters.TryGetValue(name, out var to) ? to : name;
    }

    public sealed record ParameterInfo(UnityExpressionParameters.ValueType Type, float Default, bool Saved, bool Synced);

    public readonly UnityPose Rest = new();
    public readonly List<Switch> Switches = new();
    public readonly List<Slider> Sliders = new();
    public readonly List<Gesture> Gestures = new();
    // Per contact parameter: what touching shows, what not touching shows, and the switches that must
    // be on for a touch to count at all.
    public readonly Dictionary<string, (UnityPose On, UnityPose Off, List<string> Gates)> Contacts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _gates = new(StringComparer.Ordinal);
    public readonly SortedDictionary<string, int> Skipped = new(StringComparer.Ordinal);
    public readonly HashSet<string> MaterialGuids = new(StringComparer.OrdinalIgnoreCase);
    // The menu's parameters: type, default, whether they are saved and whether they sync.
    public readonly Dictionary<string, ParameterInfo> Parameters = new(StringComparer.Ordinal);
    // The descriptor's playable layers that carry a controller of the avatar's own, and those merged in.
    public readonly SortedSet<int> Playables = new();
    // Menu icons: picture guid -> the extracted file and its kind.
    public readonly Dictionary<string, (string File, string Extension)> IconFiles = new(StringComparer.OrdinalIgnoreCase);
    public int MenuControls;
    public UnityAnimatorProgram? Program { get; private set; }
    public sealed record BodyProgram(string Name, int Type, UnityAnimatorProgram Program)
    {
        public Uri? AssetUri { get; set; }
    }
    // Kept empty: every playable layer plays in Program now.
    public readonly List<BodyProgram> BodyPrograms = new();

    private readonly UnityPackageArchive _archive;
    private UnityAnimatorController _fx = new();
    private readonly UnityExpressionParameters? _parameters;
    private readonly Dictionary<string, float> _defaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnityAnimationClip?> _clips = new(StringComparer.OrdinalIgnoreCase);
    private List<UnityAnimatorController.Layer> _layers = new();
    private HashSet<string> _referenced = new(StringComparer.Ordinal);

    // What the package's built-in parameters read on an avatar standing in a world, seen by someone else.
    private static readonly Dictionary<string, float> Builtin = new(StringComparer.Ordinal)
    {
        ["Grounded"] = 1f, ["Upright"] = 1f, ["VRMode"] = 1f, ["TrackingType"] = 3f, ["ScaleFactor"] = 1f,
        ["ScaleFactorInverse"] = 1f, ["EyeHeightAsMeters"] = 1.5f, ["EyeHeightAsPercent"] = 0.27f, ["AvatarVersion"] = 3f,
        ["IsAnimatorEnabled"] = 1f,
    };

    private static readonly HashSet<string> BuiltinNames = new(StringComparer.Ordinal)
    {
        "IsLocal", "PreviewMode", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight",
        "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright", "Grounded", "Seated", "AFK",
        "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled",
        "ScaleModified", "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent",
    };

    // A built-in parameter, fed by us, which a merge never renames.
    public static bool IsBuiltin(string name) => BuiltinNames.Contains(name);

    // Playable layers in the order they are laid over one another.
    private static readonly int[] PlayableOrder = { 0, 2, 3, 4, 5 };

    private UnityVrcPlan(UnityPackageArchive archive, UnityExpressionParameters? parameters)
    {
        _archive = archive;
        _parameters = parameters;
        foreach (var (name, value) in Builtin)
            _defaults[name] = value;
        if (parameters != null)
            foreach (var p in parameters.ByName.Values)
                Note(p.Name, p);
    }

    private void Note(string name, UnityExpressionParameters.Parameter p)
    {
        _defaults[name] = p.Default;
        Parameters[name] = new ParameterInfo(p.Type, p.Default, p.Saved, p.Synced);
    }

    public static UnityVrcPlan? Build(UnityPackageArchive archive, UnityDocument descriptor, IEnumerable<string>? contactParameters = null,
        IReadOnlyList<Merge>? merges = null)
    {
        var parameters = UnityExpressionParameters.Parse(archive.ReadText(archive.Find(descriptor["expressionParameters"]?.Ref.Guid)));
        var plan = new UnityVrcPlan(archive, parameters);
        merges ??= Array.Empty<Merge>();
        foreach (var merge in merges)
            foreach (var guid in merge.ParameterSets)
                if (UnityExpressionParameters.Parse(archive.ReadText(archive.Find(guid))) is { } set)
                    foreach (var p in set.ByName.Values)
                    {
                        var name = merge.Rename(p.Name);
                        if (!plan.Parameters.ContainsKey(name))
                            plan.Note(name, p with { Name = name });
                    }
        foreach (var merge in merges)
            foreach (var (_, parameter, _, value) in merge.Controls)
                plan._defaults.TryAdd(merge.Rename(parameter), value);

        var own = new Dictionary<int, string>();
        if (descriptor["baseAnimationLayers"] is UnitySeq layers)
            foreach (var layer in layers.Items)
                if ((layer["isDefault"]?.Int(1) ?? 1) == 0 && layer["animatorController"]?.Ref.Guid is { Length: > 0 } guid
                    && archive.Find(guid) != null)
                    own[layer["type"]?.Int() ?? -1] = guid;
        var specs = new List<UnityAnimatorProgram.Spec>();
        foreach (int type in PlayableOrder)
        {
            if (own.TryGetValue(type, out var guid))
            {
                specs.Add(new UnityAnimatorProgram.Spec(guid, type));
                plan.Playables.Add(type);
            }
            foreach (var merge in merges)
                foreach (var (mergeType, controller) in merge.Controllers)
                    if (mergeType == type && archive.Find(controller) != null)
                    {
                        specs.Add(new UnityAnimatorProgram.Spec(controller, type, merge.PathPrefix, merge.RootBindingsApplyToAvatar, merge.Parameters, merge.Rewrites));
                        plan.Playables.Add(type);
                    }
        }
        plan.Program = specs.Count > 0 ? UnityAnimatorProgram.Read(archive, specs, plan._defaults) : null;
        if (plan.Program != null)
        {
            plan._fx = plan.Program.Controller;
            foreach (var (name, value) in plan.Program.Defaults)
                plan._defaults[name] = value;
            plan._referenced = plan._fx.ReferencedParameters();
            _ = plan.Program.Graph;
        }
        plan._layers = plan._fx.Layers.Where(l => l.Playable == 5 && l.Weight > 0f).ToList();

        plan.ReadRest();
        // Contacts first: a menu switch that only unlocks them has to be known as one before the menu
        // decides what to leave out.
        foreach (var parameter in (contactParameters ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal))
            plan.ReadContact(parameter);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        plan.ReadMenu(descriptor["expressionsMenu"]?.Ref.Guid, "", seen, 0, name => name);
        foreach (var merge in merges)
            foreach (var (menu, prefix) in merge.Menus)
                plan.ReadMenu(menu, prefix.Trim('/'), seen, 0, merge.Rename);
        foreach (var merge in merges)
            foreach (var (path, parameter, slider, _) in merge.Controls)
                plan.ReadControl(path, merge.Rename(parameter), slider);
        plan.ReadGestures();
        plan.CollectMaterials();
        plan.CollectIcons();
        return plan;
    }

    private void Skip(string what) => Skipped[what] = Skipped.GetValueOrDefault(what) + 1;

    // What each layer tests, read once: every menu control asks every layer.
    private readonly Dictionary<UnityAnimatorController.Layer, HashSet<string>> _tested = new();

    private HashSet<string> Tested(UnityAnimatorController.Layer layer)
    {
        if (!_tested.TryGetValue(layer, out var names))
            _tested[layer] = names = _fx.ParametersOf(layer);
        return names;
    }

    private Dictionary<string, float> With(string parameter, float value)
    {
        var values = new Dictionary<string, float>(_defaults, StringComparer.Ordinal) { [parameter] = value };
        return values;
    }

    private void ReadRest()
    {
        foreach (var layer in _layers)
            if (_fx.Settle(layer, _defaults) is { } state)
                Rest.Merge(StatePose(state, _defaults, play: true));
    }

    private void ReadMenu(string? guid, string folder, HashSet<string> seen, int depth, Func<string, string> rename)
    {
        if (string.IsNullOrEmpty(guid) || depth > 8 || !seen.Add(guid))
            return;
        var menu = UnityExpressionMenu.Parse(_archive.ReadText(_archive.Find(guid)));
        if (menu == null)
            return;
        foreach (var control in menu.Controls)
        {
            MenuControls++;
            var path = folder.Length > 0 ? folder + "/" + control.Name : control.Name;
            if (!string.IsNullOrEmpty(control.Parameter))
                control.Parameter = rename(control.Parameter);
            for (int i = 0; i < control.SubParameters.Count; i++)
                if (control.SubParameters[i].Length > 0)
                    control.SubParameters[i] = rename(control.SubParameters[i]);
            switch (control.Type)
            {
                case UnityExpressionMenu.ControlType.SubMenu:
                    ReadMenu(control.SubMenu, path, seen, depth + 1, rename);
                    break;
                case UnityExpressionMenu.ControlType.Toggle:
                case UnityExpressionMenu.ControlType.Button:
                    ReadToggle(control, path, silent: false);
                    break;
                case UnityExpressionMenu.ControlType.Radial:
                    ReadRadial(control, path, silent: false);
                    break;
                default:
                    Skip("two and four way sticks");
                    break;
            }
        }
    }

    // A control a build tool makes: read like the menu's own, and left out quietly when nothing answers it.
    private void ReadControl(string path, string parameter, bool slider)
    {
        if (string.IsNullOrEmpty(parameter) || string.IsNullOrEmpty(path))
            return;
        var control = new UnityExpressionMenu.Control
        {
            Name = UnityExpressionMenu.CleanName(path),
            Type = slider ? UnityExpressionMenu.ControlType.Radial : UnityExpressionMenu.ControlType.Toggle,
            Parameter = slider ? "" : parameter,
            Value = 1f,
        };
        if (slider)
        {
            control.SubParameters.Add(parameter);
            ReadRadial(control, path, silent: true);
        }
        else
            ReadToggle(control, path, silent: true);
    }

    private ParameterInfo? Info(string parameter) => Parameters.TryGetValue(parameter, out var info) ? info : null;

    private void ReadToggle(UnityExpressionMenu.Control control, string path, bool silent)
    {
        var parameter = control.Parameter;
        if (string.IsNullOrEmpty(parameter))
            return;
        float initial = _defaults.GetValueOrDefault(parameter);
        bool isInt = Kind(parameter) == UnityExpressionParameters.ValueType.Int;
        float on = Kind(parameter) == UnityExpressionParameters.ValueType.Bool ? 1f : control.Value;
        bool momentary = control.Type == UnityExpressionMenu.ControlType.Button;
        bool defaultOn = !momentary && MathF.Abs(initial - on) < 0.01f;
        float off = defaultOn ? (on != 0f ? 0f : 1f) : initial;

        var entry = new Switch
        {
            Path = path, DefaultOn = defaultOn, Group = isInt ? "param:" + parameter : "", Parameter = parameter, OnValue = on, OffValue = off,
            Momentary = momentary, Saved = Info(parameter)?.Saved ?? true, Icon = control.Icon, Live = _referenced.Contains(parameter),
        };
        foreach (var layer in _layers)
        {
            if (!Tested(layer).Contains(parameter))
                continue;
            var onValues = With(parameter, on);
            var offValues = With(parameter, off);
            var onState = _fx.Settle(layer, onValues);
            var offState = _fx.Settle(layer, offValues);
            if (onState == null || offState == null || ReferenceEquals(onState, offState)
                && onState.TimeParameter != parameter && !TreeTests(onState, parameter))
                continue;
            entry.On.Merge(StatePose(onState, onValues, play: true));
            entry.Off.Merge(StatePose(offState, offValues, play: false));
        }
        if (entry.On.IsEmpty && entry.Off.IsEmpty && (_gates.Contains(parameter) || entry.Live))
        {
            entry.Gate = true;
            Switches.Add(entry);
            return;
        }
        if (entry.On.IsEmpty && entry.Off.IsEmpty)
        {
            if (!silent)
                Skip(parameter.StartsWith("VRC", StringComparison.Ordinal) ? "the avatar SDK's own pose and locomotion settings" : "menu switches no animation layer answers");
            Lumora.Core.Logging.Logger.Debug($"UnityPackage: menu '{path}' sets '{parameter}', which no layer reads");
            return;
        }
        Switches.Add(entry);
    }

    // A radial read every 32nd of the way along, and at every point a layer that tests it changes course: a
    // blend tree's thresholds, between which a pose runs straight, and both sides of a transition's.
    private const int SliderSteps = 32;

    private void ReadRadial(UnityExpressionMenu.Control control, string path, bool silent)
    {
        var parameter = control.SubParameters.FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (parameter == null)
            return;
        float initial = System.Math.Clamp(_defaults.GetValueOrDefault(parameter), 0f, 1f);
        var layers = _layers.Where(l => Tested(l).Contains(parameter)).ToList();
        bool live = _referenced.Contains(parameter);
        if (layers.Count == 0 && !live)
        {
            if (!silent)
                Skip("menu sliders that change nothing readable");
            return;
        }
        var entry = new Slider
        {
            Path = path, Parameter = parameter, Default = initial, Body = live && layers.Count == 0, Saved = Info(parameter)?.Saved ?? true,
            Icon = control.Icon, Live = live,
        };
        foreach (float at in SliderPoints(parameter))
        {
            var values = With(parameter, at);
            var pose = new UnityPose();
            foreach (var layer in layers)
                if (_fx.Settle(layer, values) is { } state)
                    pose.Merge(StatePose(state, values, play: false));
            entry.Samples.Add((at, pose));
        }
        Sliders.Add(entry);
    }

    private IEnumerable<float> SliderPoints(string parameter)
    {
        var points = new SortedSet<float>();
        for (int i = 0; i <= SliderSteps; i++)
            points.Add(i / (float)SliderSteps);
        void Add(float at)
        {
            if (at > 0f && at < 1f)
                points.Add(at);
        }
        foreach (var tree in _fx.BlendTrees.Values)
            if (tree.Parameter == parameter)
                foreach (var child in tree.Children)
                    Add(child.Threshold);
        foreach (var transition in _fx.Transitions.Values)
            foreach (var condition in transition.Conditions)
                if (condition.Parameter == parameter && condition.Mode is 3 or 4)
                {
                    Add(condition.Threshold);
                    Add(condition.Threshold + (condition.Mode == 3 ? 1e-3f : -1e-3f));
                }
        float? last = null;
        foreach (float at in points)
        {
            if (last is float before && at - before < 1e-4f)
                continue;
            last = at;
            yield return at;
        }
    }

    private void ReadGestures()
    {
        foreach (var layer in _layers)
        {
            var tested = Tested(layer);
            foreach (var hand in new[] { GestureHand.Left, GestureHand.Right })
            {
                var parameter = hand == GestureHand.Left ? "GestureLeft" : "GestureRight";
                if (!tested.Contains(parameter))
                    continue;
                var restValues = With(parameter, 0f);
                var neutral = _fx.Settle(layer, restValues);
                for (int sign = 1; sign <= 7; sign++)
                {
                    var values = With(parameter, sign);
                    values[parameter + "Weight"] = 1f;
                    var state = _fx.Settle(layer, values);
                    if (state == null || neutral != null && state.Id == neutral.Id
                        && state.TimeParameter != parameter && !TreeTests(state, parameter))
                        continue;
                    var gesture = new Gesture
                    {
                        Hand = hand,
                        Sign = (HandGesture)sign,
                        FistWeight = state.TimeParameter == parameter + "Weight" || TreeTests(state, parameter + "Weight"),
                        BlockBlinking = state.EyesAnimated,
                    };
                    gesture.On.Merge(StatePose(state, values, play: true));
                    if (neutral != null)
                        gesture.Off.Merge(StatePose(neutral, restValues, play: false));
                    if (!gesture.On.IsEmpty)
                        Gestures.Add(gesture);
                }
            }
        }
    }

    // A contact sets its parameter while touched (to how deep, for a proximity one). Fully touched is
    // the reaction; the layer is read at the state the touch puts it in, before any timed return.
    private void ReadContact(string parameter)
    {
        var on = new UnityPose();
        var off = new UnityPose();
        var onValues = With(parameter, 1f);
        var offValues = With(parameter, 0f);
        // A reaction is often locked behind a menu switch ("face contacts on"): a bool the touch's own
        // transition also asks for, off by default. Read with it on; the switch then gates the zones.
        var gates = new List<string>();
        foreach (var t in _fx.Transitions.Values)
        {
            if (!t.Conditions.Any(c => c.Parameter == parameter))
                continue;
            foreach (var c in t.Conditions)
            {
                if (c.Parameter != parameter && c.Mode == 1 && _defaults.GetValueOrDefault(c.Parameter) <= 0.5f && !gates.Contains(c.Parameter))
                    gates.Add(c.Parameter);
            }
        }
        foreach (var gate in gates)
            onValues[gate] = 1f;
        foreach (var layer in _layers)
        {
            if (!Tested(layer).Contains(parameter))
                continue;
            var touched = _fx.Settle(layer, onValues, timed: false);
            var idle = _fx.Settle(layer, offValues);
            if (touched == null || idle == null || touched.Id == idle.Id
                && touched.TimeParameter != parameter && !TreeTests(touched, parameter))
                continue;
            on.Merge(StatePose(touched, onValues, play: true));
            off.Merge(StatePose(idle, offValues, play: false));
        }
        if (on.IsEmpty && off.IsEmpty)
        {
            if (!_referenced.Contains(parameter))
                Skip("contacts no animation layer answers");
            return;
        }
        Contacts[parameter] = (on, off, gates);
        foreach (var gate in gates)
            _gates.Add(gate);
    }

    private bool TreeTests(UnityAnimatorController.State state, string parameter)
        => Program != null && Program.TryGetTree(state.Motion, out var tree) && tree.Parameter == parameter;

    private UnityExpressionParameters.ValueType Kind(string parameter)
    {
        if (Parameters.TryGetValue(parameter, out var p))
            return p.Type;
        return _fx.FindParameter(parameter)?.Type switch
        {
            UnityAnimatorController.ParameterType.Int => UnityExpressionParameters.ValueType.Int,
            UnityAnimatorController.ParameterType.Float => UnityExpressionParameters.ValueType.Float,
            _ => UnityExpressionParameters.ValueType.Bool,
        };
    }

    // What a state shows for these values: its clip (read at the point a time parameter puts it), or its
    // blend tree mixed by its parameters.
    private UnityPose StatePose(UnityAnimatorController.State state, IReadOnlyDictionary<string, float> values, bool play)
    {
        var pose = new UnityPose();
        float? at = state.TimeParameter != null ? System.Math.Clamp(values.GetValueOrDefault(state.TimeParameter), 0f, 1f) : null;
        AddMotion(pose, state.Motion, values, at, play, 0);
        return pose;
    }

    private void AddMotion(UnityPose pose, UnityRef motion, IReadOnlyDictionary<string, float> values, float? normalizedTime, bool play, int depth)
    {
        if (depth > 8 || motion.IsNull)
            return;
        if (Program != null && Program.TryGetTree(motion, out var tree))
        {
            pose.Merge(TreePose(tree, motion, values, depth));
            return;
        }
        var clip = Clip(motion.Guid);
        if (clip == null)
            return;
        if (normalizedTime is { } t)
            pose.Add(clip, t * clip.Length, play: false);
        else
            pose.Add(clip, 0f, play);
    }

    private UnityPose TreePose(UnityAnimatorController.BlendTree tree, UnityRef self, IReadOnlyDictionary<string, float> values, int depth)
    {
        var pose = new UnityPose();
        if (tree.Children.Count == 0)
            return pose;
        UnityPose Child(int i)
        {
            var child = new UnityPose();
            AddMotion(child, UnityAnimatorProgram.Retag(tree.Children[i].Motion, self), values, null, play: false, depth + 1);
            return child;
        }
        switch (tree.Type)
        {
            case 0:
            {
                // One parameter between thresholds: mix the two children either side of it.
                float v = values.GetValueOrDefault(tree.Parameter);
                var order = Enumerable.Range(0, tree.Children.Count).OrderBy(i => tree.Children[i].Threshold).ToList();
                if (v <= tree.Children[order[0]].Threshold)
                    return Child(order[0]);
                for (int k = 0; k < order.Count - 1; k++)
                {
                    float a = tree.Children[order[k]].Threshold, b = tree.Children[order[k + 1]].Threshold;
                    if (v <= b)
                        return UnityPose.Blend(Child(order[k]), Child(order[k + 1]), b > a ? (v - a) / (b - a) : 1f);
                }
                return Child(order[^1]);
            }
            case 4:
            {
                // Direct: each child is weighted by its own parameter. Settled values are 0 or 1.
                for (int i = 0; i < tree.Children.Count; i++)
                {
                    if (values.GetValueOrDefault(tree.Children[i].DirectParameter) > 0.5f)
                        pose.Merge(Child(i));
                }
                return pose;
            }
            case 1:
            case 2:
            case 3:
            {
                // Two parameters: weighed by gradient bands over the children's positions, and only children
                // with a share are mixed. Reading the first child instead put a face puppet's first expression
                // (puffed cheeks, a snarl) into the rest pose.
                float x = values.GetValueOrDefault(tree.Parameter), y = values.GetValueOrDefault(tree.ParameterY);
                var weights = BandWeights(tree, x, y);
                float total = 0f;
                UnityPose? mixed = null;
                for (int i = 0; i < weights.Length; i++)
                {
                    if (weights[i] < 1e-4f)
                        continue;
                    total += weights[i];
                    mixed = mixed == null ? Child(i) : UnityPose.Blend(mixed, Child(i), weights[i] / total);
                }
                return mixed ?? pose;
            }
            default:
                Skip("blend trees of an unknown kind");
                return pose;
        }
    }

    // Gradient band weights for a two-parameter tree. Directional trees measure a child's angle and
    // relative distance from the centre; Cartesian ones plain displacement.
    private static float[] BandWeights(UnityAnimatorController.BlendTree tree, float x, float y)
    {
        int count = tree.Children.Count;
        var weights = new float[count];
        float total = 0f;
        for (int i = 0; i < count; i++)
        {
            var a = i < tree.Positions.Count ? tree.Positions[i] : (X: 0f, Y: 0f);
            float weight = 1f;
            for (int j = 0; j < count; j++)
            {
                if (i == j)
                    continue;
                var b = j < tree.Positions.Count ? tree.Positions[j] : (X: 0f, Y: 0f);
                float dx = b.X - a.X, dy = b.Y - a.Y, px = x - a.X, py = y - a.Y;
                if (tree.Type != 3)
                {
                    float ra = MathF.Sqrt(a.X * a.X + a.Y * a.Y), rb = MathF.Sqrt(b.X * b.X + b.Y * b.Y);
                    float rp = MathF.Sqrt(x * x + y * y), average = (ra + rb) * 0.5f;
                    if (average > 1e-6f)
                    {
                        dx = (rb - ra) / average;
                        px = (rp - ra) / average;
                        dy = ra < 1e-6f || rb < 1e-6f ? 0f : MathF.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);
                        py = ra < 1e-6f || rp < 1e-6f ? 0f : MathF.Atan2(a.X * y - a.Y * x, a.X * x + a.Y * y);
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
            for (int i = 0; i < count; i++)
                weights[i] /= total;
        return weights;
    }

    // A clip by reference key: the program's, as its merge placed it, else read from the package.
    private UnityAnimationClip? Clip(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        if (Program != null && Program.Clips.TryGetValue(key, out var placed))
            return placed;
        if (_clips.TryGetValue(key, out var known))
            return known;
        var clip = UnityAnimationClip.Parse(_archive.ReadText(_archive.Find(UnityAnimatorController.Untag(key).Guid)));
        _clips[key] = clip;
        return clip;
    }

    private void CollectMaterials()
    {
        void From(UnityPose pose)
        {
            foreach (var reference in pose.References.Values)
                if (!string.IsNullOrEmpty(reference.Guid))
                    MaterialGuids.Add(reference.Guid!);
        }
        From(Rest);
        foreach (var s in Switches)
        {
            From(s.On);
            From(s.Off);
        }
        foreach (var s in Sliders)
            foreach (var (_, pose) in s.Samples)
                From(pose);
        foreach (var g in Gestures)
        {
            From(g.On);
            From(g.Off);
        }
        foreach (var (on, off, _) in Contacts.Values)
        {
            From(on);
            From(off);
        }
        // Everything the live program can swap in has to be converted too.
        if (Program != null)
            foreach (var reference in Program.Graph.References)
                if (!string.IsNullOrEmpty(reference.Guid))
                    MaterialGuids.Add(reference.Guid!);
    }

    private void CollectIcons()
    {
        foreach (var guid in Switches.Select(s => s.Icon).Concat(Sliders.Select(s => s.Icon)))
            if (!string.IsNullOrEmpty(guid) && !IconFiles.ContainsKey(guid) && _archive.Find(guid) is { File: { } file } asset
                && asset.Extension is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".webp")
                IconFiles[guid] = (file, asset.Extension);
    }
}
