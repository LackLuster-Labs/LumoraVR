// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Godot;
using Lumora.Core.Input;
using Lumora.Source.Input;
using Lumora.Source.Godot.Input;
using Lumora.Source.Godot.Input.Drivers;
using Lumora.Source.Godot.Rendering;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

// Hot-swaps between Desktop and VR rendering/input modes at runtime (F8 in exported builds; on
// launch it auto-detects a connected headset via OpenXR and starts in VR if one is found). From
// the Godot Editor use Shift+F8 - the editor consumes bare F8/F9 as its own Stop/Pause shortcuts.
// The manager owns the desktop overlay nodes (cursor / camera controller) and switches input
// ownership when toggling; VR input flows through InputInterface drivers directly, no extra
// provider node needed. Rendering follows a split-camera pattern: the desktop camera never has XR
// enabled, the XR camera only drives VR, and mode changes are queued across frame boundaries so
// queued frees and renderer state settle before another switch can start.
// - xlinka
public partial class XRModeManager : Node
{
    // SINGLETON
    public static XRModeManager Instance { get; private set; } = null!;

    // true = VR is now active, false = Desktop is now active
    public static event Action<bool> ModeChanged = null!;

    // In VR: the XRCamera3D under XROrigin3D. In desktop: the regular mono Camera3D that
    // LumoraEngineRunner resolved from %DesktopCamera and that we keep as _mainCamera.
    public Camera3D CurrentCamera => IsVRActive ? _xrCamera : _mainCamera;

    // STATE
    public bool IsVRActive { get; private set; }

    private bool _initialized = false;
    private bool _switching   = false;
    private bool _toggleQueued = false;
    private bool? _queuedVRTarget;
    private ulong _lastSwitchMsec = 0;
    private bool _xrAvailable = false;
    private bool _spectatorMirrorLogShown = false;

    // Desktop VR mirror. The desktop camera follows the headset pose, which is a second render of the
    // world. An XR viewport's texture cannot be drawn straight into the window (it is a two-layer
    // array; the canvas shows black), so the second render stays, trimmed to what the window needs,
    // and the desktop settings come back the moment VR ends. -xlinka
    private bool _cheapMirrorApplied;
    private Viewport.Scaling3DModeEnum _savedScalingMode;
    private float _savedScalingScale;
    private Viewport.Msaa _savedMsaa;
    private Viewport.ScreenSpaceAAEnum _savedScreenSpaceAA;
    private bool _savedTaa;

    private const ulong SwitchCooldownMsec = 500;

    // REFERENCES (provided at Initialize)
    private Lumora.Core.Engine _engine = null!;
    private HeadOutput         _headOutput = null!;
    private InputInterface     _inputInterface = null!;
    private Camera3D           _mainCamera = null!;
    private GodotVRDriver      _vrDriver = null!;

    // MANAGED INPUT NODES
    // Desktop-only scene overlays; VR mode has no per-mode scene node since
    // controller/head poses flow through InputInterface drivers. - xlinka
    private DesktopInput             _desktopInput = null!;
    private DesktopCameraController  _desktopCamera = null!;

    // XR SCENE NODES
    // Bootstrap.tscn defines the XR sub-tree up-front; we just hold refs.
    private XROrigin3D _xrOrigin = null!;
    private XRCamera3D _xrCamera = null!;
    private Viewport   _xrViewport = null!;


    //  KEY DETECTION



    //  INITIALIZATION


