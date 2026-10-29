// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Helpers;
using Lumora.Core.Localization;
using Lumora.Core.Networking.Session;
using Lumora.Core.Templates;
using Lumora.Nexus.Cloud;
using World = Lumora.Core.World;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Management;

public class WorldManager : IDisposable
{
    private readonly List<World> _worlds = new();
    private readonly List<World> _destroyWorlds = new();
    private readonly List<World> _privateOverlayWorlds = new();
    private readonly object _worldsLock = new object();

    // Per-pass snapshots of _worlds. One buffer each because the passes are separate calls off the same
    // main-loop thread (Engine.Update runs Update then the fixed steps, LateUpdate follows) and never nest,
    // so each pass owns its buffer for the whole pass. -xlinka
    private readonly List<World> _updateScratch = new();
    private readonly List<World> _fixedUpdateScratch = new();
    private readonly List<World> _lateUpdateScratch = new();
    private readonly List<World> _pendingStartScratch = new();

    private World _focusedWorld = null!;
    private World _userspaceWorld = null!;
    private World _setWorldFocus = null!;
    private bool _initialized = false;
    private Engine _engine = null!;

    // Background worlds (Focus == Background: not the focused world, not an overlay) still have to tick for network
    // sync + persistence, but they don't need to run their full component/hook update EVERY frame - doing so is why
    // FPS tanks with several worlds open (you pay N x the per-frame CPU for worlds you can't even see). Throttle
    // their main update to this rate; physics + late-update are skipped for them entirely (nothing's rendering
    // them). Focused/Overlay/PrivateOverlay worlds are never throttled. -xlinka
    private const double BackgroundWorldTickHz = 10.0;
    private readonly Dictionary<World, double> _backgroundUpdateAccum = new();

    // A world is throttleable only once the SESSION IS RUNNING NORMALLY: some world holds focus and it
    // isn't this one. Every world defaults to Background at construction and focus is only assigned
    // later - throttling during that boot window starved the userspace/home worlds to 10Hz with no
    // physics, so the pointer rig and the user's own Root never spawned (watchdog re-fired spawn
    // forever, leaking slots until the GPU died). The userspace world is never throttled, period. -xlinka
    // A world whose focus is being held while it settles is about to be the one in view, so it drains at
    // full rate with its physics and late pass rather than at the background rate. -xlinka
    private bool IsThrottledBackgroundWorld(World world)
        => world.Focus == World.WorldFocus.Background
           && _focusedWorld != null && !_focusedWorld.IsDestroyed
           && !ReferenceEquals(world, _focusedWorld)
           && !ReferenceEquals(world, _userspaceWorld)
           && !ReferenceEquals(world, _setWorldFocus);

    // A world that has just come up keeps its first focus request waiting while it settles (World.IsSettling),
    // so the drain of its arrival runs behind the world the user is standing in instead of in front of them.
    // Only a world's first focus waits, only while there is another live world to wait in, and never longer
    // than SettleTimeoutSeconds. Joins, hosted worlds and opened saves all come through here. -xlinka
    private const double SettleTimeoutSeconds = 15.0;
    private readonly HashSet<World> _settledWorlds = new();
    private World _settlingWorld = null!;
    private double _settlingFor;

    public IWorldManagerHook Hook { get; set; } = null!;

    public event Action<World> WorldAdded = null!;
    public event Action<World> WorldRemoved = null!;
    public event Action<World> WorldFocused = null!;

    public World FocusedWorld => _focusedWorld;

    // A focus request that is being HELD because its world has not finished assembling. The request
    // stays queued rather than dropping the user into a half-built world, which is correct but silent -
    // so the loading overlay reads this to say what everyone is waiting on. -xlinka
    public World PendingFocusWorld => _setWorldFocus;

    // The world whose focus is held right now while it settles, if any.
    public World SettlingWorld => _settlingWorld;

    // The world the user is waiting on right now, if any: a held focus request first, otherwise the
    // world they are standing in while it is still building itself. Null when nothing is loading.
    public World LoadingWorld
    {
        get
        {
            var queued = _setWorldFocus;
            if (queued != null && !queued.IsDestroyed && (queued.IsLoading || ReferenceEquals(queued, _settlingWorld)))
                return queued;
            var focused = _focusedWorld;
            if (focused != null && !focused.IsDestroyed && focused.IsLoading)
                return focused;
            return null!;
        }
    }

