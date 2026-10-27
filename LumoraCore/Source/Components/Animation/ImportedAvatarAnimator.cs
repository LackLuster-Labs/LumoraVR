// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Assets;
using Lumora.Core.Assets.Interop.Unity;
using Lumora.Core.Components.Audio;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Input;

namespace Lumora.Core.Components;

// An imported avatar's animator, played live. Every machine runs its own copy of the graph from the
// parameters that reach it: menu switches and sliders replicate as the controls they are, a driver on the
// wearer's machine writes its result back into those controls, and what each machine measures for itself
// (contacts, bone chains, voice, gestures, motion) it reads itself. So a parameter the avatar keeps to its
// wearer (IsLocal, one that does not sync) reads differently on the wearer's machine than on everyone
// else's, as it should.
//
// Built-in parameters come from the wearer: their voice, whether this is their own machine, away, muted,
// scale and eye height, gestures, the dominant viseme, and how they move (sent by the wearer at a few
// updates a second). Tracking control hands the eyes or the mouth to the animation by holding the face
// drivers off. Play Audio cues the imported audio players. -xlinka
[ComponentCategory("Animation")]
[DefaultUpdateOrder(-150)]
public sealed class ImportedAvatarAnimator : Component, ICustomInspectorUI, IInspectorActionContextHandler
{
    public void BuildInspectorBody(Helio.UI.UIBuilder ui)
    {
        InspectorUI.FixedRow(ui.Root, "Curves", InspectorUI.RowHeight, out var row, ui.Root);
        InspectorUI.RelayButton(row, this, "curves", "View curves", 0f);
    }

    public void HandleInspectorAction(string argument, Helio.UI.UIInteractionContext context)
    {
        if (argument == "curves")
            CurveStudioPanel.Open(this, "", context.WorldPoint);
    }

    public readonly AssetRef<ImportedAnimatorAsset> Controller = new();
    public readonly SyncRef<Slot> Root = new();
    public readonly SyncRef<HandGestureReader> Gestures = new();
    public readonly SyncRef<VisemeWeightDriver> Voice = new();
    public readonly Sync<float> UnitScale = new();
    public readonly SyncList<ImportedAnimatorBinding> Bindings = new();
    public readonly SyncRefList<ImportedAnimatorParameter> Parameters = new();
    public readonly SyncFieldList<float> MotionInputs = new();
    // The audio players Play Audio names, by the object path it names them with, and every clip it may cue.
    public readonly SyncRefList<AudioCuePlayer> AudioPlayers = new();
    public readonly SyncFieldList<string> AudioPaths = new();
    public readonly SyncAssetList<AudioClip> AudioClips = new();
    public readonly SyncFieldList<string> AudioClipKeys = new();

    private static readonly string[] MotionNames = { "VelocityX", "VelocityY", "VelocityZ", "AngularY", "Grounded", "Upright", "VRMode", "TrackingType", "Seated", "InStation" };
    private const int UprightIndex = 5;
    private const float UprightDeadband = 0.02f;
    // A real crouch or stand goes out at once; anything less waits for the next tenth of a second.
    private const float UprightJump = 0.15f;

    private UnityAnimatorProgram? _importProgram, _program;
    private UnityAnimatorRuntime? _runtime;
    private readonly AvatarFaceWearer _wearer = new();
    private Output[] _outputs = Array.Empty<Output>();
    private int[][] _byProperty = Array.Empty<int[]>();
    private int[] _appliedFrame = Array.Empty<int>();
    private readonly List<int> _shapesLive = new();
    private int _applyFrame;
    private bool _applyAll;
    private Input[] _inputs = Array.Empty<Input>();
    private int[] _polled = Array.Empty<int>();
    private bool[] _dirtyFlags = Array.Empty<bool>();
    private readonly List<int> _dirty = new();
    private readonly List<(IChangeable Field, Action<IChangeable> Handler)> _watched = new();
    private bool _readAll, _readOwner;
    private int _boundCount = -1, _inputCount = -1, _inputAge;
    private float _restScale = 1f;
    private double _restScaleRead = double.NegativeInfinity;
    private Builtins _builtins;
    private readonly int[] _tracking = new int[10];
    private bool _holding;
    private floatQ _lastFacing = floatQ.Identity;
    private bool _hadMotion;
    private double _lastMotionSend;
    private Slot? _view;
    private readonly Dictionary<int, (int Last, int Next)> _audioPicks = new();
    private readonly Random _random = new();
    // The avatar this animator plays on, for AvatarViewLod, found again when it is put on, taken off or moved.
    private Slot? _lodRoot;
    private UserRoot? _lodWearer;
    private bool _lodStale = true;
    private float _lodDelta;

