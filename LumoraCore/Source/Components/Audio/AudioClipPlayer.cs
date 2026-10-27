// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Audio;
using Lumora.Core.Assets;
using Lumora.Core.Media;

namespace Lumora.Core.Components.Audio;

// Plays a decoded clip into the mixer. Point an AudioOutput at it and it is a sound in the room.
//
// THE CLOCK IS NOT HERE. Playback is a SyncPlayback, so the playhead is an anchor plus a rate that
// every peer computes the same answer from, and this component never streams a position. The audio
// thread keeps its own cursor into the clip and CHASES that shared clock: it reads forward at the
// playback rate, and only when it has drifted more than a tenth of a second from where the transport
// says it should be does it jump, with a short crossfade so the correction is a smear rather than a
// click. Being the clock instead would mean four machines each playing from their own buffer position
// and calling that synchronised.
//
// NOTHING ON THE AUDIO THREAD TOUCHES A SYNC MEMBER. The transport is sampled once per world frame
// into plain fields under a lock, together with the DSP time at which it was sampled, and the audio
// thread extrapolates from that pair. Reading a replicated field from the mixer is how you get a torn
// read in the one place that cannot afford one. -xlinka
[ComponentCategory("Audio")]
public sealed class AudioClipPlayer : Component, IWorldAudioDataSource, IPlayable
{
    public readonly AssetRef<AudioClip> Clip;

    public readonly SyncPlayback Playback;

    public readonly Sync<bool> Loop;

    public readonly Sync<float> Speed;

    // How far the cursor may sit from the shared clock before it is dragged back. Under this a
    // correction would be audible more often than the drift is.
    private const double DriftTolerance = 0.1;

    // Frames of overlap when the cursor jumps. Long enough to hide the discontinuity, short enough
    // that at 48 kHz it is five milliseconds of doubled material.
    private const int CrossfadeFrames = 256;

    // The audio thread extrapolates the shared clock forward from the last world-frame snapshot. Past
    // this much of it the world thread has stopped talking (the component was disabled, the slot went
    // inactive, the world was paused) and extrapolating is guessing: go quiet instead of playing on
    // from a snapshot nobody is refreshing. A background world still ticks at 10 Hz, so this has to
    // clear that comfortably. -xlinka
    private const double SnapshotStaleAfter = 0.5;

    private readonly object _transportLock = new object();

    private AudioClipData? _clipData;
    private double _transportPosition;
    private double _transportDspTime;
    private double _transportSpeed = 1.0;
    private double _clipLength;
    private bool _transportLoop;
    private bool _cursorValid;

    private volatile bool _isActive;

    // Audio thread only, past the snapshot at the top of Read.
    private double _cursor;
    private StereoSample[] _fade = new StereoSample[CrossfadeFrames];

    // Latched authored values: the mirror into Playback fires on a real change rather than on every
    // frame the two disagree, so a peer that is not allowed to write the transport does not retry
    // forever and the peers that are do not all post the same answer at each other.
    private bool _mirrorKnown;
    private bool _lastLoop;
    private float _lastSpeed;

    public AudioClipPlayer()
    {
        Clip = new AssetRef<AudioClip>(this);
        Playback = new SyncPlayback();
        Loop = new Sync<bool>(this, false);
        Speed = new Sync<float>(this, 1f);
    }

    public AudioClip? ClipAsset => Clip.Asset;

    public AudioClipData? ClipData => Clip.Asset?.Data;

    public bool IsClipReady => Clip.Asset?.IsReady == true;

    // Why the clip did not load, from its provider, which alone can see a failed load.
    public string? LoadError => (Clip.Target as IUrlAssetProvider)?.LoadFailure ?? Clip.Asset?.FailureReason;

    // TRANSPORT

    public float Position
    {
        get => Playback.Position;
        set => Playback.Seek(value);
    }

    // -1 until the clip has arrived, which is the honest answer for a length nothing can sample yet.
    public double ClipLength
    {
        get
        {
            double duration = Clip.Asset?.Duration ?? 0.0;
            return duration > 0.0 ? duration : -1.0;
        }
    }

