// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core.Input;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Input.Drivers;

// Where a person is actually looking, from the headset.
//
// Everything downstream of this has existed for a while and has never had anything to read: the eye
// fields on HeadDevice, the stream that carries them to other people, the gaze driver that turns them
// into bone rotations and eyeLook blendshapes, the procedural saccades that stand in when nothing is
// tracking. All of it was waiting for a source. This is the source.
//
// IT GOES THROUGH OPENXR, NOT THROUGH A VENDOR SDK, and that is the main decision here.
//
// The obvious way to support a Vive Pro Eye is its own SDK: a native DLL, a runtime the person has to
// have installed, Windows only, that headset only, and a P/Invoke surface to keep working. What it
// buys over the standard extension is per-eye direction, eyelid openness and pupil diameter.
//
// The standard extension gives one combined gaze ray and nothing else - but it works on a Vive Pro
// Eye, a Quest Pro, a Pico 4 Pro and anything else whose runtime implements it, with no native
// dependency and nothing to install. A combined gaze ray is also the great majority of what anybody
// sees: it is where your eyes point, which is the thing another person in the room reads off your
// face. Openness is the one real loss, and the blink driver already covers that convincingly.
//
// So this is the floor, and it is a floor that works everywhere. A vendor path can sit beside it
// later for the extra channels, filling the same fields on the same device. -xlinka
public static class OpenXREyeGaze
{
    // Godot's eye gaze extension finds its action BY NAME: openxr_eye_gaze_interaction.cpp does
    // find_action("eye_gaze_pose") and warns "make sure to add this to your action map" when it is
    // missing. Ours bound the eye gaze input to the shared "default_pose" action instead, so the
    // extension never found an action, never populated a pose, and eye tracking could not have
    // worked on any headset however well it supported it. The action map now carries a pose action
    // of that exact name; this is the name, written down once, so the two cannot drift. -xlinka
    public const string ActionName = "eye_gaze_pose";

    // The top-level path the runtime registers the gaze tracker under.
    public const string TrackerName = "/user/eyes_ext";

    // How far in front the two eyes are taken to converge when nothing says otherwise.
    //
    // The extension hands back ONE ray from between the eyes, and an avatar has two eyeballs that
    // have to point somewhere. Aiming both down the same ray makes a wall-eyed stare, because real
    // eyes converge on what they are looking at. So the ray is turned into a POINT at this distance
    // and each eye is aimed at that point, which is what gives an avatar the small inward turn that
    // reads as actually looking at something rather than through it. Two metres is a room.
    private const float DefaultConvergence = 2.0f;

    // Half the distance between the eyes. Only used to build the per-eye directions; the avatar's own
    // eye bones are wherever the rig puts them.
    private const float HalfIpd = 0.032f;

    private static bool _checked;
    private static bool _supported;
    private static XRPositionalTracker? _tracker;
    private static bool _warned;
    private static bool _announced;

    // A runtime can hand back a gaze pose with tracking data for eyes it is not measuring: a
    // streaming runtime that advertises the extension for the headsets that have eye cameras keeps
    // advertising it for the ones that do not, and what comes out is a ray fixed dead ahead of the
    // head. Real eyes never sit bit-still, so a head-local direction that has not turned by a tenth of
    // a degree inside the window is not a tracker, and the procedural gaze keeps the avatar. -xlinka
    private const long StaleAfterMs = 750;
    private const float MovedDotGap = 1.5e-6f; // 1 - cos(0.1 degrees)
    private static System.Numerics.Vector3 _lastLocalForward;
    private static bool _haveLocalForward;
    private static bool _localForwardMoved;
    private static long _lastMoveMs;
    private static bool _staleLogged;

