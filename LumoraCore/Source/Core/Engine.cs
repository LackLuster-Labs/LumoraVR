using System;
using System.Collections.Generic;
using System.Threading;
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
/// Engine initialization state.
/// </summary>
public enum EngineState
{
    /// <summary>Not yet initialized.</summary>
    NotInitialized,
    /// <summary>Currently initializing subsystems.</summary>
    Initializing,
    /// <summary>Fully initialized and running.</summary>
    Running,
    /// <summary>Currently shutting down.</summary>
    ShuttingDown,
    /// <summary>Fully disposed.</summary>
    Disposed,
    /// <summary>Initialization failed.</summary>
    Failed
}

/// <summary>
/// Status of a subsystem initialization.
/// </summary>
public enum SubsystemStatus
{
    /// <summary>Not yet initialized.</summary>
    Pending,
    /// <summary>Currently initializing.</summary>
    Initializing,
    /// <summary>Successfully initialized.</summary>
    Ready,
    /// <summary>Initialization failed.</summary>
    Failed,
    /// <summary>Disposed.</summary>
    Disposed
}

/// <summary>
/// Engine version information.
/// </summary>
public static class EngineVersion
{
    public const int Major = 0;
    public const int Minor = 1;
    public const int Patch = 0;
    public const string Suffix = "alpha";
    public static readonly string VersionString = $"{Major}.{Minor}.{Patch}-{Suffix}";
    public static readonly DateTime BuildDate = new DateTime(2024, 1, 1); // Update with actual build
}

/// <summary>
/// Engine performance metrics.
/// </summary>
public class EngineMetrics
{
    private readonly Stopwatch _frameTimer = new Stopwatch();
    private readonly Queue<double> _frameTimes = new Queue<double>();
    private const int FrameTimeHistorySize = 120;

    /// <summary>Total frames processed.</summary>
    public long TotalFrames { get; private set; }

    /// <summary>Total time engine has been running in seconds.</summary>
    public double TotalTime { get; private set; }

    /// <summary>Time of the last frame in seconds.</summary>
    public double LastFrameTime { get; private set; }

    /// <summary>Average frame time over recent history.</summary>
    public double AverageFrameTime { get; private set; }

    /// <summary>Current frames per second.</summary>
    public double CurrentFPS => LastFrameTime > 0 ? 1.0 / LastFrameTime : 0;

    /// <summary>Average frames per second.</summary>
    public double AverageFPS => AverageFrameTime > 0 ? 1.0 / AverageFrameTime : 0;

    /// <summary>Peak memory usage in bytes.</summary>
    public long PeakMemoryUsage { get; private set; }

    /// <summary>Current memory usage in bytes.</summary>
    public long CurrentMemoryUsage => GC.GetTotalMemory(false);

    /// <summary>Number of garbage collections (Gen 0).</summary>
    public int GCCollections => GC.CollectionCount(0);

    internal void BeginFrame()
    {
        _frameTimer.Restart();
    }

    internal void EndFrame(double delta)
    {
        _frameTimer.Stop();
        TotalFrames++;
        TotalTime += delta;
        LastFrameTime = delta;

        _frameTimes.Enqueue(delta);
        while (_frameTimes.Count > FrameTimeHistorySize)
            _frameTimes.Dequeue();

        double sum = 0;
        foreach (var t in _frameTimes) sum += t;
        AverageFrameTime = sum / _frameTimes.Count;

        long currentMem = CurrentMemoryUsage;
        if (currentMem > PeakMemoryUsage)
            PeakMemoryUsage = currentMem;
    }

    public override string ToString()
    {
        return $"FPS: {CurrentFPS:F1} (avg {AverageFPS:F1}), Frames: {TotalFrames}, Memory: {CurrentMemoryUsage / 1024 / 1024}MB";
    }
}

/// <summary>
/// Core engine singleton managing all engine subsystems.
/// Provides initialization, update loop, and shutdown coordination.
/// </summary>
public class Engine : IDisposable
{
    private static Engine _instance;
    private static readonly object _instanceLock = new object();

