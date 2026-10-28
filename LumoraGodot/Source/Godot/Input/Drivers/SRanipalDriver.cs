// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading;
using Lumora.Core.Input;
using Lumora.Source.Godot.Bootstrap;
using LumoraLogger = Lumora.Core.Logging.Logger;
using SR = Lumora.Source.Godot.Input.Drivers.SRanipalNative;

namespace Lumora.Source.Godot.Input.Drivers;

// Eye and lip tracking from HTC's SRanipal runtime: the Vive Pro Eye's eye cameras and the Vive
// Facial Tracker's mouth camera.
//
// OpenXREyeGaze is the floor - one combined gaze ray, any runtime, nothing to install. This is the
// ceiling on the hardware that has it: a direction PER EYE measured rather than inferred from one
// ray, eyelid openness, pupil diameter, the widen/squeeze/frown expression channels, and the mouth.
// It writes the same HeadDevice and MouthDevice fields, so everything downstream - the eye and mouth
// streams, the gaze driver, the blink driver, the expression drivers - reads it with no idea which
// source filled them, and this one runs at UpdateOrder 100 so it lands after the gaze floor and wins
// where both are live.
//
// THE MOUTH IS THE PART NOTHING ELSE COULD FILL. MouthDevice has been sitting at fourteen zeroes
// since it was written, with a stream manager and an expression driver behind it, because no hardware
// hook existed to feed it. The facial tracker is that hook. -xlinka
public sealed class SRanipalDriver : IInputDriver
{
    // After the VR driver, which owns the head pose and the OpenXR gaze floor this replaces.
    public int UpdateOrder => 100;

    // Whether real per-eye hardware is feeding HeadDevice right now. OpenXREyeGaze reads this and
    // stands down, so the two never write the same fields in one frame.
    public static bool EyesLive { get; private set; }

    private const string ForceFlag = "--sranipal";
    private const string DisableFlag = "--no-sranipal";

    // A human pupil runs about 2 mm in bright light to 8 mm fully dark-adapted. PupilDilation is a
    // 0..1 channel, so the measured millimetres are mapped across that span.
    private const float MinPupilMm = 2.0f;
    private const float MaxPupilMm = 8.0f;

    // Sanity bounds on the IPD derived from the two gaze origins. Outside these the eye cameras have
    // lost one eye and the number is noise, not a measurement.
    private const float MinIpd = 0.045f;
    private const float MaxIpd = 0.085f;

    private InputInterface _input = null!;
    private Thread? _worker;
    private readonly AutoResetEvent _pollSignal = new(false);
    // The waits before polling starts sleep on this, not on _pollSignal, which the frame thread sets
    // every frame once either module is up.
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private readonly object _dataLock = new();

    // Serialises the native calls themselves. The worker polls while the frame thread can be
    // releasing a module out from under it on shutdown, and SRanipal does not promise to survive
    // that. Separate from _dataLock, which the frame thread takes every frame and must not sit
    // behind a native call for.
    private readonly object _apiLock = new();

    private volatile bool _eyeReady;
    private volatile bool _lipReady;
    private volatile bool _stopping;

    private SR.EyeDataV2 _eyeScratch;
    private SR.LipDataV2 _lipScratch;
    private SR.EyeDataV2 _eyeLatest;
    private SR.LipDataV2 _lipLatest;
    private SR.SRError _eyeResult = SR.SRError.NotInitial;
    private SR.SRError _lipResult = SR.SRError.NotInitial;

    private bool _eyeAnnounced;
    private bool _lipAnnounced;
    private bool _calibrationChecked;

    // A service that answers Work with a sample that never changes is not tracking anyone. The eye
    // module comes up on a machine whose headset has no eye cameras, or with eye tracking switched
    // off in the vendor console, and hands back the same frame with its validity bits set for as
    // long as it runs. Taking that as live froze the avatar's eyes dead ahead, in VR and on the
    // desktop alike, whenever the runtime happened to be running. Real eyes never hold a bit-identical
    // direction, openness and pupil for this long, so a sample that has not moved inside the window
    // means the vendor path stands down and the procedural gaze keeps the avatar alive. -xlinka
    private const long StaleAfterMs = 750;
    private SR.EyeDataV2 _lastEyeSample;
    private bool _haveEyeSample;
    private bool _eyeSampleMoved;
    private long _lastEyeMoveMs;
    private bool _eyeStaleLogged;

