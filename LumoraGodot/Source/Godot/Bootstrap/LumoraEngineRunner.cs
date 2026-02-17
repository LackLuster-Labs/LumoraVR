using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Templates;
using Aquamarine.Source.Godot.Input.Drivers;
using Aquamarine.Godot.Hooks;
using Aquamarine.Source.Godot.UI;
using Aquamarine.Source.Input;
using Aquamarine.Source.UI;
using Lumora.Godot.Input;
using Aquamarine.Godot.Debug;
using AquaLogger = Lumora.Core.Logging.Logger;

using InspectorInputHandler = Aquamarine.Source.Input.InspectorInputHandler;

namespace Aquamarine.Source.Godot.Bootstrap;

/// <summary>
/// Bootstrap script for Lumora engine initialization in Godot.
/// Handles environment setup, XR detection, and system integration.
/// </summary>
public partial class LumoraEngineRunner : Node
{
    private const string DebugFlag = "--Lumora-Debug";
    private const string DebugConsoleFlag = "--Lumora-DebugConsole";
    private const string DebugConsoleScenePath = "res://Scenes/UI/Debug/DebugWindow.tscn";
    private const double DebugPerfSendIntervalSec = 0.25;
    private const double DebugMemorySendIntervalSec = 0.5;

    // ===== CONFIGURATION =====
    [Export] public bool VerboseInit { get; set; } = false;
    [Export] public bool DumpSceneTreeOnReady { get; set; } = false;
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
    private ClipboardImporter _clipboardImporter;
    private LocalDB _localDB;
    private InspectorInputHandler _inspectorInputHandler;
    private DebugUdpSender? _debugUdpSender;

    // ===== STATE =====
    private bool _engineInitialized = false;
    private bool _shutdownRequested = false;
    private bool _missingInputInterfaceWarned = false;
    private double _debugPerfTimer;
    private double _debugMemoryTimer;
    private InitializationPhase _currentPhase = InitializationPhase.EnvironmentSetup;
    private static readonly Dictionary<Type, long> ComponentMemoryEstimateCache = new();

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
        if (HasCommandLineFlag(DebugConsoleFlag))
        {
            AquaLogger.Log("LumoraEngineRunner: Starting in debug console mode");
            CallDeferred(nameof(SwitchToDebugConsoleScene));
            return;
        }

        AquaLogger.Log("==========================================================");
        AquaLogger.Log("LumoraEngineRunner: Starting engine bootstrap...");
        AquaLogger.Log("==========================================================");

        if (HasCommandLineFlag(DebugFlag))
        {
            _debugUdpSender = new DebugUdpSender();
            LaunchDebugConsoleProcess();
        }

        // Load and show loading screen
        InitializeLoadingScreen();