    // BLINKS FROM A GAZE-ONLY TRACKER.
    //
    // This extension carries no eyelids, yet the blink driver hands the lids to whatever source reports live
    // eyes and stops its own blinking, so a headset that only sends a gaze ray (the Frame, and any runtime on
    // the standard extension) left every avatar staring without ever blinking. Two things put the blinks
    // back, both written as openness so they travel to other people like measured lids do:
    //  - the cameras lose the pupil while the lid is down, so a gaze that drops out for a moment IS a blink:
    //    the lids close on the drop and open when the gaze comes back, in step with the real eye;
    //  - a runtime that predicts the gaze straight through a blink never drops it, so when no blink has been
    //    seen for a natural interval one is played the usual way.
    // A drop longer than a blink is someone looking away or the headset coming off, and lets go as before.
    // -xlinka
    private const long BlinkDropMaxMs = 450;
    private const float CloseSeconds = 0.06f;
    private const float OpenSeconds = 0.11f;
    private const float SyntheticHoldSeconds = 0.04f;
    private static long _dropStartMs = -1;
    private static long _lastBlinkMs;
    private static long _nextBlinkGapMs = 4000;
    private static float _openness = 1f;
    private static float _syntheticElapsed = -1f;
    private static long _lastUpdateMs;
    private static readonly System.Random _blinkRandom = new();

    public static bool IsSupported => _supported;

    public static void Reset()
    {
        _checked = false;
        _supported = false;
        _tracker = null;
        _warned = false;
        _announced = false;
        _haveLocalForward = false;
        _localForwardMoved = false;
        _staleLogged = false;
        _dropStartMs = -1;
        _openness = 1f;
        _syntheticElapsed = -1f;
    }