    private EngineState _state = EngineState.NotInitialized;
    private readonly object _stateLock = new object();
    private bool _hostingLocalHome;
    private int? _localHomePort;
    private World _pendingUserSpaceSetup;
    private readonly Dictionary<string, SubsystemStatus> _subsystemStatus = new Dictionary<string, SubsystemStatus>();
    private Exception _initializationError;

    // Engine timing
    private readonly Stopwatch _engineTimer = new Stopwatch();
    private readonly EngineMetrics _metrics = new EngineMetrics();
    private double _fixedTimeAccumulator;
    private const double DefaultFixedTimestep = 1.0 / 60.0;

    // Core subsystems
    public WorldManager WorldManager { get; private set; }
    public FocusManager FocusManager { get; private set; }
    public Input.InputInterface InputInterface { get; private set; }
    public AssetManager AssetManager { get; private set; }
    public GlobalCoroutineManager CoroutineManager { get; private set; }
    public PhysicsManager PhysicsManager { get; private set; }
    public RemoteAudioManager AudioManager { get; private set; }

    // Events
    /// <summary>Fired when engine state changes.</summary>
    public event Action<EngineState> OnStateChanged;

    /// <summary>Fired before each update cycle.</summary>
    public event Action<double> OnPreUpdate;

    /// <summary>Fired after each update cycle.</summary>
    public event Action<double> OnPostUpdate;

    /// <summary>Fired when a subsystem status changes.</summary>
    public event Action<string, SubsystemStatus> OnSubsystemStatusChanged;

    #region Static Properties

    /// <summary>
    /// Get the engine instance, throws if not initialized.
    /// </summary>
    public static Engine Instance
    {
        get
        {
            if (_instance == null)
                throw new InvalidOperationException("Engine not initialized. Call Engine.InitializeAsync() first.");
            return _instance;
        }
    }

    /// <summary>
    /// Current engine instance (null if not initialized).
    /// </summary>
    public static Engine Current => _instance;

    /// <summary>
    /// Check if the engine is initialized and running.
    /// </summary>
    public static bool IsInitialized => _instance?._state == EngineState.Running;

    /// <summary>
    /// Check if an engine instance exists.
    /// </summary>
    public static bool HasInstance => _instance != null;

    /// <summary>
    /// Enable debug output and features.
    /// </summary>
    public static bool ShowDebug { get; set; } = true;

    /// <summary>
    /// Whether running as a dedicated server (no rendering).
    /// </summary>
    public static bool IsDedicatedServer { get; set; } = false;

    /// <summary>
    /// Platform identifier string.
    /// </summary>
    public static string Platform => Environment.OSVersion.Platform.ToString();

    /// <summary>
    /// Whether running in editor mode.
    /// </summary>
    public static bool IsEditor { get; set; } = false;

    #endregion

    #region Instance Properties

    /// <summary>
    /// Current engine state.
    /// </summary>
    public EngineState State
    {
        get { lock (_stateLock) return _state; }
        private set
        {
            lock (_stateLock)
            {
                if (_state != value)
                {
                    _state = value;
                    OnStateChanged?.Invoke(value);
                }
            }
        }
    }

    /// <summary>
    /// Whether the engine is currently shutting down.
    /// </summary>
    public bool IsShuttingDown => State == EngineState.ShuttingDown;

    /// <summary>
    /// Whether the engine is running.
    /// </summary>
    public bool IsRunning => State == EngineState.Running;

    /// <summary>
    /// Total time since engine start in seconds.
    /// </summary>
    public double TotalTime => _metrics.TotalTime;

    /// <summary>
    /// Total frames processed since engine start.
    /// </summary>
    public long FrameCount => _metrics.TotalFrames;

    /// <summary>
    /// Engine performance metrics.
    /// </summary>
    public EngineMetrics Metrics => _metrics;

    /// <summary>
    /// Fixed timestep for physics updates.
    /// </summary>
    public double FixedTimestep { get; set; } = DefaultFixedTimestep;

    /// <summary>
    /// Maximum fixed updates per frame (prevents spiral of death).
    /// </summary>
    public int MaxFixedUpdatesPerFrame { get; set; } = 8;

    /// <summary>
    /// Auto-host local home world on startup.
    /// </summary>
    public bool AutoHostLocalHome { get; set; } = true;

