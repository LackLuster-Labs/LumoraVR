// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Audio;
using Lumora.Core.Audio;
using Lumora.Core.Math;
using GroupKind = Lumora.Core.Audio.AudioTypeGroup;
using SpaceKind = Lumora.Core.Audio.AudioDistanceSpace;

namespace Lumora.Core.Components.Audio;

// A sound in the world: a slot, a thing to play, and the shape of how far it carries. It does not
// decode or mix anything itself - it points at a source (a video, a voice, a clip) and hands the
// mixer a set of parameters once a frame.
//
// The aliases at the top are not decoration: the fields AudioTypeGroup and DistanceSpace are named
// after their own enums, and without them every use of the enum inside this class would bind to the
// field instead of the type. -xlinka
[ComponentCategory("Audio")]
public class AudioOutput : Component
{
    public readonly Sync<float> Volume;

    // Must resolve to an IWorldAudioDataSource. Anything else is ignored rather than refused: a ref
    // that lost its target mid-session should go quiet, not throw.
    public readonly SyncRef<Component> Source;

    // 0 = the same stereo mix everywhere, 1 = fully positioned in the room.
    public readonly Sync<float> SpatialBlend;
    public readonly Sync<bool> Spatialize;

    // Inside this distance of an ear the sound is not positioned at all, and it fades into positioning
    // over the transition range. Without it a source sitting on the ear pans hard left or right on a
    // millimetre of head movement.
    public readonly Sync<float> SpatializationStartDistance;
    public readonly Sync<float> SpatializationTransitionRange;

    public readonly Sync<float> DopplerLevel;
    public readonly Sync<float> Pitch;

    // On forces the same volume everywhere with no panning. Off leaves the decision to SpatialBlend, so
    // a blend of zero is global anyway.
    public readonly Sync<bool> Global;

    public readonly Sync<AudioRolloffCurve> RolloffMode;
    public readonly Sync<float> MinDistance;
    public readonly Sync<float> MaxDistance;

    // Higher wins when the voice limit is reached.
    public readonly Sync<int> Priority;

    public readonly Sync<GroupKind> AudioTypeGroup;

    // Local scales the distances by the slot's own scale, so a shrunk world object carries a shrunk
    // sound. Global keeps them in world metres whatever the slot is scaled to.
    public readonly Sync<SpaceKind> DistanceSpace;
    public readonly Sync<float> MinScale;
    public readonly Sync<float> MaxScale;

    // Bypasses the listeners' effect chains. UI and the user's own voice monitor want this: a reverb
    // applied to the sound of your own menu is a room you are not standing in. The output's own Effects
    // still run.
    public readonly Sync<bool> IgnoreAudioEffects;

    // Run in order on this output's own sound, once, before it is placed in the room: every ear hears the
    // result, each from where it stands, so a radio filter or an echo belongs to this one speaker. An entry
    // that is off, gone, or already running in another chain (a listener's or another output's) is skipped
    // until it is free.
    public readonly SyncRefList<AudioEffect> Effects;

    public readonly SyncRefList<AudioListener> ExcludedListeners;

    internal AudioOutputVoice? Voice;
    internal float3 LastPosition;
    internal bool HasLastPosition;
    internal bool InManager;
    internal bool QueuedForUpdate;
    internal bool QueuedForAddRemove;
    internal bool ExclusionsDirty = true;

    // What the voice was last handed as its own chain. This machine's own.
    internal readonly List<IAudioDspFilter> PushedEffects = new();

    private IAudioDataSource? _pushedSource;