    // One binding as the output reads it: its property (or four, for a material colour or vector), where it
    // writes, its rest, and what it last wrote.
    private sealed class Output
    {
        public ImportedAnimatorBinding Binding = null!;
        public ImportedAnimatorChannel Channel;
        public int Property = -1;
        public int[]? Channels;
        public float4 Rest;
        public float4 Applied = new(float.NaN, float.NaN, float.NaN, float.NaN);
        public SkinnedMeshRenderer? Renderer;
        public string Shape = "";
        public int ShapeIndex = -1;
        public bool Claimed;
        public float Basis = float.NaN;
    }

    private struct Input
    {
        public ImportedAnimatorParameter? Source;
        public int Parameter;
        public bool Fed, Trigger, Polled;
        public float Last;
    }

    private struct Builtins
    {
        public int IsLocal, Voice, Viseme, GestureLeft, GestureRight, GestureLeftWeight, GestureRightWeight, Afk, MuteSelf,
            IsAnimatorEnabled, ScaleFactor, ScaleFactorInverse, ScaleModified, EyeHeightMeters, EyeHeightPercent,
            VelocityMagnitude, AvatarVersion;
        public int[] Motion;
    }

    public UnityAnimatorRuntime? Runtime => _runtime;
    public bool IsOwner { get; private set; }

    // How the tracking control of the states left a body part: 0 untouched, 1 tracked, 2 animated. Parts in
    // order: head, hands, hip, feet, fingers, eyes, mouth.
    public int TrackingMode(int part) => part >= 0 && part < _tracking.Length ? _tracking[part] : 0;

    public override void OnInit() { base.OnInit(); UnitScale.Value = 1f; }

    public override void OnAwake()
    {
        base.OnAwake();
        Slot.ActiveChanged += OnSlotActiveChanged;
        Slot.ParentChanged += OnLodMoved;
        Slot.ActiveUserRootChanged += OnLodMoved;
    }

    public void InitializeForImport(UnityAnimatorProgram program) => _importProgram = program;

    public override void OnUpdate(float delta)
    {
        _lodDelta += delta;
        if (Waits())
        {
            ApplyLive();
            return;
        }
        float step = _lodDelta;
        _lodDelta = 0f;
        Step(step);
    }

    // Somebody else's avatar, or one standing in the world, runs every few frames once it is behind the viewer
    // or far off (AvatarViewLod), each run carrying the time of the frames it sat out. A menu control that
    // changed runs it at once, so a toggle shows on the frame it arrives; what it measures for itself (voice,
    // gestures, contacts) waits for its turn. The wearer's own is never held back. -xlinka
    private bool Waits()
    {
        if (_runtime == null || _dirty.Count > 0 || _boundCount != Bindings.Count || !Enabled.Value || !Slot.IsActive)
            return false;
        _wearer.Update(Root.Target ?? Slot);
        var wearer = _wearer.Current;
        if (wearer != null && wearer.IsLocalUserRoot)
            return false;
        // An avatar nobody wears has its drivers decided here when this machine is the host, and what they set
        // goes out to everyone: held back here it would reach every other machine late. It still rests on its
        // idle frames, which change nothing to send. -xlinka
        if (wearer == null && World.IsAuthority && _runtime.Graph.HasDrivers)
            return false;
        if (_lodStale || !ReferenceEquals(wearer, _lodWearer) || _lodRoot is { IsDestroyed: true })
        {
            _lodRoot = AvatarViewLod.RootOf(Slot);
            _lodWearer = wearer;
            _lodStale = false;
        }
        // On the avatar's own pose frames, so the skeleton takes the animation with the IK it rides on.
        return !AvatarViewLod.Look(_lodRoot).RunsThisFrame(World.Time.UpdateIndex);
    }

    private void OnLodMoved(Slot slot) => _lodStale = true;

    // Also usable by headless validation with an explicit step.
    public void Step(float delta)
    {
        if (!Enabled.Value || !Slot.IsActive) { Stop(); return; }
        if (Controller.Asset?.Program != null) _importProgram = null;
        var program = Controller.Asset?.Program ?? _importProgram;
        if (program == null) { Stop(); return; }
        if (!ReferenceEquals(program, _program))
        {
            Stop();
            _program = program;
            _runtime = new UnityAnimatorRuntime(program.Graph);
            _builtins = Find(_runtime);
            _boundCount = _inputCount = -1;
            Array.Clear(_tracking);
            _audioPicks.Clear();
        }
        var runtime = _runtime!;
        _wearer.Update(Root.Target ?? Slot);
        bool owner = _wearer.Current is { } wearer ? wearer.IsLocalUserRoot : World.IsAuthority;
        IsOwner = owner;
        runtime.Owner = owner;
        // Inputs gain feeds after import (contacts, bone chains), so their kinds are looked at again now and then,
        // and laid out afresh only when one has changed: a rebuild resubscribes every input and allocates.
        if (_inputCount != Parameters.Count)
            MapInputs(runtime);
        else if (++_inputAge > 600)
        {
            _inputAge = 0;
            if (Mapped(runtime))
                _readAll = true;
            else
                MapInputs(runtime);
        }
        if (_boundCount != Bindings.Count) MapBindings(runtime);

        ReadInputs(runtime, owner);
        ReadBuiltins(runtime, owner, delta);
        // Nothing read this frame differs from what the last full frame started from and nothing playing moves
        // by itself, so the frame comes out as that one did: only the clocks run on. -xlinka
        if (!runtime.Rest(delta))
        {
            runtime.Advance(delta);
            if (owner)
                foreach (int p in runtime.Driven)
                    Push(p);
            foreach (var e in runtime.Events)
                Handle(runtime, e);
            runtime.Evaluate();
        }
        Apply(runtime);
        Hold();
    }