    public World UserspaceWorld
    {
        get => _userspaceWorld;
        set
        {
            if (value != null && !ComponentExecutionPolicy.IsUserspace(value))
                throw new InvalidOperationException("Only engine-created private userspace can own personal controls.");
            _userspaceWorld = value!;
            if (_userspaceWorld != null)
            {
                // Userspace is always a private overlay
                PrivateOverlayWorld(_userspaceWorld);
                Components.WorldLoadIndicator.Attach(_userspaceWorld);
            }
        }
    }

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

    // Any world still assembling itself, focused or not.
    public bool AnyWorldLoading()
    {
        lock (_worldsLock)
        {
            for (int i = 0; i < _worlds.Count; i++)
            {
                var world = _worlds[i];
                if (!world.IsDestroyed && world.IsLoading)
                    return true;
            }
        }
        return false;
    }

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

    public World StartLocal(string name, string templateName = "", Action<World> init = null!)
    {
        try
        {
            LumoraLogger.Log($"WorldManager: Starting local world '{name}' with template '{templateName}'");

            var world = World.LocalWorld(_engine, name, (w) =>
            {
                WorldTemplates.ApplyTemplate(w, templateName);
                init?.Invoke(w);
            });

            AddWorld(world);

            LumoraLogger.Log($"WorldManager: Local world '{name}' started successfully");
            return world;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Failed to start local world '{name}': {ex}");
            return null!;
        }
    }

    public World StartSession(string name, ushort port, string hostUserName = null!, string templateName = "", Action<World> init = null!)
    {
        return StartSession(name, port, hostUserName, templateName, SessionVisibility.Private, 16, init);
    }

    public World StartSession(
        string name,
        ushort port,
        string hostUserName,
        string templateName,
        SessionVisibility visibility,
        int maxUsers,
        Action<World> init = null!,
        WorldMode mode = WorldMode.Builder,
        GroupHosting? group = null)
    {
        try
        {
            LumoraLogger.Log($"WorldManager: Starting session '{name}' on port {port} with template '{templateName}', visibility {visibility}, max {maxUsers}, mode {mode}");

            var world = World.StartSession(_engine, name, port, hostUserName, visibility, maxUsers,
                w => ApplySessionSetup(w, templateName, init, mode, group));

            AddWorld(world);

            LumoraLogger.Log($"WorldManager: Session '{name}' started successfully on port {port}");
            return world;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Failed to start session '{name}': {ex}");
            return null!;
        }
    }

    // Same as StartSession, but the world's content is prepared off-thread (a file read plus a parse, say)
    // and integrated once it lands; the world is created up front and held pre-Running until then, so the
    // frame keeps drawing instead of freezing for the whole load. -xlinka
    public World StartSessionDeferred<T>(
        string name,
        ushort port,
        string hostUserName,
        string templateName,
        SessionVisibility visibility,
        int maxUsers,
        Func<T> prepare,
        Action<World, T> integrate,
        WorldMode mode = WorldMode.Builder)
    {
        try
        {
            LumoraLogger.Log($"WorldManager: Starting session '{name}' on port {port} with template '{templateName}', visibility {visibility}, max {maxUsers}, mode {mode}");

            var world = World.StartSessionDeferred(_engine, name, port, hostUserName, visibility, maxUsers, prepare,
                (w, payload) => ApplySessionSetup(w, templateName, w2 => integrate(w2, payload), mode, null));

            AddWorld(world);

            LumoraLogger.Log($"WorldManager: Session '{name}' created on port {port}, waiting on its content");
            return world;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Failed to start session '{name}': {ex}");
            return null!;
        }
    }

    // Template + caller init + mode stamping, in the order both start paths need: before the host user
    // exists and before the world runs.
    private static void ApplySessionSetup(World w, string templateName, Action<World>? init, WorldMode mode,
        GroupHosting? group)
    {
        WorldTemplates.ApplyTemplate(w, templateName);
        FinishSessionSetup(w, init, mode, group);
    }