    public AudioOutput()
    {
        Volume = new Sync<float>(this, 1f);
        Source = new SyncRef<Component>(this);
        SpatialBlend = new Sync<float>(this, 1f);
        Spatialize = new Sync<bool>(this, true);
        SpatializationStartDistance = new Sync<float>(this, 0.01f);
        SpatializationTransitionRange = new Sync<float>(this, 0.01f);
        DopplerLevel = new Sync<float>(this, 1f);
        Pitch = new Sync<float>(this, 1f);
        Global = new Sync<bool>(this, false);
        RolloffMode = new Sync<AudioRolloffCurve>(this, AudioRolloffCurve.LogarithmicFadeOff);
        MinDistance = new Sync<float>(this, 1f);
        MaxDistance = new Sync<float>(this, 500f);
        Priority = new Sync<int>(this, 128);
        AudioTypeGroup = new Sync<GroupKind>(this, GroupKind.SoundEffect);
        DistanceSpace = new Sync<SpaceKind>(this, SpaceKind.Local);
        MinScale = new Sync<float>(this, 0f);
        MaxScale = new Sync<float>(this, float.PositiveInfinity);
        IgnoreAudioEffects = new Sync<bool>(this, false);
        Effects = new SyncRefList<AudioEffect>(this);
        ExcludedListeners = new SyncRefList<AudioListener>(this);
    }

    public IWorldAudioDataSource? ResolvedSource => Source.Target as IWorldAudioDataSource;

    // The user this output belongs to, if it sits inside somebody's avatar. Null for an emitter in the
    // world, which is most of them.
    public User? ActiveUser => Slot?.ActiveUser;

    // A blocked user's whole avatar goes silent for me and for nobody else, so the test is here rather
    // than on the voice output: whatever they are carrying that makes noise is part of what I blocked.
    public bool ShouldBeEnabled
        => !IsDestroyed && Enabled.Value && Slot != null && Slot.IsActive && ResolvedSource != null
           && ActiveUser is not { IsAudioLocallyBlocked: true } and not { IsBlockedByMe: true };

    public bool IsRegistered => Voice != null;

    // How many filters this machine's mixer runs on this output's own sound. None while it has no voice here.
    public int ActiveEffectCount => PushedEffects.Count;

    // The last block this output actually produced, copied rather than consumed. This is what a
    // visualiser reads: reading the SOURCE would advance a stream's cursor and take the audio out of
    // the mixer's mouth. Returns how many samples were written. -xlinka
    public int CopyMonitor(System.Span<Lumora.Audio.StereoSample> destination)
        => Voice?.CopyMonitor(destination) ?? 0;

    public float ActualVolume
    {
        get
        {
            float volume = Clamp01(Volume.Value);
            // Per-user level, folded in here rather than written onto Volume: Volume is authored and
            // replicated, and my slider for one person is neither.
            var user = ActiveUser;
            if (user != null)
                volume *= Clamp(Sanitize(user.LocalVolume), 0f, User.MaxLocalVolume);
            var system = Engine.Current?.AudioSystem;
            return system == null ? volume : volume * system.GetGroupVolume(AudioTypeGroup.Value);
        }
    }

    // The distances the mixer actually uses: the authored ones, taken through the slot's scale when the
    // output is measured in local space. An infinite max stays infinite instead of becoming a NaN.
    public void GetActualDistances(out float minDistance, out float maxDistance, out float startDistance, out float transitionRange)
    {
        float scale = 1f;
        if (DistanceSpace.Value == SpaceKind.Local && Slot != null)
        {
            var slotScale = Slot.GlobalScale;
            float average = (slotScale.x + slotScale.y + slotScale.z) / 3f;
            scale = Clamp(average, Sanitize(MinScale.Value), MaxScale.Value);
            if (!IsFinite(scale) || scale < 0f)
                scale = 1f;
        }

        minDistance = Sanitize(MinDistance.Value) * scale;
        maxDistance = float.IsPositiveInfinity(MaxDistance.Value)
            ? float.PositiveInfinity
            : Sanitize(MaxDistance.Value) * scale;
        startDistance = Sanitize(SpatializationStartDistance.Value) * scale;
        transitionRange = Sanitize(SpatializationTransitionRange.Value) * scale;

        if (minDistance > maxDistance)
        {
            minDistance = maxDistance;
            maxDistance += minDistance * 0.0001f;
        }
    }