    private static Builtins Find(UnityAnimatorRuntime runtime)
    {
        var b = new Builtins
        {
            IsLocal = runtime.Parameter("IsLocal"), Voice = runtime.Parameter("Voice"), Viseme = runtime.Parameter("Viseme"),
            GestureLeft = runtime.Parameter("GestureLeft"), GestureRight = runtime.Parameter("GestureRight"),
            GestureLeftWeight = runtime.Parameter("GestureLeftWeight"), GestureRightWeight = runtime.Parameter("GestureRightWeight"),
            Afk = runtime.Parameter("AFK"), MuteSelf = runtime.Parameter("MuteSelf"), IsAnimatorEnabled = runtime.Parameter("IsAnimatorEnabled"),
            ScaleFactor = runtime.Parameter("ScaleFactor"), ScaleFactorInverse = runtime.Parameter("ScaleFactorInverse"),
            ScaleModified = runtime.Parameter("ScaleModified"), EyeHeightMeters = runtime.Parameter("EyeHeightAsMeters"),
            EyeHeightPercent = runtime.Parameter("EyeHeightAsPercent"), VelocityMagnitude = runtime.Parameter("VelocityMagnitude"),
            AvatarVersion = runtime.Parameter("AvatarVersion"),
            Motion = new int[MotionNames.Length],
        };
        for (int i = 0; i < MotionNames.Length; i++)
            b.Motion[i] = runtime.Parameter(MotionNames[i]);
        return b;
    }

    // ---- Inputs ----

    private void MapInputs(UnityAnimatorRuntime runtime)
    {
        Unwatch();
        _inputCount = Parameters.Count;
        _inputAge = 0;
        var inputs = new Input[_inputCount];
        var polled = new List<int>();
        for (int i = 0; i < _inputCount; i++)
        {
            var p = Parameters[i];
            int index = p == null ? -1 : runtime.Parameter(p.Parameter.Value ?? "");
            inputs[i] = new Input
            {
                Source = index >= 0 ? p : null, Parameter = index, Fed = p?.IsFed == true || p?.OverrideInput.Value == true,
                Trigger = index >= 0 && runtime.Graph.ParameterTypes[index] == UnityAnimatorController.ParameterType.Trigger,
                Last = i < _inputs.Length && _inputs[i].Source == p ? _inputs[i].Last : float.NaN,
            };
            if (p == null || index < 0 || inputs[i].Trigger)
                continue;
            if (Measured(p))
            {
                polled.Add(i);
                inputs[i].Polled = true;
                continue;
            }
            int at = i;
            Action<IChangeable> changed = _ => Dirty(at);
            void Watch(IChangeable? field)
            {
                if (field == null)
                    return;
                field.Changed += changed;
                _watched.Add((field, changed));
            }
            Watch(p.Value);
            Watch(p.Enabled);
            Watch(p.Synced);
            Watch(p.OverrideInput);
            for (int s = 0; s < p.Switches.Count; s++)
                Watch(p.Switches[s]);
            Watch(p.Source.Target);
        }
        _inputs = inputs;
        _polled = polled.ToArray();
        _dirtyFlags = new bool[_inputCount];
        _dirty.Clear();
        _readAll = true;
    }

    // Whether the inputs still read as MapInputs laid them out: the same parameters, feeds and watched fields,
    // walked in the order it watched them.
    private bool Mapped(UnityAnimatorRuntime runtime)
    {
        int k = 0;
        for (int i = 0; i < _inputs.Length; i++)
        {
            var p = Parameters[i];
            int index = p == null ? -1 : runtime.Parameter(p.Parameter.Value ?? "");
            ref var input = ref _inputs[i];
            if (!ReferenceEquals(input.Source, index >= 0 ? p : null) || input.Parameter != index
                || input.Fed != (p?.IsFed == true || p?.OverrideInput.Value == true))
                return false;
            if (p == null || index < 0 || input.Trigger)
                continue;
            if (input.Polled != Measured(p))
                return false;
            if (input.Polled)
                continue;
            if (!Watched(ref k, p.Value) || !Watched(ref k, p.Enabled) || !Watched(ref k, p.Synced) || !Watched(ref k, p.OverrideInput))
                return false;
            for (int s = 0; s < p.Switches.Count; s++)
                if (p.Switches[s] is { } field && !Watched(ref k, field))
                    return false;
            if (p.Source.Target is { } source && !Watched(ref k, source))
                return false;
        }
        return k == _watched.Count;
    }

