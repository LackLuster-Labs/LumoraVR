// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Components;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Management;

public class WorldLoadingOperation
{
    public string WorldName { get; set; } = null!;
    public Uri Address { get; set; } = null!;
    // Every address the session can be reached at; Address is the first, kept for display.
    public IReadOnlyList<Uri> Addresses { get; set; } = Array.Empty<Uri>();
    public World World { get; internal set; } = null!;
    public WorldLoadingPhase Phase { get; internal set; }
    public float Progress { get; internal set; }
    public string StatusMessage { get; internal set; } = null!;
    public bool IsComplete { get; internal set; }
    public bool IsFailed { get; internal set; }
    public string? ErrorMessage { get; internal set; }
    public bool IsCancelled { get; internal set; }
    // Waiting on the account, not the network: its email is not verified, so the join never started or the host
    // turned it away for that. The loading panel says so; WorldLoadingService.Resume carries on with the same addresses.
    public bool IsHeld { get; internal set; }
    // The host was the one that said no, rather than this side before connecting.
    public bool HeldByHost { get; internal set; }
    internal bool FocusWhenReady { get; set; }
    internal int Attempt { get; set; }

    internal TaskCompletionSource<World> CompletionSource { get; } = new();

    // Resolves to null on failure or cancel.
    public Task<World> Task => CompletionSource.Task;

    public void Cancel()
    {
        IsCancelled = true;
        CompletionSource.TrySetResult(null!);
    }
}

public enum WorldLoadingPhase
{
    Connecting,           // 0-20%
    WaitingForJoinGrant,  // 20-40%
    DownloadingWorldData, // 40-80%
    InitializingWorld,    // 80-95%
    Ready                 // 100%
}

// The user stays in the current world while the new one loads.
public class WorldLoadingService
{
    private readonly Engine _engine;
    private readonly object _gate = new();
    private WorldLoadingOperation _currentOperation = null!;

    public event Action<WorldLoadingOperation> OnLoadingProgress = null!;

    public event Action<WorldLoadingOperation> OnLoadingStarted = null!;

    public event Action<WorldLoadingOperation> OnLoadingComplete = null!;

    public event Action<WorldLoadingOperation> OnLoadingFailed = null!;

    // A join stopped to wait for a verified email; see WorldLoadingOperation.IsHeld.
    public event Action<WorldLoadingOperation> OnLoadingHeld = null!;

    public WorldLoadingOperation CurrentOperation => _currentOperation;

    // A held join is not loading anything: another join may start, and replaces it.
    public bool IsLoading => _currentOperation != null && !_currentOperation.IsComplete && !_currentOperation.IsFailed
        && !_currentOperation.IsHeld;

    public WorldLoadingOperation? HeldOperation
        => _currentOperation is { IsHeld: true, IsCancelled: false } held ? held : null;

    public WorldLoadingService(Engine engine)
    {
        _engine = engine;
    }

    // Null when a load is already running.
    public WorldLoadingOperation JoinSessionAsync(string name, string address, ushort port, bool focusWhenReady = true)
    {
        if (IsLoading)
        {
            LumoraLogger.Warn("WorldLoadingService: Already loading a world");
            return null!;
        }

        var uri = new UriBuilder("lnl", address, port).Uri;
        return JoinSessionAsync(name, uri, focusWhenReady);
    }

    public WorldLoadingOperation JoinSessionAsync(string name, Uri uri, bool focusWhenReady = true)
        => JoinSessionAsync(name, new[] { uri }, focusWhenReady);