    public bool IsStreaming => false;

    public bool IsPlaying => Playback.IsPlaying;

    public bool IsFinished => Playback.IsFinished;

    public float NormalizedPosition
    {
        get => Playback.HasLength ? Playback.NormalizedPosition : -1f;
        set
        {
            if (Playback.HasLength)
                Playback.NormalizedPosition = value;
        }
    }

    // Explicit because the authored values carry these names as sync members, and those are the ones
    // that replicate and save. Playback follows them; it is not the other way round.
    float IPlayable.Speed
    {
        get => Speed.Value;
        set => Speed.Value = value;
    }

    bool IPlayable.Loop
    {
        get => Loop.Value;
        set => Loop.Value = value;
    }

    [SyncMethod]
    [ScriptMethod]
    public void Play() => Playback.Play();

    [SyncMethod]
    [ScriptMethod]
    public void Stop() => Playback.Stop();

    [SyncMethod]
    [ScriptMethod]
    public void Pause() => Playback.Pause();

    [SyncMethod]
    [ScriptMethod]
    public void Resume() => Playback.Resume();

    [SyncMethod]
    [ScriptMethod]
    public void TogglePlayback() => Playback.TogglePlayback();

    // WORLD FRAME

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();

        var asset = Clip.Asset;
        var data = asset != null && asset.IsReady ? asset.Data : null;

        // Pushed every frame rather than off a ref-changed event: the asset behind the ref can be
        // rebuilt in place (a reload, an import finishing) without the ref ever moving, and a stale
        // length wraps every sample to the wrong place.
        Playback.Length = data != null ? (float)data.Duration : -1f;

        MirrorAuthoredState();

        var world = World;
        bool background = world != null && world.Focus == Lumora.Core.World.WorldFocus.Background;
        bool playing = data != null && Enabled.Value && !background && Playback.IsPlaying;

        double dsp = Engine.Current?.AudioSystem?.Simulator.DspTime ?? 0.0;

        lock (_transportLock)
        {
            if (!ReferenceEquals(_clipData, data))
            {
                _clipData = data;
                // A different clip is a different timeline; there is nothing to crossfade from.
                _cursorValid = false;
            }
            _transportPosition = Playback.Position;
            _transportDspTime = dsp;
            _transportSpeed = Playback.Speed;
            _transportLoop = Playback.LoopMode != PlaybackLoopMode.Once;
            _clipLength = data?.Duration ?? 0.0;
            if (!playing)
                _cursorValid = false;
        }

