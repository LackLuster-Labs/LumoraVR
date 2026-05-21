// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
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
/// Rendering is dual-SubViewport: the desktop SubViewport stays mono and the XR
/// SubViewport stays stereo (<c>use_xr=true</c>) for the whole process. Toggle
/// is purely "which container is visible + which viewport renders" - we never
/// flip <c>Viewport.use_xr</c> at runtime, which avoids Godot's render-pipeline
/// cache invalidation when view_count changes.
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

        // Bind XR nodes from Bootstrap.tscn. They exist for the whole process
        // lifetime now - no more runtime creation. Looking up via CurrentScene
        // because XRModeManager is added at runtime and has no Owner, which
        // makes `GetNode("%X")` from `this` fail.
        var sceneRoot = GetTree()?.CurrentScene;
        _xrOrigin = sceneRoot?.GetNodeOrNull<XROrigin3D>("%XROrigin3D");
        _xrCamera = sceneRoot?.GetNodeOrNull<XRCamera3D>("%XRCamera3D");

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
        if (!_initialized || _switching) return;
        if (IsStandalone) return;

        bool f8Down = global::Godot.Input.IsKeyPressed(global::Godot.Key.F8);
        if (f8Down && !_f8WasDown)
        {
            LumoraLogger.Log($"XRModeManager: F8 polled (IsVRActive={IsVRActive}, queuing toggle)");
            CallDeferred(MethodName.ToggleMode);
        }
        _f8WasDown = f8Down;
    }

    // =====================================================================
    //  PUBLIC TOGGLE API
    // =====================================================================

    /// <summary>
    /// Toggle between Desktop and VR mode. Safe to call from any thread / signal.
    /// </summary>
    public void ToggleMode()
    {
        if (_switching) return;

        LumoraLogger.Log($"XRModeManager: F8 toggling {(IsVRActive ? "VR -> Desktop" : "Desktop -> VR")}");

        if (IsVRActive)
            SwitchToDesktop();
        else
            SwitchToVR();
    }

    // =====================================================================
    //  SWITCH  →  DESKTOP
    // =====================================================================

    /// <summary>
    /// Switch to Desktop (screen) rendering and input mode.
    /// </summary>
    public void SwitchToDesktop()
    {
        if (_switching || !IsVRActive) return;
        _switching = true;

        LumoraLogger.Log("XRModeManager: Switching → Desktop mode…");

        // 1. Remove VR input providers.
        TeardownVRInput();

        // 2. Flip the root viewport back to mono. This is what makes the OS
        //    window actually show the desktop camera view. Godot emits a one-
        //    shot batch of framebuffer/pipeline-cache warnings here because
        //    the renderer was built for stereo; the engine recovers within a
        //    frame and continues running. Known Godot 4 limitation, no fix
        //    short of reloading the scene.
        //    - xlinka
        GetViewport().UseXR = false;

        // 3. Hide the XR origin so any 3D content under it doesn't keep being
        //    rendered. The XR session itself stays alive.
        if (_xrOrigin != null && GodotObject.IsInstanceValid(_xrOrigin))
            _xrOrigin.Visible = false;

        // 4. Restore the desktop camera as the active camera.
        if (_mainCamera != null && GodotObject.IsInstanceValid(_mainCamera))
            _mainCamera.MakeCurrent();

        // 5. Inform HeadOutput of the mode change.
        _headOutput?.NotifyVRActiveChanged(false);
        _headOutput?.SwitchOutputType(HeadOutput.OutputType.Screen);

        // 6. Create desktop input providers.
        SetupDesktopInput();

        IsVRActive = false;
        _switching  = false;

        LumoraLogger.Log("XRModeManager: ✓ Desktop mode active. Press F8 to switch back to VR.");
        ModeChanged?.Invoke(false);
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
        if (_switching || IsVRActive) return;
        _switching = true;

        LumoraLogger.Log("XRModeManager: Switching to VR mode...");

        try
        {
            var xrInterface = XRServer.FindInterface("OpenXR");
            if (xrInterface == null)
            {
                LumoraLogger.Warn("XRModeManager: OpenXR interface not found. Is a VR runtime running? Switch aborted.");
                _switching = false;
                return;
            }

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
                    _switching = false;
                    return;
                }
            }

            if (XRServer.PrimaryInterface == null)
                XRServer.PrimaryInterface = xrInterface;

            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

            TeardownDesktopInput();

            // Flip the root viewport back to stereo. Going Desktop -> VR is
            // the clean direction; the warnings only fire when going the
            // other way.
            GetViewport().UseXR = true;

            // Show the XR origin back (we hide it when going to desktop so
            // any 3D content under it isn't being culled into the wrong view).
            if (_xrOrigin != null && GodotObject.IsInstanceValid(_xrOrigin))
                _xrOrigin.Visible = true;

            _headOutput?.NotifyVRActiveChanged(true);
            _headOutput?.SwitchOutputType(HeadOutput.OutputType.VR);

            SetupVRInput();

            IsVRActive = true;
            _switching = false;

            LumoraLogger.Log("XRModeManager: ✓ VR mode active. Press F8 to return to Desktop.");
            ModeChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            // Last-resort catch: log the error and stay in desktop mode so the game keeps running.
            LumoraLogger.Error($"XRModeManager: Unexpected error during VR switch, staying in Desktop mode. ({ex.Message})");
            try
            {
                GetViewport().UseXR = false;
                if (_mainCamera != null && GodotObject.IsInstanceValid(_mainCamera))
                    _mainCamera.MakeCurrent();
                if (_desktopInput == null || !GodotObject.IsInstanceValid(_desktopInput))
                    SetupDesktopInput();
            }
            catch { /* ignore recovery errors */ }

            IsVRActive = false;
            _switching  = false;
        }
    }


    // =====================================================================
    //  INPUT PROVIDER SETUP / TEARDOWN
    // =====================================================================

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