    // Everything of the setup after the template, for both the one-call build and the staged one.
    private static void FinishSessionSetup(World w, Action<World>? init, WorldMode mode, GroupHosting? group,
        World.WorldAccessLevel? access = null)
    {
        init?.Invoke(w);
        // A NEW world gets the host-picked mode; a world LOADED by init (e.g. a saved world)
        // already has its WorldSettings (with its own mode) - don't create a second one or
        // clobber the saved mode. Set before StartRunning so the permission preset applies.
        // Settings the template build attached by reading the mode early are still defaults, so they
        // take the host's pick too; otherwise a Social or Event host came up Builder. -xlinka
        var settings = w.RootSlot?.GetComponent<WorldSettings>();
        if (settings == null || settings.SeededByEngine)
            w.Mode = mode;
        // Advertise the world's mode in the session listing so the browser can tag it.
        WorldModePermissions.StampModeTag(w.Session?.Metadata?.Tags, w.Mode);
        if (group == null && access.HasValue)
            SeedAccessLevel(w, access.Value);
        ApplyGroupHosting(w, group);
    }

    // The join level a world is hosted at, written before it runs for the same reason the group's is: the
    // live handler would re-advertise mid-create. The session's start only knows four visibilities, so
    // Friends+ needs this to land as itself, and a saved world needs it over the level its file carried.
    // The listing is moved to match when it is not already there. -xlinka
    private static void SeedAccessLevel(World w, World.WorldAccessLevel level)
    {
        var config = w?.Configuration;
        if (w == null || config == null)
            return;

        var visibility = WorldSettings.ToVisibility(level);
        using (w.EnterConfigEdit())
        {
            config.AccessLevel.Value = level;
            config.IsPublic.Value = visibility == SessionVisibility.Public;
        }
        if (w.Session?.Metadata is { } metadata && metadata.Visibility != visibility)
            w.Session.SetVisibility(visibility);
    }

    // The group half of hosting, applied here so it lands next to the mode tag and before the world runs.
    //
    // AccessLevel is written PRE-RUNNING on purpose: its live handler re-advertises the session, and
    // firing that mid-create would fight the visibility the session was started with. The session itself
    // was started Public whichever tier this is, because members have to be able to find a members-only
    // world before the door can refuse anybody else. -xlinka
    private static void ApplyGroupHosting(World w, GroupHosting? group)
    {
        if (w == null || group == null || string.IsNullOrWhiteSpace(group.GroupId))
            return;

        var config = w.Configuration;
        if (config == null)
            return;

        // Hosting is an engine path, so it seeds the config inside the world's edit scope. -xlinka
        using (w.EnterConfigEdit())
        {
            config.HostGroupId.Value = group.GroupId;
            config.AccessLevel.Value = group.MembersOnly
                ? World.WorldAccessLevel.GroupMembers
                : World.WorldAccessLevel.GroupPublic;
        }
        GroupSessionTags.Stamp(w.Session?.Metadata?.Tags, group.GroupId, group.GroupTag);

        var root = w.RootSlot;
        if (root != null && root.GetComponent<Components.GroupHostRoster>() == null)
            root.AttachComponent<Components.GroupHostRoster>();
    }

    private static string LocalHostName => Engine.Current?.LocalUserName ?? Environment.MachineName;

    // Picks a free local UDP port and hosts under this machine's own name. Returns the new world, or null on failure.
    //
    // A group world always registers publicly, whichever group tier it is on: the browser's Groups filter
    // is how a member finds it and the door is what keeps everybody else out. Group+ is not offered by
    // anything that calls this, because with no contacts system it would be the same world as members
    // only wearing a different word.
    //
    // The access level is the create form's pick, which starts out at the account's default join level. A group
    // world takes the group's tier from the hosting record instead. -xlinka
    public World HostNewWorld(string templateName, string worldName, World.WorldAccessLevel access, int maxUsers,
        WorldMode mode = WorldMode.Builder, GroupHosting? group = null)
    {
        ushort port = SimpleIpHelpers.HostingPortUdp();
        access = SettingsCatalog.DefaultJoinLevel.Clamp(access);
        var announced = group != null ? SessionVisibility.Public : WorldSettings.ToVisibility(access);
        var world = StartTemplateSession(worldName, port, LocalHostName, templateName, announced, System.Math.Max(1, maxUsers), mode, group, access);
        if (world != null)
            SwitchToWorld(world);
        return world!;
    }

