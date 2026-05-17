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
    // Created on first VR switch and reused thereafter.
    private XROrigin3D  _xrOrigin;
    private XRCamera3D  _xrCamera;

    // =====================================================================
    //  KEY DETECTION
    // =====================================================================

    // Godot Editor consumes function keys at the engine level before the game sees them:
    //   F5 = Run, F6 = Run Scene, F7 = Pause, F8 = Stop, F9 = Continue…
    // Exported / released builds receive plain F8 unmodified.
    // When running from the editor, Shift+F8 is used instead. The editor does NOT
    // intercept F8 with a modifier, so it reaches our handler safely.
    private static bool IsTogglePressed(InputEventKey key)
    {
        if (!key.Pressed || key.Echo) return false;
        if (key.Keycode != global::Godot.Key.F8) return false;

        if (OS.HasFeature("editor"))
        {
            // Editor: Shift+F8 avoids editor Stop (bare F8) and Pause (F9) shortcuts.
            return key.ShiftPressed && !key.CtrlPressed && !key.AltPressed;
        }

        // Exported build: plain F8, no modifier.
        return !key.ShiftPressed && !key.CtrlPressed && !key.AltPressed;
    }

    // =====================================================================
    //  INITIALIZATION
    // =====================================================================

    /// <summary>
    /// Initialize the manager.  Call this once after the engine is fully ready.
    /// </summary>
    /// <param name="engine">The running Lumora engine instance.</param>
    /// <param name="headOutput">The active HeadOutput node.</param>
    /// <param name="inputInterface">The engine's InputInterface.</param>
    /// <param name="mainCamera">The main (desktop) Camera3D.</param>
    /// <param name="vrDriver">The low-level Godot VR driver (for node refresh).</param>
    /// <param name="startingInVR">Whether the engine launched in VR mode.</param>
    public void Initialize(
        Lumora.Core.Engine  engine,
        HeadOutput          headOutput,
        InputInterface      inputInterface,
        Camera3D            mainCamera,
        GodotVRDriver       vrDriver,
        bool                startingInVR)
    {
        _engine         = engine;
        _headOutput     = headOutput;
        _inputInterface = inputInterface;
        _mainCamera     = mainCamera;
        _vrDriver       = vrDriver;
        IsVRActive      = startingInVR;
        Instance        = this;

        // Cache any XR nodes that were already created during boot.
        FindXRNodesFromTree();

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

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!_initialized || _switching)
            return;

        if (@event is InputEventKey key && IsTogglePressed(key))
        {
            GetViewport().SetInputAsHandled();
            // Defer so the switch runs at the start of the next frame,
            // never in the middle of Godot's input-processing pipeline.
            // This prevents native crashes from freeing nodes or calling
            // OpenXR APIs while the engine is still dispatching events.
            CallDeferred(MethodName.ToggleMode);
        }
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

        // 2. Disable XR rendering on the viewport.
        GetViewport().UseXR = false;

        // 3. Hide the XR origin so it doesn't block the scene.
        if (_xrOrigin != null && GodotObject.IsInstanceValid(_xrOrigin))
            _xrOrigin.Visible = false;

        // 4. Restore the regular camera as the active camera.
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
    /// Will attempt to initialise OpenXR if it is not already running.
    /// Falls back gracefully (and without crashing) if no headset or runtime is available.
    /// </summary>
    public void SwitchToVR()
    {
        if (_switching || IsVRActive) return;
        _switching = true;

        LumoraLogger.Log("XRModeManager: Switching to VR mode...");

        try
        {
            // 1. Locate the OpenXR interface (always present if the plugin is loaded).
            var xrInterface = XRServer.FindInterface("OpenXR");
            if (xrInterface == null)
            {
                LumoraLogger.Warn("XRModeManager: OpenXR interface not found. Is a VR runtime running? Switch aborted.");
                _switching = false;
                return;
            }

            // 2. Attempt initialization only if not already running.
            if (!xrInterface.IsInitialized())
            {
                LumoraLogger.Log("XRModeManager: Calling OpenXR.Initialize()...");
                bool ok = false;
                try
                {
                    ok = xrInterface.Initialize();
                }
                catch (Exception initEx)
                {
                    LumoraLogger.Error($"XRModeManager: OpenXR.Initialize() threw an exception: {initEx.Message}");
                }

                if (!ok)
                {
                    LumoraLogger.Warn("XRModeManager: OpenXR.Initialize() failed. Make sure SteamVR (or another OpenXR runtime) is running before pressing F8. Switch aborted.");
                    _switching = false;
                    return;
                }
            }

            XRServer.PrimaryInterface = xrInterface;

            // 3. Enable XR rendering on the viewport.
            GetViewport().UseXR = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

            // 4. Remove desktop input providers (deferred-safe, we're already deferred).
            TeardownDesktopInput();

            // 5. Ensure the XR camera hierarchy exists.
            FindOrCreateXRNodes();

            // 6. Refresh the low-level VR driver's XR node references.
            _vrDriver?.FindXRNodes(GetTree().Root);

            // 7. Inform HeadOutput of the mode change.
            _headOutput?.NotifyVRActiveChanged(true);
            _headOutput?.SwitchOutputType(HeadOutput.OutputType.VR);

            // 8. Create VR input providers.
            SetupVRInput();

            IsVRActive = true;
            _switching  = false;

            LumoraLogger.Log("XRModeManager: ✓ VR mode active. Press F8 to return to Desktop.");
            ModeChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            // Last-resort catch: log the error and stay in desktop mode so the game keeps running.
            LumoraLogger.Error($"XRModeManager: Unexpected error during VR switch, staying in Desktop mode. ({ex.Message})");
            // Make sure desktop mode is still functional.
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
    //  XR SCENE NODE HELPERS
    // =====================================================================

    /// <summary>
    /// Look for XR nodes that may have been created during the boot XR detection phase.
    /// </summary>
    private void FindXRNodesFromTree()
    {
        var parent = GetParent();
        if (parent == null) return;

        _xrOrigin = parent.GetNodeOrNull<XROrigin3D>("XROrigin3D");
        if (_xrOrigin != null)
            _xrCamera = _xrOrigin.GetNodeOrNull<XRCamera3D>("XRCamera3D");
    }

    /// <summary>
    /// Ensure XROrigin3D and XRCamera3D exist when switching to VR.
    /// Reuses nodes created during boot; creates them dynamically otherwise.
    /// </summary>
    private void FindOrCreateXRNodes()
    {
        var parent = GetParent();
        if (parent == null) return;

        // --- XROrigin3D ---
        _xrOrigin = parent.GetNodeOrNull<XROrigin3D>("XROrigin3D");
        if (_xrOrigin == null)
        {
            _xrOrigin      = new XROrigin3D();
            _xrOrigin.Name = "XROrigin3D";
            parent.AddChild(_xrOrigin);
            LumoraLogger.Log("XRModeManager: Created XROrigin3D");
        }
        else
        {
            _xrOrigin.Visible = true;
        }

        // --- XRCamera3D ---
        _xrCamera = _xrOrigin.GetNodeOrNull<XRCamera3D>("XRCamera3D");
        if (_xrCamera == null)
        {
            _xrCamera      = new XRCamera3D();
            _xrCamera.Name = "XRCamera3D";
            _xrOrigin.AddChild(_xrCamera);
            LumoraLogger.Log("XRModeManager: Created XRCamera3D");
        }
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