        _isActive = playing;
    }

    private void MirrorAuthoredState()
    {
        bool loop = Loop.Value;
        float speed = Speed.Value;
        if (!float.IsFinite(speed))
            speed = 1f;

        if (_mirrorKnown && loop == _lastLoop && speed == _lastSpeed)
            return;

        _mirrorKnown = true;
        _lastLoop = loop;
        _lastSpeed = speed;

        // Both setters are no-ops when the value already matches, so the first pass after a load or a
        // join costs nothing and only a real edit moves the transport.
        Playback.LoopMode = loop ? PlaybackLoopMode.Loop : PlaybackLoopMode.Once;
        Playback.Speed = speed;
    }

    public override void OnDisabled()
    {
        base.OnDisabled();
        // OnCommonUpdate does not run on a disabled component, so the snapshot the audio thread is
        // reading would otherwise be the last one from while it was playing.
        _isActive = false;
    }

    public override void OnDestroy()
    {
        _isActive = false;
        lock (_transportLock)
        {
            _clipData = null;
            _cursorValid = false;
        }
        base.OnDestroy();
    }

    // AUDIO THREAD

    public int ChannelCount => 2;

    public bool IsActive => _isActive;

    public void Read(Span<StereoSample> buffer, AudioSimulator simulator)
    {
        AudioClipData? clip;
        double expected;
        double speed;
        double length;
        bool loop;
        bool resync;

        lock (_transportLock)
        {
            clip = _clipData;
            if (clip == null || !_isActive)
            {
                _cursorValid = false;
                buffer.Clear();
                return;
            }

            length = _clipLength;
            loop = _transportLoop;
            speed = _transportSpeed;

            // The transport was read on the world thread some fraction of a frame ago; the device
            // clock says how much of it has gone by since, so the expected position is where the
            // shared clock is NOW rather than where it was when the frame started.
            double elapsed = simulator.DspTime - _transportDspTime;
            if (elapsed < 0.0 || double.IsNaN(elapsed))
                elapsed = 0.0;
            if (elapsed > SnapshotStaleAfter)
            {
                _cursorValid = false;
                buffer.Clear();
                return;
            }
            expected = _transportPosition + elapsed * speed;

            resync = !_cursorValid;
            _cursorValid = true;
        }

        if (length <= 0.0 || clip.FrameCount <= 0)
        {
            buffer.Clear();
            return;
        }

        expected = loop ? Wrap(expected, length) : System.Math.Clamp(expected, 0.0, length);

        bool jump = resync || Drift(_cursor, expected, length, loop) > DriftTolerance;
        if (resync)
            _cursor = expected;

        double step = speed * clip.SampleRate / simulator.SampleRate;
        if (double.IsNaN(step) || double.IsInfinity(step))
            step = 0.0;

        if (jump && !resync)
        {
            // Render the tail of the old position first, then move and render the real buffer over
            // it. Both are the same material at the same rate, so the blend is a crossfade rather
            // than a fade to silence and back.
            int fade = System.Math.Min(CrossfadeFrames, buffer.Length);
            var old = _fade.AsSpan(0, fade);
            Render(old, clip, _cursor * clip.SampleRate, step, loop);

            _cursor = expected;
            double end = Render(buffer, clip, _cursor * clip.SampleRate, step, loop);
            _cursor = Settle(end, clip, loop);

            for (int i = 0; i < fade; i++)
                buffer[i] = old[i].LerpTo(buffer[i], (i + 1) / (float)fade);
            return;
        }

        double finish = Render(buffer, clip, _cursor * clip.SampleRate, step, loop);
        _cursor = Settle(finish, clip, loop);
    }

    // Walks the clip in FRAMES and hands back where it stopped, so the caller keeps one cursor and
    // the interpolation never re-derives it from a rounded seconds value.
    private static double Render(Span<StereoSample> target, AudioClipData clip, double startFrame, double step, bool loop)
    {
        int frames = clip.FrameCount;
        double position = startFrame;
        for (int i = 0; i < target.Length; i++)
        {
            target[i] = SampleAt(clip, position, frames, loop);
            position += step;
        }
        return position;
    }

    private static StereoSample SampleAt(AudioClipData clip, double position, int frames, bool loop)
    {
        if (loop)
        {
            position %= frames;
            if (position < 0.0)
                position += frames;
        }
        else if (position < 0.0 || position >= frames)
        {
            // Past the end of a clip that does not loop there is nothing to play, and silence is the
            // answer until the transport says it has finished.
            return default;
        }

        int index = (int)position;
        if (index >= frames)
            index = frames - 1;
        int next = index + 1;
        if (next >= frames)
            next = loop ? 0 : frames - 1;

        return clip.FrameAt(index).LerpTo(clip.FrameAt(next), (float)(position - index));
    }

    private static double Settle(double endFrame, AudioClipData clip, bool loop)
    {
        double seconds = endFrame / clip.SampleRate;
        if (!double.IsFinite(seconds))
            return 0.0;
        // A looping cursor left unwrapped runs out of double precision after long enough, and the
        // drift check compares it against a wrapped transport.
        return loop ? Wrap(seconds, clip.Duration) : seconds;
    }

    private static double Wrap(double value, double length)
    {
        if (length <= 0.0 || !double.IsFinite(value))
            return 0.0;
        double wrapped = value % length;
        return wrapped < 0.0 ? wrapped + length : wrapped;
    }

    // A looping clip one sample before the end and one sample after it are next to each other, not a
    // whole clip apart.
    private static double Drift(double cursor, double expected, double length, bool loop)
    {
        double direct = System.Math.Abs(cursor - expected);
        if (!loop || length <= 0.0)
            return direct;
        return System.Math.Min(direct, length - direct);
    }
}