    // A new world built from its template into the world held before it runs, a few steps a frame, the way a
    // saved world is loaded, so hosting one never freezes the frame it was asked for in. The mode and group
    // setup land after the last step, still before the world runs. -xlinka
    private World StartTemplateSession(string name, ushort port, string hostUserName, string templateName,
        SessionVisibility visibility, int maxUsers, WorldMode mode, GroupHosting? group, World.WorldAccessLevel access)
    {
        try
        {
            LumoraLogger.Log($"WorldManager: Starting session '{name}' on port {port} with template '{templateName}', visibility {visibility}, max {maxUsers}, mode {mode}");

            var world = World.StartSessionDeferred(_engine, name, port, hostUserName, visibility, maxUsers, () => true,
                (w, _) => WorldTemplates.ApplyTemplateStaged(w, templateName, () => FinishSessionSetup(w, null, mode, group, access)));

            AddWorld(world);

            LumoraLogger.Log($"WorldManager: Session '{name}' created on port {port}, building its content");
            return world;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Failed to start session '{name}': {ex}");
            return null!;
        }
    }

    // Builds it into a blank world (so a template's default content isn't duplicated) and loads the save; the
    // world's mode comes from the file. Returns the new world, or null on failure. A save that is not a cloud
    // record (a file on this machine) loses another account's sealed objects on the way in.
    //
    // Opening a save hosts a new session of it, so it opens at the account's default join level unless the
    // caller names one (the favourite home stays private, as the home does). The level the file was saved at
    // was never what the listing said anyway: it opened unlisted whatever the Session screen showed. A save
    // made while hosted for a group keeps its own, because opening it here is not hosting it for the group.
    // -xlinka
    public World OpenSavedWorld(string path, string worldName = null!, bool fromCloudRecord = false,
        World.WorldAccessLevel? access = null)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return null!;

