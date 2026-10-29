// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Components;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Photos;

// Whether the streaming program on this machine is recording or streaming, for the mark over this user's
// name. Every few seconds, on a pool thread and never on a frame: one look at the running processes, then
// while the program is open the banners in its own log and whatever the link to it says. The link is this
// machine's one link to that program, shared with the camera; when the program turns up it is tried once,
// quietly, with the address and password the camera keeps, and never again until the program closes or
// either of those changes. Open and idle is nothing.
//
// Self-reported like the live mark: this machine puts it on its own user record and nobody else can, but
// a modified client can always leave it off. -xlinka
public static class CaptureWatch
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(4);

    private static readonly object Gate = new();
    private static readonly CaptureScan Scan = new();
    private static Timer? _timer;
    private static Action<Action>? _post;
    private static int _polling;
    private static volatile CaptureState _detected;
    private static volatile bool _connecting;
    private static int _tried;
    private static long _lastPollTicks;

    public static event Action? Changed;

    // This machine's link to the streaming program. The camera works it; this only reads it, and opens it when
    // the program turns up.
    public static ObsLink Obs { get; } = new();

    public static ObsLogTail ObsLog { get; } = new();

    // What looks at the running processes. Null where nothing can (phones and standalone headsets).
    public static ICaptureProbe? Probe { get; set; } = DefaultProbe();

    // What goes on this user's record: nothing while the setting is off.
    public static CaptureState State => EngineSettings.ShowCapture ? _detected : CaptureState.None;

    public static CaptureState Detected => _detected;

    public static TimeSpan LastPollCost => TimeSpan.FromTicks(Interlocked.Read(ref _lastPollTicks));

    // post runs work on the engine update; Changed is raised there.
    public static void Start(Action<Action> post)
    {
        lock (Gate)
        {
            _post = post;
            if (_timer != null || Probe == null)
                return;
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), Interval);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _timer = null;
            _post = null;
        }
        Obs.Dispose();
    }

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
            return;
        try
        {
            var before = _detected;
            if (Poll() != before && _post is { } post)
                post(RaiseChanged);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"CaptureWatch: poll failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static void RaiseChanged()
    {
        LumoraLogger.Log($"CaptureWatch: {_detected}");
        Changed?.Invoke();
    }

    // One look, on the caller's thread.
    public static CaptureState Poll()
    {
        lock (Gate)
        {
            long started = Stopwatch.GetTimestamp();
            Scan.Reset();
            var state = CaptureState.None;
            if (EngineSettings.ShowCapture)
            {
                try
                {
                    Probe?.Scan(Scan);
                }
                catch (Exception)
                {
                    // a probe that cannot look this time looks again next time
                }
                state = Scan.Strongest;

                if (Scan.ObsOpen)
                {
                    try
                    {
                        state = Max(state, ObsLog.Read());
                    }
                    catch (Exception)
                    {
                        ObsLog.Forget();
                    }
                    ConnectQuietly();
                }
                else
                {
                    ObsLog.Forget();
                    _tried = 0;
                }

                // The link speaks for its program even on another machine: that is still this user's output.
                if (Obs.IsConnected)
                {
                    if (Obs.Streaming)
                        state = CaptureState.Streaming;
                    else if (Obs.Recording)
                        state = Max(state, CaptureState.Recording);
                }
            }
            _detected = state;
            Interlocked.Exchange(ref _lastPollTicks, Stopwatch.GetElapsedTime(started).Ticks);
            return state;
        }
    }

    private static CaptureState Max(CaptureState a, CaptureState b) => a > b ? a : b;

    private static void ConnectQuietly()
    {
        if (Obs.IsConnected || _connecting)
            return;
        var address = Settings.ReadValue(CameraDirector.ObsAddressKey, CameraDirector.DefaultObsAddress) ?? CameraDirector.DefaultObsAddress;
        var password = Settings.ReadValue(CameraDirector.ObsPasswordKey, string.Empty) ?? string.Empty;
        int attempt = HashCode.Combine(address, password) | 1;
        if (attempt == _tried)
            return;
        _tried = attempt;
        if (!CameraDirector.TryAddress(address, out var host, out int port))
            return;
        _connecting = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Obs.ConnectAsync(host, port, password.Length == 0 ? null : password).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            finally
            {
                _connecting = false;
            }
        });
    }

    private static ICaptureProbe? DefaultProbe()
    {
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || OperatingSystem.IsBrowser())
            return null;
        if (OperatingSystem.IsWindows())
        {
            if (WinePlatform.IsWine && !WinePlatform.IsSimulated)
                return new BothProbes(new WindowsProcessProbe(), new ProcProbe(@"Z:\proc"));
            return new WindowsProcessProbe();
        }
        if (OperatingSystem.IsLinux())
            return new ProcProbe("/proc");
        return null;
    }
}
