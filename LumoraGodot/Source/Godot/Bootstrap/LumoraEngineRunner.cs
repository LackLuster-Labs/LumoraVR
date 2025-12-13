using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Input;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Templates;
using Aquamarine.Source.Godot.Input.Drivers;
using Aquamarine.Godot.Hooks;
using Aquamarine.Source.Godot.UI;
using Aquamarine.Source.Input;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Source.Godot.Bootstrap;

/// <summary>
/// Bootstrap script for Lumora engine initialization in Godot.
/// Handles environment setup, XR detection, and system integration.
/// </summary>
public partial class LumoraEngineRunner : Node
{
    // ===== CONFIGURATION =====
    [Export] public bool VerboseInit { get; set; } = false;
    [Export] public bool AutoHostLocalHome { get; set; } = true;
    [Export] public bool AutoConnectLocalHome { get; set; } = true;
    [Export] public int LocalHomePort { get; set; } = 44844;

    // ===== CORE SYSTEMS =====
    private Lumora.Core.Engine _engine;
    private HeadOutput _headOutput;
    private SystemInfoHook _systemInfoHook;
    private InputInterface _inputInterface;
    private LoadingScreen _loadingScreen;

    // ===== INPUT DRIVERS =====
    private GodotMouseDriver _mouseDriver;
    private GodotKeyboardDriver _keyboardDriver;
    private GodotVRDriver _vrDriver;

    // ===== STATE =====
    private bool _engineInitialized = false;
    private bool _shutdownRequested = false;
    private InitializationPhase _currentPhase = InitializationPhase.EnvironmentSetup;

    // ===== SCENE REFERENCES =====
    private Node3D _inputRoot;
    private Camera3D _mainCamera;

    /// <summary>
    /// Initialization phases for engine bootstrap.
    /// </summary>
    private enum InitializationPhase
    {
        EnvironmentSetup,
        XRDetection,
        HeadOutputCreation,
        EngineCoreInit,
        SystemIntegration,
        UserspaceSetup,
        Ready
    }

    public override void _Ready()
    {
        GD.Print("==========================================================");
        GD.Print("LumoraEngineRunner: _Ready() called - Starting bootstrap...");
        GD.Print("==========================================================");

        AquaLogger.Log("==========================================================");
        AquaLogger.Log("LumoraEngineRunner: Starting engine bootstrap...");
        AquaLogger.Log("==========================================================");

        // Load and show loading screen
        InitializeLoadingScreen();

        // Start initialization sequence
        CallDeferred(MethodName.StartInitialization);
    }

    /// <summary>
    /// Initialize and display the loading screen.
    /// </summary>
    private void InitializeLoadingScreen()
    {
        GD.Print("InitializeLoadingScreen: Loading loading screen scene...");
        // Load LoadingScreen scene
        var loadingScreenScene = GD.Load<PackedScene>(LumAssets.UI.LoadingScreen);
        if (loadingScreenScene != null)
        {
            GD.Print("InitializeLoadingScreen: Scene loaded, instantiating...");
            _loadingScreen = loadingScreenScene.Instantiate<LoadingScreen>();
            AddChild(_loadingScreen);
            //_loadingScreen.Show();
            // No need to call Show() - it's already visible from _Ready()
            //forgor
            GD.Print("InitializeLoadingScreen: Loading screen displayed");
            AquaLogger.Log("LoadingScreen: Initialized and displayed");
        }
        else
        {
            GD.PrintErr("InitializeLoadingScreen: Failed to load scene - continuing without loading UI");
            AquaLogger.Warn("LoadingScreen: Failed to load scene - continuing without loading UI");
        }
    }