    // Every address a session advertised: the join tries them nearest first and falls through on failure,
    // which is how a joiner outside the host's network ends up on Steam.
    public WorldLoadingOperation JoinSessionAsync(string name, IReadOnlyList<Uri> uris, bool focusWhenReady = true)
    {
        if (IsLoading)
        {
            LumoraLogger.Warn("WorldLoadingService: Already loading a world");
            return null!;
        }
        if (uris == null || uris.Count == 0)
        {
            LumoraLogger.Warn("WorldLoadingService: no address to join");
            return null!;
        }

        var operation = new WorldLoadingOperation
        {
            WorldName = name,
            Address = uris[0],
            Addresses = uris,
            Phase = WorldLoadingPhase.Connecting,
            Progress = 0f,
            StatusMessage = "Connecting...",
            FocusWhenReady = focusWhenReady,
        };

        WorldLoadingOperation? replaced;
        lock (_gate)
        {
            replaced = _currentOperation is { IsHeld: true } waiting ? waiting : null;
            _currentOperation = operation;
        }
        replaced?.Cancel();

        // Every join funnels through here (browser, invites, orbs and portals, links, Discord), so this is the one
        // check. A read already in hand holds it before anything shows; one still out is waited on in the load.
        if (AccountEmail.Known && AccountEmail.JoinsHeld)
        {
            Hold(operation, byHost: false);
            return operation;
        }

        Start(operation);
        return operation;
    }

    // Carries a held join on with the same name and addresses, checking the account again first.
    public bool Resume(WorldLoadingOperation operation)
    {
        lock (_gate)
        {
            if (operation == null || !operation.IsHeld || operation.IsCancelled || !ReferenceEquals(_currentOperation, operation))
                return false;
            operation.IsHeld = false;
            operation.HeldByHost = false;
        }
        LumoraLogger.Log($"WorldLoadingService: '{operation.WorldName}' carries on");
        operation.Phase = WorldLoadingPhase.Connecting;
        operation.Progress = 0f;
        operation.StatusMessage = "Connecting...";
        Start(operation);
        return true;
    }

    // Lets a held join go: nothing was loading, so nothing is left behind.
    public void Drop(WorldLoadingOperation operation)
    {
        if (operation == null)
            return;
        operation.Cancel();
        lock (_gate)
        {
            if (ReferenceEquals(_currentOperation, operation))
                _currentOperation = null!;
        }
    }

    private void Start(WorldLoadingOperation operation)
    {
        int attempt;
        lock (_gate)
            attempt = ++operation.Attempt;
        OnLoadingStarted?.Invoke(operation);
        _ = LoadWorldAsync(operation, attempt);
    }

    private void Hold(WorldLoadingOperation operation, bool byHost)
    {
        lock (_gate)
        {
            if (operation.IsCancelled || !ReferenceEquals(_currentOperation, operation))
                return;
            operation.IsHeld = true;
            operation.HeldByHost = byHost;
        }
        operation.World = null!;
        operation.Phase = WorldLoadingPhase.Connecting;
        operation.Progress = 0f;
        operation.StatusMessage = EmailJoinGate.Refusal;
        LumoraLogger.Log($"WorldLoadingService: join of '{operation.WorldName}' held, the account's email is not verified ({(byHost ? "the host said" : "known here")})");
        OnLoadingHeld?.Invoke(operation);
    }