    // Fill the head device's eye fields. Called once a frame from the VR driver, after the head pose
    // is in, because the gaze is expressed relative to where the head is.
    public static void Update(HeadDevice headDevice)
    {
        if (headDevice == null)
            return;

        // Real per-eye hardware beats a combined ray. When the vendor path is feeding this device,
        // stand down entirely rather than write the same fields twice a frame and have the later
        // writer win by accident.
        if (SRanipalDriver.EyesLive || SteamLinkFaceDriver.EyesLive)
            return;

        if (!Resolve())
        {
            if (headDevice.HasEyeTracking)
                Clear(headDevice);
            return;
        }

        var tracker = _tracker;
        if (tracker == null)
        {
            Clear(headDevice);
            return;
        }

        // The gaze arrives as a pose: a position between the eyes and a rotation looking down the
        // gaze. Its own tracking confidence decides whether it is worth anything this frame - a
        // blink, a person looking away from the cameras, or a headset lifted off the face all land
        // here as an untracked pose rather than as a stale direction that keeps staring.
        long now = System.Environment.TickCount64;
        float dt = _lastUpdateMs == 0 ? 0f : System.Math.Clamp((now - _lastUpdateMs) / 1000f, 0f, 0.1f);
        _lastUpdateMs = now;

        var pose = tracker.GetPose("default");
        if (pose == null || !pose.HasTrackingData)
        {
            // A live gaze that drops out for a moment is a blink: keep the last gaze, close the lids, and
            // only let go once the drop is too long to be one.
            if (headDevice.HasEyeTracking)
            {
                if (_dropStartMs < 0)
                    _dropStartMs = now;
                if (now - _dropStartMs <= BlinkDropMaxMs)
                {
                    _syntheticElapsed = -1f;
                    _openness = System.Math.Max(0f, _openness - dt / CloseSeconds);
                    WriteOpenness(headDevice);
                    return;
                }
            }
            Clear(headDevice);
            return;
        }
        if (_dropStartMs >= 0)
        {
            _dropStartMs = -1;
            _lastBlinkMs = now;
            _nextBlinkGapMs = NextBlinkGap();
        }

        var transform = pose.Transform;
        // -Z is forward in this engine's convention and in Godot's.
        var forward = -transform.Basis.Z.Normalized();
        if (!forward.IsFinite() || forward.LengthSquared() < 0.5f)
        {
            Clear(headDevice);
            return;
        }

        var origin = transform.Origin;
        var target = origin + forward * DefaultConvergence;

        // Each eye aims at the same point from its own side of the head, which is what makes the two
        // converge. The offsets are along the gaze pose's own right axis, so they stay correct when
        // the head rolls.
        var right = transform.Basis.X.Normalized();
        var leftEye = origin - right * HalfIpd;
        var rightEye = origin + right * HalfIpd;

        var leftDir = (target - leftEye).Normalized();
        var rightDir = (target - rightEye).Normalized();

        // THE GAZE POSE IS IN PLAY SPACE AND THE FIELDS ARE HEAD-LOCAL.
        //
        // The tracker hands back a pose relative to the XR reference space, the same space the head
        // pose is in, while EyeGazeDriver treats these three directions as head-relative with -Z
        // forward: it takes a yaw and pitch off them and applies that on top of wherever the head is
        // already looking. Handing it play-space directions means turning your head turns the
        // avatar's eyes by the same amount again, so a person looking straight ahead while facing
        // ninety degrees left has their eyes pinned at the far corner. Rotating by the inverse head
        // rotation puts them in the frame the consumer is actually reading. -xlinka
        var headInverse = System.Numerics.Quaternion.Conjugate(headDevice.Rotation);
        var localForward = ToHeadLocal(forward, headInverse);

        // Live means the gaze is still turning relative to the head, not just that the runtime
        // stamped the pose as tracked.
        if (!_haveLocalForward)
        {
            _haveLocalForward = true;
            _lastLocalForward = localForward;
        }
        else if (1f - System.Numerics.Vector3.Dot(localForward, _lastLocalForward) > MovedDotGap)
        {
            _lastLocalForward = localForward;
            _localForwardMoved = true;
            _lastMoveMs = now;
        }
        if (!_localForwardMoved || now - _lastMoveMs > StaleAfterMs)
        {
            Clear(headDevice);
            if (!_staleLogged)
            {
                _staleLogged = true;
                LumoraLogger.Log("OpenXREyeGaze: the runtime reports a gaze that never moves; the procedural gaze stays in charge");
            }
            return;
        }

        headDevice.HasEyeTracking = true;
        headDevice.CombinedEyeGazeDirection = localForward;
        headDevice.LeftEyeGazeDirection = ToHeadLocal(leftDir, headInverse);
        headDevice.RightEyeGazeDirection = ToHeadLocal(rightDir, headInverse);

        // Widen, squeeze, frown and pupil diameter are NOT in this extension and are left as they were.
        // Openness is not either, but leaving it at 1 meant no blinks at all (see the blink notes above).
        UpdateBlink(now, dt);
        WriteOpenness(headDevice);

        if (!_announced)
        {
            _announced = true;
            LumoraLogger.Log("OpenXREyeGaze: eye tracking live");
        }
    }

    // Out of VR nothing calls Update, so a gaze written on the last VR frame would sit on the device
    // for the whole flat session and hold the avatar's eyes on it. Called every flat frame by the VR
    // driver; the vendor path owns the fields while it is live and is left alone. -xlinka
    public static void Release(HeadDevice headDevice)
    {
        if (headDevice == null || SRanipalDriver.EyesLive || SteamLinkFaceDriver.EyesLive)
            return;
        Clear(headDevice);
        _haveLocalForward = false;
        _localForwardMoved = false;
    }

    private static void Clear(HeadDevice headDevice)
    {
        _dropStartMs = -1;
        _syntheticElapsed = -1f;
        if (!headDevice.HasEyeTracking)
            return;
        headDevice.HasEyeTracking = false;
        headDevice.CombinedEyeGazeDirection = System.Numerics.Vector3.Zero;
        headDevice.LeftEyeGazeDirection = System.Numerics.Vector3.Zero;
        headDevice.RightEyeGazeDirection = System.Numerics.Vector3.Zero;
        // Back to open, so the procedural blink takes over from open lids rather than from a half blink.
        _openness = 1f;
        WriteOpenness(headDevice);
        _announced = false;
    }

