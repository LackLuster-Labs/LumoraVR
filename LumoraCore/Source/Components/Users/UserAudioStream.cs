// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Audio;
using Lumora.Core.Audio;
using Lumora.Core.Components.Audio;
using Lumora.Core.Networking.Streams;

namespace Lumora.Core.Components;

// The world-side face of a user's voice stream. Two jobs, and which one it is doing depends entirely
// on whose user this is:
//
//   - the local user: pull filtered microphone samples off the default capture input and write them
//     into the stream, or do not, if anything says the microphone is off.
//   - everybody else: nothing at all. The stream fills itself from the wire.
//
// The stream's own world-thread tick is no longer done here: the engine ticks every user's streams
// from World.Update, so a stream with nothing pointed at it still keeps its ring sized.
//
// An AudioOutput cannot point at a stream directly - its Source is a component ref - so this is also
// what the head's output actually plays: it forwards the mixer straight through to the stream. -xlinka
[ComponentCategory("Users")]
public class UserAudioStream : Component, IWorldAudioDataSource
{
    public readonly SyncRef<AudioStream> Stream;

    // Which tap on the capture device to take. Filtered is the voice path: gated, normalized, shaped for
    // speech. Raw is what a device being streamed into the world wants, because a noise gate applied to
    // music removes the quiet parts of it. -xlinka
    public readonly Sync<bool> UseFilteredData;

    // Off for a device stream: a capture that is not a voice is not muted by the voice controls and is
    // not stopped by the host silencing this user's microphone.
    public readonly Sync<bool> IsVoiceCapture;

    // Held by whoever owns the stream: nothing is captured or sent, and every peer stops playing what is
    // left in its ring. Not the component's Enabled: a disabled component gets no update, so it could
    // neither close the capture nor notice being switched back on. -xlinka
    public readonly Sync<bool> Paused;

    // Which input to capture from. Null falls back to the system default, which is what a voice wants.
    private AudioInput? _preferredInput;

    // Mirrors for the audio and capture threads. The mixer reads IsActive and Read on the audio
    // thread, and neither is allowed anywhere near a sync member.
    private AudioStream? _source;
    private volatile bool _sourceUsable;

    private AudioStream? _captureTarget;
    private volatile bool _muted = true;

    private AudioInput? _subscribedInput;
    private bool _subscribed;
    private bool _subscribedFiltered = true;
    private bool _wasPaused;

    public UserAudioStream()
    {
        Stream = new SyncRef<AudioStream>(this);
        UseFilteredData = new Sync<bool>(this, true);
        IsVoiceCapture = new Sync<bool>(this, true);
        Paused = new Sync<bool>(this, false);
    }

    // Local only: an input device is this machine's, so which one a stream captures from cannot be a
    // replicated field.
    public AudioInput? PreferredInput
    {
        get => _preferredInput;
        set => _preferredInput = value;
    }

    // The input this stream captures from, so anything else listening to the same voice (the mouth)
    // taps the same device.
    public AudioInput? CaptureInput => _preferredInput ?? Engine.Current?.AudioSystem?.DefaultAudioInput;

    public User? TargetUser => Stream.Target?.Owner ?? Slot?.ActiveUserRoot?.ActiveUser;

    // True while this machine is not sending anything for this user. Only ever meaningful for the
    // local user; a remote one is always "muted" here because this peer never captures for them.
    public bool IsMuted => _muted;

    // AUDIO THREAD

    public int ChannelCount => 2;

    public bool IsActive
    {
        get
        {
            var source = _source;
            return _sourceUsable && source != null && source.IsActive;
        }
    }

    public void Read(Span<StereoSample> buffer, AudioSimulator simulator)
    {
        var source = _source;
        if (source == null)
        {
            buffer.Clear();
            return;
        }
        source.Read(buffer, simulator);
    }

    // WORLD THREAD

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var stream = Stream.Target;
        _source = stream;
        bool paused = Paused.Value;
        // The tail a receiver still holds would otherwise play the moment the stream comes back.
        if (paused && !_wasPaused && stream != null && !stream.IsDestroyed && !stream.IsLocal)
            stream.ResetBuffer();
        _wasPaused = paused;
        _sourceUsable = stream != null && !stream.IsDestroyed && Enabled.Value && !paused;

        if (stream == null || stream.IsDestroyed)
        {
            Unsubscribe();
            return;
        }

        var owner = stream.Owner;
        if (owner == null || !ReferenceEquals(owner, World?.LocalUser))
        {
            // A remote user's voice is written by the wire, not by this machine's microphone.
            Unsubscribe();
            return;
        }

        _captureTarget = stream;
        ApplyCaptureState(stream, owner);
    }

    // Three separate ways to end up silent, and every one of them is final: the local setting, the
    // host's silencing (which a guest cannot lift off themselves), and a world that is not the one
    // being looked at, because talking into the world behind the dashboard is not a thing anybody
    // meant to do.
    private void ApplyCaptureState(AudioStream stream, User owner)
    {
        bool voice = IsVoiceCapture.Value;
        bool muted = !Enabled.Value
            || Paused.Value
            || Slot?.IsActive != true
            || (voice ? !AudioInput.IsVoiceCaptureAllowed(owner, World) : World?.IsFocused != true);

        if (muted)
        {
            if (!_muted)
            {
                _muted = true;
                // Whatever was half said when the mute landed is dropped rather than held for the
                // next time the microphone opens.
                (stream as EncodedAudioStream)?.FlushEncoder();
            }
            Unsubscribe();
            return;
        }

        var input = CaptureInput;
        bool filtered = UseFilteredData.Value;
        if (!ReferenceEquals(input, _subscribedInput) || filtered != _subscribedFiltered)
        {
            Unsubscribe();
            Subscribe(input, filtered);
        }

        _muted = !_subscribed;
    }

    private void Subscribe(AudioInput? input, bool filtered)
    {
        if (input == null || _subscribed)
            return;

        try
        {
            if (filtered)
                input.NewFilteredSamples += OnCapturedSamples;
            else
                input.NewRawSamples += OnCapturedSamples;
            _subscribedInput = input;
            _subscribedFiltered = filtered;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"UserAudioStream: could not open the capture input: {ex.Message}");
            _subscribedInput = null;
            _subscribed = false;
        }
    }

    private void Unsubscribe()
    {
        _muted = true;
        if (!_subscribed)
        {
            _subscribedInput = null;
            return;
        }

        var input = _subscribedInput;
        bool filtered = _subscribedFiltered;
        _subscribed = false;
        _subscribedInput = null;

        try
        {
            if (input != null)
            {
                if (filtered)
                    input.NewFilteredSamples -= OnCapturedSamples;
                else
                    input.NewRawSamples -= OnCapturedSamples;
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"UserAudioStream: could not close the capture input: {ex.Message}");
        }
    }

    // CAPTURE THREAD. Plain fields only, and a stream write that locks for the length of a memcpy.
    private void OnCapturedSamples(ReadOnlySpan<StereoSample> samples)
    {
        if (_muted)
            return;

        var stream = _captureTarget;
        if (stream == null || stream.IsDestroyed)
            return;

        stream.Write(samples);
    }

    public override void OnDestroy()
    {
        Unsubscribe();
        _captureTarget = null;
        _source = null;
        _sourceUsable = false;
        base.OnDestroy();
    }
}
