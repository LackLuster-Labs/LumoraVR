// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Assets.Animation;
using Lumora.Core.Math;
using Lumora.Core.Media;
using Lumora.Core.Persistence;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// Plays one animation clip, writing every bound channel through a drive each update.
//
// SYNC MODEL - derived, not streamed, and none of it lives here any more: Playback is a SyncPlayback,
// which owns the anchor, the speed, the loop mode and the playing flag as one member and answers the
// position from the session clock. That member's header has the reasoning. What this component adds
// is the LENGTH - the clip's duration, which is not state because the clip itself replicates and
// every peer's copy of this component reads the same number off it.
//
// It used to be five loose Sync fields (Playing, Speed, WrapMode, AnchorPosition, AnchorTicks). They
// still load: see Load below. -xlinka
// Version 1 is the consolidated Playback member. A file that stamps nothing is the loose-member shape,
// and TypeMigrations rewrites it on the way in.
[SaveTypeVersion(1)]
[ComponentCategory("Animation")]
public class Animator : Component, ICustomInspectorUI, IPlayable, IInspectorActionContextHandler
{
    public readonly AssetRef<AnimationAsset> Clip;

    public readonly SyncPlayback Playback;

    // one entry per bound channel
    public readonly SyncList<AnimationBinding> Bindings;

    // With any set, the clip plays only while one of them is on, and while all are off every bound field
    // sits at the value it had before the clip first moved it: a tail that wags while its switch is on and
    // comes back to rest when it is not. -xlinka
    public readonly SyncRefList<IField<bool>> Switches;

    // Typed writers, rebuilt whenever the clip or the binding list changes. Never persisted: they are
    // derived from the clip plus the bindings, both of which replicate.
    private readonly List<ITrackBinder> _binders = new();
    private bool _bindersValid;
    private int _boundCount;
    private int _unboundCount;

    // The clip instance the current binder table was built against. The asset behind Clip can be
    // replaced without the REF ever changing (the provider's URL moves, or the asset reloads), so a
    // ref-change event alone would leave binders pointing into a discarded clip's tracks.
    private AnimationClip? _boundClip;

    // Frames until the next retry while something is still unresolved. A joining peer receives the
    // binding elements before their target refs finish resolving, so the first rebuild legitimately
    // finds nothing to drive and has to be retried - but retrying every frame would rebuild the whole
    // table (and allocate a binder per bound track) forever whenever a target is permanently gone. -xlinka
    private int _retryCountdown;
    private const int RetryFrames = 30;

    public Animator()
    {
        Clip = new AssetRef<AnimationAsset>(this);
        Playback = new SyncPlayback();
        Bindings = new SyncList<AnimationBinding>();
        Switches = new SyncRefList<IField<bool>>(this);
    }

    public AnimationClip? CurrentClip => Clip.Asset?.Clip;

    // seconds; 0 with no clip
    public float Duration => CurrentClip?.Duration ?? 0f;

    public int BoundTrackCount => _boundCount;

    // target destroyed, or never bound
    public int UnboundTrackCount => _unboundCount;

    // wrapped into the clip
    public float Position
    {
        get => Playback.Position;
        set => Playback.Seek(value);
    }

    // unwrapped; used to tell whether a non-looping clip has run out
    public float RawPosition => Playback.RawPosition;

    public bool IsFinished => Playback.IsFinished;

    public PlaybackLoopMode WrapMode
    {
        get => Playback.LoopMode;
        set => Playback.LoopMode = value;
    }

    // negative plays backwards
    public float Speed
    {
        get => Playback.Speed;
        set => Playback.Speed = value;
    }

    // TRANSPORT SEAM
    //
    // The same buttons, seekers and drivers that run a video run this. Where the two vocabularies
    // disagree the interface wins EXPLICITLY rather than the public method changing meaning under the
    // inspector: Play here has always meant "carry on" and the interface's Play means "from the top",
    // which is Restart. A clip is never a stream, so ClipLength is always a real number. -xlinka
    double IPlayable.ClipLength => Duration;

    bool IPlayable.IsStreaming => false;

    bool IPlayable.IsPlaying => Playback.IsPlaying;

    float IPlayable.NormalizedPosition
    {
        get => Playback.NormalizedPosition;
        set => Playback.NormalizedPosition = value;
    }

    bool IPlayable.Loop
    {
        get => Playback.LoopMode != PlaybackLoopMode.Once;
        set => Playback.LoopMode = value ? PlaybackLoopMode.Loop : PlaybackLoopMode.Once;
    }