    /// <summary>
    /// Main initialization sequence (async).
    /// </summary>
    private async void StartInitialization()
    {
        GD.Print("StartInitialization: Beginning async initialization...");
        try
        {
            // PHASE 1: Environment Setup
            _currentPhase = InitializationPhase.EnvironmentSetup;
            GD.Print("[Phase 1/6] Environment Setup - Starting...");
            await PhaseEnvironmentSetup();
            GD.Print("[Phase 1/6] Environment Setup - Completed");

            // PHASE 2: XR Detection
            _currentPhase = InitializationPhase.XRDetection;
            GD.Print("[Phase 2/6] XR Detection - Starting...");
            await PhaseXRDetection();
            GD.Print("[Phase 2/6] XR Detection - Completed");

            // PHASE 3: HeadOutput Creation
            _currentPhase = InitializationPhase.HeadOutputCreation;
            GD.Print("[Phase 3/6] HeadOutput Creation - Starting...");
            await PhaseHeadOutputCreation();
            GD.Print("[Phase 3/6] HeadOutput Creation - Completed");

            // PHASE 4: Engine Core Initialization
            _currentPhase = InitializationPhase.EngineCoreInit;
            GD.Print("[Phase 4/6] Engine Core Initialization - Starting...");
            await PhaseEngineCoreInit();
            GD.Print("[Phase 4/6] Engine Core Initialization - Completed");

            // PHASE 5: System Integration
            _currentPhase = InitializationPhase.SystemIntegration;
            GD.Print("[Phase 5/6] System Integration - Starting...");
            await PhaseSystemIntegration();
            GD.Print("[Phase 5/6] System Integration - Completed");

            // PHASE 6: Userspace Setup
            _currentPhase = InitializationPhase.UserspaceSetup;
            GD.Print("[Phase 6/6] Userspace Setup - Starting...");
            await PhaseUserspaceSetup();
            GD.Print("[Phase 6/6] Userspace Setup - Completed");

            // PHASE 7: Ready
            _currentPhase = InitializationPhase.Ready;
            GD.Print("All phases completed! Calling OnEngineReady()...");
            OnEngineReady();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CRITICAL ERROR: Initialization failed at phase {_currentPhase}");
            GD.PrintErr($"Exception: {ex.Message}");
            GD.PrintErr($"Stack trace: {ex.StackTrace}");
            AquaLogger.Error($"LumoraEngineRunner: Initialization failed at phase {_currentPhase}: {ex.Message}");
            AquaLogger.Error($"Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// PHASE 1: Environment Setup
    /// - Parse command-line arguments
    /// - Configure platform settings
    /// </summary>
    private async Task PhaseEnvironmentSetup()
    {
        AquaLogger.Log("[Phase 1/6] Environment Setup");
        _loadingScreen?.UpdatePhase(0); // Phase index 0

        // Leave vsync off but allow high framerates; mouse driver normalizes deltas to 60 Hz feel
        global::Godot.Engine.MaxFps = 0;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

        // Prevent screen sleep
        DisplayServer.ScreenSetKeepOn(true);

        var args = OS.GetCmdlineArgs();
        AquaLogger.Log($"Command-line args: {string.Join(" ", args)}");

        await Task.Delay(150); // Artificial delay to show phase message
    }

    /// <summary>
    /// PHASE 2: XR Detection
    /// - Detect if OpenXR is available and initialized
    /// - Fallback to screen mode if not
    /// </summary>
    private async Task PhaseXRDetection()
    {
        AquaLogger.Log("[Phase 2/6] XR Detection");
        _loadingScreen?.UpdatePhase(1); // Phase index 1

        var xrInterface = XRServer.FindInterface("OpenXR");
        if (xrInterface != null)
        {
            // Ensure the interface is initialized (some runtimes need an explicit call)
            if (!xrInterface.IsInitialized())
            {
                AquaLogger.Log("XR: OpenXR interface found but not initialized. Attempting Initialize()...");
                if (!xrInterface.Initialize())
                {
                    AquaLogger.Warn("XR: OpenXR Initialize() failed - falling back to screen mode");
                    return;
                }
            }

            // Make it the primary interface if none is set
            if (XRServer.PrimaryInterface == null)
            {
                XRServer.PrimaryInterface = xrInterface;
            }

            // Switch viewport to XR rendering
            GetViewport().UseXR = true;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);

            AquaLogger.Log("XR Device: OpenXR (Active) - viewport switched to XR");
        }
        else
        {
            AquaLogger.Log("XR Device: None (Screen Mode)");
        }

        await Task.Delay(120); // Artificial delay to show phase message
    }

    /// <summary>
    /// PHASE 3: HeadOutput Creation
    /// - Create camera management system
    /// - Setup VR or screen rendering
    /// </summary>
    private async Task PhaseHeadOutputCreation()
    {
        AquaLogger.Log("[Phase 3/6] HeadOutput Creation");
        _loadingScreen?.UpdatePhase(2); // Phase index 2

        // Find or create main camera
        _mainCamera = GetViewport().GetCamera3D();
        if (_mainCamera == null)
        {
            _mainCamera = new Camera3D();
            _mainCamera.Name = "MainCamera";
            AddChild(_mainCamera);
            GetViewport().AddChild(_mainCamera);
            AquaLogger.Log("Created new MainCamera");
        }

        // Create HeadOutput system
        _headOutput = new HeadOutput();
        AddChild(_headOutput);
        _headOutput.Initialize(_mainCamera);

        AquaLogger.Log($"HeadOutput initialized with camera: {_mainCamera.Name}");

        await Task.Delay(180); // Artificial delay to show phase message
    }

    /// <summary>
    /// PHASE 4: Engine Core Initialization
    /// - Create Engine instance
    /// - Run async engine.InitializeAsync()
    /// - Wait for completion
    /// </summary>
    private async Task PhaseEngineCoreInit()
    {
        GD.Print("PhaseEngineCoreInit: Starting engine core initialization...");
        AquaLogger.Log("[Phase 4/6] Engine Core Initialization");
        _loadingScreen?.UpdatePhase(3); // Phase index 3

        try
        {
            GD.Print("PhaseEngineCoreInit: Creating SystemInfoHook...");
            // Create SystemInfoHook
            _systemInfoHook = new SystemInfoHook();
            AddChild(_systemInfoHook);
            GD.Print("PhaseEngineCoreInit: SystemInfoHook created");

            GD.Print("PhaseEngineCoreInit: Creating Engine instance...");
            // Create Engine with configuration
            _engine = new Lumora.Core.Engine
            {
                AutoHostLocalHome = this.AutoHostLocalHome,
                AutoConnectLocalHome = this.AutoConnectLocalHome
            };
            GD.Print("PhaseEngineCoreInit: Engine instance created");

            // Register hooks BEFORE engine initialization
            // This ensures slots get hooks when they're created
            GD.Print("PhaseEngineCoreInit: Registering hooks...");
            RegisterHooks();
            GD.Print("PhaseEngineCoreInit: Hooks registered");

            // Initialize engine asynchronously
            GD.Print("PhaseEngineCoreInit: Calling Engine.InitializeAsync()...");
            AquaLogger.Log("LumoraEngineRunner: Calling Engine.InitializeAsync()...");
            await _engine.InitializeAsync();
            GD.Print("PhaseEngineCoreInit: Engine.InitializeAsync() completed!");

            AquaLogger.Log("LumoraEngineRunner: Engine initialized successfully");

            GD.Print("PhaseEngineCoreInit: Initializing WorldManager hook...");
            // Initialize WorldManager hook
            var worldManagerHook = WorldManagerHook.Constructor();
            // IMPORTANT: Set Hook BEFORE Initialize() so existing worlds can find it
            _engine.WorldManager.Hook = worldManagerHook;
            worldManagerHook.Initialize(_engine.WorldManager, GetTree().Root);
            GD.Print("PhaseEngineCoreInit: WorldManager hook initialized");
            AquaLogger.Log("LumoraEngineRunner: WorldManager hook initialized");

            await Task.Delay(200); // Artificial delay to show phase message (longer for core init)
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"LumoraEngineRunner: Engine initialization failed: {ex.Message}");
            AquaLogger.Error($"Stack trace: {ex.StackTrace}");
            throw; // Re-throw to be caught by StartInitialization
        }
    }

    /// <summary>
    /// PHASE 5: System Integration
    /// - Register input drivers
    /// - Setup audio system
    /// - Configure engine callbacks
    /// </summary>
    private async Task PhaseSystemIntegration()
    {
        AquaLogger.Log("[Phase 5/6] System Integration");
        _loadingScreen?.UpdatePhase(4); // Phase index 4

        // Get InputInterface from Engine (it's already initialized in Engine.InitializeAsync)
        _inputInterface = _engine.InputInterface;
        _engine.AudioManager.Initialize(AudioMixer.GetMixer());

        // Register input drivers
        RegisterInputDrivers();

        await Task.Delay(150); // Artificial delay to show phase message
    }

    /// <summary>
    /// Register all input drivers with InputInterface.
    /// </summary>
    private void RegisterInputDrivers()
    {
        // Create InputManager for handling Godot input and mouse capture
        var inputManager = new InputManager();
        AddChild(inputManager);

        // Keyboard driver - must use RegisterKeyboardDriver
        _keyboardDriver = new GodotKeyboardDriver();
        _inputInterface.RegisterKeyboardDriver(_keyboardDriver);

        // Mouse driver - must use RegisterMouseDriver to create Mouse device
        _mouseDriver = new GodotMouseDriver();
        _inputInterface.RegisterMouseDriver(_mouseDriver);

        // VR driver - handles head and controller tracking
        _vrDriver = new GodotVRDriver();
        _inputInterface.RegisterVRDriver(_vrDriver);
        _vrDriver.InitializeVR();

        // Find XR nodes in scene tree for proper Godot 4.x VR tracking
        _vrDriver.FindXRNodes(GetTree().Root);
    }

    /// <summary>
    /// Create renderer for new world.
    /// NOTE: WorldHook is now created by WorldManagerHook automatically
    /// </summary>

    /// <summary>
    /// Remove renderer for deleted world.
    /// </summary>

    /// <summary>
    /// Update renderers when world focus changes.
    /// </summary>

    /// <summary>
    /// PHASE 6: Userspace Setup
    /// - Initialize userspace world
    /// - Setup dashboard and UI
    /// </summary>
    private async Task PhaseUserspaceSetup()
    {
        AquaLogger.Log("[Phase 6/6] Userspace Setup");
        _loadingScreen?.UpdatePhase(5); // Phase index 5

        try
        {
            var userspace = Userspace.SetupUserspace(_engine);
            AquaLogger.Log($"LumoraEngineRunner: Userspace created: '{userspace.WorldName.Value}'");
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"LumoraEngineRunner: Failed to setup userspace: {ex.Message}");
            throw;
        }

        await Task.Delay(100); // Allow world to initialize
        AquaLogger.Log("LumoraEngineRunner: Userspace setup complete");
    }

