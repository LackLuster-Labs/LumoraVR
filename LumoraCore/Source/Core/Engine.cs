using System;
using System.Threading.Tasks;
using System.Diagnostics;
using Lumora.Core.Logging;
using Lumora.Core.Management;
using Lumora.Core.Helpers;
using Lumora.Core.Assets;
using Lumora.Core.Coroutines;
using Lumora.Core.Physics;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

/// <summary>
/// Core engine singleton managing all engine subsystems.
/// </summary>
public class Engine : IDisposable
{
    private static Engine _instance;

    private bool _initialized;
    private bool _initializing;
    private bool _hostingLocalHome;
    private int? _localHomePort;
    private World _pendingUserSpaceSetup;

    // Engine state and performance
    private Stopwatch _engineTimer = new Stopwatch();
    private double _totalTime;
    private int _frameCount;

    // Core subsystems
    public WorldManager WorldManager { get; private set; }
    public FocusManager FocusManager { get; private set; }
    public Input.InputInterface InputInterface { get; private set; }
    public AssetManager AssetManager { get; private set; }
    public GlobalCoroutineManager CoroutineManager { get; private set; }
    public PhysicsManager PhysicsManager { get; private set; }
    public RemoteAudioManager AudioManager { get; private set; }

    public static Engine Instance
    {
        get
        {
            if (_instance == null)
            {
                throw new InvalidOperationException("Engine not initialized. Call Engine.InitializeAsync() first.");
            }
            return _instance;
        }
    }

    /// <summary>
    /// Current engine instance.
    /// </summary>
    public static Engine Current => _instance;

    /// <summary>
    /// Check if the engine is initialized.
    /// </summary>
    public static bool IsInitialized => _instance != null && _instance._initialized;

    public static bool ShowDebug { get; set; } = true;
    public static bool IsDedicatedServer { get; set; } = false;

    /// <summary>
    /// Whether the engine is currently shutting down.
    /// </summary>
    public bool IsShuttingDown { get; private set; }

    /// <summary>
    /// Total time since engine start in seconds.
    /// </summary>
    public double TotalTime => _totalTime;

    /// <summary>
    /// Total frames processed since engine start.
    /// </summary>
    public int FrameCount => _frameCount;

    /// <summary>
    /// Auto-host local home world on startup.
    /// </summary>
    public bool AutoHostLocalHome { get; set; } = true;

    /// <summary>
    /// Auto-connect to local home on startup.
    /// </summary>
    public bool AutoConnectLocalHome { get; set; } = true;