    void IPlayable.Play() => Playback.Play();

    void IPlayable.Resume() => Playback.Resume();

    [SyncMethod]
    [ScriptMethod]
    public void Play() => Playback.Resume();

    [SyncMethod]
    [ScriptMethod]
    public void Pause() => Playback.Pause();

    [SyncMethod]
    [ScriptMethod]
    public void Stop() => Playback.Stop();

    [SyncMethod]
    [ScriptMethod]
    public void Restart() => Playback.Play();

    [SyncMethod]
    [ScriptMethod]
    public void TogglePlayback() => Playback.TogglePlayback();

    public override void OnAwake()
    {
        base.OnAwake();

        // Both inputs to the binder table replicate, so every peer rebuilds independently and lands on
        // the same table without any of it being sent.
        Clip.OnValueChange += _ => _bindersValid = false;
        Bindings.ElementsAdded += (_, _, _) => _bindersValid = false;
        Bindings.ElementsRemoved += (_, _, _) => _bindersValid = false;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var clip = CurrentClip;
        if (clip == null || clip.TrackCount == 0)
        {
            Playback.Length = -1f;
            return;
        }

        // Pushed every frame rather than off a clip-changed event: the asset behind Clip can be
        // rebuilt in place (an importer finishing, a reload) without the ref ever moving, and a stale
        // length silently wraps every sample to the wrong frame.
        Playback.Length = clip.Duration;

        // A different clip instance invalidates the table even when the ref never moved.
        if (!ReferenceEquals(clip, _boundClip))
        {
            _bindersValid = false;
        }

        if (!_bindersValid || (_unboundCount > 0 && --_retryCountdown <= 0))
        {
            RebuildBinders(clip);
        }
        if (_binders.Count == 0)
        {
            return;
        }

        if (Switches.Count > 0 && !AnySwitchOn())
        {
            for (int i = 0; i < _binders.Count; i++)
            {
                _binders[i].ApplyRest();
            }
            return;
        }

        float time = Playback.Position;
        for (int i = 0; i < _binders.Count; i++)
        {
            _binders[i].Apply(time);
        }
    }

    private bool AnySwitchOn()
    {
        for (int i = 0; i < Switches.Count; i++)
        {
            if (Switches[i] is { } field && field.Value)
            {
                return true;
            }
        }
        return false;
    }

    // LEGACY SAVES
    // Playback used to be five loose members. A save written then still names them, and Worker.Load
    // walks members that EXIST, so those keys would be dropped on the floor without this.
    //
    // The old anchor is not carried across even though its timeline is the same shape: it names an
    // instant on a clock that has moved on by however long the file sat on disk, so honouring it would
    // fast-forward the clip by that much. The saved position is what the file actually meant, and
    // re-anchoring it to now is what SyncPlayback's own load does with its own format. -xlinka
    public override void Load(DataTreeNode node, LoadControl control)
    {
        base.Load(node, control);

        if (node is not DataTreeDictionary dictionary || dictionary.TryGetNode("Playback") != null)
        {
            return;
        }

        var positionNode = dictionary.TryGetNode("AnchorPosition");
        var playingNode = dictionary.TryGetNode("Playing");
        if (positionNode == null && playingNode == null)
        {
            return;
        }

        Playback.SetLoadedState(
            dictionary.ExtractOrDefault("Playing", false),
            SyncPlayback.ParseLoopMode(dictionary.TryGetNode("WrapMode"), PlaybackLoopMode.Loop),
            dictionary.ExtractOrDefault("AnchorPosition", 0f),
            dictionary.ExtractOrDefault("Speed", 1f));
    }

    // BINDING

    public int AutoBind(Slot root)
    {
        var clip = CurrentClip;
        if (clip == null)
        {
            LumoraLogger.Warn("Animator.AutoBind: clip is not loaded yet; nothing to bind");
            return 0;
        }
        return AutoBind(root, clip);
    }