    private bool Watched(ref int k, IChangeable field) => k < _watched.Count && ReferenceEquals(_watched[k++].Field, field);

    // Contacts and bone chains are measured on every machine every frame; menu controls and plain values only
    // change now and then, and say so.
    private static bool Measured(ImportedAnimatorParameter p)
    {
        if (p.Contacts.Count > 0)
            return true;
        if (p.Source.Target is { } source && source is not SyncElement { Parent: AvatarSlider })
            return true;
        for (int s = 0; s < p.Switches.Count; s++)
            if (p.Switches[s] is { } field && field is not SyncElement { Parent: AvatarToggle })
                return true;
        return false;
    }

    private void Dirty(int input)
    {
        if (input < _dirtyFlags.Length && !_dirtyFlags[input])
        {
            _dirtyFlags[input] = true;
            _dirty.Add(input);
        }
    }

    private void Unwatch()
    {
        foreach (var (field, handler) in _watched)
            field.Changed -= handler;
        _watched.Clear();
    }

    // Inputs fed by something measured are read every frame; the rest when one of their fields changed. A plain
    // Value is applied only when it changes, so the animation's own writes to the parameter are not undone by it.
    private void ReadInputs(UnityAnimatorRuntime runtime, bool owner)
    {
        if (owner != _readOwner)
        {
            _readOwner = owner;
            _readAll = true;
        }
        if (_readAll)
        {
            _readAll = false;
            for (int i = 0; i < _inputs.Length; i++)
                ReadInput(runtime, owner, i);
        }
        else
        {
            foreach (int i in _polled)
                ReadInput(runtime, owner, i);
            for (int k = 0; k < _dirty.Count; k++)
                ReadInput(runtime, owner, _dirty[k]);
        }
        foreach (int i in _dirty)
            _dirtyFlags[i] = false;
        _dirty.Clear();
    }

    private void ReadInput(UnityAnimatorRuntime runtime, bool owner, int i)
    {
        ref var input = ref _inputs[i];
        var p = input.Source;
        if (p == null || input.Trigger || p.IsDestroyed || !p.Enabled.Value)
            return;
        bool mine = owner || p.Synced.Value;
        if (!input.Fed)
        {
            if (!mine)
                return;
            float held = p.Value.Value;
            if (held != input.Last)
                runtime.Values[input.Parameter] = held;
            input.Last = held;
            return;
        }
        float value = p.Read(menu: mine);
        runtime.Values[input.Parameter] = value;
        input.Last = value;
    }

    private void Push(int parameter)
    {
        float value = _runtime!.Values[parameter];
        for (int i = 0; i < _inputs.Length; i++)
        {
            ref var input = ref _inputs[i];
            if (input.Parameter != parameter || input.Source is not { IsDestroyed: false } p)
                continue;
            if (!p.Synced.Value || p.Contacts.Count > 0)
                continue;
            p.Write(value);
            input.Last = input.Fed ? p.Read(menu: true) : p.Value.Value;
        }
    }

    private void ReadBuiltins(UnityAnimatorRuntime runtime, bool owner, float delta)
    {
        var b = _builtins;
        var values = runtime.Values;
        void Set(int index, float value) { if (index >= 0) values[index] = value; }
        Set(b.IsLocal, owner ? 1f : 0f);
        Set(b.IsAnimatorEnabled, 1f);
        Set(b.AvatarVersion, 3f);
        if (Gestures.Target is { IsDestroyed: false } gestures)
        {
            Set(b.GestureLeft, (int)gestures.Left); Set(b.GestureRight, (int)gestures.Right);
            Set(b.GestureLeftWeight, gestures.LeftFist); Set(b.GestureRightWeight, gestures.RightFist);
        }
        if (Voice.Target?.Source.Target is { IsDestroyed: false } voice)
        {
            Set(b.Voice, voice.Level);
            Set(b.Viseme, voice.Dominant);
        }
        var root = _wearer.Current;
        var user = root?.ActiveUser;
        Set(b.Afk, user != null && !user.IsDestroyed && !user.IsPresent.Value ? 1f : 0f);
        Set(b.MuteSelf, user != null && !user.IsDestroyed && user.IsMuted.Value ? 1f : 0f);
        if (root != null && !root.IsDestroyed)
        {
            float scale = root.GlobalScale;
            // The scale the avatar is meant to be worn at changes rarely and takes a walk to find.
            double now = World.Time.TotalTime;
            if (now - _restScaleRead > 1.0 && (b.ScaleFactor >= 0 || b.ScaleFactorInverse >= 0 || b.ScaleModified >= 0))
            {
                _restScale = AvatarScale.DefaultFor(root);
                _restScaleRead = now;
            }
            float factor = _restScale > 1e-4f ? scale / _restScale : 1f;
            Set(b.ScaleFactor, factor);
            Set(b.ScaleFactorInverse, factor > 1e-4f ? 1f / factor : 1f);
            Set(b.ScaleModified, MathF.Abs(factor - 1f) > 0.01f ? 1f : 0f);
            if (b.EyeHeightMeters >= 0 || b.EyeHeightPercent >= 0)
            {
                float eye = EyeHeight(root);
                Set(b.EyeHeightMeters, eye);
                Set(b.EyeHeightPercent, System.Math.Clamp((eye - 0.2f) / 4.8f, 0f, 1f));
            }
        }
        bool wantsMotion = false;
        foreach (int index in b.Motion) wantsMotion |= index >= 0;
        if (!wantsMotion && b.VelocityMagnitude < 0)
            return;
        if (owner && root != null)
            SendMotion(root);
        else if (owner)
            _hadMotion = false;
        if (root == null)
            return;
        for (int i = 0; i < MotionNames.Length && i < MotionInputs.Count; i++)
            Set(b.Motion[i], MotionInputs[i]);
        if (MotionInputs.Count >= 3)
            Set(b.VelocityMagnitude, new float3(MotionInputs[0], MotionInputs[1], MotionInputs[2]).Length);
    }