    private async Task LoadWorldAsync(WorldLoadingOperation operation, int attempt)
    {
        bool focusWhenReady = operation.FocusWhenReady;
        try
        {
            if (await EmailJoinGate.HoldsAsync())
            {
                Hold(operation, byHost: false);
                return;
            }
            if (operation.IsCancelled)
                return;

            LumoraLogger.Log($"WorldLoadingService: Starting load for '{operation.WorldName}' at {operation.Address}");

            UpdateProgress(operation, WorldLoadingPhase.Connecting, 0.05f, "Connecting to server...");

            // Not in the world manager until it is running.
            var world = new World();
            world.WorldName.Value = operation.WorldName;
            world.SessionID.Value = "Unknown"; // Will be set by server
            world.AuthorityID = 0; // Server is authority
            world.LocalID = -1; // Will be assigned by server
            operation.World = world;

            world.Initialize(isAuthority: false);
            UpdateProgress(operation, WorldLoadingPhase.Connecting, 0.15f, "Initializing world...");

            world.OnStateChanged += (oldState, newState) => OnWorldStateChanged(operation, oldState, newState);

            UpdateProgress(operation, WorldLoadingPhase.Connecting, 0.18f, "Establishing connection...");
            var connected = await world.JoinSessionAsync(operation.Addresses);

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

            UpdateProgress(operation, WorldLoadingPhase.WaitingForJoinGrant, 0.25f, "Requesting access...");

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
                var state = world.State;
                string reason = world.InitializationFailureReason;
                world.Dispose();
                if (state == World.WorldState.Failed && await EmailJoinGate.RefusedForEmailAsync(reason))
                {
                    Hold(operation, byHost: true);
                    return;
                }
                FailOperation(operation, state == World.WorldState.Failed && !string.IsNullOrWhiteSpace(reason)
                    ? Security.Integrity.BuildRefusals.Localize(reason)
                    : $"World failed to load (state: {state})");
                return;
            }

            LumoraLogger.Log($"WorldLoadingService: World '{operation.WorldName}' loaded successfully");

            _engine.WorldManager?.AddWorld(world);

            // Focus if requested. The world manager holds that request while the world settles; the join
            // stays on screen, saying what it is waiting on, until the world is the one in view.
            if (focusWhenReady)
            {
                _engine.WorldManager?.FocusWorld(world);
                await WaitForFocusAsync(operation, world);
                if (operation.IsCancelled)
                {
                    _engine.WorldManager?.CancelFocus(world);
                    _engine.WorldManager?.DestroyWorld(world);
                    return;
                }
                // Lost while its focus was held (disconnected, closed by the host): a failed join, not a Ready
                // one, and its focus request must not land on what is left of it.
                if (_engine.WorldManager?.FocusedWorld != world && (world.IsDestroyed || world.State != World.WorldState.Running))
                {
                    _engine.WorldManager?.CancelFocus(world);
                    if (!world.IsDestroyed)
                        _engine.WorldManager?.DestroyWorld(world);
                    FailOperation(operation, "The world closed before it was ready");
                    return;
                }
            }

            UpdateProgress(operation, WorldLoadingPhase.Ready, 1.0f, "Ready!");
            operation.IsComplete = true;

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
            lock (_gate)
            {
                // A held join stays current for the panel, and a resumed one belongs to the run that resumed it.
                if (_currentOperation == operation && !operation.IsHeld && operation.Attempt == attempt)
                    _currentOperation = null!;
            }
        }
    }

    // Until the world is focused, the request goes to another world, or the world goes away. The world
    // manager gives up holding after its own timeout, so the safety stop here is only for a manager
    // that never processes the request at all.
    private async Task WaitForFocusAsync(WorldLoadingOperation operation, World world)
    {
        var startTime = DateTime.UtcNow;
        var safety = TimeSpan.FromSeconds(30);
        while (!operation.IsCancelled && !world.IsDestroyed && world.State == World.WorldState.Running)
        {
            var manager = _engine.WorldManager;
            if (manager == null || manager.FocusedWorld == world || manager.PendingFocusWorld != world)
                return;
            if (DateTime.UtcNow - startTime > safety)
                return;

            var waiting = world.DescribeSettle();
            UpdateProgress(operation, WorldLoadingPhase.InitializingWorld, 0.97f,
                string.IsNullOrEmpty(waiting) ? "Finishing up..." : $"Finishing up: {waiting}");
            await Task.Delay(100);
        }
    }

    private void OnWorldStateChanged(WorldLoadingOperation operation, World.WorldState oldState, World.WorldState newState)
    {
        if (operation.IsComplete || operation.IsFailed || operation.IsCancelled)
            return;

        // Failed is the load's to report once it sees the state: a refusal over the email holds the join
        // rather than failing it, and only the load can wait on the account to tell which it was.
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
        // Once: a second call would put a vaguer reason over the one the person should read.
        if (operation.IsFailed || operation.IsCancelled)
            return;
        operation.IsFailed = true;
        operation.ErrorMessage = error;
        operation.StatusMessage = $"Failed: {error}";

        LumoraLogger.Error($"WorldLoadingService: {error}");

        OnLoadingFailed?.Invoke(operation);
        operation.CompletionSource.TrySetResult(null!);
    }

    public void CancelCurrentOperation()
    {
        _currentOperation?.Cancel();
    }

}