    /// <summary>
    /// Auto-connect to local home on startup.
    /// </summary>
    public bool AutoConnectLocalHome { get; set; } = true;

    /// <summary>
    /// Error that occurred during initialization (if any).
    /// </summary>
    public Exception InitializationError => _initializationError;

    /// <summary>
    /// Get the status of all subsystems.
    /// </summary>
    public IReadOnlyDictionary<string, SubsystemStatus> SubsystemStatuses => _subsystemStatus;

    #endregion

    #region Initialization

    /// <summary>
    /// Create a new engine instance without initializing.
    /// Use InitializeAsync() to initialize.
    /// </summary>
    public Engine()
    {
        lock (_instanceLock)
        {
            if (_instance != null)
                throw new InvalidOperationException("Engine instance already exists. Call Dispose() first.");
            _instance = this;
        }
    }

    /// <summary>
    /// Initialize the engine and all subsystems asynchronously.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (State != EngineState.NotInitialized)
        {
            AquaLogger.Warn($"Engine already in state {State}, skipping initialization.");
            return;
        }

        State = EngineState.Initializing;
        _engineTimer.Start();

        AquaLogger.Log("=====================================");
        AquaLogger.Log($"Lumora Engine v{EngineVersion.VersionString}");
        AquaLogger.Log($"Platform: {Platform}");
        AquaLogger.Log("=====================================");
        AquaLogger.Log("Engine Initialization Starting");
        AquaLogger.Log("=====================================");