    /// <summary>
    /// Called when engine is fully initialized and ready.
    /// </summary>
    private async void OnEngineReady()
    {
        AquaLogger.Log("==========================================================");
        AquaLogger.Log("LumoraEngineRunner: Engine initialization COMPLETE!");
        AquaLogger.Log("==========================================================");

        // Update loading screen to 100% and show "Ready!" message
        _loadingScreen?.UpdatePhase(6); // Phase index 6 = Ready

        _engineInitialized = true;

        // DEBUG: Print scene tree
        await Task.Delay(500); // Wait for everything to settle
        PrintSceneTree();

        // Restore normal screen timeout
        DisplayServer.ScreenSetKeepOn(false);

        // NOTE: WorldRenderers are now created via event subscriptions when worlds are added.

        // Keep "Ready!" message visible for 1.5 seconds before hiding
        await Task.Delay(1500);

        // Hide loading screen with fade-out animation
        if (_loadingScreen != null)
        {
            AquaLogger.Log("LoadingScreen: Hiding loading screen");
            _loadingScreen.Hide();
        }
    }

    /// <summary>
    /// Main update loop - runs every frame.
    /// </summary>
    public override void _Process(double delta)
    {
        if (!_engineInitialized || _shutdownRequested)
            return;

        // Update input interface FIRST so tracking data is ready for world update
        // This ensures TrackedDevicePositioner updates slots BEFORE SlotHooks sync to Godot
        if (_inputInterface != null)
        {
            _inputInterface.UpdateInputs((float)delta);
        }
        else
        {
            AquaLogger.Log("[LumoraEngineRunner._Process] WARNING: No InputInterface!");
        }

        // Run engine update loop (world updates, slot hooks sync to Godot)
        _engine?.Update(delta);

        // Update Godot metrics for debug panels
        UpdateGodotMetrics();

        // Update HeadOutput camera positioning
        _headOutput?.UpdatePositioning(_engine);
    }

