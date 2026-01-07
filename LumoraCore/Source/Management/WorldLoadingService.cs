using System;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Components;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Management;

/// <summary>
/// Represents a world loading operation with progress tracking.
/// </summary>
public class WorldLoadingOperation
{
    public string WorldName { get; set; }
    public Uri Address { get; set; }
    public World World { get; internal set; }
    public WorldLoadingPhase Phase { get; internal set; }
    public float Progress { get; internal set; }
    public string StatusMessage { get; internal set; }
    public bool IsComplete { get; internal set; }
    public bool IsFailed { get; internal set; }
    public string? ErrorMessage { get; internal set; }
    public bool IsCancelled { get; internal set; }

    internal TaskCompletionSource<World> CompletionSource { get; } = new();

    /// <summary>
    /// Task that completes when loading finishes. Returns the loaded World or null if failed/cancelled.
    /// </summary>
    public Task<World> Task => CompletionSource.Task;

    public void Cancel()
    {
        IsCancelled = true;
        CompletionSource.TrySetResult(null);
    }
}

/// <summary>
/// Phases of world loading for progress indication.
/// </summary>
public enum WorldLoadingPhase
{
    Connecting,           // 0-20%
    WaitingForJoinGrant,  // 20-40%
    DownloadingWorldData, // 40-80%
    InitializingWorld,    // 80-95%
    Ready                 // 100%
}

/// <summary>
/// Service for loading worlds in the background with progress tracking.
/// User stays in current world while new world loads.
/// </summary>
public class WorldLoadingService
{
    private readonly Engine _engine;
    private WorldLoadingOperation _currentOperation;
    private SessionJoinIndicator _currentIndicator;

    /// <summary>
    /// Fired when world loading progress updates.
    /// </summary>
    public event Action<WorldLoadingOperation> OnLoadingProgress;

    /// <summary>
    /// Fired when world loading starts.
    /// </summary>
    public event Action<WorldLoadingOperation> OnLoadingStarted;

    /// <summary>
    /// Fired when world loading completes successfully.
    /// </summary>
    public event Action<WorldLoadingOperation> OnLoadingComplete;

    /// <summary>
    /// Fired when world loading fails.
    /// </summary>
    public event Action<WorldLoadingOperation> OnLoadingFailed;

    /// <summary>
    /// Currently active loading operation, or null if none.
    /// </summary>
    public WorldLoadingOperation CurrentOperation => _currentOperation;

    /// <summary>
    /// Whether a world is currently loading.
    /// </summary>
    public bool IsLoading => _currentOperation != null && !_currentOperation.IsComplete && !_currentOperation.IsFailed;

