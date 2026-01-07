using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Templates;
using World = Lumora.Core.World;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Management;

/// <summary>
/// Manages all worlds (local, hosted sessions, joined sessions).
/// Core world management system for Lumora Engine.
///
/// Responsibilities:
/// - World lifecycle (creation, destruction)
/// - Focus management (focused, background, overlay worlds)
/// - Update loop coordination
/// - World discovery and lookup
/// </summary>
public class WorldManager : IDisposable
{
    private readonly List<World> _worlds = new();
    private readonly List<World> _destroyWorlds = new();
    private readonly List<World> _privateOverlayWorlds = new();
    private readonly object _worldsLock = new object();

    private World _focusedWorld;
    private World _userspaceWorld;
    private World _setWorldFocus; // Queued focus change
    private bool _initialized = false;
    private Engine _engine;

    // Platform hook for world container
    public IWorldManagerHook Hook { get; set; }

    // Events for world lifecycle notifications
    public event Action<World> WorldAdded;
    public event Action<World> WorldRemoved;
    public event Action<World> WorldFocused;

    /// <summary>
    /// Currently focused world (main world user is interacting with).
    /// </summary>
    public World FocusedWorld => _focusedWorld;

    /// <summary>
    /// The userspace world (always present, contains UI overlays and settings).
    /// </summary>
    public World UserspaceWorld
    {
        get => _userspaceWorld;
        set
        {
            _userspaceWorld = value;
            if (_userspaceWorld != null)
            {
                // Userspace is always a private overlay
                PrivateOverlayWorld(_userspaceWorld);
            }
        }
    }

    /// <summary>
    /// Number of worlds currently managed.
    /// </summary>
    public int WorldCount
    {
        get
        {
            lock (_worldsLock)
            {
                return _worlds.Count;
            }
        }
    }