    internal AudioOutputParams BuildParams(float deltaTime)
    {
        var position = Slot != null ? Slot.GlobalPosition : float3.Zero;
        var rotation = Slot != null ? Slot.GlobalRotation : floatQ.Identity;
        var velocity = WorldAudioManager.VelocityOf(LastPosition, position, deltaTime, HasLastPosition);
        LastPosition = position;
        HasLastPosition = true;

        GetActualDistances(out float minDistance, out float maxDistance, out float start, out float transition);

        float blend = Clamp01(SpatialBlend.Value);
        bool global = Global.Value || blend <= 0.0001f;

        return new AudioOutputParams
        {
            Position = position,
            Rotation = rotation,
            Velocity = velocity,
            Volume = ActualVolume,
            Priority = Priority.Value,
            Spatialize = Spatialize.Value,
            SpatialBlend = blend,
            SpatializationStartDistance = start,
            SpatializationTransitionRange = transition,
            Pitch = Sanitize(Pitch.Value),
            DopplerLevel = Sanitize(DopplerLevel.Value),
            Shape = global ? AudioShape.Global : AudioShape.Sphere(minDistance, maxDistance, RolloffMode.Value),
            IgnoreEffects = IgnoreAudioEffects.Value,
        };
    }

    // Pushed only when the ref actually moved: handing the mixer a source swap means a stream reset on
    // the audio thread, and a reset per frame is a stutter per frame.
    internal void PushSource()
    {
        var voice = Voice;
        if (voice == null)
            return;
        IAudioDataSource? source = ResolvedSource;
        if (ReferenceEquals(source, _pushedSource))
            return;
        _pushedSource = source;
        voice.SetSource(source);
    }

    internal void ClearPushedSource() => _pushedSource = null;

    // Listeners THIS MACHINE leaves out, on top of the replicated list. Not replicated and not saved,
    // because "do not play this into my ear" is a fact about my ear: the replicated list is authored
    // scene data and a peer writing its own answer into it would be handing everyone else that answer,
    // and would be a foreign write to a host-allocated element besides. -xlinka
    private readonly List<AudioListener> _locallyExcluded = new List<AudioListener>();

    public bool IsLocallyExcluded(AudioListener listener)
        => listener != null && _locallyExcluded.Contains(listener);

    public void SetLocallyExcluded(AudioListener listener, bool excluded)
    {
        if (listener == null)
            return;
        bool present = _locallyExcluded.Contains(listener);
        if (present == excluded)
            return;
        if (excluded)
            _locallyExcluded.Add(listener);
        else
            _locallyExcluded.Remove(listener);
        // The manager re-reads the list on the next frame's exclusion pass; nothing else has to happen.
        ExclusionsDirty = true;
    }

    internal void CollectExcludedVoices(List<AudioListenerVoice> output)
    {
        for (int i = 0; i < ExcludedListeners.Count; i++)
        {
            var listener = ExcludedListeners[i];
            var voice = listener?.Voice;
            if (voice != null)
                output.Add(voice);
        }

        for (int i = _locallyExcluded.Count - 1; i >= 0; i--)
        {
            var listener = _locallyExcluded[i];
            if (listener == null || listener.IsDestroyed)
            {
                _locallyExcluded.RemoveAt(i);
                continue;
            }
            var voice = listener.Voice;
            if (voice != null && !output.Contains(voice))
                output.Add(voice);
        }
    }

    // What a userspace sound wants: on the UI slider, no doppler because the panel follows your head,
    // and no room effects because the panel is not in the room.
    public void SetupAsUI()
    {
        AudioTypeGroup.Value = GroupKind.UI;
        DopplerLevel.Value = 0f;
        IgnoreAudioEffects.Value = true;
    }

    public override void OnStart()
    {
        base.OnStart();
        World?.Audio?.RegisterOutput(this);
    }

    public override void OnChanges()
    {
        base.OnChanges();
        // Any change pass re-reads the exclusion list. It is a handful of refs, and a change pass on
        // this component is rare next to the per-frame parameter push.
        ExclusionsDirty = true;
        World?.Audio?.MarkOutputChanged(this);
    }

    public override void OnDestroy()
    {
        World?.Audio?.UnregisterOutput(this);
        base.OnDestroy();
    }

    private static float Clamp01(float value)
        => float.IsNaN(value) ? 0f : (value < 0f ? 0f : (value > 1f ? 1f : value));

    private static float Clamp(float value, float min, float max)
        => value < min ? min : (value > max ? max : value);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static float Sanitize(float value)
        => float.IsNaN(value) || value < 0f ? 0f : value;
}