    // The viewpoint's height above the avatar's feet, in metres as the world sees it.
    private float EyeHeight(UserRoot root)
    {
        var model = Root.Target ?? Slot;
        if (_view == null || _view.IsDestroyed)
            _view = model.FindChild("AvatarReferences", recursive: false)?.FindChild("View", recursive: false);
        if (_view != null)
            return MathF.Max(0f, _view.GlobalPosition.y - model.GlobalPosition.y);
        return MathF.Max(0f, root.HeadPosition.y - root.FeetPosition.y);
    }

    // The wearer measures how they move and sends it a few times a second, or at once when it changes a lot.
    private void SendMotion(UserRoot root)
    {
        while (MotionInputs.Count < MotionNames.Length) MotionInputs.Add(0);
        double now = World.Time.TotalTime;
        var controller = root.Slot.GetComponent<CharacterController>();
        var velocity = root.Slot.GlobalVectorToLocal(controller?.GetVelocity() ?? float3.Zero) / MathF.Max(root.GlobalScale, 1e-4f);
        var input = Engine.Current?.InputInterface;
        bool vr = input?.IsVRActive == true;
        bool seated = root.Slot.GetComponent<LocomotionController>()?.SeatHoldsCharacter == true;
        float upright = System.Math.Clamp(root.Slot.GlobalPointToLocal(root.HeadPosition).y / MathF.Max(input?.UserHeight ?? 1.7f, .1f), 0f, 1f);
        // A headset never holds still, so Upright moved every frame and went out every frame, and an avatar
        // whose controller reads it never rested on any machine. Two percent of standing height is no
        // crouch anybody means. -xlinka
        if (MathF.Abs(upright - MotionInputs[UprightIndex]) < UprightDeadband)
            upright = MotionInputs[UprightIndex];
        var facing = root.Slot.GlobalRotation;
        double delta = now - _lastMotionSend;
        var forward = _lastFacing.Inverse * (facing * float3.Forward);
        float angular = _hadMotion && delta > 0 && delta < .5 ? MathF.Atan2(-forward.x, -forward.z) * (180f / MathF.PI) / (float)delta : 0;
        Span<float> next = stackalloc float[MotionNames.Length];
        next[0] = velocity.x; next[1] = velocity.y; next[2] = -velocity.z; next[3] = angular;
        next[4] = controller?.IsGrounded() != false ? 1 : 0; next[5] = upright; next[6] = vr ? 1 : 0;
        next[7] = !vr ? 1 : input?.GetBodyNode(BodyNode.Hips)?.IsTracking == true
            && input.GetBodyNode(BodyNode.LeftFoot)?.IsTracking == true && input.GetBodyNode(BodyNode.RightFoot)?.IsTracking == true ? 6 : 3;
        next[8] = seated ? 1 : 0; next[9] = seated ? 1 : 0;
        bool jump = false;
        for (int i = 0; i < next.Length; i++)
        {
            float before = MotionInputs[i];
            if (i == UprightIndex ? MathF.Abs(before - next[i]) > UprightJump
                : i >= 4 ? before != next[i] : MathF.Abs(before - next[i]) > 0.75f)
                jump = true;
        }
        if (!jump && now - _lastMotionSend < 0.1)
            return;
        for (int i = 0; i < next.Length; i++)
            if (MotionInputs[i] != next[i])
                MotionInputs[i] = next[i];
        _lastFacing = facing;
        _hadMotion = true;
        _lastMotionSend = now;
    }

    // ---- Behaviour events ----