        // Start initialization sequence
        CallDeferred(MethodName.StartInitialization);
    }

    private void SwitchToDebugConsoleScene()
    {
        var result = GetTree().ChangeSceneToFile(DebugConsoleScenePath);
        if (result != Error.Ok)
        {
            AquaLogger.Error($"DebugConsole: failed to open scene '{DebugConsoleScenePath}' ({result})");
        }
    }

    private static bool HasCommandLineFlag(string flag)
    {
        foreach (var arg in OS.GetCmdlineArgs())
        {
            if (arg.Trim().Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.Trim().Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void LaunchDebugConsoleProcess()
    {
        try
        {
            var executablePath = OS.GetExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                AquaLogger.Warn("DebugConsole: could not resolve executable path");
                return;
            }

            var args = new List<string>();
            if (OS.HasFeature("editor"))
            {
                args.Add("--path");
                args.Add(ProjectSettings.GlobalizePath("res://"));
            }

            args.Add("--");
            args.Add(DebugConsoleFlag);

            var processId = OS.CreateProcess(executablePath, args.ToArray(), false);
            if (processId > 0)
            {
                AquaLogger.Log($"DebugConsole: launched separate process (pid={processId})");
            }
            else
            {
                AquaLogger.Warn($"DebugConsole: process launch failed (pid={processId})");
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"DebugConsole: process launch exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Initialize and display the loading screen.
    /// </summary>
    private void InitializeLoadingScreen()
    {
        if (VerboseInit)
        {
            AquaLogger.Debug("InitializeLoadingScreen: Loading loading screen scene...");
        }

        // Load LoadingScreen scene
        var loadingScreenScene = GD.Load<PackedScene>(LumAssets.UI.LoadingScreen);
        if (loadingScreenScene != null)
        {
            if (VerboseInit)
            {
                AquaLogger.Debug("InitializeLoadingScreen: Scene loaded, instantiating...");
            }

            _loadingScreen = loadingScreenScene.Instantiate<LoadingScreen>();
            AddChild(_loadingScreen);
            //_loadingScreen.Show();
            // No need to call Show() - it's already visible from _Ready()
            //forgor
            AquaLogger.Log("LoadingScreen: Initialized and displayed");
        }
        else
        {
            AquaLogger.Warn("LoadingScreen: Failed to load scene - continuing without loading UI");
        }
    }

    /// <summary>
    /// Main initialization sequence (async).
    /// </summary>
    private async void StartInitialization()
    {
        if (VerboseInit)
        {
            AquaLogger.Debug("StartInitialization: Beginning async initialization...");
        }

        try
        {
            // PHASE 1: Environment Setup
            _currentPhase = InitializationPhase.EnvironmentSetup;
            await PhaseEnvironmentSetup();

            // PHASE 2: XR Detection
            _currentPhase = InitializationPhase.XRDetection;
            await PhaseXRDetection();

            // PHASE 3: HeadOutput Creation
            _currentPhase = InitializationPhase.HeadOutputCreation;
            await PhaseHeadOutputCreation();

            // PHASE 4: Engine Core Initialization
            _currentPhase = InitializationPhase.EngineCoreInit;
            await PhaseEngineCoreInit();

            // PHASE 5: System Integration
            _currentPhase = InitializationPhase.SystemIntegration;
            await PhaseSystemIntegration();

            // PHASE 6: Userspace Setup
            _currentPhase = InitializationPhase.UserspaceSetup;
            await PhaseUserspaceSetup();

            // PHASE 7: Ready
            _currentPhase = InitializationPhase.Ready;
            OnEngineReady();
        }
        catch (Exception ex)
        {
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
        AquaLogger.Log("[Phase 4/6] Engine Core Initialization");
        _loadingScreen?.UpdatePhase(3); // Phase index 3

        try
        {
            if (VerboseInit)
            {
                AquaLogger.Debug("PhaseEngineCoreInit: Creating SystemInfoHook...");
            }

            // Create SystemInfoHook
            _systemInfoHook = new SystemInfoHook();
            AddChild(_systemInfoHook);
            if (VerboseInit)
            {
                AquaLogger.Debug("PhaseEngineCoreInit: SystemInfoHook created");
            }

            if (VerboseInit)
            {
                AquaLogger.Debug("PhaseEngineCoreInit: Creating Engine instance...");
            }

            // Create Engine with configuration
            _engine = new Lumora.Core.Engine
            {
                AutoHostLocalHome = this.AutoHostLocalHome,
                AutoConnectLocalHome = this.AutoConnectLocalHome
            };
            _engine.ResourceRoot = ProjectSettings.GlobalizePath("res://");
            if (VerboseInit)
            {
                AquaLogger.Debug("PhaseEngineCoreInit: Engine instance created");
            }

            // Register hooks BEFORE engine initialization
            // This ensures slots get hooks when they're created
            if (VerboseInit)
            {
                AquaLogger.Debug("PhaseEngineCoreInit: Registering hooks...");
            }

            RegisterHooks();
            if (VerboseInit)
            {
                AquaLogger.Debug("PhaseEngineCoreInit: Hooks registered");
            }

            // Initialize engine asynchronously
            AquaLogger.Log("LumoraEngineRunner: Calling Engine.InitializeAsync()...");
            await _engine.InitializeAsync();

            AquaLogger.Log("LumoraEngineRunner: Engine initialized successfully");

            if (VerboseInit)
            {
                AquaLogger.Debug("PhaseEngineCoreInit: Initializing WorldManager hook...");
            }

            // Initialize WorldManager hook
            var worldManagerHook = WorldManagerHook.Constructor();
            // IMPORTANT: Set Hook BEFORE Initialize() so existing worlds can find it
            _engine.WorldManager.Hook = worldManagerHook;
            worldManagerHook.Initialize(_engine.WorldManager, GetTree().Root);
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

        // Create desktop input provider if not in VR mode
        // This provides the center-screen cursor and hand simulation for desktop
        bool isVRActive = XRServer.PrimaryInterface != null && GetViewport().UseXR;
        if (!isVRActive)
        {
            var desktopInput = new DesktopInput();
            desktopInput.Name = "DesktopInput";
            AddChild(desktopInput);
            desktopInput.SetCamera(_mainCamera);
            AquaLogger.Log("DesktopInput: Created for non-VR mode");
        }

        // Initialize LocalDB for asset storage
        _localDB = new LocalDB();
        _ = _localDB.InitializeAsync();

        // Wire up LocalDB to Engine for local:// URI resolution
        if (_engine != null)
        {
            _engine.LocalDB = _localDB;
        }

        // Create clipboard importer for Ctrl+V paste handling
        _clipboardImporter = new ClipboardImporter();
        _clipboardImporter.Name = "ClipboardImporter";
        AddChild(_clipboardImporter);
        _clipboardImporter.Initialize(_localDB, null, _mainCamera);
        _clipboardImporter.OnAssetImported += OnClipboardAssetImported;
        AquaLogger.Log("ClipboardImporter: Created for paste handling");
    }

    /// <summary>
    /// Called when an asset is imported from clipboard.
    /// </summary>
    private void OnClipboardAssetImported(string filePath, Lumora.Core.Slot slot)
    {
        AquaLogger.Log($"ClipboardImporter: Asset imported from '{filePath}' to slot '{slot?.SlotName.Value}'");
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

            // Create dashboard toggle input handler
            var dashboardToggle = new DashboardToggle();
            dashboardToggle.Name = "DashboardToggle";
            AddChild(dashboardToggle);
            AquaLogger.Log("LumoraEngineRunner: DashboardToggle created");

            // Note: 3D loading indicator is now created in userspace world via WorldLoadingService
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

        // Set up clipboard importer with engine reference for dynamic slot lookup
        if (_clipboardImporter != null)
        {
            _clipboardImporter.SetEngine(_engine);
            if (_engine?.WorldManager?.FocusedWorld != null)
            {
                _clipboardImporter.SetTargetSlot(_engine.WorldManager.FocusedWorld.RootSlot);
            }
            AquaLogger.Log("ClipboardImporter: Configured with engine reference");
        }

        // Create inspector input handler for "I" key inspection
        _inspectorInputHandler = new InspectorInputHandler();
        _inspectorInputHandler.Name = "InspectorInputHandler";
        _inspectorInputHandler.Engine = _engine;
        AddChild(_inspectorInputHandler);
        AquaLogger.Log("InspectorInputHandler: Created for object inspection");

        // Optional scene tree dump for deep diagnostics only.
        if (DumpSceneTreeOnReady)
        {
            await Task.Delay(500); // Wait for everything to settle
            PrintSceneTree();
        }

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
        else if (!_missingInputInterfaceWarned)
        {
            _missingInputInterfaceWarned = true;
            AquaLogger.Warn("LumoraEngineRunner: No InputInterface available in _Process");
        }

        // Run engine update loop (world updates, slot hooks sync to Godot)
        _engine?.Update(delta);

        // Update Godot metrics for debug panels
        UpdateGodotMetrics();
        SendDebugPerf(delta);
        SendDebugMemory(delta);

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
    /// Register all Godot-specific hooks with fewer, broader connectors.
    /// Called before engine initialization.
    /// </summary>
    private void RegisterHooks()
    {
        AquaLogger.Log("Registering Godot hooks...");
        GodotHookRegistry.RegisterAll();

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

    private void SendDebugPerf(double delta)
    {
        if (_debugUdpSender == null || _engine?.WorldManager?.FocusedWorld == null)
        {
            return;
        }

        _debugPerfTimer += delta;
        if (_debugPerfTimer < DebugPerfSendIntervalSec)
        {
            return;
        }
        _debugPerfTimer = 0;

        var world = _engine.WorldManager.FocusedWorld;
        var metrics = world.Metrics;
        var fps = world.LocalUser?.FPS.Value ?? (float)global::Godot.Engine.GetFramesPerSecond();
        var frameTime = fps > 0f ? 1000f / fps : 0f;

        _debugUdpSender.SendPerf(
            fps,
            frameTime,
            (float)metrics.RenderTimeMs,
            (float)metrics.PhysicsTimeMs,
            world.WorldName.Value ?? "Unnamed",
            metrics.SlotCount,
            metrics.ComponentCount,
            world.GetAllUsers().Count,
            GC.GetTotalMemory(false),
            metrics.VideoMemoryBytes,
            metrics.GodotObjectCount,
            metrics.GodotNodeCount);
    }

    private void SendDebugMemory(double delta)
    {
        if (_debugUdpSender == null || _engine?.WorldManager?.FocusedWorld == null)
        {
            return;
        }

        _debugMemoryTimer += delta;
        if (_debugMemoryTimer < DebugMemorySendIntervalSec)
        {
            return;
        }
        _debugMemoryTimer = 0;

        var world = _engine.WorldManager.FocusedWorld;
        var metrics = world.Metrics;

        var gcBytes = GC.GetTotalMemory(false);
        var gcInfo = GC.GetGCMemoryInfo();
        var committedBytes = gcInfo.TotalCommittedBytes > 0 ? gcInfo.TotalCommittedBytes : gcBytes;

        long workingSetBytes = 0;
        long privateBytes = 0;

        try
        {
            using var process = Process.GetCurrentProcess();
            workingSetBytes = process.WorkingSet64;
            privateBytes = process.PrivateMemorySize64;
        }
        catch
        {
            // Ignore process metric failures in restricted environments.
        }

        var breakdown = BuildComponentMemoryBreakdown(world.RootSlot);
        var estimatedBytes = breakdown.Sum(x => x.bytes);
        var topComponents = breakdown
            .OrderByDescending(x => x.bytes)
            .Take(24)
            .ToList();

        _debugUdpSender.SendMemory(
            committedBytes,
            gcBytes,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            estimatedBytes,
            workingSetBytes,
            privateBytes,
            metrics.VideoMemoryBytes,
            metrics.GodotObjectCount,
            metrics.GodotNodeCount,
            topComponents);
    }

    private static List<(string name, int count, long bytes)> BuildComponentMemoryBreakdown(Slot rootSlot)
    {
        var perType = new Dictionary<string, (int count, long bytes)>(StringComparer.Ordinal);
        var stack = new Stack<Slot>();
        stack.Push(rootSlot);

        while (stack.Count > 0)
        {
            var slot = stack.Pop();

            foreach (var component in slot.Components)
            {
                if (component == null)
                {
                    continue;
                }

                var type = component.GetType();
                var typeName = type.Name;
                var estimated = EstimateComponentMemory(type);

                if (perType.TryGetValue(typeName, out var existing))
                {
                    perType[typeName] = (existing.count + 1, existing.bytes + estimated);
                }
                else
                {
                    perType[typeName] = (1, estimated);
                }
            }

            foreach (var child in slot.Children)
            {
                stack.Push(child);
            }
        }

        var result = new List<(string name, int count, long bytes)>(perType.Count);
        foreach (var entry in perType)
        {
            result.Add((entry.Key, entry.Value.count, entry.Value.bytes));
        }

        return result;
    }

    private static long EstimateComponentMemory(Type type)
    {
        if (ComponentMemoryEstimateCache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        long estimate = 64; // base object overhead

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var property in properties)
        {
            if (!typeof(ISyncMember).IsAssignableFrom(property.PropertyType))
            {
                continue;
            }

            var syncType = property.PropertyType;
            if (!syncType.IsGenericType)
            {
                estimate += 32;
                continue;
            }

            var valueType = syncType.GetGenericArguments().FirstOrDefault();
            if (valueType == typeof(string))
                estimate += 80;
            else if (valueType == typeof(float) || valueType == typeof(int))
                estimate += 24;
            else if (valueType == typeof(float2))
                estimate += 32;
            else if (valueType == typeof(float3))
                estimate += 40;
            else if (valueType == typeof(float4) || valueType == typeof(floatQ))
                estimate += 48;
            else if (valueType == typeof(float4x4))
                estimate += 96;
            else if (valueType == typeof(bool))
                estimate += 20;
            else if (valueType?.IsEnum == true)
                estimate += 24;
            else
                estimate += 48;
        }

        ComponentMemoryEstimateCache[type] = estimate;
        return estimate;
    }

    /// <summary>
    /// Debug: Print the entire scene tree to see what exists.
    /// </summary>
    private void PrintSceneTree()
    {
        AquaLogger.Debug("==========================================================");
        AquaLogger.Debug("DEBUG: SCENE TREE DUMP");
        AquaLogger.Debug("==========================================================");
        PrintNodeTree(GetTree().Root, 0);
        AquaLogger.Debug("==========================================================");

        // Also print world info
        AquaLogger.Debug($"Engine worlds count: {_engine?.WorldManager?.Worlds?.Count ?? 0}");
        if (_engine?.WorldManager?.Worlds != null)
        {
            foreach (var world in _engine.WorldManager.Worlds)
            {
                AquaLogger.Debug($"  World: {world.WorldName.Value}");
                AquaLogger.Debug($"    State: {world.State}");
                AquaLogger.Debug($"    Focus: {world.Focus}");
                AquaLogger.Debug($"    RootSlot children: {world.RootSlot?.Children.Count ?? 0}");
                if (world.RootSlot != null)
                {
                    PrintSlotTree(world.RootSlot, 4);
                }
            }
        }
        AquaLogger.Debug("==========================================================");
    }

    private void PrintNodeTree(Node node, int indent)
    {
        string prefix = new string(' ', indent);
        AquaLogger.Debug($"{prefix}{node.Name} ({node.GetType().Name}) - Visible: {node is CanvasItem ci && ci.Visible || node is Node3D n3d && n3d.Visible}");

        foreach (Node child in node.GetChildren())
        {
            PrintNodeTree(child, indent + 2);
        }
    }

    private void PrintSlotTree(Lumora.Core.Slot slot, int indent)
    {
        string prefix = new string(' ', indent);
        AquaLogger.Debug($"{prefix}Slot: {slot.SlotName.Value}");
        AquaLogger.Debug($"{prefix}  Components: {slot.Components.Count}");
        foreach (var component in slot.Components)
        {
            AquaLogger.Debug($"{prefix}    - {component.GetType().Name}");
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

        _debugUdpSender?.Dispose();
        _engine?.Dispose();
        _headOutput?.Dispose();

        base._ExitTree();
    }
}