    /// <summary>
    /// Handle Godot input events (for scroll wheel, text input, etc.)
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        base._Input(@event);

        if (!_engineInitialized)
            return;

        // Forward to mouse driver for scroll wheel
        _mouseDriver?.HandleInputEvent(@event);

        // Forward to keyboard driver for text input
        _keyboardDriver?.HandleInputEvent(@event);
    }

    /// <summary>
    /// Engine panic callback.
    /// </summary>
    private void OnEnginePanic(Exception ex)
    {
        AquaLogger.Error($"ENGINE PANIC: {ex.Message}");
        AquaLogger.Error($"Stack trace: {ex.StackTrace}");
        _shutdownRequested = true;
    }

    /// <summary>
    /// Engine shutdown callback.
    /// </summary>
    private void OnEngineShutdown()
    {
        AquaLogger.Log("Engine shutdown requested");
        _shutdownRequested = true;
    }


    /// <summary>
    /// Register all Godot-specific hooks.
    /// Called before engine initialization.
    /// </summary>
    private void RegisterHooks()
    {
        AquaLogger.Log("Registering Godot hooks...");

        // Register slot hook (MUST be registered first!)
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Slot, Aquamarine.Godot.Hooks.SlotHook>();

        // Register mesh hooks (use fully qualified names to avoid ambiguity with Godot types)
        Lumora.Core.World.HookTypes.Register<ProceduralMesh, Aquamarine.Godot.Hooks.MeshHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.Meshes.BoxMesh, Aquamarine.Godot.Hooks.MeshHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.Meshes.QuadMesh, Aquamarine.Godot.Hooks.MeshHook>();

        // Register MeshRenderer hook for avatar visibility
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.MeshRenderer, Aquamarine.Godot.Hooks.MeshRendererHook>();

        // Register SkeletonBuilder and SkinnedMeshRenderer hooks for skeletal animation
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.SkeletonBuilder, Aquamarine.Godot.Hooks.SkeletonHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.SkinnedMeshRenderer, Aquamarine.Godot.Hooks.SkinnedMeshHook>();

        // Register HeadOutput hook for camera management
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.HeadOutput, Aquamarine.Godot.Hooks.HeadOutputHook>();

        // Register physics hooks
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.BoxCollider, Aquamarine.Godot.Hooks.PhysicsColliderHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.CapsuleCollider, Aquamarine.Godot.Hooks.PhysicsColliderHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.SphereCollider, Aquamarine.Godot.Hooks.PhysicsColliderHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.CharacterController, Aquamarine.Godot.Hooks.CharacterControllerHook>();

        // Register GodotUI hooks (native Godot UI rendered to 3D)
        Lumora.Core.World.HookTypes.Register<Lumora.Core.GodotUI.GodotUICanvas, Aquamarine.Godot.Hooks.GodotUI.GodotUICanvasHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.GodotUI.GodotPanel, Aquamarine.Godot.Hooks.GodotUI.GodotPanelHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.GodotUI.GodotLabel, Aquamarine.Godot.Hooks.GodotUI.GodotLabelHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.GodotUI.GodotButton, Aquamarine.Godot.Hooks.GodotUI.GodotButtonHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.GodotUI.GodotScrollContainer, Aquamarine.Godot.Hooks.GodotUI.GodotScrollContainerHook>();

        // Register GodotUI scene loading hooks
        Lumora.Core.World.HookTypes.Register<Lumora.Core.GodotUI.GodotSceneCanvas, Aquamarine.Godot.Hooks.GodotUI.GodotSceneCanvasHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.GodotUI.GodotControlRef, Aquamarine.Godot.Hooks.GodotUI.GodotControlRefHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.GodotUI.GodotLabelBinding, Aquamarine.Godot.Hooks.GodotUI.GodotLabelBindingHook>();
        Lumora.Core.World.HookTypes.Register<Lumora.Core.GodotUI.GodotValueBinding, Aquamarine.Godot.Hooks.GodotUI.GodotValueBindingHook>();

        // Register GodotUIPanel hook - inheritance now works automatically for derived types
        Lumora.Core.World.HookTypes.Register<Lumora.Core.GodotUI.GodotUIPanel, Aquamarine.Godot.Hooks.GodotUI.GodotUIPanelHook>();

        // Register Nameplate hook for user nameplates
        Lumora.Core.World.HookTypes.Register<Lumora.Core.Components.Nameplate, Aquamarine.Godot.Hooks.NameplateHook>();

        AquaLogger.Log("Hook registration complete");
    }

    /// <summary>
    /// Update Godot-specific metrics for debug panels.
    /// </summary>
    private void UpdateGodotMetrics()
    {
        if (_engine?.WorldManager?.FocusedWorld == null) return;

        var metrics = _engine.WorldManager.FocusedWorld.Metrics;
        var perfMonitor = Performance.Singleton;

        // Render time from Godot
        metrics.RenderTimeMs = perfMonitor.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
        metrics.PhysicsTimeMs = perfMonitor.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;

        // Memory from Godot
        metrics.VideoMemoryBytes = (long)perfMonitor.GetMonitor(Performance.Monitor.RenderVideoMemUsed);

        // Object counts
        metrics.GodotObjectCount = (int)perfMonitor.GetMonitor(Performance.Monitor.ObjectCount);
        metrics.GodotNodeCount = (int)perfMonitor.GetMonitor(Performance.Monitor.ObjectNodeCount);
    }

    /// <summary>
    /// Debug: Print the entire scene tree to see what exists.
    /// </summary>
    private void PrintSceneTree()
    {
        GD.Print("==========================================================");
        GD.Print("DEBUG: SCENE TREE DUMP");
        GD.Print("==========================================================");
        PrintNodeTree(GetTree().Root, 0);
        GD.Print("==========================================================");

        // Also print world info
        GD.Print($"Engine worlds count: {_engine?.WorldManager?.Worlds?.Count ?? 0}");
        if (_engine?.WorldManager?.Worlds != null)
        {
            foreach (var world in _engine.WorldManager.Worlds)
            {
                GD.Print($"  World: {world.WorldName.Value}");
                GD.Print($"    State: {world.State}");
                GD.Print($"    Focus: {world.Focus}");
                GD.Print($"    RootSlot children: {world.RootSlot?.Children.Count ?? 0}");
                if (world.RootSlot != null)
                {
                    PrintSlotTree(world.RootSlot, 4);
                }
            }
        }
        GD.Print("==========================================================");
    }

    private void PrintNodeTree(Node node, int indent)
    {
        string prefix = new string(' ', indent);
        GD.Print($"{prefix}{node.Name} ({node.GetType().Name}) - Visible: {node is CanvasItem ci && ci.Visible || node is Node3D n3d && n3d.Visible}");

        foreach (Node child in node.GetChildren())
        {
            PrintNodeTree(child, indent + 2);
        }
    }

    private void PrintSlotTree(Lumora.Core.Slot slot, int indent)
    {
        string prefix = new string(' ', indent);
        GD.Print($"{prefix}Slot: {slot.SlotName.Value}");
        GD.Print($"{prefix}  Components: {slot.Components.Count}");
        foreach (var component in slot.Components)
        {
            GD.Print($"{prefix}    - {component.GetType().Name}");
        }

        foreach (var child in slot.Children)
        {
            PrintSlotTree(child, indent + 2);
        }
    }

    /// <summary>
    /// Cleanup on exit.
    /// </summary>
    public override void _ExitTree()
    {
        AquaLogger.Log("LumoraEngineRunner: Shutting down...");

        _engine?.Dispose();
        _headOutput?.Dispose();

        base._ExitTree();
    }
}