    /// <summary>
    /// Initialize the engine and all subsystems asynchronously.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            AquaLogger.Warn("Engine already initialized, skipping.");
            return;
        }

        if (_initializing)
        {
            AquaLogger.Warn("Engine initialization already in progress.");
            return;
        }

        if (_instance != null && _instance != this)
        {
            throw new InvalidOperationException("Engine singleton already exists!");
        }

        _initializing = true;
        _instance = this;
        _engineTimer.Start();

        AquaLogger.Log("=====================================");
        AquaLogger.Log("Lumora Engine Initialization Starting");
        AquaLogger.Log("=====================================");

        try
        {
            // Phase 1: Core Systems
            AquaLogger.Log("Phase 1: Initializing Core Systems...");

            // Initialize FocusManager
            FocusManager = new FocusManager();
            AquaLogger.Log("  ✓ FocusManager initialized");

            // Initialize InputInterface
            InputInterface = new Input.InputInterface();
            await InputInterface.InitializeAsync();
            AquaLogger.Log("  ✓ InputInterface initialized");

            // Initialize GlobalCoroutineManager
            CoroutineManager = new GlobalCoroutineManager();
            AquaLogger.Log("  ✓ GlobalCoroutineManager initialized");

            // Phase 2: Asset and Audio Systems
            AquaLogger.Log("Phase 2: Initializing Asset Systems...");

            // Initialize AssetManager
            AssetManager = new AssetManager();
            await AssetManager.InitializeAsync();
            AquaLogger.Log("  ✓ AssetManager initialized");

            // Initialize AudioSystem
            AudioManager = new();
            foreach (string name in new string[] { "Music", "Effects", "Voice" })
            {
                if (AudioManager.Mixer.CreateAudioBus(name, out var bus) && AudioManager.Mixer.TryGetAudioBusByName("Master", out var master))
                    bus.Target = master;
            }

            // Phase 3: Physics and Networking
            AquaLogger.Log("Phase 3: Initializing Physics and Networking...");

            // Initialize PhysicsManager
            PhysicsManager = new PhysicsManager();
            await PhysicsManager.InitializeAsync();
            AquaLogger.Log("  ✓ PhysicsManager initialized");

            // Phase 4: World Management
            AquaLogger.Log("Phase 4: Initializing World Management...");

            // Initialize WorldManager asynchronously
            WorldManager = new WorldManager();
            await WorldManager.InitializeAsync(this);
            AquaLogger.Log("  ✓ WorldManager initialized");

            // Phase 5: Post-initialization
            AquaLogger.Log("Phase 5: Post-initialization setup...");
            ProcessStartupArguments();

            _initialized = true;
            _initializing = false;

            var initTime = _engineTimer.Elapsed.TotalSeconds;
            AquaLogger.Log("=====================================");
            AquaLogger.Log($"Engine Initialized Successfully in {initTime:F2}s");
            AquaLogger.Log("=====================================");
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"Engine initialization failed: {ex.Message}");
            AquaLogger.Error(ex.StackTrace);
            _initializing = false;
            _instance = null;
            throw;
        }
    }

    private void ProcessStartupArguments()
    {
        bool skipLocalHome = false;
        IsDedicatedServer = false;

        if (!skipLocalHome && AutoHostLocalHome)
        {
            StartLocalHome();

            if (AutoConnectLocalHome)
            {
                SwitchToLocalHome();
            }
        }
    }

    private void StartLocalHome()
    {
        if (_hostingLocalHome)
        {
            SwitchToLocalHome();
            return;
        }

        if (_localHomePort is null)
        {
            _localHomePort = SimpleIpHelpers.GetAvailablePortUdp(10) ?? 6000;
        }

        var world = WorldManager?.StartSession("LocalHome", (ushort)_localHomePort.Value, GetHostUserName(), "LocalHome");
        if (world == null)
        {
            AquaLogger.Error("Engine: Failed to start LocalHome session.");
            return;
        }

        _hostingLocalHome = true;
        AquaLogger.Log($"Engine: LocalHome hosted on port {_localHomePort.Value}.");

        // Store reference for userspace setup (will be handled by EngineDriver)
        _pendingUserSpaceSetup = world;
    }

    /// <summary>
    /// Get the world pending userspace setup (for EngineDriver to handle).
    /// </summary>
    public World GetPendingUserSpaceSetup()
    {
        return _pendingUserSpaceSetup;
    }

    /// <summary>
    /// Clear the pending userspace setup.
    /// </summary>
    public void ClearPendingUserSpaceSetup()
    {
        _pendingUserSpaceSetup = null;
    }

    private void SwitchToLocalHome()
    {
        var world = WorldManager?.GetWorldByName("LocalHome");
        if (world != null)
        {
            WorldManager.SwitchToWorld(world);
            AquaLogger.Log("Engine: Switched to LocalHome world.");
        }
        else
        {
            AquaLogger.Warn("Engine: LocalHome world not found when attempting to switch.");
        }
    }

    private string GetHostUserName()
    {
        return System.Environment.MachineName;
    }

    public void JoinLocalHome()
    {
        SwitchToLocalHome();
    }

    public void JoinServer(string address, int port, string worldName = "RemoteWorld")
    {
        WorldManager?.JoinSession(worldName, address, (ushort)port);
    }

    public void JoinNatServer(string identifier)
    {
        AquaLogger.Warn($"Engine: NAT join for session '{identifier}' not implemented yet.");
    }

    public void JoinNatServerRelay(string identifier)
    {
        AquaLogger.Warn($"Engine: Relay join for session '{identifier}' not implemented yet.");
    }

    /// <summary>
    /// Update the engine with comprehensive update stages.
    /// </summary>
    public void Update(double delta)
    {
        if (!_initialized)
            return;

        _frameCount++;
        _totalTime += delta;

        // Stage 1: Input Processing
        InputInterface?.ProcessInput(delta);

        // Stage 2: Global Coroutines
        CoroutineManager?.Update((float)delta);

        // Stage 3: Physics Update (before world updates)
        PhysicsManager?.PreWorldUpdate((float)delta);

        // Stage 4: World Updates (includes components, changes, etc.)
        WorldManager?.Update(delta);

        // Stage 5: Late Physics (after world updates)
        PhysicsManager?.PostWorldUpdate((float)delta);

        // Stage 6: Asset Processing
        AssetManager?.Update((float)delta);

        // Stage 7: Audio Processing
    }

    /// <summary>
    /// Fixed update for physics and deterministic operations.
    /// </summary>
    public void FixedUpdate(double fixedDelta)
    {
        if (!_initialized)
            return;

        // Fixed physics step
        PhysicsManager?.FixedUpdate((float)fixedDelta);

        // Fixed world updates
        WorldManager?.FixedUpdate(fixedDelta);
    }

    /// <summary>
    /// Late update for camera and final positioning.
    /// </summary>
    public void LateUpdate(double delta)
    {
        if (!_initialized)
            return;

        // Late world updates (cameras, final transforms)
        WorldManager?.LateUpdate(delta);
    }

    /// <summary>
    /// Dispose the engine and all subsystems.
    /// </summary>
    public void Dispose()
    {
        if (!_initialized && !_initializing)
            return;

        IsShuttingDown = true;

        AquaLogger.Log("=====================================");
        AquaLogger.Log("Engine Shutdown Starting...");
        AquaLogger.Log("=====================================");

        // Dispose in reverse initialization order
        WorldManager?.Dispose();
        AquaLogger.Log("  ✓ WorldManager disposed");

        PhysicsManager?.Dispose();
        AquaLogger.Log("  ✓ PhysicsManager disposed");

        //	AquaLogger.Log("  ✓ AudioSystem disposed");

        AssetManager?.Dispose();
        AquaLogger.Log("  ✓ AssetManager disposed");

        CoroutineManager?.Dispose();
        AquaLogger.Log("  ✓ CoroutineManager disposed");

        InputInterface?.Dispose();
        AquaLogger.Log("  ✓ InputInterface disposed");

        // FocusManager doesn't need disposal
        FocusManager = null;
        AquaLogger.Log("  ✓ FocusManager cleaned up");

        if (_instance == this)
        {
            _instance = null;
        }

        _initialized = false;
        _initializing = false;
        _engineTimer.Stop();

        var runTime = _engineTimer.Elapsed.TotalSeconds;
        AquaLogger.Log("=====================================");
        AquaLogger.Log($"Engine Shutdown Complete (ran for {runTime:F2}s)");
        AquaLogger.Log("=====================================");
    }
}