    private void Handle(UnityAnimatorRuntime runtime, UnityAnimatorRuntime.Event e)
    {
        switch (runtime.Graph.Behaviours[e.Behaviour])
        {
            case UnityAnimatorController.TrackingControl tracking:
                for (int i = 0; i < _tracking.Length; i++)
                    if (tracking.Modes[i] != 0)
                        _tracking[i] = tracking.Modes[i];
                break;
            case UnityAnimatorGraph.PlayAudio audio:
                PlayAudio(runtime, e.Behaviour, audio, e.Kind == UnityAnimatorRuntime.EventKind.AudioEnter);
                break;
        }
    }

    private void PlayAudio(UnityAnimatorRuntime runtime, int behaviour, UnityAnimatorGraph.PlayAudio audio, bool enter)
    {
        var settings = audio.Source;
        AudioCuePlayer? cue = null;
        for (int i = 0; i < AudioPaths.Count && i < AudioPlayers.Count; i++)
            if (AudioPaths[i] == audio.Path) { cue = AudioPlayers[i]; break; }
        if (cue == null || cue.IsDestroyed)
            return;
        if (!enter)
        {
            if (settings.StopOnExit) cue.StopCue();
            if (settings.PlayOnExit) cue.Cue(settings.Delay);
            return;
        }
        if (settings.StopOnEnter)
            cue.StopCue();
        bool stopped = !cue.IsCuePlaying;
        bool Applies(int setting) => setting == 0 || setting == 1 && stopped;
        if (Applies(settings.ClipsApply) && settings.Clips.Count > 0)
            cue.CueClip = Pick(runtime, behaviour, audio);
        if (Applies(settings.VolumeApply))
        {
            float volume = Between(settings.Volume);
            cue.CueVolume = cue.Volume.Value > 1e-4f ? volume / cue.Volume.Value : volume;
        }
        if (Applies(settings.PitchApply))
        {
            float pitch = Between(settings.Pitch);
            cue.CuePitch = cue.Pitch.Value > 1e-4f ? pitch / cue.Pitch.Value : pitch;
        }
        if (Applies(settings.LoopApply))
            cue.CueLoop = settings.Loop;
        if (settings.PlayOnEnter)
            cue.Cue(settings.Delay);
    }

    private float Between(float2 range) => range.x + (float)_random.NextDouble() * (range.y - range.x);

    // Random, random without the last one, round in order, or the one a parameter names.
    private IAssetProvider<AudioClip>? Pick(UnityAnimatorRuntime runtime, int behaviour, UnityAnimatorGraph.PlayAudio audio)
    {
        var clips = audio.Source.Clips;
        int count = clips.Count;
        var (last, next) = _audioPicks.TryGetValue(behaviour, out var known) ? known : (-1, 0);
        int pick = audio.Source.Order switch
        {
            1 when count > 1 && last >= 0 => (last + 1 + _random.Next(count - 1)) % count,
            2 => next % count,
            3 => audio.Parameter >= 0 ? System.Math.Clamp((int)runtime.Values[audio.Parameter], 0, count - 1) : 0,
            _ => _random.Next(count),
        };
        _audioPicks[behaviour] = (pick, (pick + 1) % count);
        var key = UnityAnimatorController.Untag(clips[pick]).Guid;
        for (int i = 0; i < AudioClipKeys.Count && i < AudioClips.Count; i++)
            if (string.Equals(AudioClipKeys[i], key, StringComparison.OrdinalIgnoreCase))
                return AudioClips.GetElement(i).Target;
        return null;
    }

    // Eyes or mouth handed to the animation hold the face drivers off them.
    private void Hold()
    {
        bool eyes = _tracking[8] == 2, mouth = _tracking[9] == 2;
        var within = Root.Target ?? Slot;
        if (eyes || mouth)
        {
            FaceHold.Set(within, this, eyes, mouth);
            _holding = true;
        }
        else if (_holding)
        {
            FaceHold.Release(within, this);
            _holding = false;
        }
    }

    // ---- Output ----