    // WHETHER TO RUN AT ALL
    //
    // Deliberately NOT gated on being in VR. Registration happens once at startup and there is no
    // second chance: gating on the headset being up at that instant means anyone who boots flat and
    // puts a headset on afterwards never gets eye tracking for the rest of the session. The hardware
    // check is the honest question.
    //
    // Nor on the active OpenXR runtime, which used to veto it whenever a streaming runtime was the
    // active one. That is one machine-wide registry value naming the runtime a headset session would go
    // through. It says nothing about what is plugged into the PC, and a flat launch goes through no
    // runtime at all, so a facial tracker on such a machine never got as far as being asked: every
    // launch logged "skipped" and the face never moved. -xlinka
    public static bool ShouldRegister()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (LaunchArgs.HasFlag(DisableFlag))
        {
            LumoraLogger.Log($"SRanipal: disabled by {DisableFlag}.");
            return false;
        }

        if (LaunchArgs.HasFlag(ForceFlag))
        {
            LumoraLogger.Log($"SRanipal: forced on by {ForceFlag}.");
            _mayStartRuntime = true;
            return true;
        }

        // Wine says Windows to every question, and SRanipal is not an API, it is a Windows SERVICE
        // talking to USB cameras on a Windows MACHINE. Under Proton there is nothing on the other end
        // of the pipe, so the whole probe is seconds spent on a certain no. -xlinka
        if (Lumora.Core.WinePlatform.IsWine)
        {
            LumoraLogger.Log($"SRanipal: skipped, running under {Lumora.Core.WinePlatform.Describe()} " +
                             "and the SRanipal service lives outside the prefix.");
            return false;
        }

        var runtimeExe = SR.FindRuntimeExecutable();
        if (runtimeExe != null)
        {
            _mayStartRuntime = SR.IsVendorDevicePresent();
            LumoraLogger.Log(_mayStartRuntime
                ? $"SRanipal: runtime found at '{runtimeExe}', and tracking hardware is plugged in."
                : $"SRanipal: runtime found at '{runtimeExe}'. No tracking hardware is plugged in, so it is used " +
                  $"once the SRanipal runtime is running (start it, or pass {ForceFlag} to start it from here).");
            return true;
        }

        if (SR.SafeIsViveProEye())
        {
            LumoraLogger.Log("SRanipal: no runtime install found, but the headset reports itself as a Vive Pro Eye.");
            _mayStartRuntime = true;
            return true;
        }

