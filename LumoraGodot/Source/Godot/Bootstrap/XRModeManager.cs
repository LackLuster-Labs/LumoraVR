// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading.Tasks;
using Godot;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Source.Input;
using Lumora.Godot.Input;
using Lumora.Source.Godot.Input;
using Lumora.Source.Godot.Input.Drivers;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

/// <summary>
/// Manages dynamic hot-swapping between Desktop and VR rendering/input modes at runtime.
/// Uses F8 for runtime mode switching in exported builds.
///
/// On launch: auto-detects a connected VR headset via OpenXR and starts in VR if one is found.
/// At runtime: press <b>F8</b> to toggle between Desktop and VR without restarting.
/// (When running from the Godot Editor, use <b>Shift+F8</b>. The editor consumes
/// bare F8/F9 as its own Stop/Pause shortcuts before the game can see them.)
///
/// The manager owns and recreates the mode-specific input provider nodes
/// (DesktopInput + DesktopCameraController  ←→  EngineVRInputProvider + Laser/Grab managers)
/// so the rest of the engine doesn't need to know which mode is active.
///
/// Rendering follows the BarkVR-style split-camera pattern: the desktop camera
/// never has XR enabled, the XR camera only drives VR, and mode changes are
/// queued across frame boundaries so queued frees and renderer state settle
/// before another switch can start.
/// - xlinka
/// </summary>
public partial class XRModeManager : Node
{
    // ===== SINGLETON =====
    public static XRModeManager Instance { get; private set; }

    /// <summary>
    /// Fired after every successful mode switch.
    /// <c>true</c> = VR is now active, <c>false</c> = Desktop is now active.
    /// </summary>
    public static event Action<bool> ModeChanged;

    /// <summary>
    /// The Camera3D currently driving rendering for the active mode.
    /// In VR: the XRCamera3D under XROrigin3D. In desktop: the regular
    /// mono Camera3D that <see cref="LumoraEngineRunner"/> resolved from
    /// %DesktopCamera and that we keep as <c>_mainCamera</c>.
    /// </summary>
    public Camera3D CurrentCamera => IsVRActive ? _xrCamera : _mainCamera;

    // ===== STATE =====
    /// <summary>Whether VR mode is currently active.</summary>
    public bool IsVRActive { get; private set; }

    private bool _initialized = false;
    private bool _switching   = false;
    private bool _toggleQueued = false;
    private bool? _queuedVRTarget;
    private ulong _lastSwitchMsec = 0;
    private bool _xrAvailable = false;
    private bool _spectatorMirrorLogShown = false;

    private const ulong SwitchCooldownMsec = 500;

    // ===== REFERENCES (provided at Initialize) =====
    private Lumora.Core.Engine _engine;
    private HeadOutput         _headOutput;
    private InputInterface     _inputInterface;
    private Camera3D           _mainCamera;
    private GodotVRDriver      _vrDriver;

    // ===== MANAGED INPUT NODES =====
    // Only one set is alive at a time; the other is null.
    private DesktopInput             _desktopInput;
    private DesktopCameraController  _desktopCamera;
    private EngineVRInputProvider    _vrInputProvider;
    private LaserInteractionManager  _vrLaserManager;
    private GrabManager              _vrGrabManager;

    // ===== XR SCENE NODES =====
    // Bootstrap.tscn defines the XR sub-tree up-front; we just hold refs.
    private XROrigin3D _xrOrigin;
    private XRCamera3D _xrCamera;
    private Viewport   _xrViewport;

    // =====================================================================
    //  KEY DETECTION
    // =====================================================================

    // =====================================================================
    //  INITIALIZATION
    // =====================================================================