    // used by importers that already hold the built clip, skipping the asset round trip through the
    // local DB; bindings are track-index based so they still match once the clip arrives via Clip
    public int AutoBind(Slot root, AnimationClip clip)
    {
        if (root == null)
        {
            LumoraLogger.Warn("Animator.AutoBind: null root");
            return 0;
        }
        if (clip == null)
        {
            LumoraLogger.Warn("Animator.AutoBind: null clip");
            return 0;
        }

        Bindings.Clear();

        // One name sweep, reused for every track. A per-track FindChild would be O(tracks * slots),
        // which on a full-body clip is tens of thousands of walks for a table we can build once.
        var byName = new Dictionary<string, Slot>(StringComparer.Ordinal);
        IndexSubtree(root, byName);

        int bound = 0;
        for (int i = 0; i < clip.TrackCount; i++)
        {
            var track = clip[i];
            if (!byName.TryGetValue(track.Node, out var slot))
            {
                continue;
            }

            var field = ResolveProperty(slot, track.Property);
            if (field == null || field.ValueType != track.ValueType)
            {
                continue;
            }

            // A field already under someone else's drive would be refused by the link manager anyway;
            // skipping it here keeps the binding list honest about what is actually driving.
            if (field.IsDriven)
            {
                continue;
            }

            var binding = Bindings.Add();
            binding.TrackIndex.Value = i;
            binding.Target.DriveTarget(field);
            bound++;
        }

        _bindersValid = false;
        LumoraLogger.Debug($"Animator.AutoBind: bound {bound}/{clip.TrackCount} tracks under '{root.SlotName.Value}'");
        return bound;
    }

    private static void IndexSubtree(Slot slot, Dictionary<string, Slot> byName)
    {
        if (slot == null)
        {
            return;
        }
        // First wins, matching how the model importer resolves duplicate node names to slots.
        var name = slot.SlotName.Value;
        if (!string.IsNullOrEmpty(name) && !byName.ContainsKey(name))
        {
            byName[name] = slot;
        }
        foreach (var child in slot.Children)
        {
            IndexSubtree(child, byName);
        }
    }

    // recognized forms: Position/Rotation/Scale (transform), BlendShape.<name> (skinned renderer
    // weight), <Component>.<Member>, or a bare slot member name
    public static IField? ResolveProperty(Slot slot, string property)
    {
        if (slot == null || string.IsNullOrEmpty(property))
        {
            return null;
        }

        switch (property)
        {
            case "Position":
                return slot.LocalPosition;
            case "Rotation":
                return slot.LocalRotation;
            case "Scale":
                return slot.LocalScale;
        }

        int dot = property.IndexOf('.');
        if (dot <= 0 || dot >= property.Length - 1)
        {
            return slot.TryGetField(property);
        }

        string head = property.Substring(0, dot);
        string tail = property.Substring(dot + 1);

        if (head == "BlendShape")
        {
            var renderer = slot.GetComponent<SkinnedMeshRenderer>();
            if (renderer == null)
            {
                return null;
            }
            int index = renderer.GetBlendShapeIndex(tail);
            if (index < 0 || index >= renderer.BlendShapeWeights.Count)
            {
                return null;
            }
            return renderer.BlendShapeWeights.GetElement(index);
        }

        foreach (var component in slot.Components)
        {
            if (component != null && component.GetType().Name == head)
            {
                return component.TryGetField(tail);
            }
        }
        return null;
    }

    private void RebuildBinders(AnimationClip clip)
    {
        _binders.Clear();
        _boundCount = 0;
        _unboundCount = 0;

        for (int i = 0; i < Bindings.Count; i++)
        {
            var binding = Bindings[i];
            if (binding == null)
            {
                continue;
            }

            int trackIndex = binding.TrackIndex.Value;
            if (trackIndex < 0 || trackIndex >= clip.TrackCount)
            {
                _unboundCount++;
                continue;
            }

            var field = binding.Target.Field;
            if (field == null)
            {
                // Not granted yet (the ref may still be resolving on this peer) or the target is gone.
                _unboundCount++;
                continue;
            }

            var binder = CreateBinder(field.ValueType);
            if (binder == null || !binder.Setup(binding, field, clip[trackIndex]))
            {
                _unboundCount++;
                continue;
            }

            _binders.Add(binder);
            _boundCount++;
        }

        _bindersValid = true;
        _boundClip = clip;
        _retryCountdown = RetryFrames;
    }

    // TYPED WRITE PATH
    // A binding knows its field's type only at runtime, so the write goes through a per-type binder
    // built once and cached. This is what keeps sampling and writing allocation-free per frame.

    private interface ITrackBinder
    {
        bool Setup(AnimationBinding binding, IField field, AnimationTrack track);
        void Apply(float time);
        void ApplyRest();
    }

    private sealed class TrackBinder<T> : ITrackBinder
    {
        private AnyFieldDrive _drive = null!;
        private SyncField<T> _field = null!;
        private IAnimationTrack<T> _track = null!;
        private T _rest = default!;