        var name = string.IsNullOrEmpty(worldName) ? System.IO.Path.GetFileNameWithoutExtension(path) : worldName;
        var level = SettingsCatalog.DefaultJoinLevel.Clamp(access ?? EngineSettings.DefaultJoinLevel);
        ushort port = SimpleIpHelpers.HostingPortUdp();
        // Read + decompress + parse on a task, integrate on the world thread once it lands.
        var world = StartSessionDeferred(name, port, LocalHostName, "", SessionVisibility.Private,
            16,
            () => Persistence.WorldStorage.ReadTree(path),
            (w, tree) => Persistence.WorldStorage.IntegrateTreeStaged(w, tree, path, loaded =>
            {
                if (loaded && !fromCloudRecord)
                    Security.ItemSeals.DropForeignOnLoad(w.RootSlot);
                if (string.IsNullOrEmpty(w.Configuration?.HostGroupId.Value))
                    SeedAccessLevel(w, level);
            }));
        if (world != null)
            SwitchToWorld(world);
        return world!;
    }

    public World JoinSession(string name, string address, ushort port)
    {
        // Plain host+port -> assume LNL (the only scheme that's addressed this way).
        return JoinSession(name, new UriBuilder("lnl", address, port).Uri);
    }

    // The connect path resolves the transport from the scheme (NetworkManagerRegistry.FindForUri), so this
    // works for any registered manager - which is why Discord Ask-to-Join hands us the whole URI.
    public World JoinSession(string name, Uri uri)
    {
        // The loading service holds such a join on its panel; this bare path has no panel, so it only refuses.
        if (AccountEmail.JoinsHeld)
        {
            LumoraLogger.Warn($"WorldManager: not joining {uri}, the account's email is not verified");
            return null!;
        }
        try
        {
            LumoraLogger.Log($"WorldManager: Joining session {uri}");
            var world = World.JoinSession(_engine, name, uri);
            AddWorld(world);
            LumoraLogger.Log($"WorldManager: Successfully joined session {uri}");
            return world;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Failed to join session {uri}: {ex.Message}");
            return null!;
        }
    }

    public async Task<World> JoinSessionAsync(string name, string address, ushort port)
    {
        if (await EmailJoinGate.HoldsAsync())
        {
            LumoraLogger.Warn($"WorldManager: not joining {address}:{port}, the account's email is not verified");
            return null!;
        }
        try
        {
            LumoraLogger.Log($"WorldManager: Joining session at {address}:{port}");

            var uri = new UriBuilder("lnl", address, port).Uri;
            var world = await World.JoinSessionAsync(_engine, name, uri);
            if (world == null)
            {
                LumoraLogger.Error($"WorldManager: Failed to join session at {address}:{port}");
                return null!;
            }

            AddWorld(world);
            LumoraLogger.Log($"WorldManager: Successfully joined session at {address}:{port}");
            return world;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Failed to join session at {address}:{port}: {ex.Message}");
            return null!;
        }
    }

    public void AddWorld(World world)
    {
        if (world == null)
            return;

        world.WorldManager = this;

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

        GcPacer.WorldArriving();

        try
        {
            WorldAdded?.Invoke(world);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Error in WorldAdded event: {ex.Message}");
        }
    }

    // Leaves a joined world the way a lost connection would: focus goes home first, then the world closes.
    public void LeaveWorld(World world)
    {
        if (world == null || world.IsDestroyed || IsLocalHome(world))
            return;
        OnWorldDisconnected(world);
    }

    private void OnWorldDisconnected(World disconnectedWorld)
    {
        LumoraLogger.Log($"WorldManager: World '{disconnectedWorld.WorldName.Value}' disconnected");

        if (_focusedWorld == disconnectedWorld)
        {
            LumoraLogger.Debug("WorldManager: Disconnected world was focused, switching to fallback");

            World fallbackWorld = null!;
            lock (_worldsLock)
            {
                fallbackWorld = _worlds.Find(w => !w.IsDestroyed && 
                                                  w.State == World.WorldState.Running && 
                                                  w.WorldName.Value == "LocalHome")!;

                if (fallbackWorld == null)
                {
                    fallbackWorld = _worlds.Find(w => !w.IsDestroyed && 
                                                      w.State == World.WorldState.Running &&
                                                      w != disconnectedWorld)!;
                }
            }

            if (fallbackWorld != null)
            {
                LumoraLogger.Debug($"WorldManager: Switching focus to fallback world '{fallbackWorld.WorldName.Value}'");
                FocusWorld(fallbackWorld);
            }
            else
            {
                LumoraLogger.Warn("WorldManager: No fallback world available after disconnect");
                _focusedWorld = null!;
            }
        }

        DestroyWorld(disconnectedWorld);
    }

    // The home is where every closed world sends you and what the userspace was set up against, so it stays
    // open for the whole run. Shutdown disposes it directly and never comes through here. -xlinka
    public static bool IsLocalHome(World? world)
        => world != null && world.WorldName?.Value == "LocalHome";

    // Only the words change: lookups and the saved home still go by WorldName "LocalHome". -xlinka
    public static readonly LocaleText LocalHomeTitle = "World.Local".AsLocale("Local");

    public static string DisplayName(World? world)
    {
        if (world == null)
            return string.Empty;
        if (IsLocalHome(world))
            return LocalHomeTitle.Resolve();
        string? name = world.WorldName?.Value;
        return string.IsNullOrEmpty(name) ? world.Name : name;
    }

    public void DestroyWorld(World world)
    {
        if (world == null)
            return;
        if (IsLocalHome(world) && !world.IsDisposed)
        {
            LumoraLogger.Warn("WorldManager: The local home cannot be closed");
            return;
        }

        lock (_destroyWorlds)
        {
            if (!_destroyWorlds.Contains(world))
            {
                _destroyWorlds.Add(world);
                LumoraLogger.Debug($"WorldManager: Queued world '{world.WorldName.Value}' for destruction");
            }
        }
    }

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

    public World GetWorldByName(string name)
    {
        lock (_worldsLock)
        {
            return _worlds.Find(w => w.WorldName.Value == name)!;
        }
    }

    // Visible only to the local user, always on top.
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

        LumoraLogger.Debug($"WorldManager: Set world '{world.WorldName.Value}' as private overlay");
    }

    public void SwitchToWorld(World world)
    {
        FocusWorld(world);
    }

    private void RemoveWorld(World world)
    {
        lock (_worldsLock)
        {
            _worlds.Remove(world);
        }
        _backgroundUpdateAccum.Remove(world); // don't keep a dead world alive via the throttle accumulator
        _settledWorlds.Remove(world);
        if (ReferenceEquals(_settlingWorld, world))
            _settlingWorld = null!;

        try
        {
            WorldRemoved?.Invoke(world);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"WorldManager: Error in WorldRemoved event: {ex.Message}");
        }
    }

    public void Update(double delta)
    {
        if (!_initialized)
            return;

        // Ahead of the focus change: a world whose content just landed finishes starting here, so the
        // focus request queued when it was created lands on a running world in the same frame.
        PumpPendingSessionStarts();

        ProcessFocusChange(delta);

        ProcessDestructions();

        UpdateWorlds(delta);
    }

    private void PumpPendingSessionStarts()
    {
        var pending = _pendingStartScratch;
        pending.Clear();
        lock (_worldsLock)
        {
            for (int i = 0; i < _worlds.Count; i++)
            {
                if (_worlds[i].IsSessionStartPending)
                    pending.Add(_worlds[i]);
            }
        }

        for (int i = 0; i < pending.Count; i++)
        {
            pending[i].TickPendingSessionStart();
        }

        pending.Clear();
    }

    public void FixedUpdate(double fixedDelta)
    {
        if (!_initialized)
            return;

        FixedUpdateWorlds(fixedDelta);
    }

    public void LateUpdate(double delta)
    {
        if (!_initialized)
            return;

        LateUpdateWorlds(delta);
    }

    // Drops a held focus request for this world, if that is the one waiting. A cancelled join uses it.
    public void CancelFocus(World world)
    {
        if (world != null && ReferenceEquals(_setWorldFocus, world))
            _setWorldFocus = null!;
        if (world != null && ReferenceEquals(_settlingWorld, world))
            _settlingWorld = null!;
    }

    private void ProcessFocusChange(double delta)
    {
        World targetWorld = _setWorldFocus;
        _setWorldFocus = null!;

        if (_focusedWorld != null && _focusedWorld.IsDestroyed && targetWorld == null)
        {
            lock (_worldsLock)
            {
                targetWorld = _worlds.Find(w => !w.IsDestroyed && w.State == World.WorldState.Running)!;
            }
        }

        // A world whose content is still being prepared off-thread has no host user yet. Hold the request
        // until it finishes starting rather than focusing a half-built world. -xlinka
        if (targetWorld != null && targetWorld.IsSessionStartPending)
        {
            _setWorldFocus = targetWorld;
            return;
        }

        if (targetWorld != null && targetWorld != _focusedWorld && HoldForSettle(targetWorld, delta))
        {
            _setWorldFocus = targetWorld;
            return;
        }
        if (!ReferenceEquals(targetWorld, _settlingWorld))
            _settlingWorld = null!;

        if (targetWorld != null && targetWorld != _focusedWorld)
        {
            if (_focusedWorld != null && !_focusedWorld.IsDestroyed)
            {
                _focusedWorld.Focus = World.WorldFocus.Background;
                if (_focusedWorld.LocalUser != null)
                {
                    var user = _focusedWorld.LocalUser;
                    _focusedWorld.RunSynchronously(() =>
                    {
                        user.IsPresent.Value = false;
                        // Background worlds tick less often. Cancel a pending locomotion gesture
                        // now, even when focus returns before the next background update.
                        user.Root?.Slot?.GetComponent<Components.LocomotionController>()?.SuspendInput();
                    });
                }
                LumoraLogger.Debug($"WorldManager: Unfocused world '{_focusedWorld.WorldName.Value}'");
            }

            _focusedWorld = targetWorld;
            _focusedWorld.Focus = World.WorldFocus.Focused;
            // Point the tracking space at the new world now. The frame's input pass synced it to the world
            // that was focused then, and this world's queued device positioners run in its update below:
            // without this they place head and hands through the old world's root for one frame, and the
            // character capsule follows them there. -xlinka
            _engine?.InputInterface?.SyncTrackingSpaceToFocusedLocalUser();
            if (_focusedWorld.LocalUser != null)
            {
                var user = _focusedWorld.LocalUser;
                _focusedWorld.RunSynchronously(() =>
                {
                    user.IsPresent.Value = true;
                });
            }

            if (_engine?.FocusManager != null)
            {
                _engine.FocusManager.SwitchToWorld(_focusedWorld);
            }

            try
            {
                WorldFocused?.Invoke(_focusedWorld);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"WorldManager: Error in WorldFocused event: {ex.Message}");
            }

            LumoraLogger.Debug($"WorldManager: Focused world '{_focusedWorld.WorldName.Value}'");
        }
    }

    private bool HoldForSettle(World world, double delta)
    {
        if (_settledWorlds.Contains(world))
            return false;

        if (!ReferenceEquals(_settlingWorld, world))
        {
            _settlingWorld = world;
            _settlingFor = 0.0;
        }
        else
        {
            _settlingFor += delta;
        }

        bool canWait = world.State == World.WorldState.Running && !world.IsDestroyed
                       && _focusedWorld != null && !_focusedWorld.IsDestroyed;
        bool settling = canWait && world.IsSettling;
        if (settling && _settlingFor < SettleTimeoutSeconds)
            return true;

        if (settling)
            LumoraLogger.Warn($"WorldManager: '{world.WorldName.Value}' still settling after {SettleTimeoutSeconds:0} s ({world.DescribeSettle()}), focusing it anyway");
        else if (_settlingFor > 0.0)
            LumoraLogger.Log($"WorldManager: '{world.WorldName.Value}' settled in {_settlingFor:0.00} s");

        _settledWorlds.Add(world);
        _settlingWorld = null!;
        return false;
    }

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

                    if (_focusedWorld == world)
                    {
                        _focusedWorld = null!;
                    }

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

        lock (_worldsLock)
        {
            _worlds.RemoveAll(w => w.IsDestroyed);
        }
    }

    private void UpdateWorlds(double delta)
    {
        var runningWorlds = _updateScratch;
        CollectRunningWorlds(runningWorlds);

        // Update each world. Background worlds are throttled to BackgroundWorldTickHz (accumulate real time, run a
        // single catch-up update when the interval elapses) instead of updating every frame - this is the main
        // fix for the multi-world FPS drop. Focused/overlay worlds update every frame.
        double interval = BackgroundWorldTickHz > 0 ? 1.0 / BackgroundWorldTickHz : 0.0;
        for (int i = 0; i < runningWorlds.Count; i++)
        {
            var world = runningWorlds[i];
            try
            {
                if (IsThrottledBackgroundWorld(world))
                {
                    if (interval <= 0.0)
                        continue; // throttle disabled -> background worlds fully paused
                    double acc = (_backgroundUpdateAccum.TryGetValue(world, out var a) ? a : 0.0) + delta;
                    if (acc < interval)
                    {
                        _backgroundUpdateAccum[world] = acc;
                        continue; // not this frame
                    }
                    _backgroundUpdateAccum[world] = 0.0;
                    world.Update(acc); // pass the accumulated time so the world clock advances correctly
                }
                else
                {
                    _backgroundUpdateAccum.Remove(world); // just focused/overlaid - drop any stale accumulator
                    world.Update(delta);
                }
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"WorldManager: Error updating world '{world.WorldName.Value}': {ex}");
            }
        }
    }

    private void CollectRunningWorlds(List<World> output)
    {
        output.Clear();
        lock (_worldsLock)
        {
            for (int i = 0; i < _worlds.Count; i++)
            {
                var world = _worlds[i];
                if (world.State == World.WorldState.Running && !world.IsDestroyed)
                {
                    output.Add(world);
                }
            }
        }
    }

    private void FixedUpdateWorlds(double fixedDelta)
    {
        var runningWorlds = _fixedUpdateScratch;
        CollectRunningWorlds(runningWorlds);

        // Fixed update each world. Throttled background worlds skip physics entirely - nothing is rendering
        // them, and they resume stepping the moment they're focused. -xlinka
        for (int i = 0; i < runningWorlds.Count; i++)
        {
            var world = runningWorlds[i];
            try
            {
                if (IsThrottledBackgroundWorld(world))
                    continue;
                world.FixedUpdate(fixedDelta);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"WorldManager: Error in fixed update for world '{world.WorldName.Value}': {ex.Message}");
            }
        }
    }

    private void LateUpdateWorlds(double delta)
    {
        var runningWorlds = _lateUpdateScratch;
        CollectRunningWorlds(runningWorlds);

        // Late update each world. Throttled background worlds skip late-update (cameras/render-side
        // follow-ups) - they aren't being rendered, so there's nothing to update late for them. -xlinka
        for (int i = 0; i < runningWorlds.Count; i++)
        {
            var world = runningWorlds[i];
            try
            {
                if (IsThrottledBackgroundWorld(world))
                    continue;
                world.LateUpdate(delta);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"WorldManager: Error in late update for world '{world.WorldName.Value}': {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        LumoraLogger.Log("WorldManager: Disposing...");

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

        _focusedWorld = null!;
        _initialized = false;
    }
}