        return false;
    }

    // WHO STARTS THE RUNTIME
    //
    // Initial() starts sr_runtime.exe itself when it is not running, and the runtime's manifest asks
    // for elevation. On a machine with nothing plugged in that is an admin prompt nobody asked for, then
    // minutes inside Initial waiting on a runtime that never comes up: measured on a machine with the
    // runtime installed and not running, three minutes and then InitialFailed, for the eyes alone, with
    // the lips queued behind them. So Initial is only called while the runtime is running, with one
    // exception: a machine with the hardware plugged in, or the force flag, may start it, once.
    // Otherwise the worker waits for the runtime and attaches the moment someone starts it. -xlinka
    private static bool _mayStartRuntime;
    private bool _startSpent;
    private bool _waitLogged;
    private const int RuntimePollMs = 2000;
    private const int RuntimeSettleMs = 3000;
    private const int RetryDelayMs = 2000;

    public void RegisterInputs(InputInterface inputInterface)
    {
        _input = inputInterface;

        if (!SR.IsLibraryUsable())
        {
            LumoraLogger.Warn("SRanipal: the runtime is installed but SRanipal.dll could not be loaded; " +
                              "eye and lip tracking stay off.");
            return;
        }

        // Initial() talks to the SRanipal service and can sit there for seconds when the service is
        // starting or a camera is enumerating. It does not go on the frame thread.
        _worker = new Thread(Worker)
        {
            Name = "SRanipal",
            Priority = ThreadPriority.BelowNormal,
            IsBackground = true,
        };
        _worker.Start();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    public void Shutdown()
    {
        if (_stopping)
            return;
        _stopping = true;
        _stopSignal.Set();
        _pollSignal.Set();

        try
        {
            lock (_apiLock)
            {
                if (_eyeReady)
                    SR.Release(SR.AnipalTypeEye);
                if (_lipReady)
                    SR.Release(SR.AnipalTypeLip);
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SRanipal: release failed on shutdown ({ex.Message}).");
        }

        _eyeReady = false;
        _lipReady = false;
        EyesLive = false;
    }

    // THE WORKER
    //
    // Brings both modules up, then polls exactly once per frame: the frame thread signals after it
    // has taken the last sample, this wakes, fetches, and goes back to sleep. No busy loop burning a
    // core, and no sample older than one frame.
    private void Worker()
    {
        InitializeModules();
        if (_stopping)
            return;

        if (!_eyeReady && !_lipReady)
        {
            LumoraLogger.Log("SRanipal: neither eye nor lip tracking came up; the driver is idle.");
            return;
        }

        // Prime, so the first frame that asks has something.
        _pollSignal.Set();

        while (!_stopping)
        {
            _pollSignal.WaitOne();
            if (_stopping)
                break;

            if (_eyeReady)
            {
                SR.SRError result;
                try { lock (_apiLock) result = SR.GetEyeDataV2(ref _eyeScratch); }
                catch (Exception ex) { LumoraLogger.Warn($"SRanipal: eye poll threw ({ex.Message})."); break; }

                lock (_dataLock)
                {
                    _eyeResult = result;
                    if (result == SR.SRError.Work)
                        _eyeLatest = _eyeScratch;
                }
            }

            if (_lipReady)
            {
                SR.SRError result;
                try { lock (_apiLock) result = SR.GetLipDataV2(ref _lipScratch); }
                catch (Exception ex) { LumoraLogger.Warn($"SRanipal: lip poll threw ({ex.Message})."); break; }

                lock (_dataLock)
                {
                    _lipResult = result;
                    if (result == SR.SRError.Work)
                        _lipLatest = _lipScratch;
                }
            }
        }

        // Out of the loop means stopping, or a native call that threw. Either way nothing is feeding
        // these any more, so the frame thread must stop reading a snapshot that will never change and
        // signalling a thread that is gone.
        _eyeReady = false;
        _lipReady = false;
        EyesLive = false;
    }

    // The service can be up before the camera is, so a first failure is worth waiting out. Bounded,
    // because a machine that simply has no facial tracker must not retry forever.
    //
    // The two modules take turns rather than one after the other. The eyes used to run all their
    // attempts first, and a facial tracker on a headset without eye cameras waited behind every one of
    // them before it was asked at all. -xlinka
    private void InitializeModules()
    {
        const int MaxAttempts = 6;
        bool eyeSettled = false;
        bool lipSettled = false;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            bool last = attempt == MaxAttempts;
            if (!eyeSettled)
            {
                if (!AwaitRuntime())
                    return;
                eyeSettled = TryInitialize(SR.AnipalTypeEye, attempt, last);
            }
            if (!lipSettled)
            {
                if (!AwaitRuntime())
                    return;
                lipSettled = TryInitialize(SR.AnipalTypeLip, attempt, last);
            }
            if ((eyeSettled && lipSettled) || last)
                return;
            if (_stopSignal.Wait(RetryDelayMs))
                return;
        }
    }

    // False when shutting down.
    private bool AwaitRuntime()
    {
        if (_stopping)
            return false;
        if (SR.IsRuntimeRunning())
            return true;
        if (_mayStartRuntime && !_startSpent)
        {
            _startSpent = true;
            LumoraLogger.Log("SRanipal: the runtime is not running; starting it (it may ask for administrator rights).");
            return true;
        }

        if (!_waitLogged)
        {
            _waitLogged = true;
            LumoraLogger.Log("SRanipal: waiting for the SRanipal runtime to be started; eye and lip tracking attach when it is.");
        }
        while (!_stopSignal.Wait(RuntimePollMs))
        {
            if (SR.IsRuntimeRunning())
            {
                // A process that has just appeared is still bringing its cameras and ports up, and an
                // answer it gives in that moment can settle a module for the whole session.
                LumoraLogger.Log("SRanipal: the runtime is running.");
                return !_stopSignal.Wait(RuntimeSettleMs);
            }
            // Hardware plugged in after launch gets the same one start it would have had at launch.
            if (!_startSpent && SR.IsVendorDevicePresent())
            {
                _startSpent = true;
                LumoraLogger.Log("SRanipal: tracking hardware was plugged in; starting the runtime (it may ask for administrator rights).");
                return true;
            }
        }
        return false;
    }

    // True when this module is settled: up, or answered in a way another attempt will not change.
    private bool TryInitialize(int anipalType, int attempt, bool last)
    {
        bool isLip = anipalType == SR.AnipalTypeLip;
        string label = isLip ? "lip" : "eye";

        SR.SRError result;
        try
        {
            lock (_apiLock)
                result = SR.Initial(anipalType, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SRanipal: {label} init threw ({ex.Message}).");
            return true;
        }

        if (result == SR.SRError.Work)
        {
            if (isLip) _lipReady = true; else _eyeReady = true;
            LumoraLogger.Log($"SRanipal: {label} tracking initialized.");
            return true;
        }

        // Nothing plugged in. Not an error worth shouting about and not worth retrying at all for
        // the facial tracker, which most people do not own.
        if (result is SR.SRError.UnknownModule or SR.SRError.DeviceNotFound or SR.SRError.ServiceNotFound
                   or SR.SRError.DisabledByUser or SR.SRError.NotSupportEyeTracking)
        {
            LumoraLogger.Log($"SRanipal: no {label} tracking device ({result}).");
            return true;
        }

        if (result is SR.SRError.RuntimeNotFound or SR.SRError.NotImplemented)
        {
            LumoraLogger.Log($"SRanipal: {label} tracking unavailable ({result}).");
            return true;
        }

        if (last)
            LumoraLogger.Warn($"SRanipal: {label} tracking gave up after {attempt} attempts (last: {result}).");
        return last;
    }

    public void UpdateInputs(float deltaTime)
    {
        if (!_eyeReady && !_lipReady)
            return;

        SR.EyeDataV2 eye = default;
        SR.LipDataV2 lip = default;
        SR.SRError eyeResult, lipResult;

        lock (_dataLock)
        {
            eyeResult = _eyeResult;
            lipResult = _lipResult;
            if (_eyeReady) eye = _eyeLatest;
            if (_lipReady) lip = _lipLatest;
        }

        if (_eyeReady)
            ApplyEyes(in eye, eyeResult);
        if (_lipReady)
            ApplyLips(in lip, lipResult);

        // Ask for the next sample now that this one is consumed.
        _pollSignal.Set();
    }

    private void ApplyEyes(in SR.EyeDataV2 data, SR.SRError result)
    {
        var head = _input?.HeadDevice;
        if (head == null)
            return;

        // NoUser means the headset is off a face. Not a tracking failure, so the fields are simply
        // left where they are and the procedural gaze takes the avatar back over.
        if (result != SR.SRError.Work || data.NoUser != 0)
        {
            DropEyes(head);
            return;
        }

        ref readonly var verbose = ref data.Verbose;
        bool leftOk = IsUsable(in verbose.Left);
        bool rightOk = IsUsable(in verbose.Right);
        bool combinedOk = IsUsable(in verbose.Combined.EyeData);

        if (!combinedOk && !leftOk && !rightOk)
        {
            DropEyes(head);
            return;
        }

        // Live means the sample is still changing, not just that the service answered.
        long now = Environment.TickCount64;
        if (!_haveEyeSample)
        {
            _haveEyeSample = true;
            _lastEyeSample = data;
        }
        else if (!SameEyeSample(in data, in _lastEyeSample))
        {
            _lastEyeSample = data;
            _eyeSampleMoved = true;
            _lastEyeMoveMs = now;
        }
        if (!_eyeSampleMoved || now - _lastEyeMoveMs > StaleAfterMs)
        {
            DropEyes(head);
            if (!_eyeStaleLogged)
            {
                _eyeStaleLogged = true;
                LumoraLogger.Log("SRanipal: the eye module is up but its data never changes (no eye tracker is feeding it); the procedural gaze stays in charge.");
            }
            return;
        }

        if (combinedOk)
            head.CombinedEyeGazeDirection = ToHeadLocal(verbose.Combined.EyeData.GazeDirectionNormalized);
        if (leftOk)
            head.LeftEyeGazeDirection = ToHeadLocal(verbose.Left.GazeDirectionNormalized);
        if (rightOk)
            head.RightEyeGazeDirection = ToHeadLocal(verbose.Right.GazeDirectionNormalized);

        // A combined ray is only missing when one eye is lost; average what is left rather than
        // leaving the avatar's shared gaze on a stale frame.
        if (!combinedOk && leftOk && rightOk)
        {
            var mid = head.LeftEyeGazeDirection + head.RightEyeGazeDirection;
            if (mid.LengthSquared() > 1e-6f)
                head.CombinedEyeGazeDirection = System.Numerics.Vector3.Normalize(mid);
        }

        if (verbose.Left.IsValid(SR.EyeValidity.EyeOpenness))
            head.LeftEyeOpenness = Clamp01(verbose.Left.EyeOpenness);
        if (verbose.Right.IsValid(SR.EyeValidity.EyeOpenness))
            head.RightEyeOpenness = Clamp01(verbose.Right.EyeOpenness);

        ApplyPupil(head, in verbose);
        ApplyIpd(head, in verbose, leftOk, rightOk);

        ref readonly var expr = ref data.Expression;
        head.LeftEyeWiden = Clamp01(expr.Left.EyeWide);
        head.RightEyeWiden = Clamp01(expr.Right.EyeWide);
        head.LeftEyeSqueeze = Clamp01(expr.Left.EyeSqueeze);
        head.RightEyeSqueeze = Clamp01(expr.Right.EyeSqueeze);
        head.LeftEyeFrown = Clamp01(expr.Left.EyeFrown);
        head.RightEyeFrown = Clamp01(expr.Right.EyeFrown);

        head.HasEyeTracking = true;
        EyesLive = true;

        if (!_eyeAnnounced)
        {
            _eyeAnnounced = true;
            LumoraLogger.Log("SRanipal: eye tracking live (per-eye direction, openness, pupil, expression).");
            CheckCalibration();
        }
    }

    // Pupil diameter is the one channel that is genuinely a measurement rather than a blend weight,
    // and the expression driver falls back to a procedural dilation without it. Both eyes when both
    // are reporting, since they dilate together.
    private static void ApplyPupil(HeadDevice head, in SR.VerboseData verbose)
    {
        float sum = 0f;
        int count = 0;
        if (verbose.Left.IsValid(SR.EyeValidity.PupilDiameter) && verbose.Left.PupilDiameterMm > 0f)
        {
            sum += verbose.Left.PupilDiameterMm;
            count++;
        }
        if (verbose.Right.IsValid(SR.EyeValidity.PupilDiameter) && verbose.Right.PupilDiameterMm > 0f)
        {
            sum += verbose.Right.PupilDiameterMm;
            count++;
        }
        if (count == 0)
            return;

        float mm = sum / count;
        head.PupilDilation = Clamp01((mm - MinPupilMm) / (MaxPupilMm - MinPupilMm));
    }

    // The two gaze origins are the eyeballs themselves, so the distance between them is the wearer's
    // real IPD. Nothing else in the engine can measure it; the headset's own reported value is the
    // lens separation the person dialled in, which is a different number.
    private static void ApplyIpd(HeadDevice head, in SR.VerboseData verbose, bool leftOk, bool rightOk)
    {
        if (!leftOk || !rightOk)
            return;
        if (!verbose.Left.IsValid(SR.EyeValidity.GazeOrigin) || !verbose.Right.IsValid(SR.EyeValidity.GazeOrigin))
            return;

        var left = verbose.Left.GazeOriginMm;
        var right = verbose.Right.GazeOriginMm;
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        float dz = left.Z - right.Z;
        float metres = MathF.Sqrt(dx * dx + dy * dy + dz * dz) * 0.001f;

        if (metres >= MinIpd && metres <= MaxIpd)
            head.IPD = metres;
    }

    private void CheckCalibration()
    {
        if (_calibrationChecked)
            return;
        _calibrationChecked = true;

        try
        {
            byte need = 0;
            SR.IsUserNeedCalibration(ref need);
            if (need != 0)
                LumoraLogger.Warn("SRanipal: this user has not calibrated their eyes. Gaze will be off until " +
                                  "the Vive eye calibration is run from the SteamVR dashboard.");
        }
        catch
        {
            // Older runtimes do not export it. Nothing depends on the answer.
        }
    }

    private static void DropEyes(HeadDevice head)
    {
        if (!EyesLive)
            return;
        EyesLive = false;
        head.HasEyeTracking = false;
    }

    // Exact comparison on purpose: a repeated or synthesized frame is bit-identical, a measured one
    // never is.
    private static bool SameEyeSample(in SR.EyeDataV2 a, in SR.EyeDataV2 b)
        => SameEye(in a.Verbose.Combined.EyeData, in b.Verbose.Combined.EyeData)
        && SameEye(in a.Verbose.Left, in b.Verbose.Left)
        && SameEye(in a.Verbose.Right, in b.Verbose.Right);

    private static bool SameEye(in SR.SingleEyeData a, in SR.SingleEyeData b)
        => a.GazeDirectionNormalized.X == b.GazeDirectionNormalized.X
        && a.GazeDirectionNormalized.Y == b.GazeDirectionNormalized.Y
        && a.GazeDirectionNormalized.Z == b.GazeDirectionNormalized.Z
        && a.EyeOpenness == b.EyeOpenness
        && a.PupilDiameterMm == b.PupilDiameterMm;

    private static bool IsUsable(in SR.SingleEyeData eye)
    {
        if (!eye.IsValid(SR.EyeValidity.GazeDirection))
            return false;
        var d = eye.GazeDirectionNormalized;
        if (!float.IsFinite(d.X) || !float.IsFinite(d.Y) || !float.IsFinite(d.Z))
            return false;
        return d.X * d.X + d.Y * d.Y + d.Z * d.Z > 0.25f;
    }

    // SRanipal reports in a right-handed frame with +X LEFT, +Y up and +Z FORWARD, relative to the
    // headset. Godot is also right-handed but with +X right and -Z forward, so the conversion is a
    // half turn about up: negate X and Z. Negating X alone is the conversion into a left-handed space
    // with +Z forward, and here it would put every gaze a mirror image out. These come out head-local
    // already, which is exactly what EyeGazeDriver wants. -xlinka
    private static System.Numerics.Vector3 ToHeadLocal(in SR.SRVector3 v)
        => new(-v.X, v.Y, -v.Z);

    private static float Clamp01(float value)
    {
        if (float.IsNaN(value))
            return 0f;
        return value < 0f ? 0f : value > 1f ? 1f : value;
    }

    // THE MOUTH
    //
    // SRanipal's 37 lip shapes onto the device-agnostic ones MouthDevice carries. The first fourteen
    // average the left/right pairs they have one channel for; the rest carry each pair side by side and
    // the shapes those fourteen had no channel for. The ape shape (jaw open behind closed lips) has no
    // counterpart and is left alone rather than approximated out of something that means something else.
    private void ApplyLips(in SR.LipDataV2 data, SR.SRError result)
    {
        var mouth = _input?.MouthDevice;
        if (mouth == null)
            return;

        if (result != SR.SRError.Work)
        {
            if (mouth.IsTracking)
            {
                mouth.IsTracking = false;
                mouth.ClearWeights();
            }
            return;
        }

        var prediction = data.Prediction;
        ReadOnlySpan<float> w = prediction.BlendShapeWeight;

        mouth.SetWeight(MouthShape.JawOpen, Clamp01(w[SR.LipJawOpen]));
        mouth.SetWeight(MouthShape.JawLeft, Clamp01(w[SR.LipJawLeft]));
        mouth.SetWeight(MouthShape.JawRight, Clamp01(w[SR.LipJawRight]));
        mouth.SetWeight(MouthShape.JawForward, Clamp01(w[SR.LipJawForward]));
        mouth.SetWeight(MouthShape.MouthPucker, Clamp01(w[SR.LipMouthPout]));
        mouth.SetWeight(MouthShape.SmileLeft, Clamp01(w[SR.LipMouthSmileLeft]));
        mouth.SetWeight(MouthShape.SmileRight, Clamp01(w[SR.LipMouthSmileRight]));
        mouth.SetWeight(MouthShape.FrownLeft, Clamp01(w[SR.LipMouthSadLeft]));
        mouth.SetWeight(MouthShape.FrownRight, Clamp01(w[SR.LipMouthSadRight]));
        mouth.SetWeight(MouthShape.UpperUp, Clamp01((w[SR.LipMouthUpperUpLeft] + w[SR.LipMouthUpperUpRight]) * 0.5f));
        mouth.SetWeight(MouthShape.LowerDown, Clamp01((w[SR.LipMouthLowerDownLeft] + w[SR.LipMouthLowerDownRight]) * 0.5f));
        mouth.SetWeight(MouthShape.CheekPuff, Clamp01((w[SR.LipCheekPuffLeft] + w[SR.LipCheekPuffRight]) * 0.5f));
        mouth.SetWeight(MouthShape.TongueOut, Clamp01(w[SR.LipTongueLongStep1]));

        // The same shapes again where the set above folds a pair, and the ones it had no channel for, so an avatar
        // that names a shape for each of them gets the tracker's own reading.
        mouth.SetWeight(MouthShape.UpperUpLeft, Clamp01(w[SR.LipMouthUpperUpLeft]));
        mouth.SetWeight(MouthShape.UpperUpRight, Clamp01(w[SR.LipMouthUpperUpRight]));
        mouth.SetWeight(MouthShape.LowerDownLeft, Clamp01(w[SR.LipMouthLowerDownLeft]));
        mouth.SetWeight(MouthShape.LowerDownRight, Clamp01(w[SR.LipMouthLowerDownRight]));
        mouth.SetWeight(MouthShape.CheekPuffLeft, Clamp01(w[SR.LipCheekPuffLeft]));
        mouth.SetWeight(MouthShape.CheekPuffRight, Clamp01(w[SR.LipCheekPuffRight]));
        mouth.SetWeight(MouthShape.CheekSuck, Clamp01(w[SR.LipCheekSuck]));
        mouth.SetWeight(MouthShape.UpperLipLeft, Clamp01(w[SR.LipMouthUpperLeft]));
        mouth.SetWeight(MouthShape.UpperLipRight, Clamp01(w[SR.LipMouthUpperRight]));
        mouth.SetWeight(MouthShape.LowerLipLeft, Clamp01(w[SR.LipMouthLowerLeft]));
        mouth.SetWeight(MouthShape.LowerLipRight, Clamp01(w[SR.LipMouthLowerRight]));
        mouth.SetWeight(MouthShape.UpperLipOverturn, Clamp01(w[SR.LipMouthUpperOverturn]));
        mouth.SetWeight(MouthShape.LowerLipOverturn, Clamp01(w[SR.LipMouthLowerOverturn]));
        mouth.SetWeight(MouthShape.UpperLipInside, Clamp01(w[SR.LipMouthUpperInside]));
        mouth.SetWeight(MouthShape.LowerLipInside, Clamp01(w[SR.LipMouthLowerInside]));
        mouth.SetWeight(MouthShape.LowerLipOverlay, Clamp01(w[SR.LipMouthLowerOverlay]));
        mouth.SetWeight(MouthShape.TongueExtend, Clamp01(w[SR.LipTongueLongStep2]));
        mouth.SetWeight(MouthShape.TongueUp, Clamp01(w[SR.LipTongueUp]));
        mouth.SetWeight(MouthShape.TongueDown, Clamp01(w[SR.LipTongueDown]));
        mouth.SetWeight(MouthShape.TongueLeft, Clamp01(w[SR.LipTongueLeft]));
        mouth.SetWeight(MouthShape.TongueRight, Clamp01(w[SR.LipTongueRight]));
        mouth.SetWeight(MouthShape.TongueRoll, Clamp01(w[SR.LipTongueRoll]));
        mouth.SetWeight(MouthShape.TongueUpLeft, Clamp01(w[SR.LipTongueUpLeft]));
        mouth.SetWeight(MouthShape.TongueUpRight, Clamp01(w[SR.LipTongueUpRight]));
        mouth.SetWeight(MouthShape.TongueDownLeft, Clamp01(w[SR.LipTongueDownLeft]));
        mouth.SetWeight(MouthShape.TongueDownRight, Clamp01(w[SR.LipTongueDownRight]));

        // MouthWide is a lip-corner STRETCH, and SRanipal has no shape that means that. Filling it
        // from the smile pair would double-count a smile that is already going to SmileLeft and
        // SmileRight, so it stays at rest until a tracker that measures it shows up. A cheek suck has
        // its own channel; CheekPuff cannot go negative, so it reads as no puff there.

        mouth.IsTracking = true;
        mouth.IsWorn = true;

        if (!_lipAnnounced)
        {
            _lipAnnounced = true;
            LumoraLogger.Log("SRanipal: lip tracking live.");
        }
    }
}