    private void MapBindings(UnityAnimatorRuntime runtime)
    {
        ReleaseShapes();
        _boundCount = Bindings.Count;
        _outputs = new Output[_boundCount];
        _appliedFrame = new int[_boundCount];
        var index = runtime.Graph.BindingIndex;
        var byProperty = new List<int>?[runtime.Graph.Bindings.Count];
        void Reads(int property, int output)
        {
            if (property >= 0)
                (byProperty[property] ??= new List<int>()).Add(output);
        }
        for (int i = 0; i < _boundCount; i++)
        {
            var binding = Bindings[i];
            var output = new Output { Binding = binding! };
            _outputs[i] = output;
            if (binding == null)
                continue;
            var key = binding.Key;
            output.Channel = binding.Channel.Value;
            output.Rest = binding.Rest.Value;
            switch (output.Channel)
            {
                case ImportedAnimatorChannel.Color:
                case ImportedAnimatorChannel.Vector:
                {
                    var channels = new int[4];
                    string[] names = { "r", "g", "b", "a" }, alternate = { "x", "y", "z", "w" };
                    for (int c = 0; c < 4; c++)
                        channels[c] = index.TryGetValue(key with { Attribute = key.Attribute + "." + names[c] }, out int at)
                            || index.TryGetValue(key with { Attribute = key.Attribute + "." + alternate[c] }, out at) ? at : -1;
                    if (channels[0] < 0 && index.TryGetValue(key, out int single))
                        channels[0] = single;
                    output.Channels = channels;
                    foreach (int c in channels) Reads(c, i);
                    break;
                }
                case ImportedAnimatorChannel.Position:
                case ImportedAnimatorChannel.Scale:
                case ImportedAnimatorChannel.Rotation:
                    output.Property = index.GetValueOrDefault(new UnityBinding(key.Path, 4, output.Channel switch
                    {
                        ImportedAnimatorChannel.Position => "position",
                        ImportedAnimatorChannel.Scale => "scale",
                        _ => "rotation",
                    }, null), -1);
                    Reads(output.Property, i);
                    break;
                case ImportedAnimatorChannel.BlendShape:
                    output.Property = index.GetValueOrDefault(key, -1);
                    output.Renderer = binding.Renderer.Target;
                    output.Shape = (binding.Property.Value ?? "").StartsWith("blendShape.", StringComparison.Ordinal)
                        ? binding.Property.Value!["blendShape.".Length..] : "";
                    Reads(output.Property, i);
                    break;
                default:
                    output.Property = index.GetValueOrDefault(key, -1);
                    Reads(output.Property, i);
                    break;
            }
        }
        _byProperty = byProperty.Select(l => l?.ToArray() ?? Array.Empty<int>()).ToArray();
        _applyAll = true;
    }

    // Only what the animation moved this frame is written, along with shapes the animation holds partly over
    // whatever else drives them, which move when that does.
    private void Apply(UnityAnimatorRuntime runtime)
    {
        float unit = MathF.Max(1e-6f, MathF.Abs(UnitScale.Value));
        _applyFrame++;
        if (_applyAll)
        {
            _applyAll = false;
            for (int i = 0; i < _outputs.Length; i++)
                ApplyOne(runtime, i, unit);
            return;
        }
        foreach (int property in runtime.Changed)
            foreach (int i in _byProperty[property])
                if (_appliedFrame[i] != _applyFrame)
                    ApplyOne(runtime, i, unit);
        ApplyLive(runtime, unit);
    }

    // A frame held back by distance still mixes the partly held shapes over what moved underneath them.
    private void ApplyLive()
    {
        if (_shapesLive.Count == 0 || _runtime == null || _applyAll)
            return;
        _applyFrame++;
        ApplyLive(_runtime, MathF.Max(1e-6f, MathF.Abs(UnitScale.Value)));
    }

    private void ApplyLive(UnityAnimatorRuntime runtime, float unit)
    {
        for (int k = _shapesLive.Count - 1; k >= 0; k--)
        {
            int i = _shapesLive[k];
            if (_appliedFrame[i] == _applyFrame)
                continue;
            // Held partly and unmoved here: only a change underneath moves it.
            var output = _outputs[i];
            if (output.Claimed && output.Renderer is { IsDestroyed: false } renderer && output.ShapeIndex >= 0
                && renderer.GetBlendShapeWeight(output.ShapeIndex) == output.Basis)
                continue;
            ApplyOne(runtime, i, unit);
        }
    }

    private void ApplyOne(UnityAnimatorRuntime runtime, int i, float unit)
    {
        _appliedFrame[i] = _applyFrame;
        var output = _outputs[i];
        var binding = output.Binding;
        if (binding == null || binding.IsDestroyed)
            return;
        int b = output.Property;
        var rest = output.Rest;
        float4 value;
        switch (output.Channel)
        {
            case ImportedAnimatorChannel.BlendShape:
                if (b >= 0) Shape(runtime, output, i, b);
                return;
            case ImportedAnimatorChannel.Material:
                if (b >= 0) Material(runtime, output, b);
                return;
            case ImportedAnimatorChannel.Color:
            case ImportedAnimatorChannel.Vector:
            {
                var channels = output.Channels;
                if (channels == null)
                    return;
                value = rest;
                for (int c = 0; c < 4; c++)
                    if (channels[c] >= 0)
                        value[c] = runtime.Value(channels[c], new float4(rest[c], 0, 0, 0)).x;
                break;
            }
            case ImportedAnimatorChannel.Position:
            {
                if (b < 0) return;
                var v = runtime.Value(b, new float4(-rest.x * unit, rest.y * unit, rest.z * unit, 0));
                value = new float4(-v.x / unit, v.y / unit, v.z / unit, 0);
                break;
            }
            case ImportedAnimatorChannel.Rotation:
            {
                if (b < 0) return;
                var q = runtime.Rotation(b, new floatQ(rest.x, -rest.y, -rest.z, rest.w));
                value = new float4(q.x, -q.y, -q.z, q.w);
                break;
            }
            default:
                if (b < 0) return;
                value = runtime.Value(b, rest);
                break;
        }
        if (value == output.Applied)
            return;
        output.Applied = value;
        var field = binding.Target.Field;
        if (field == null)
            return;
        switch (output.Channel)
        {
            case ImportedAnimatorChannel.Boolean: Write(field, value.x > 0.5f); break;
            case ImportedAnimatorChannel.Number: Write(field, value.x); break;
            case ImportedAnimatorChannel.Position:
            case ImportedAnimatorChannel.Scale: Write(field, new float3(value.x, value.y, value.z)); break;
            case ImportedAnimatorChannel.Rotation: Write(field, new floatQ(value.x, value.y, value.z, value.w).Normalized); break;
            case ImportedAnimatorChannel.Color: Write(field, new colorHDR(value.x, value.y, value.z, value.w)); break;
            case ImportedAnimatorChannel.Vector: Write(field, value); break;
        }
    }