    // call this once after the engine is fully ready
    public void Initialize(
        Lumora.Core.Engine engine,
        HeadOutput         headOutput,
        InputInterface     inputInterface,
        Camera3D           mainCamera,
        GodotVRDriver      vrDriver,
        bool               startingInVR,
        bool               allowAutoVr = true)
    {
        // --desktop is a decision, not a fallback: never drag someone into VR who asked to stay flat.
        _autoVrAllowed = allowAutoVr;
        _engine         = engine;
        _headOutput     = headOutput;
        _inputInterface = inputInterface;
        _mainCamera     = mainCamera;
        _vrDriver       = vrDriver;
        IsVRActive      = startingInVR;
        Instance        = this;
        _xrAvailable    = startingInVR;

        // Bind XR nodes from Bootstrap.tscn. They exist for the whole process
        // lifetime now - no more runtime creation. Looking up via CurrentScene
        // because XRModeManager is added at runtime and has no Owner, which
        // makes `GetNode("%X")` from `this` fail.
        var sceneRoot = GetTree()?.CurrentScene;
        _xrOrigin = sceneRoot?.GetNodeOrNull<XROrigin3D>("%XROrigin3D")!;
        _xrCamera = sceneRoot?.GetNodeOrNull<XRCamera3D>("%XRCamera3D")!;
        _xrViewport = XRViewportLayout.Ensure(sceneRoot ?? this, GetViewport());

        ApplyRenderingMode(startingInVR);
        _headOutput?.NotifyVRActiveChanged(startingInVR);
        _headOutput?.SwitchOutputType(startingInVR ? HeadOutput.OutputType.VR : HeadOutput.OutputType.Screen);

        // Desktop mode needs its scene overlay (cursor, raycast, camera controller).
        // VR has nothing extra to spin up here. - xlinka
        if (!startingInVR)
            SetupDesktopInput();

        _initialized = true;
        // Settings may have loaded before XR detection completed. Notify on the initial mode too,
        // so desktop frame caps and vsync are released on a VR boot, not only after an F8 switch.
        ModeChanged?.Invoke(startingInVR);

        var keyHint = OS.HasFeature("editor") ? "Shift+F8 (editor)" : "F8";
        LumoraLogger.Log($"XRModeManager: Initialized. Mode={( startingInVR ? "VR" : "Desktop" )}");
        LumoraLogger.Debug($"XRModeManager: Press {keyHint} at any time to toggle between Desktop and VR.");
        if (!IsStandalone)
            LumoraLogger.Debug("XRModeManager: Press F9 to re-detect a headset plugged in after launch "
                           + "(one way into VR, never out of it).");
    }


    //  INPUT HANDLING