    /// <summary>
    /// Get all managed worlds (readonly).
    /// </summary>
    public IReadOnlyList<World> Worlds
    {
        get
        {
            lock (_worldsLock)
            {
                return _worlds.AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Initialize the WorldManager asynchronously. Called by Engine.
    /// Sets up core world management infrastructure.
    /// </summary>
    public async Task InitializeAsync(Engine engine)
    {
        if (_initialized)
        {
            LumoraLogger.Warn("WorldManager already initialized.");
            return;
        }

        _engine = engine;

        await Task.CompletedTask;

        _initialized = true;
        LumoraLogger.Log("WorldManager initialized.");
    }

    /// <summary>
    /// Start a local world (single-user, no networking).
    /// Creates and initializes a new local world instance.
    /// </summary>
    /// <param name="name">World name</param>
    /// <param name="templateName">Template to apply (LocalHome, Grid, Empty, SocialSpace)</param>
    /// <param name="init">Initialization callback</param>
    /// <returns>Created world</returns>
    public World StartLocal(string name, string templateName = "Empty", Action<World> init = null)
    {
        try
        {
            LumoraLogger.Log($"WorldManager: Starting local world '{name}' with template '{templateName}'");

            // Use World static factory with template application
            var world = World.LocalWorld(_engine, name, (w) =>
            {
                WorldTemplates.ApplyTemplate(w, templateName);
                init?.Invoke(w);
            });

            // Add to managed worlds
            AddWorld(world);

            LumoraLogger.Log($"WorldManager: Local world '{name}' started successfully");
            return world;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Failed to start local world '{name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Start a hosted session (authority/server).
    /// Creates and initializes a new hosted multiplayer session.
    /// </summary>
    /// <param name="name">World name</param>
    /// <param name="port">Network port</param>
    /// <param name="hostUserName">Host user name</param>
    /// <param name="templateName">Template to apply (LocalHome, Grid, Empty, SocialSpace)</param>
    /// <param name="init">Initialization callback</param>
    /// <returns>Created world</returns>
    public World StartSession(string name, ushort port, string hostUserName = null, string templateName = "Empty", Action<World> init = null)
    {
        try
        {
            LumoraLogger.Log($"WorldManager: Starting session '{name}' on port {port} with template '{templateName}'");

            // Use World static factory with template application
            var world = World.StartSession(_engine, name, port, hostUserName, (w) =>
            {
                WorldTemplates.ApplyTemplate(w, templateName);
                init?.Invoke(w);
            });

            // Add to managed worlds
            AddWorld(world);

            LumoraLogger.Log($"WorldManager: Session '{name}' started successfully on port {port}");
            return world;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Failed to start session '{name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Join a remote session (client).
    /// Connects to and initializes a remote multiplayer session.
    /// </summary>
    /// <param name="name">World name</param>
    /// <param name="address">Server address</param>
    /// <param name="port">Server port</param>
    /// <returns>Created world</returns>
    public World JoinSession(string name, string address, ushort port)
    {
        try
        {
            LumoraLogger.Log($"WorldManager: Joining session at {address}:{port}");

            // Use World static factory for session joining
            var uri = new UriBuilder("lnl", address, port).Uri;
            var world = World.JoinSession(_engine, name, uri);

            // Add to managed worlds
            AddWorld(world);

            LumoraLogger.Log($"WorldManager: Successfully joined session at {address}:{port}");
            return world;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Failed to join session at {address}:{port}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Join a remote session (client) asynchronously.
    /// </summary>
    public async Task<World> JoinSessionAsync(string name, string address, ushort port)
    {
        try
        {
            LumoraLogger.Log($"WorldManager: Joining session at {address}:{port}");

            var uri = new UriBuilder("lnl", address, port).Uri;
            var world = await World.JoinSessionAsync(_engine, name, uri);
            if (world == null)
            {
                LumoraLogger.Error($"WorldManager: Failed to join session at {address}:{port}");
                return null;
            }

            AddWorld(world);
            LumoraLogger.Log($"WorldManager: Successfully joined session at {address}:{port}");
            return world;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Failed to join session at {address}:{port}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Add a world to managed list and fire events.
    /// Adds world to managed collection and triggers events.
    /// </summary>
    public void AddWorld(World world)
    {
        if (world == null)
            return;

        // Set WorldManager reference
        world.WorldManager = this;

        // Subscribe to world disconnection events
        if (world.Session != null)
        {
            world.Session.OnDisconnected += () => OnWorldDisconnected(world);
        }

        lock (_worldsLock)
        {
            if (!_worlds.Contains(world))
            {
                _worlds.Add(world);
            }
        }

        // Fire event
        try
        {
            WorldAdded?.Invoke(world);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Error in WorldAdded event: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle world disconnection and switch focus to fallback world.
    /// </summary>
    private void OnWorldDisconnected(World disconnectedWorld)
    {
        LumoraLogger.Log($"WorldManager: World '{disconnectedWorld.WorldName.Value}' disconnected");

        // If this was the focused world, switch to a fallback
        if (_focusedWorld == disconnectedWorld)
        {
            LumoraLogger.Log("WorldManager: Disconnected world was focused, switching to fallback");

            // Find fallback world (LocalHome or any available world)
            World fallbackWorld = null;
            lock (_worldsLock)
            {
                // Prefer LocalHome world
                fallbackWorld = _worlds.Find(w => !w.IsDestroyed && 
                                                  w.State == World.WorldState.Running && 
                                                  w.WorldName.Value == "LocalHome");

                // If no LocalHome, use any available running world
                if (fallbackWorld == null)
                {
                    fallbackWorld = _worlds.Find(w => !w.IsDestroyed && 
                                                      w.State == World.WorldState.Running &&
                                                      w != disconnectedWorld);
                }
            }

            if (fallbackWorld != null)
            {
                LumoraLogger.Log($"WorldManager: Switching focus to fallback world '{fallbackWorld.WorldName.Value}'");
                FocusWorld(fallbackWorld);
            }
            else
            {
                LumoraLogger.Warn("WorldManager: No fallback world available after disconnect");
                _focusedWorld = null;
            }
        }

        // Queue the disconnected world for destruction
        DestroyWorld(disconnectedWorld);
    }

    /// <summary>
    /// Queue a world for destruction (safe async removal).
    /// Safely schedules world cleanup and removal.
    /// </summary>
    public void DestroyWorld(World world)
    {
        if (world == null)
            return;

        lock (_destroyWorlds)
        {
            if (!_destroyWorlds.Contains(world))
            {
                _destroyWorlds.Add(world);
                LumoraLogger.Log($"WorldManager: Queued world '{world.WorldName.Value}' for destruction");
            }
        }
    }

    /// <summary>
    /// Request focus change to a specific world.
    /// Changes active world context for user interaction.
    /// </summary>
    public void FocusWorld(World world)
    {
        if (world == null)
        {
            LumoraLogger.Warn("WorldManager: Cannot focus null world");
            return;
        }

        if (world.IsDestroyed)
        {
            LumoraLogger.Warn($"WorldManager: Cannot focus destroyed world '{world.WorldName.Value}'");
            return;
        }

        // Queue focus change (processed in update loop)
        _setWorldFocus = world;
    }

    /// <summary>
    /// Get a world by name.
    /// </summary>
    public World GetWorldByName(string name)
    {
        lock (_worldsLock)
        {
            return _worlds.Find(w => w.WorldName.Value == name);
        }
    }

    /// <summary>
    /// Set world as private overlay (visible only to local user, always on top).
    /// </summary>
    public void PrivateOverlayWorld(World world)
    {
        if (world == null) return;

        world.Focus = World.WorldFocus.PrivateOverlay;

        if (!_privateOverlayWorlds.Contains(world))
        {
            _privateOverlayWorlds.Add(world);
        }

        // Add to managed worlds so it gets a WorldHook
        AddWorld(world);

        LumoraLogger.Log($"WorldManager: Set world '{world.WorldName.Value}' as private overlay");
    }

    /// <summary>
    /// Switch to a world by name (convenience method).
    /// </summary>
    public void SwitchToWorld(World world)
    {
        FocusWorld(world);
    }

    /// <summary>
    /// Remove a world from management (internal use).
    /// </summary>
    private void RemoveWorld(World world)
    {
        lock (_worldsLock)
        {
            _worlds.Remove(world);
        }

        // Fire event
        try
        {
            WorldRemoved?.Invoke(world);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Error in WorldRemoved event: {ex.Message}");
        }
    }

    /// <summary>
    /// Update all worlds. Called by Engine.
    /// Processes world updates, focus changes, and cleanup.
    /// </summary>
    public void Update(double delta)
    {
        if (!_initialized)
            return;

        // Process queued focus change
        ProcessFocusChange();

        // Process queued destructions
        ProcessDestructions();

        // Update all running worlds
        UpdateWorlds(delta);
    }

    /// <summary>
    /// Fixed update for physics and deterministic operations.
    /// Called at fixed intervals defined by the physics timestep.
    /// </summary>
    public void FixedUpdate(double fixedDelta)
    {
        if (!_initialized)
            return;

        // Fixed update all running worlds
        FixedUpdateWorlds(fixedDelta);
    }

    /// <summary>
    /// Late update for cameras and final positioning.
    /// Called after all regular updates have completed.
    /// </summary>
    public void LateUpdate(double delta)
    {
        if (!_initialized)
            return;

        // Late update all running worlds
        LateUpdateWorlds(delta);
    }

    /// <summary>
    /// Process queued focus change.
    /// Handles world focus transitions and notifications.
    /// </summary>
    private void ProcessFocusChange()
    {
        World targetWorld = _setWorldFocus;
        _setWorldFocus = null;

        // Auto-fallback if focused world destroyed
        if (_focusedWorld != null && _focusedWorld.IsDestroyed && targetWorld == null)
        {
            // Find first non-destroyed background world
            lock (_worldsLock)
            {
                targetWorld = _worlds.Find(w => !w.IsDestroyed && w.State == World.WorldState.Running);
            }
        }

        // Apply focus change
        if (targetWorld != null && targetWorld != _focusedWorld)
        {
            // Unfocus previous world
            if (_focusedWorld != null && !_focusedWorld.IsDestroyed)
            {
                _focusedWorld.Focus = World.WorldFocus.Background;
                if (_focusedWorld.LocalUser != null)
                {
                    var user = _focusedWorld.LocalUser;
                    _focusedWorld.RunSynchronously(() =>
                    {
                        user.IsPresent.Value = false;
                    });
                }
                LumoraLogger.Log($"WorldManager: Unfocused world '{_focusedWorld.WorldName.Value}'");
            }

            // Focus new world
            _focusedWorld = targetWorld;
            _focusedWorld.Focus = World.WorldFocus.Focused;
            if (_focusedWorld.LocalUser != null)
            {
                var user = _focusedWorld.LocalUser;
                _focusedWorld.RunSynchronously(() =>
                {
                    user.IsPresent.Value = true;
                });
            }

            // Update Engine's FocusManager
            if (_engine?.FocusManager != null)
            {
                _engine.FocusManager.SwitchToWorld(_focusedWorld);
            }

            // Fire event
            try
            {
                WorldFocused?.Invoke(_focusedWorld);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"WorldManager: Error in WorldFocused event: {ex.Message}");
            }

            LumoraLogger.Log($"WorldManager: Focused world '{_focusedWorld.WorldName.Value}'");
        }
    }

    /// <summary>
    /// Process queued world destructions.
    /// Safely disposes and removes queued worlds.
    /// </summary>
    private void ProcessDestructions()
    {
        lock (_destroyWorlds)
        {
            if (_destroyWorlds.Count == 0)
                return;

            foreach (var world in _destroyWorlds)
            {
                if (world.IsDisposed)
                    continue;

                try
                {
                    world.Dispose();

                    // Clear focus if this was focused world
                    if (_focusedWorld == world)
                    {
                        _focusedWorld = null;
                    }

                    // Remove from list
                    RemoveWorld(world);

                    LumoraLogger.Log($"WorldManager: Destroyed world '{world.WorldName.Value}'");
                }
                catch (Exception ex)
                {
                    LumoraLogger.Error($"WorldManager: Error disposing world: {ex.Message}");
                }
            }

            _destroyWorlds.Clear();
        }

        // Clean up any destroyed worlds from main list
        lock (_worldsLock)
        {
            _worlds.RemoveAll(w => w.IsDestroyed);
        }
    }

    /// <summary>
    /// Update all running worlds.
    /// Runs update cycle for all active worlds.
    /// </summary>
    private void UpdateWorlds(double delta)
    {
        // Get snapshot of running worlds
        List<World> runningWorlds = new List<World>();
        lock (_worldsLock)
        {
            foreach (var world in _worlds)
            {
                if (world.State == World.WorldState.Running && !world.IsDestroyed)
                {
                    runningWorlds.Add(world);
                }
            }
        }

        // Update each world
        foreach (var world in runningWorlds)
        {
            try
            {
                world.Update(delta);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"WorldManager: Error updating world '{world.WorldName.Value}': {ex}");
            }
        }
    }

    /// <summary>
    /// Fixed update all running worlds.
    /// Runs fixed update cycle for physics on all active worlds.
    /// </summary>
    private void FixedUpdateWorlds(double fixedDelta)
    {
        // Get snapshot of running worlds
        List<World> runningWorlds = new List<World>();
        lock (_worldsLock)
        {
            foreach (var world in _worlds)
            {
                if (world.State == World.WorldState.Running && !world.IsDestroyed)
                {
                    runningWorlds.Add(world);
                }
            }
        }

        // Fixed update each world
        foreach (var world in runningWorlds)
        {
            try
            {
                world.FixedUpdate(fixedDelta);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"WorldManager: Error in fixed update for world '{world.WorldName.Value}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Late update all running worlds.
    /// Runs late update cycle for cameras on all active worlds.
    /// </summary>
    private void LateUpdateWorlds(double delta)
    {
        // Get snapshot of running worlds
        List<World> runningWorlds = new List<World>();
        lock (_worldsLock)
        {
            foreach (var world in _worlds)
            {
                if (world.State == World.WorldState.Running && !world.IsDestroyed)
                {
                    runningWorlds.Add(world);
                }
            }
        }

        // Late update each world
        foreach (var world in runningWorlds)
        {
            try
            {
                world.LateUpdate(delta);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"WorldManager: Error in late update for world '{world.WorldName.Value}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Dispose the WorldManager and all worlds.
    /// </summary>
    public void Dispose()
    {
        LumoraLogger.Log("WorldManager: Disposing...");

        // Destroy all worlds
        lock (_worldsLock)
        {
            foreach (var world in _worlds)
            {
                try
                {
                    world?.Dispose();
                }
                catch (Exception ex)
                {
                    LumoraLogger.Error($"WorldManager: Error disposing world: {ex.Message}");
                }
            }
            _worlds.Clear();
        }

        lock (_destroyWorlds)
        {
            _destroyWorlds.Clear();
        }

        _focusedWorld = null;
        _initialized = false;
    }
}