    // A shape the animation holds is claimed over the face and menu drivers; one held only partly mixes with
    // what they put underneath, so it is written every frame. One whose mesh is still loading is tried again.
    private void Shape(UnityAnimatorRuntime runtime, Output output, int i, int b)
    {
        var renderer = output.Renderer;
        bool live = false;
        if (renderer != null && !renderer.IsDestroyed)
        {
            int index = output.ShapeIndex;
            if (index < 0 || renderer.BlendShapeName(index) != output.Shape)
                output.ShapeIndex = index = renderer.GetBlendShapeIndex(output.Shape);
            float amount = runtime.Amount(b);
            bool extra = runtime.HasExtra(b);
            if (index < 0)
                live = true;
            else if (amount == 0f && !extra)
            {
                if (output.Claimed)
                {
                    renderer.ReleaseBlendShape(index, output.Binding);
                    output.Claimed = false;
                }
            }
            else if (!renderer.ClaimBlendShape(index, output.Binding, SkinnedMeshRenderer.BlendShapePriorityGesture))
                live = true;
            else
            {
                output.Claimed = true;
                live = amount < 1f || extra;
                float basis = renderer.GetBlendShapeWeight(index);
                output.Basis = basis;
                float weight = runtime.Value(b, new float4(basis * 100f, 0, 0, 0)).x / 100f;
                renderer.DriveBlendShapeWeight(index, System.Math.Clamp(weight, 0f, 1f));
            }
        }
        int at = _shapesLive.IndexOf(i);
        if (live && at < 0) _shapesLive.Add(i);
        else if (!live && at >= 0) _shapesLive.RemoveAt(at);
    }

    private void Material(UnityAnimatorRuntime runtime, Output output, int b)
    {
        var binding = output.Binding;
        int reference = runtime.Reference(b);
        if (output.Applied.x == reference)
            return;
        IAssetProvider<MaterialAsset>? material = null;
        if (reference >= 0 && reference < runtime.Graph.References.Count && runtime.Graph.References[reference].Guid is { } guid)
            for (int k = 0; k < binding.MaterialKeys.Count && k < binding.Materials.Count; k++)
                if (string.Equals(binding.MaterialKeys[k], guid, StringComparison.OrdinalIgnoreCase))
                {
                    material = binding.Materials.GetElement(k).Target;
                    break;
                }
        if (reference < 0 || material == null)
            material = binding.Materials.Count > 0 ? binding.Materials.GetElement(0).Target : null;
        output.Applied = new float4(reference, 0, 0, 0);
        binding.MaterialSlot.LocalValueOnly = true;
        binding.MaterialSlot.SetValue(material!);
    }

    private static void Write<T>(IField field, T value)
    {
        if (field is SyncField<T> target) target.SetDrivenValueLocal(value);
    }

    private void ReleaseShapes()
    {
        foreach (var output in _outputs)
        {
            if (output == null)
                continue;
            if (output.Claimed && output.Renderer is { IsDestroyed: false } renderer && output.ShapeIndex >= 0)
                renderer.ReleaseBlendShape(output.ShapeIndex, output.Binding);
            output.Claimed = false;
            output.Applied = new float4(float.NaN, float.NaN, float.NaN, float.NaN);
        }
        _shapesLive.Clear();
        _applyAll = true;
    }

    private void Stop()
    {
        if (_boundCount < 0 && !_holding)
            return;
        ReleaseShapes();
        if (_holding)
        {
            FaceHold.Release(Root.Target ?? Slot, this);
            _holding = false;
        }
        Array.Clear(_tracking);
        _boundCount = -1;
    }

    private void OnSlotActiveChanged(Slot slot) { if (!slot.IsActive) Stop(); }
    public override void OnChanges() { base.OnChanges(); if (!Enabled.Value) Stop(); }

    public override void OnDestroy()
    {
        Slot.ActiveChanged -= OnSlotActiveChanged;
        Slot.ParentChanged -= OnLodMoved;
        Slot.ActiveUserRootChanged -= OnLodMoved;
        Stop();
        Unwatch();
        base.OnDestroy();
    }
}