    // _Input is kept ONLY as a diagnostic - if F8 ever reaches the event
    // pipeline we log it. The actual toggle is driven from _Process polling
    // below, which is robust against focused Controls eating the event.
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo &&
            (key.PhysicalKeycode == global::Godot.Key.F8 || key.Keycode == global::Godot.Key.F8))
        {
            LumoraLogger.Debug($"XRModeManager: F8 _Input event (shift={key.ShiftPressed}, ctrl={key.CtrlPressed}, alt={key.AltPressed})");
        }
    }

    // On standalone (Quest, Pico, Focus 3, Steam Frame, ...) there is no meaningful desktop mode - the headset
    // IS the screen - so F8 is disabled and gameplay code should avoid offering mode-swap UI here.
    public static bool IsStandalone => HeadsetDeviceMode.IsHeadset;

    // Edge-triggered polling. Consults Godot's polled key state every frame.
    // This survives any focused Control / GUI consumption that would have
    // swallowed the F8 event before _Input or _UnhandledInput saw it.
    // Disabled on standalone headsets - there's no keyboard and no desktop
    // mode to switch to.
    // - xlinka
    private bool _f8WasDown;
    private bool _f9WasDown;
    public override void _Process(double delta)
    {
        if (!_initialized) return;
        if (IsStandalone)
        {
            // Linux headsets can start the app before their runtime is ready. They still need
            // the automatic retry, even though desktop hotkeys and monitor mirroring do not apply.
            if (!IsVRActive)
                WatchForHeadset(delta);
            return;
        }

        bool f8Down = global::Godot.Input.IsKeyPressed(global::Godot.Key.F8);
        if (f8Down && !_f8WasDown)
        {
            LumoraLogger.Debug($"XRModeManager: F8 polled (IsVRActive={IsVRActive}, queuing toggle)");
            ToggleMode();
        }
        _f8WasDown = f8Down;

        // F9: plug a headset in after the game is already running, then ask for VR.
        //
        // Deliberately NOT F8. F8 toggles, so pressing it while already in VR throws you back to the
        // desktop - which is the opposite of what someone who just put a headset on wants, and easy to
        // do by accident when you are wearing one and cannot see the keyboard. This one only ever moves
        // toward VR. -xlinka
        bool f9Down = global::Godot.Input.IsKeyPressed(global::Godot.Key.F9);
        if (f9Down && !_f9WasDown)
            RetryVR();
        _f9WasDown = f9Down;

        if (IsVRActive)
            SyncDesktopMirrorCamera();
        else
            WatchForHeadset(delta);
        UpdateMonitorView(IsVRActive);
    }

    // A HEADSET THAT ARRIVES AFTER LAUNCH GETS PICKED UP WITHOUT A KEYBOARD.
    //
    // F9 already covers this, and F9 is useless in the one case that needs it most. Streaming over
    // Steam Link or Virtual Desktop, the order is always: start the game, put the headset on,
    // connect. By the time the link is up the person is inside a headset looking at nothing, with the
    // keyboard on a desk they cannot see, and the only way back into VR is a key they cannot press.
    // The same is true of starting SteamVR after the game, which is most of the time.
    //
    // Gated on a VR server process actually running, so a machine with no VR on it never probes at
    // all and the log stays clean. Only ever enters VR from a session that was NOT up and just came
    // up, so pressing F8 to sit on the desktop on purpose is never overruled. -xlinka
    private const float HeadsetProbeIntervalSeconds = 4f;
    private float _headsetProbeTimer;
    private bool _autoVrAllowed = true;
    private bool _autoVrFailureLogged;

    private static readonly string[] VrServerProcessNames =
    {
        "vrserver",                  // SteamVR, including everything Steam Link streams into
        "vrmonitor",
        "VirtualDesktop.Streamer",
        "OVRServer_x64",             // Oculus / Meta PC runtime
        "monado-service",
    };

    private void WatchForHeadset(double delta)
    {
        if (!_autoVrAllowed || _switching || _toggleQueued)
            return;

        _headsetProbeTimer += (float)delta;
        if (_headsetProbeTimer < HeadsetProbeIntervalSeconds)
            return;
        _headsetProbeTimer = 0f;

        var xrInterface = XRServer.FindInterface("OpenXR");
        if (xrInterface == null || xrInterface.IsInitialized())
            return;

        if (!IsStandalone && !AnyVrServerRunning())
            return;

        var openXRInterface = xrInterface as OpenXRInterface;
        ViewportQuality.ConfigureOpenXRBeforeInitialize(openXRInterface);

        bool ok;
        try { ok = xrInterface.Initialize(); }
        catch { ok = false; }

        // SteamVR started after the game (a person who put the headset on and connected Steam Link
        // later) while another runtime is active: ask SteamVR, as the launch does.
        if (!ok && OpenXRRuntimeReport.SteerToRunningSteamVR())
        {
            LumoraLogger.Log("XRModeManager: the active OpenXR runtime has no headset but SteamVR is running; asking SteamVR instead");
            try { ok = xrInterface.Initialize(); }
            catch { ok = false; }
        }

        if (!ok)
        {
            if (!_autoVrFailureLogged)
            {
                _autoVrFailureLogged = true;
                LumoraLogger.Log("XRModeManager: a VR runtime is running but still reports no headset; " +
                                 "watching for one. " + OpenXRRuntimeReport.DiagnoseNoHeadset());
            }
            return;
        }

        // The active runtime can have been switched while we sat here, so the cached read goes.
        OpenXRRuntimeReport.Invalidate();
        _autoVrFailureLogged = false;
        LumoraLogger.Log("XRModeManager: a headset appeared after launch; entering VR.");
        SwitchToVR();
    }

    private static bool AnyVrServerRunning()
    {
        // INSIDE A PREFIX THIS QUESTION CANNOT BE ANSWERED. Process.GetProcessesByName sees the Wine
        // process list, and SteamVR under Proton is a native Linux process that was never in it - so
        // the gate reads "no VR anywhere" forever and the watch above never fires, which is exactly
        // the audience most likely to need it. Probe unconditionally there and accept the cost: a
        // failing Initialize() every four seconds against a runtime that is not there. -xlinka
        if (Lumora.Core.WinePlatform.IsWine)
            return true;

        foreach (var name in VrServerProcessNames)
        {
            System.Diagnostics.Process[] found;
            try { found = System.Diagnostics.Process.GetProcessesByName(name); }
            catch { continue; }

            try
            {
                if (found.Length > 0)
                    return true;
            }
            finally
            {
                foreach (var process in found)
                    process.Dispose();
            }
        }
        return false;
    }


    //  PUBLIC TOGGLE API


    // safe to call from any thread / signal
    public void ToggleMode()
    {
        QueueModeSwitch(!IsVRActive);
    }

    public void SwitchToDesktop()
    {
        QueueModeSwitch(false);
    }

    // The OpenXR session is normally established at boot in LumoraEngineRunner.PhaseXRDetection.
    // If it isn't (user launched in --desktop or the runtime came up late), this will attempt
    // Initialize() once and bail cleanly back to desktop if that still fails.
    public void SwitchToVR()
    {
        QueueModeSwitch(true);
    }

    // Re-detect a headset that arrived after launch, and enter VR if one did. One-way: never leaves VR.
    //
    // Starting flat and plugging in later is a normal thing to do, and until now it meant restarting.
    // It works because the OpenXR INSTANCE is created at boot even with nothing attached - only
    // xrGetSystem fails, with XR_ERROR_FORM_FACTOR_UNAVAILABLE - so the interface object survives and
    // can be initialised later once a device actually exists. Everything below it is the same recovery
    // path F8 already uses, which catches a throwing Initialize() as well as a failing one, so the
    // worst case is a log line and staying on the desktop. -xlinka
    public void RetryVR()
    {
        if (!_initialized)
            return;

        if (IsStandalone)
        {
            LumoraLogger.Log("XRModeManager: F9 ignored - standalone headset is always in VR.");
            return;
        }

        if (IsVRActive)
        {
            LumoraLogger.Log("XRModeManager: F9 ignored - already in VR.");
            return;
        }

        var xrInterface = XRServer.FindInterface("OpenXR");
        if (xrInterface == null)
        {
            LumoraLogger.Warn("XRModeManager: F9 - no OpenXR interface in this process. A runtime has to be "
                            + "installed and running BEFORE the game starts; plugging in a headset cannot "
                            + "create one. Staying on desktop.");
            return;
        }

        LumoraLogger.Log($"XRModeManager: F9 - re-detecting XR (sessionUp={xrInterface.IsInitialized()}). "
                       + "Attempting to enter VR.");
        SwitchToVR();
    }

    private void QueueModeSwitch(bool targetVR)
    {
        if (!_initialized)
            return;

        if (IsStandalone && !targetVR)
        {
            LumoraLogger.Warn("XRModeManager: Desktop mode is not available on standalone XR devices.");
            return;
        }

        if (targetVR == IsVRActive)
            return;

        if (_switching || _toggleQueued)
        {
            LumoraLogger.Debug("XRModeManager: Mode switch already queued or active; ignoring duplicate request.");
            return;
        }

        var now = Time.GetTicksMsec();
        if (_lastSwitchMsec != 0 && now - _lastSwitchMsec < SwitchCooldownMsec)
        {
            LumoraLogger.Log("XRModeManager: Mode switch ignored; renderer is still settling from the last switch.");
            return;
        }

        _queuedVRTarget = targetVR;
        _toggleQueued = true;
        LumoraLogger.Debug($"XRModeManager: Queued {(targetVR ? "Desktop -> VR" : "VR -> Desktop")} switch.");
        CallDeferred(nameof(ProcessQueuedModeSwitch));
    }

    private async void ProcessQueuedModeSwitch()
    {
        if (!_toggleQueued)
            return;

        var targetVR = _queuedVRTarget ?? !IsVRActive;
        _queuedVRTarget = null;
        _toggleQueued = false;

        if (!_initialized || _switching || targetVR == IsVRActive)
            return;

        _switching = true;
        var switched = false;

        try
        {
            await WaitProcessFrames(1);
            switched = targetVR
                ? await SwitchToVRInternalAsync()
                : await SwitchToDesktopInternalAsync();
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"XRModeManager: Unexpected mode switch error. ({ex.Message})");
            if (targetVR)
                await RecoverDesktopAfterFailedVRSwitchAsync();
        }
        finally
        {
            if (switched)
                _lastSwitchMsec = Time.GetTicksMsec();

            _switching = false;
        }
    }

    private async Task<bool> SwitchToDesktopInternalAsync()
    {
        if (!IsVRActive)
            return false;

        LumoraLogger.Debug("XRModeManager: Switching to Desktop mode...");

        ApplyRenderingMode(false);
        _headOutput?.NotifyVRActiveChanged(false);
        _headOutput?.SwitchOutputType(HeadOutput.OutputType.Screen);
        await WaitProcessFrames(1);

        SetupDesktopInput();
        await WaitProcessFrames(1);

        IsVRActive = false;

        LumoraLogger.Log("XRModeManager: Desktop mode active. Press F8 to switch back to VR.");
        ModeChanged?.Invoke(false);
        return true;
    }

    private async Task<bool> SwitchToVRInternalAsync()
    {
        if (IsVRActive)
            return false;

        LumoraLogger.Debug("XRModeManager: Switching to VR mode...");

        try
        {
            var xrInterface = XRServer.FindInterface("OpenXR");
            if (xrInterface == null)
            {
                LumoraLogger.Warn("XRModeManager: OpenXR interface not found. Is a VR runtime running? Switch aborted.");
                return false;
            }

            var openXRInterface = xrInterface as OpenXRInterface;
            ViewportQuality.ConfigureOpenXRBeforeInitialize(openXRInterface, LumoraLogger.Log);

            if (!_xrAvailable)
                LumoraLogger.Debug("XRModeManager: OpenXR capability not established yet; checking runtime now.");

            // Recovery path - session is normally already up from PhaseXRDetection.
            if (!xrInterface.IsInitialized())
            {
                LumoraLogger.Debug("XRModeManager: OpenXR session not yet initialized; attempting Initialize()...");
                bool ok = false;
                try { ok = xrInterface.Initialize(); }
                catch (Exception initEx)
                {
                    LumoraLogger.Error($"XRModeManager: OpenXR.Initialize() threw an exception: {initEx.Message}");
                }

                if (!ok)
                {
                    LumoraLogger.Warn("XRModeManager: OpenXR.Initialize() failed. " +
                        "Check the active OpenXR runtime in Windows matches your headset. Switch aborted.");
                    return false;
                }
            }

            _xrAvailable = true;

            XRServer.PrimaryInterface = xrInterface;

            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            _vrDriver?.InitializeVR();
            _vrDriver?.FindXRNodes(GetTree()?.Root);

            TeardownDesktopInput();
            await WaitProcessFrames(1);

            ApplyRenderingMode(true);
            _headOutput?.NotifyVRActiveChanged(true);
            _headOutput?.SwitchOutputType(HeadOutput.OutputType.VR);
            await WaitProcessFrames(1);

            IsVRActive = true;

            LumoraLogger.Log("XRModeManager: VR mode active. Press F8 to return to Desktop.");
            ModeChanged?.Invoke(true);
            return true;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"XRModeManager: Unexpected error during VR switch, staying in Desktop mode. ({ex.Message})");
            await RecoverDesktopAfterFailedVRSwitchAsync();
            return false;
        }
    }

    private async Task RecoverDesktopAfterFailedVRSwitchAsync()
    {
        try
        {
            ApplyRenderingMode(false);
            _headOutput?.NotifyVRActiveChanged(false);
            _headOutput?.SwitchOutputType(HeadOutput.OutputType.Screen);
            await WaitProcessFrames(1);

            if (_desktopInput == null || !GodotObject.IsInstanceValid(_desktopInput))
                SetupDesktopInput();

            IsVRActive = false;
        }
        catch
        {
            // Nothing else useful to do here; the original switch failure was already logged.
        }
    }

    private async Task WaitProcessFrames(int frameCount)
    {
        var tree = GetTree();
        if (tree == null)
            return;

        for (var i = 0; i < frameCount; i++)
            await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }



    //  INPUT PROVIDER SETUP / TEARDOWN


    private void ApplyRenderingMode(bool vrActive)
    {
        var rootViewport = GetViewport();
        var xrViewport = ResolveXRViewport(GetTree()?.CurrentScene);

        if (rootViewport != null)
        {
            xrViewport ??= rootViewport;
            bool sessionActive = IsOpenXRSessionActive();
            // Configure only when attaching a session. Reapplying quality setters to a live headset
            // also recreates render buffers, even when the viewport remains stereo. -xlinka
            if (sessionActive && (vrActive || xrViewport != rootViewport) && !xrViewport.UseXR)
                ViewportQuality.ConfigureOpenXRAfterInitialize(
                    XRServer.FindInterface("OpenXR") as OpenXRInterface, xrViewport, LumoraLogger.Log);
            XRViewportLayout.ApplySessionState(rootViewport, xrViewport, sessionActive, vrActive);
        }

        if (_xrOrigin != null && GodotObject.IsInstanceValid(_xrOrigin))
            _xrOrigin.Visible = true;

        var camerasShareViewport = CamerasShareViewport();

        if (vrActive)
        {
            _vrDriver?.SetModeActive(true);

            if (camerasShareViewport && _mainCamera != null && GodotObject.IsInstanceValid(_mainCamera))
                _mainCamera.Current = false;

            if (_xrCamera != null && GodotObject.IsInstanceValid(_xrCamera))
                _xrCamera.MakeCurrent();
            else
                LumoraLogger.Warn("XRModeManager: Cannot make XR camera current - %XRCamera3D is missing.");

            if (!camerasShareViewport)
            {
                if (_mainCamera != null && GodotObject.IsInstanceValid(_mainCamera))
                    _mainCamera.MakeCurrent();
                SetDesktopMirrorQuality(true);
                SyncDesktopMirrorCamera();
            }
            UpdateMonitorView(true);
        }
        else
        {
            UpdateMonitorView(false);
            SetDesktopMirrorQuality(false);
            _spectatorMirrorLogShown = false;
            _vrDriver?.SetModeActive(false);

            if (camerasShareViewport && _xrCamera != null && GodotObject.IsInstanceValid(_xrCamera))
                _xrCamera.Current = false;

            if (_mainCamera != null && GodotObject.IsInstanceValid(_mainCamera))
                _mainCamera.MakeCurrent();
            else
                LumoraLogger.Warn("XRModeManager: Cannot make desktop camera current - %DesktopCamera is missing.");

            if (!camerasShareViewport && _xrCamera != null && GodotObject.IsInstanceValid(_xrCamera))
                _xrCamera.MakeCurrent();
        }
    }

    private Viewport ResolveXRViewport(Node? sceneRoot = null)
    {
        if (_xrViewport != null && GodotObject.IsInstanceValid(_xrViewport))
            return _xrViewport;

        if (_xrCamera != null && GodotObject.IsInstanceValid(_xrCamera) && _xrCamera.IsInsideTree())
        {
            _xrViewport = _xrCamera.GetViewport();
            if (_xrViewport != null)
                return _xrViewport;
        }

        if (_xrOrigin != null && GodotObject.IsInstanceValid(_xrOrigin) && _xrOrigin.IsInsideTree())
        {
            _xrViewport = _xrOrigin.GetViewport();
            if (_xrViewport != null)
                return _xrViewport;
        }

        sceneRoot ??= GetTree()?.CurrentScene!;
        _xrViewport = sceneRoot?.GetNodeOrNull<SubViewport>("%XRViewport")!;
        return _xrViewport!;
    }

    private bool IsOpenXRSessionActive()
    {
        return _xrAvailable && XRServer.FindInterface("OpenXR")?.IsInitialized() == true;
    }

    private bool CamerasShareViewport()
    {
        if (_mainCamera == null || _xrCamera == null)
            return false;

        if (!GodotObject.IsInstanceValid(_mainCamera) || !GodotObject.IsInstanceValid(_xrCamera))
            return false;

        return _mainCamera.GetViewport() == _xrCamera.GetViewport();
    }

    // Keep the desktop window on its own viewport. OpenXR owns the eye swapchains and may draw only
    // part of them; attaching those directly exposed stale edge pixels and fought window resize updates.
    // The existing desktop camera follows the head. A director feed is composited over it normally.
    // On a desktop the feed fills the window too, except while the dash is open: that is where you work the
    // camera from, and the pointer only means anything over your own view. -xlinka
    private MonitorPresentation? _monitorPresentation;

    // The window shows a camera's feed instead of the view, this frame.
    public static bool FeedOnWindow { get; private set; }

    private void UpdateMonitorView(bool vrActive)
    {
        if (IsStandalone || OS.HasFeature("android"))
            return;
        var root = GetTree()?.Root;
        if (root == null)
            return;

        bool separate = _xrViewport is SubViewport && GodotObject.IsInstanceValid(_xrViewport) && !CamerasShareViewport();
        bool room = vrActive ? separate : Lumora.Core.Engine.Current?.InputInterface?.IsDashboardOpen != true;
        // Only once the feed holds a picture drawn since it started filming: a camera that was off still shows its
        // last one, and the window flashed that old shot for a few frames before the new one came. -xlinka
        var feed = room && !EyedropperArmed()
            && Lumora.Core.Photos.DesktopFeed.Shown?.Hook is Lumora.Godot.Hooks.RenderTextureHook { PictureCurrent: true } hook
            ? hook.FilmViewport
            : null;
        bool showFeed = feed != null && GodotObject.IsInstanceValid(feed);
        if (showFeed && _monitorPresentation == null)
        {
            _monitorPresentation = new MonitorPresentation { Name = "MonitorCameraFeed" };
            AddChild(_monitorPresentation);
        }
        _monitorPresentation?.ShowFeed(showFeed ? feed!.GetTexture() : null);
        FeedOnWindow = showFeed;
        // Drawing the feed only needs the canvas. Otherwise keep the spectator camera rendering the
        // entire window every frame, including after F8, feed removal, resize and eyedropper use.
        if (root.Disable3D != showFeed)
            root.Disable3D = showFeed;
    }

    public override void _ExitTree()
    {
        if (_monitorPresentation is { Visible: true } && GetTree()?.Root is { } root)
            root.Disable3D = false;
        base._ExitTree();
    }

    private static bool EyedropperArmed()
        => Lumora.Core.Components.ColorPickerPanel.ActiveSampler is { IsSampling: true } panel && !panel.IsDestroyed;

    // Preserve desktop preferences across mode changes; XR and its mirror own quality while active.
    private void SetDesktopMirrorQuality(bool on)
    {
        var root = GetViewport();
        if (root == null)
            return;

        if (on)
        {
            if (_cheapMirrorApplied)
                return;
            _savedScalingMode = root.Scaling3DMode;
            _savedScalingScale = root.Scaling3DScale;
            _savedMsaa = root.Msaa3D;
            _savedScreenSpaceAA = root.ScreenSpaceAA;
            _savedTaa = root.UseTaa;
            ViewportQuality.ApplyDesktopMirrorDefaults(root);
            _cheapMirrorApplied = true;
            LumoraLogger.Debug("XRModeManager: desktop mirror at full resolution with 4x MSAA, while VR is active.");
            return;
        }

        if (!_cheapMirrorApplied)
            return;
        root.Scaling3DMode = _savedScalingMode;
        root.Scaling3DScale = _savedScalingScale;
        root.Msaa3D = _savedMsaa;
        root.ScreenSpaceAA = _savedScreenSpaceAA;
        root.UseTaa = _savedTaa;
        _cheapMirrorApplied = false;
    }

    private void SyncDesktopMirrorCamera()
    {
        if (_mainCamera == null || _xrCamera == null)
            return;

        if (!GodotObject.IsInstanceValid(_mainCamera) || !GodotObject.IsInstanceValid(_xrCamera))
            return;

        if (!_mainCamera.IsInsideTree() || !_xrCamera.IsInsideTree())
            return;

        if (_mainCamera.GetViewport() == _xrCamera.GetViewport())
            return;

        _mainCamera.GlobalTransform = _xrCamera.GlobalTransform;
        _mainCamera.Near = _xrCamera.Near;
        _mainCamera.Far = _xrCamera.Far;
        if (float.IsFinite(_xrCamera.Fov))
            _mainCamera.Fov = Mathf.Clamp(_xrCamera.Fov, 1f, 179f);

        if (!_mainCamera.Current)
            _mainCamera.MakeCurrent();

        if (!_spectatorMirrorLogShown)
        {
            LumoraLogger.Debug("XRModeManager: DesktopCamera is mirroring XRCamera3D for the monitor view.");
            _spectatorMirrorLogShown = true;
        }
    }

    private void SetupDesktopInput()
    {
        FreeIfValid(ref _desktopInput);
        FreeIfValid(ref _desktopCamera);

        _desktopInput = new DesktopInput();
        _desktopInput.Name = "DesktopInput";
        AddChild(_desktopInput);
        _desktopInput.SetCamera(_mainCamera);

        _desktopCamera = new DesktopCameraController();
        _desktopCamera.Name = "DesktopCameraController";
        AddChild(_desktopCamera);
        _desktopCamera.Initialize(_engine, _headOutput);

        LumoraLogger.Debug("XRModeManager: Desktop input providers ready (F5=third-person, F6=free-cam)");
    }

    public void EnableTouchControls(GodotMouseDriver mouseDriver)
    {
        if (!AndroidDeviceMode.IsPhone || IsVRActive) return;
        var layer = new CanvasLayer { Name = "TouchControlsLayer", Layer = 110 };
        var controls = new TouchControls { Name = "TouchControls" };
        controls.Initialize(_inputInterface, mouseDriver, _desktopCamera);
        layer.AddChild(controls);
        AddChild(layer);
        _inputInterface.RegisterInputDriver(controls);
    }

    private void TeardownDesktopInput()
    {
        FreeIfValid(ref _desktopInput);
        FreeIfValid(ref _desktopCamera);
        LumoraLogger.Debug("XRModeManager: Desktop input providers removed");
    }


    //  UTILITIES


    private static void FreeIfValid<T>(ref T node) where T : Node
    {
        if (node != null && GodotObject.IsInstanceValid(node))
            node.QueueFree();   // deferred, safe to call from any callback
        node = null!;
    }
}