        public bool Setup(AnimationBinding binding, IField field, AnimationTrack track)
        {
            if (field is not SyncField<T> typedField || track is not IAnimationTrack<T> typedTrack)
            {
                return false;
            }
            _drive = binding.Target;
            _field = typedField;
            _track = typedTrack;
            // Taken once, the first time this binding meets its field, before anything has been written.
            if (binding.Rest is not T rest)
            {
                rest = typedField.Value;
                binding.Rest = rest;
            }
            _rest = rest;
            return true;
        }

        public void ApplyRest()
        {
            if (_drive.IsLinkValid && !_field.IsDestroyed)
            {
                _field.SetDrivenValueLocal(_rest);
            }
        }

        public void Apply(float time)
        {
            // IsLinkValid is the guard that a refused or released link writes nothing.
            if (!_drive.IsLinkValid || _field.IsDestroyed)
            {
                return;
            }
            if (_track.Evaluate(time, out var value))
            {
                // Local-only: the clip, the anchor and the bindings all replicate, so every peer
                // derives this same value itself. Broadcasting it would double the traffic and fight
                // the remote peer's own computation.
                _field.SetDrivenValueLocal(value);
            }
        }
    }

    private static readonly Dictionary<Type, Func<ITrackBinder>> _binderFactories = new();

    private static ITrackBinder? CreateBinder(Type valueType)
    {
        if (valueType == null)
        {
            return null;
        }

        Func<ITrackBinder>? factory;
        lock (_binderFactories)
        {
            if (!_binderFactories.TryGetValue(valueType, out factory))
            {
                if (!AnimationCodecs.IsSupported(valueType))
                {
                    return null;
                }
                var closed = typeof(TrackBinder<>).MakeGenericType(valueType);
                factory = () => (ITrackBinder)Activator.CreateInstance(closed)!;
                _binderFactories[valueType] = factory;
            }
        }
        return factory();
    }

    // INSPECTOR

    public void BuildInspectorBody(UIBuilder ui)
    {
        var clip = CurrentClip;
        if (clip == null)
        {
            AddStatRow(ui, "Clip", Clip.Target == null ? "none assigned" : "loading...");
            return;
        }

        AddStatRow(ui, "Clip", string.IsNullOrEmpty(clip.Name) ? "(unnamed)" : clip.Name);
        AddStatRow(ui, "Duration", $"{clip.Duration:0.###} s");
        if (clip.Framerate > 0f)
        {
            AddStatRow(ui, "Source rate", $"{clip.Framerate:0.##} fps");
        }
        AddStatRow(ui, "Tracks", clip.TrackCount.ToString());
        AddStatRow(ui, "Bound", $"{_boundCount} driving");
        if (_unboundCount > 0)
        {
            AddStatRow(ui, "Unbound", $"{_unboundCount} not resolved");
        }
        AddStatRow(ui, "Position", $"{Playback.Position:0.###} s");
        AddStatRow(ui, "Loop", Playback.LoopMode.ToString());
        AddStatRow(ui, "State", Playback.IsPlaying ? $"playing x{Playback.Speed:0.##}" : "paused");

        if (Clip.Target is Assets.AnimationProvider)
        {
            InspectorUI.FixedRow(ui.Root, "Curves", InspectorUI.RowHeight, out var row, ui.Root);
            InspectorUI.RelayButton(row, this, "curves", "Edit curves", 0f);
        }
    }

    public void HandleInspectorAction(string argument, UIInteractionContext context)
    {
        if (argument == "curves" && Clip.Target is Assets.AnimationProvider provider)
            CurveStudioPanel.Open(provider, null, context.WorldPoint);
    }

    private static void AddStatRow(UIBuilder ui, string label, string value)
    {
        // Theme from the hosting panel's UI tree, NOT this component's world slot: the animator's slot
        // has no UITheme above it, and text without a font renders nothing.
        InspectorUI.FixedRow(ui.Root, label, 24f, out var rowUi, ui.Root);
        rowUi.PushStyle();
        rowUi.MinWidth(150f);
        rowUi.PreferredWidth(190f);
        rowUi.FlexibleWidth(0f);
        var labelText = rowUi.Text(label, InspectorUI.FontSize - 1f, InspectorUI.MutedColor);
        InspectorUI.FillParent(labelText.RectTransform!);
        labelText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        labelText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        rowUi.PopStyle();
        rowUi.PushStyle();
        rowUi.FlexibleWidth(1f);
        var valueText = rowUi.Text(value, InspectorUI.FontSize - 1f, InspectorUI.TextColor);
        InspectorUI.FillParent(valueText.RectTransform!);
        valueText.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        valueText.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        rowUi.PopStyle();
    }
}