    /// <summary>
    /// Initialize the manager.  Call this once after the engine is fully ready.
    /// </summary>
    /// <param name="engine">The running Lumora engine instance.</param>
    /// <param name="headOutput">The active HeadOutput node.</param>
    /// <param name="inputInterface">The engine's InputInterface.</param>
    /// <param name="mainCamera">The main (desktop) Camera3D, from %DesktopCamera.</param>
    /// <param name="vrDriver">The low-level Godot VR driver (for node refresh).</param>
    /// <param name="startingInVR">Whether the engine launched in VR mode.</param>
    public void Initialize(
        Lumora.Core.Engine engine,
        HeadOutput         headOutput,
        InputInterface     inputInterface,
        Camera3D           mainCamera,
        GodotVRDriver      vrDriver,
        bool               startingInVR)
    {
        _engine         = engine;
        _headOutput     = headOutput;
        _inputInterface = inputInterface;
        _mainCamera     = mainCamera;
        _vrDriver       = vrDriver;
        IsVRActive      = startingInVR;
        Instance        = this;
        _xrAvailable    = startingInVR || XRServer.FindInterface("OpenXR")?.IsInitialized() == true;

        // Bind XR nodes from Bootstrap.tscn. They exist for the whole process
        // lifetime now - no more runtime creation. Looking up via CurrentScene
        // because XRModeManager is added at runtime and has no Owner, which
        // makes `GetNode("%X")` from `this` fail.
        var sceneRoot = GetTree()?.CurrentScene;
        _xrOrigin = sceneRoot?.GetNodeOrNull<XROrigin3D>("%XROrigin3D");
        _xrCamera = sceneRoot?.GetNodeOrNull<XRCamera3D>("%XRCamera3D");
        _xrViewport = ResolveXRViewport(sceneRoot);

        ApplyRenderingMode(startingInVR);
        _headOutput?.NotifyVRActiveChanged(startingInVR);
        _headOutput?.SwitchOutputType(startingInVR ? HeadOutput.OutputType.VR : HeadOutput.OutputType.Screen);

        // Spin up the correct input providers for the initial mode.
        if (startingInVR)
            SetupVRInput();
        else
            SetupDesktopInput();

        _initialized = true;

        var keyHint = OS.HasFeature("editor") ? "Shift+F8 (editor)" : "F8";
        LumoraLogger.Log($"XRModeManager: Initialized. Mode={( startingInVR ? "VR" : "Desktop" )}");
        LumoraLogger.Log($"XRModeManager: Press {keyHint} at any time to toggle between Desktop and VR.");
    }

    // =====================================================================
    //  INPUT HANDLING
    // =====================================================================