    // Lids reopen after a dropped-out blink ends, and a blink is played when the gaze has gone a natural
    // interval without one.
    private static void UpdateBlink(long now, float dt)
    {
        if (_syntheticElapsed >= 0f)
        {
            _syntheticElapsed += dt;
            float t = _syntheticElapsed;
            if (t < CloseSeconds)
                _openness = 1f - t / CloseSeconds;
            else if (t < CloseSeconds + SyntheticHoldSeconds)
                _openness = 0f;
            else if (t < CloseSeconds + SyntheticHoldSeconds + OpenSeconds)
                _openness = (t - CloseSeconds - SyntheticHoldSeconds) / OpenSeconds;
            else
            {
                _openness = 1f;
                _syntheticElapsed = -1f;
            }
            return;
        }

        if (_openness < 1f)
        {
            _openness = System.Math.Min(1f, _openness + dt / OpenSeconds);
            return;
        }

        if (_lastBlinkMs == 0)
            _lastBlinkMs = now;
        if (now - _lastBlinkMs >= _nextBlinkGapMs)
        {
            _syntheticElapsed = 0f;
            _lastBlinkMs = now;
            _nextBlinkGapMs = NextBlinkGap();
        }
    }

    // People blink about every 2.5 to 6 seconds, not on a clock.
    private static long NextBlinkGap() => 2500 + _blinkRandom.Next(3500);

    private static void WriteOpenness(HeadDevice headDevice)
    {
        headDevice.LeftEyeOpenness = _openness;
        headDevice.RightEyeOpenness = _openness;
    }

    private static bool Resolve()
    {
        if (!_checked)
        {
            try
            {
                // The answer is only valid once OpenXR is up, and this runs from the first VR frame
                // onward - which can be before the session exists. Asking early and LATCHING the "no"
                // would turn a supported headset into an unsupported one for the rest of the run, so
                // the question is only settled once there is a runtime to answer it. -xlinka
                if (XRServer.FindInterface("OpenXR") is OpenXRInterface openxr && openxr.IsInitialized())
                {
                    _checked = true;
                    _supported = openxr.IsEyeGazeInteractionSupported();
                }
                else
                {
                    return false;
                }
            }
            catch (System.Exception ex)
            {
                LumoraLogger.Warn($"OpenXREyeGaze: could not ask about eye gaze support: {ex.Message}");
                _checked = true;
                _supported = false;
            }

            LumoraLogger.Log(_supported
                ? "OpenXREyeGaze: the runtime reports eye gaze support"
                : "OpenXREyeGaze: this runtime has no eye gaze; the procedural gaze stays in charge");
        }

        if (!_supported)
            return false;

        if (_tracker != null && GodotObject.IsInstanceValid(_tracker))
            return true;

        // The tracker appears when the runtime binds the interaction profile, which can be several
        // frames after the session starts, so this keeps looking rather than giving up once.
        _tracker = XRServer.GetTracker(TrackerName) as XRPositionalTracker;
        if (_tracker == null && !_warned)
        {
            _warned = true;
            LumoraLogger.Warn(
                $"OpenXREyeGaze: the runtime supports eye gaze but no '{TrackerName}' tracker has appeared yet. " +
                $"The action map needs a pose action named '{ActionName}' bound to {TrackerName}/input/gaze_ext/pose.");
        }
        return _tracker != null;
    }

    // HeadDevice carries these as System.Numerics vectors, like the rest of its pose surface.
    private static System.Numerics.Vector3 ToEngine(Vector3 v)
        => new System.Numerics.Vector3(v.X, v.Y, v.Z);

    private static System.Numerics.Vector3 ToHeadLocal(Vector3 v, System.Numerics.Quaternion headInverse)
    {
        var world = ToEngine(v);
        // An untracked head leaves the rotation at identity, and rotating by identity is a no-op, so
        // the direction passes through unchanged rather than becoming zero.
        var local = System.Numerics.Vector3.Transform(world, headInverse);
        return local.LengthSquared() > 1e-8f ? System.Numerics.Vector3.Normalize(local) : world;
    }
}