    public WorldLoadingService(Engine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Join a session with background loading and progress tracking.
    /// User stays in current world until the new world is ready.
    /// </summary>
    /// <param name="name">World name</param>
    /// <param name="address">Server address</param>
    /// <param name="port">Server port</param>
    /// <param name="focusWhenReady">If true, automatically focus the world when ready</param>
    /// <returns>Loading operation for tracking progress, or null if already loading</returns>
    public WorldLoadingOperation JoinSessionAsync(string name, string address, ushort port, bool focusWhenReady = true)
    {
        if (IsLoading)
        {
            LumoraLogger.Warn("WorldLoadingService: Already loading a world");
            return null;
        }

        var uri = new UriBuilder("lnl", address, port).Uri;
        return JoinSessionAsync(name, uri, focusWhenReady);
    }

    /// <summary>
    /// Join a session with background loading and progress tracking.
    /// </summary>
    public WorldLoadingOperation JoinSessionAsync(string name, Uri uri, bool focusWhenReady = true)
    {
        if (IsLoading)
        {
            LumoraLogger.Warn("WorldLoadingService: Already loading a world");
            return null;
        }

        var operation = new WorldLoadingOperation
        {
            WorldName = name,
            Address = uri,
            Phase = WorldLoadingPhase.Connecting,
            Progress = 0f,
            StatusMessage = "Connecting..."
        };

        _currentOperation = operation;

        // Fire started event
        OnLoadingStarted?.Invoke(operation);

        // Start loading in background
        _ = LoadWorldAsync(operation, focusWhenReady);

        return operation;
    }

    private async Task LoadWorldAsync(WorldLoadingOperation operation, bool focusWhenReady)
    {
        try
        {
            LumoraLogger.Log($"WorldLoadingService: Starting load for '{operation.WorldName}' at {operation.Address}");

            // Create 3D indicator in userspace world
            CreateSessionJoinIndicator(operation);

            // Phase 1: Connecting (0-20%)
            UpdateProgress(operation, WorldLoadingPhase.Connecting, 0.05f, "Connecting to server...");

            // Create world in background (not added to WorldManager yet)
            var world = new World();
            world.WorldName.Value = operation.WorldName;
            world.SessionID.Value = "Unknown"; // Will be set by server
            world.AuthorityID = 0; // Server is authority
            world.LocalID = -1; // Will be assigned by server
            operation.World = world;

            // Initialize world as client
            world.Initialize(isAuthority: false);
            UpdateProgress(operation, WorldLoadingPhase.Connecting, 0.15f, "Initializing world...");

            // Subscribe to state changes for progress tracking
            world.OnStateChanged += (oldState, newState) => OnWorldStateChanged(operation, oldState, newState);

            // Update indicator with target world (session sync will be updated after connect)
            UpdateIndicatorTarget(operation);

            // Connect to server
            UpdateProgress(operation, WorldLoadingPhase.Connecting, 0.18f, "Establishing connection...");
            var connected = await world.JoinSessionAsync(operation.Address);

            if (operation.IsCancelled)
            {
                world.Dispose();
                return;
            }

            if (!connected)
            {
                FailOperation(operation, "Failed to connect to server");
                world.Dispose();
                return;
            }

            // Update indicator with session sync now that connection is established
            UpdateIndicatorTarget(operation);

            // Phase 2: Waiting for JoinGrant (20-40%)
            UpdateProgress(operation, WorldLoadingPhase.WaitingForJoinGrant, 0.25f, "Requesting access...");

            // Wait for world to finish loading (transitions through states)
            var timeout = TimeSpan.FromSeconds(60);
            var startTime = DateTime.UtcNow;

            while (!operation.IsCancelled &&
                   world.State != World.WorldState.Running &&
                   world.State != World.WorldState.Failed &&
                   world.State != World.WorldState.Destroyed)
            {
                await Task.Delay(100);

                if (DateTime.UtcNow - startTime > timeout)
                {
                    FailOperation(operation, "Connection timeout");
                    world.Dispose();
                    return;
                }
            }

            if (operation.IsCancelled)
            {
                world.Dispose();
                return;
            }

            if (world.State != World.WorldState.Running)
            {
                FailOperation(operation, $"World failed to load (state: {world.State})");
                world.Dispose();
                return;
            }

            // Phase 5: Ready (100%)
            UpdateProgress(operation, WorldLoadingPhase.Ready, 1.0f, "Ready!");
            operation.IsComplete = true;

            LumoraLogger.Log($"WorldLoadingService: World '{operation.WorldName}' loaded successfully");

            // Destroy the 3D indicator
            DestroySessionJoinIndicator();

            // Add to WorldManager
            _engine.WorldManager?.AddWorld(world);

            // Focus if requested
            if (focusWhenReady)
            {
                _engine.WorldManager?.FocusWorld(world);
            }

            // Fire complete event
            OnLoadingComplete?.Invoke(operation);
            operation.CompletionSource.TrySetResult(world);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldLoadingService: Error loading world: {ex.Message}");
            FailOperation(operation, ex.Message);
            operation.World?.Dispose();
        }
        finally
        {
            if (_currentOperation == operation)
            {
                _currentOperation = null;
            }
        }
    }

    private void OnWorldStateChanged(WorldLoadingOperation operation, World.WorldState oldState, World.WorldState newState)
    {
        if (operation.IsComplete || operation.IsFailed || operation.IsCancelled)
            return;

        switch (newState)
        {
            case World.WorldState.WaitingForJoinGrant:
                UpdateProgress(operation, WorldLoadingPhase.WaitingForJoinGrant, 0.30f, "Waiting for access grant...");
                break;

            case World.WorldState.InitializingDataModel:
                UpdateProgress(operation, WorldLoadingPhase.DownloadingWorldData, 0.45f, "Downloading world data...");
                break;

            case World.WorldState.Running:
                UpdateProgress(operation, WorldLoadingPhase.InitializingWorld, 0.95f, "Finalizing...");
                break;

            case World.WorldState.Failed:
                FailOperation(operation, "World initialization failed");
                break;
        }
    }

    private void UpdateProgress(WorldLoadingOperation operation, WorldLoadingPhase phase, float progress, string message)
    {
        operation.Phase = phase;
        operation.Progress = progress;
        operation.StatusMessage = message;

        OnLoadingProgress?.Invoke(operation);
    }

    private void FailOperation(WorldLoadingOperation operation, string error)
    {
        operation.IsFailed = true;
        operation.ErrorMessage = error;
        operation.StatusMessage = $"Failed: {error}";

        LumoraLogger.Error($"WorldLoadingService: {error}");

        // Destroy the 3D indicator
        DestroySessionJoinIndicator();

        OnLoadingFailed?.Invoke(operation);
        operation.CompletionSource.TrySetResult(null);
    }

    /// <summary>
    /// Cancel current loading operation.
    /// </summary>
    public void CancelCurrentOperation()
    {
        _currentOperation?.Cancel();
        DestroySessionJoinIndicator();
    }

    /// <summary>
    /// Create a 3D SessionJoinIndicator in the userspace world.
    /// </summary>
    private void CreateSessionJoinIndicator(WorldLoadingOperation operation)
    {
        // Clean up any existing indicator
        DestroySessionJoinIndicator();

        // Get the userspace world
        var userspaceWorld = _engine?.WorldManager?.UserspaceWorld;
        if (userspaceWorld == null)
        {
            LumoraLogger.Warn("WorldLoadingService: No userspace world available for 3D indicator");
            return;
        }

        // Create indicator in userspace world
        userspaceWorld.RunSynchronously(() =>
        {
            try
            {
                var indicatorSlot = userspaceWorld.AddSlot("SessionJoinIndicator");
                _currentIndicator = indicatorSlot.AttachComponent<SessionJoinIndicator>();

                LumoraLogger.Log($"WorldLoadingService: Created 3D loading indicator in userspace for '{operation.WorldName}'");
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"WorldLoadingService: Failed to create 3D indicator: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Update the indicator with target world and session sync after world is created.
    /// </summary>
    private void UpdateIndicatorTarget(WorldLoadingOperation operation)
    {
        if (_currentIndicator == null || operation?.World == null)
            return;

        _currentIndicator.TargetWorld = operation.World;
        _currentIndicator.SessionSync = operation.World.Session?.Sync;
    }

    /// <summary>
    /// Destroy the current 3D SessionJoinIndicator.
    /// </summary>
    private void DestroySessionJoinIndicator()
    {
        if (_currentIndicator == null)
            return;

        try
        {
            var indicator = _currentIndicator;
            _currentIndicator = null;

            // Destroy on the userspace world's thread
            indicator.World?.RunSynchronously(() =>
            {
                indicator.Slot?.Destroy();
            });

            LumoraLogger.Log("WorldLoadingService: Destroyed 3D loading indicator");
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldLoadingService: Error destroying indicator: {ex.Message}");
        }
    }
}