    // _Input is kept ONLY as a diagnostic - if F8 ever reaches the event
    // pipeline we log it. The actual toggle is driven from _Process polling
    // below, which is robust against focused Controls eating the event.
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey key && key.Pressed && !key.Echo &&
            (key.PhysicalKeycode == global::Godot.Key.F8 || key.Keycode == global::Godot.Key.F8))
        {
            LumoraLogger.Log($"XRModeManager: F8 _Input event (shift={key.ShiftPressed}, ctrl={key.CtrlPressed}, alt={key.AltPressed})");
        }
    }

    /// <summary>
    /// True when running on a standalone XR device (Quest, Pico, Focus 3, ...).
    /// On standalone there is no meaningful "desktop mode" - the headset IS
    /// the screen - so F8 is disabled and gameplay code should avoid offering
    /// mode-swap UI when this is true.
    /// </summary>
    public static bool IsStandalone => OS.HasFeature("android");

    // Edge-triggered polling. Consults Godot's polled key state every frame.
    // This survives any focused Control / GUI consumption that would have
    // swallowed the F8 event before _Input or _UnhandledInput saw it.
    // Disabled on standalone Android - there's no keyboard and no desktop
    // mode to switch to.
    // - xlinka
    private bool _f8WasDown;
    public override void _Process(double delta)
    {
        if (!_initialized) return;
        if (IsStandalone) return;

        bool f8Down = global::Godot.Input.IsKeyPressed(global::Godot.Key.F8);
        if (f8Down && !_f8WasDown)
        {
            LumoraLogger.Log($"XRModeManager: F8 polled (IsVRActive={IsVRActive}, queuing toggle)");
            ToggleMode();
        }
        _f8WasDown = f8Down;

        if (IsVRActive)
            SyncDesktopMirrorCamera();
    }

    // =====================================================================
    //  PUBLIC TOGGLE API
    // =====================================================================

    /// <summary>
    /// Toggle between Desktop and VR mode. Safe to call from any thread / signal.
    /// </summary>
    public void ToggleMode()
    {
        QueueModeSwitch(!IsVRActive);
    }

    // =====================================================================
    //  SWITCH  →  DESKTOP
    // =====================================================================

    /// <summary>
    /// Switch to Desktop (screen) rendering and input mode.
    /// </summary>
    public void SwitchToDesktop()
    {
        QueueModeSwitch(false);
    }

    // =====================================================================
    //  SWITCH  →  VR
    // =====================================================================

    /// <summary>
    /// Switch to VR rendering and input mode.
    /// The OpenXR session is normally established at boot in
    /// LumoraEngineRunner.PhaseXRDetection. If it isn't (user launched in
    /// <c>--desktop</c> or the runtime came up late), this will attempt
    /// Initialize() once and bail cleanly back to desktop if that still fails.
    /// </summary>
    public void SwitchToVR()
    {
        QueueModeSwitch(true);
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
            LumoraLogger.Log("XRModeManager: Mode switch already queued or active; ignoring duplicate request.");
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
        LumoraLogger.Log($"XRModeManager: Queued {(targetVR ? "Desktop -> VR" : "VR -> Desktop")} switch.");
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

        LumoraLogger.Log("XRModeManager: Switching to Desktop mode...");

        TeardownVRInput();
        await WaitProcessFrames(1);

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

        LumoraLogger.Log("XRModeManager: Switching to VR mode...");

        try
        {
            var xrInterface = XRServer.FindInterface("OpenXR");
            if (xrInterface == null)
            {
                LumoraLogger.Warn("XRModeManager: OpenXR interface not found. Is a VR runtime running? Switch aborted.");
                return false;
            }

            if (!_xrAvailable)
                LumoraLogger.Log("XRModeManager: OpenXR capability not established yet; checking runtime now.");

            // Recovery path - session is normally already up from PhaseXRDetection.
            if (!xrInterface.IsInitialized())
            {
                LumoraLogger.Log("XRModeManager: OpenXR session not yet initialized; attempting Initialize()...");
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

            SetupVRInput();
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


    // =====================================================================
    //  INPUT PROVIDER SETUP / TEARDOWN
    // =====================================================================

    private void ApplyRenderingMode(bool vrActive)
    {
        var rootViewport = GetViewport();
        var xrViewport = ResolveXRViewport(GetTree()?.CurrentScene);

        if (rootViewport != null && rootViewport != xrViewport)
            rootViewport.UseXR = false;

        var keepXRViewportEnabled = vrActive || IsOpenXRSessionActive();

        if (xrViewport != null)
        {
            xrViewport.UseXR = keepXRViewportEnabled;
        }
        else if (rootViewport != null)
        {
            rootViewport.UseXR = keepXRViewportEnabled;
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

            if (!camerasShareViewport && _mainCamera != null && GodotObject.IsInstanceValid(_mainCamera))
                _mainCamera.MakeCurrent();

            SyncDesktopMirrorCamera();
        }
        else
        {
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

    private Viewport ResolveXRViewport(Node sceneRoot = null)
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

        sceneRoot ??= GetTree()?.CurrentScene;
        _xrViewport = sceneRoot?.GetNodeOrNull<SubViewport>("%XRViewport");
        return _xrViewport;
    }

    private bool IsOpenXRSessionActive()
    {
        return _xrAvailable || XRServer.FindInterface("OpenXR")?.IsInitialized() == true;
    }

    private bool CamerasShareViewport()
    {
        if (_mainCamera == null || _xrCamera == null)
            return false;

        if (!GodotObject.IsInstanceValid(_mainCamera) || !GodotObject.IsInstanceValid(_xrCamera))
            return false;

        return _mainCamera.GetViewport() == _xrCamera.GetViewport();
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
        _mainCamera.Fov = _xrCamera.Fov;

        if (!_mainCamera.Current)
            _mainCamera.MakeCurrent();

        if (!_spectatorMirrorLogShown)
        {
            LumoraLogger.Log("XRModeManager: DesktopCamera is mirroring XRCamera3D for the monitor view.");
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
        _desktopCamera.Initialize(_engine);

        LumoraLogger.Log("XRModeManager: Desktop input providers ready (F5=third-person, F6=free-cam)");
    }

    private void TeardownDesktopInput()
    {
        FreeIfValid(ref _desktopInput);
        FreeIfValid(ref _desktopCamera);
        LumoraLogger.Log("XRModeManager: Desktop input providers removed");
    }

    private void SetupVRInput()
    {
        FreeIfValid(ref _vrInputProvider);
        FreeIfValid(ref _vrLaserManager);
        FreeIfValid(ref _vrGrabManager);

        _vrInputProvider = new EngineVRInputProvider();
        _vrInputProvider.Name = "VRInputProvider";
        AddChild(_vrInputProvider);
        _vrInputProvider.Initialize(_inputInterface);

        _vrLaserManager = new LaserInteractionManager();
        _vrLaserManager.Name = "LaserInteraction";
        AddChild(_vrLaserManager);

        _vrGrabManager = new GrabManager();
        _vrGrabManager.Name = "GrabManager";
        AddChild(_vrGrabManager);

        LumoraLogger.Log("XRModeManager: VR input providers ready");
    }

    private void TeardownVRInput()
    {
        FreeIfValid(ref _vrInputProvider);
        FreeIfValid(ref _vrLaserManager);
        FreeIfValid(ref _vrGrabManager);
        LumoraLogger.Log("XRModeManager: VR input providers removed");
    }

    // =====================================================================
    //  UTILITIES
    // =====================================================================

    /// <summary>
    /// Queue a node for deletion at the end of the current frame.
    /// Using QueueFree (rather than Free) is safe to call during input callbacks
    /// and avoids use-after-free crashes when nodes are freed mid-frame.
    /// </summary>
    private static void FreeIfValid<T>(ref T node) where T : Node
    {
        if (node != null && GodotObject.IsInstanceValid(node))
            node.QueueFree();   // deferred, safe to call from any callback
        node = null;
    }
}