        try
        {
            // Phase 1: Core Systems
            AquaLogger.Log("Phase 1: Initializing Core Systems...");

            await InitializeSubsystem("FocusManager", async () =>
            {
                FocusManager = new FocusManager();
                await Task.CompletedTask;
            }, cancellationToken);

            await InitializeSubsystem("InputInterface", async () =>
            {
                InputInterface = new Input.InputInterface();
                await InputInterface.InitializeAsync();
            }, cancellationToken);

            await InitializeSubsystem("CoroutineManager", async () =>
            {
                CoroutineManager = new GlobalCoroutineManager();
                await Task.CompletedTask;
            }, cancellationToken);

            // Phase 2: Asset and Audio Systems
            AquaLogger.Log("Phase 2: Initializing Asset Systems...");

            await InitializeSubsystem("AssetManager", async () =>
            {
                AssetManager = new AssetManager();
                await AssetManager.InitializeAsync();
            }, cancellationToken);

            // Initialize AudioSystem
            AudioManager = new();
            foreach (string name in new string[] { "Music", "Effects", "Voice" })
            {
                if (AudioManager.Mixer.CreateAudioBus(name, out var bus) && AudioManager.Mixer.TryGetAudioBusByName("Master", out var master))
                    bus.Target = master;
            }

            // Phase 3: Physics
            AquaLogger.Log("Phase 3: Initializing Physics...");

            await InitializeSubsystem("PhysicsManager", async () =>
            {
                PhysicsManager = new PhysicsManager();
                await PhysicsManager.InitializeAsync();
            }, cancellationToken);

            // Phase 4: World Management
            AquaLogger.Log("Phase 4: Initializing World Management...");

            await InitializeSubsystem("WorldManager", async () =>
            {
                WorldManager = new WorldManager();
                await WorldManager.InitializeAsync(this);
            }, cancellationToken);

            // Phase 5: Post-initialization
            AquaLogger.Log("Phase 5: Post-initialization setup...");
            ProcessStartupArguments();

            State = EngineState.Running;

            var initTime = _engineTimer.Elapsed.TotalSeconds;
            AquaLogger.Log("=====================================");
            AquaLogger.Log($"Engine Initialized Successfully in {initTime:F2}s");
            AquaLogger.Log($"Subsystems: {_subsystemStatus.Count} initialized");
            AquaLogger.Log("=====================================");
        }
        catch (OperationCanceledException)
        {
            AquaLogger.Warn("Engine initialization was cancelled.");
            State = EngineState.Failed;
            _initializationError = new OperationCanceledException("Initialization cancelled");
            throw;
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"Engine initialization failed: {ex.Message}");
            AquaLogger.Error(ex.StackTrace);
            State = EngineState.Failed;
            _initializationError = ex;
            lock (_instanceLock) { _instance = null; }
            throw;
        }
    }

    private async Task InitializeSubsystem(string name, Func<Task> initializer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        SetSubsystemStatus(name, SubsystemStatus.Initializing);

        try
        {
            var sw = Stopwatch.StartNew();
            await initializer();
            sw.Stop();
            SetSubsystemStatus(name, SubsystemStatus.Ready);
            AquaLogger.Log($"  [{sw.ElapsedMilliseconds}ms] {name} initialized");
        }
        catch (Exception ex)
        {
            SetSubsystemStatus(name, SubsystemStatus.Failed);
            AquaLogger.Error($"  [FAILED] {name}: {ex.Message}");
            throw;
        }
    }

    private void SetSubsystemStatus(string name, SubsystemStatus status)
    {
        _subsystemStatus[name] = status;
        OnSubsystemStatusChanged?.Invoke(name, status);
    }

    private void ProcessStartupArguments()
    {
        bool skipLocalHome = false;

        if (!skipLocalHome && AutoHostLocalHome)
        {
            StartLocalHome();

            if (AutoConnectLocalHome)
            {
                SwitchToLocalHome();
            }
        }
    }

    #endregion

    #region Local Home Management

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

        _pendingUserSpaceSetup = world;
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
            AquaLogger.Warn("Engine: LocalHome world not found.");
        }
    }

    private string GetHostUserName()
    {
        return Environment.MachineName;
    }

    /// <summary>
    /// Get the world pending userspace setup.
    /// </summary>
    public World GetPendingUserSpaceSetup() => _pendingUserSpaceSetup;

    /// <summary>
    /// Clear the pending userspace setup.
    /// </summary>
    public void ClearPendingUserSpaceSetup() => _pendingUserSpaceSetup = null;

    /// <summary>
    /// Join the local home world.
    /// </summary>
    public void JoinLocalHome() => SwitchToLocalHome();

    /// <summary>
    /// Join a remote server.
    /// </summary>
    public void JoinServer(string address, int port, string worldName = "RemoteWorld")
    {
        WorldManager?.JoinSession(worldName, address, (ushort)port);
    }

    /// <summary>
    /// Join via NAT punch-through.
    /// </summary>
    public void JoinNatServer(string identifier)
    {
        AquaLogger.Warn($"Engine: NAT join for session '{identifier}' not implemented yet.");
    }

    /// <summary>
    /// Join via relay server.
    /// </summary>
    public void JoinNatServerRelay(string identifier)
    {
        AquaLogger.Warn($"Engine: Relay join for session '{identifier}' not implemented yet.");
    }

    #endregion

    #region Update Loop

    /// <summary>
    /// Main update loop - call every frame.
    /// </summary>
    public void Update(double delta)
    {
        if (State != EngineState.Running)
            return;

        _metrics.BeginFrame();
        OnPreUpdate?.Invoke(delta);

        try
        {
            // Stage 1: Input Processing
            InputInterface?.ProcessInput(delta);

            // Stage 2: Global Coroutines
            CoroutineManager?.Update((float)delta);

            // Stage 3: Fixed Updates (physics timestep)
            ProcessFixedUpdates(delta);

            // Stage 4: World Updates
            WorldManager?.Update(delta);

            // Stage 5: Asset Processing
            AssetManager?.Update((float)delta);
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"Engine Update error: {ex.Message}");
            if (ShowDebug) AquaLogger.Error(ex.StackTrace);
        }

        OnPostUpdate?.Invoke(delta);
        _metrics.EndFrame(delta);
    }

    private void ProcessFixedUpdates(double delta)
    {
        _fixedTimeAccumulator += delta;

        int fixedUpdates = 0;
        while (_fixedTimeAccumulator >= FixedTimestep && fixedUpdates < MaxFixedUpdatesPerFrame)
        {
            FixedUpdateInternal(FixedTimestep);
            _fixedTimeAccumulator -= FixedTimestep;
            fixedUpdates++;
        }

        // Prevent spiral of death
        if (_fixedTimeAccumulator > FixedTimestep * MaxFixedUpdatesPerFrame)
        {
            _fixedTimeAccumulator = 0;
        }
    }

    private void FixedUpdateInternal(double fixedDelta)
    {
        PhysicsManager?.PreWorldUpdate((float)fixedDelta);
        WorldManager?.FixedUpdate(fixedDelta);
        PhysicsManager?.PostWorldUpdate((float)fixedDelta);
    }

    /// <summary>
    /// Fixed update for deterministic physics (called automatically by Update).
    /// Can also be called manually for custom fixed-step simulations.
    /// </summary>
    public void FixedUpdate(double fixedDelta)
    {
        if (State != EngineState.Running)
            return;

        FixedUpdateInternal(fixedDelta);
    }

    /// <summary>
    /// Late update for camera and final positioning.
    /// </summary>
    public void LateUpdate(double delta)
    {
        if (State != EngineState.Running)
            return;

        WorldManager?.LateUpdate(delta);
    }

    #endregion

    #region Shutdown

    /// <summary>
    /// Request graceful shutdown.
    /// </summary>
    public void RequestShutdown()
    {
        if (State != EngineState.Running)
            return;

        AquaLogger.Log("Engine: Shutdown requested.");
        Dispose();
    }

    /// <summary>
    /// Dispose the engine and all subsystems.
    /// </summary>
    public void Dispose()
    {
        if (State == EngineState.Disposed || State == EngineState.NotInitialized)
            return;

        State = EngineState.ShuttingDown;

        AquaLogger.Log("=====================================");
        AquaLogger.Log("Engine Shutdown Starting...");
        AquaLogger.Log("=====================================");

        // Dispose in reverse initialization order
        DisposeSubsystem("WorldManager", () => { WorldManager?.Dispose(); WorldManager = null; });
        DisposeSubsystem("PhysicsManager", () => { PhysicsManager?.Dispose(); PhysicsManager = null; });
        DisposeSubsystem("AudioManager", () => { AudioManager = null; });
        DisposeSubsystem("AssetManager", () => { AssetManager?.Dispose(); AssetManager = null; });
        DisposeSubsystem("CoroutineManager", () => { CoroutineManager?.Dispose(); CoroutineManager = null; });
        DisposeSubsystem("InputInterface", () => { InputInterface?.Dispose(); InputInterface = null; });
        DisposeSubsystem("FocusManager", () => { FocusManager = null; });

        _engineTimer.Stop();
        var runTime = _engineTimer.Elapsed.TotalSeconds;

        State = EngineState.Disposed;

        lock (_instanceLock)
        {
            if (_instance == this)
                _instance = null;
        }

        AquaLogger.Log("=====================================");
        AquaLogger.Log($"Engine Shutdown Complete");
        AquaLogger.Log($"Total runtime: {runTime:F2}s, Frames: {_metrics.TotalFrames}");
        AquaLogger.Log("=====================================");
    }

    private void DisposeSubsystem(string name, Action disposer)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            disposer();
            sw.Stop();
            SetSubsystemStatus(name, SubsystemStatus.Disposed);
            AquaLogger.Log($"  [{sw.ElapsedMilliseconds}ms] {name} disposed");
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"  [ERROR] {name} disposal: {ex.Message}");
        }
    }

    #endregion

    #region Utility

    /// <summary>
    /// Get diagnostic information about the engine.
    /// </summary>
    public string GetDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Lumora Engine v{EngineVersion.VersionString}");
        sb.AppendLine($"State: {State}");
        sb.AppendLine($"Platform: {Platform}");
        sb.AppendLine($"Runtime: {TotalTime:F2}s");
        sb.AppendLine($"Frames: {FrameCount}");
        sb.AppendLine($"FPS: {_metrics.CurrentFPS:F1} (avg {_metrics.AverageFPS:F1})");
        sb.AppendLine($"Memory: {_metrics.CurrentMemoryUsage / 1024 / 1024}MB (peak {_metrics.PeakMemoryUsage / 1024 / 1024}MB)");
        sb.AppendLine($"GC Collections: {_metrics.GCCollections}");
        sb.AppendLine("Subsystems:");
        foreach (var kvp in _subsystemStatus)
            sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
        if (WorldManager != null)
            sb.AppendLine($"Worlds: {WorldManager.WorldCount}");
        return sb.ToString();
    }

    #endregion
}
