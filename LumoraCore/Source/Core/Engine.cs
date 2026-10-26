// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Lumora.Core.Logging;
using Lumora.Core.Management;
using Lumora.Core.Helpers;
using Lumora.Core.Assets;
using Lumora.Core.Coroutines;
using Lumora.Core.Physics;
using Lumora.Core.Persistence;
using Lumora.Core.Templates;
using Lumora.Nexus;
using Lumora.Nexus.Cloud.Cdn;
using Lumora.Nexus.Diagnostics;
using Lumora.Nexus.Cloud;
using Lumora.Nexus.Transport;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

public enum EngineState
{
    NotInitialized,
    Initializing,
    Running,
    ShuttingDown,
    Disposed,
    Failed
}

public enum SubsystemStatus
{
    Pending,
    Initializing,
    Ready,
    Failed,
    Disposed
}

public static class EngineVersion
{
    public const int Major = 0;
    public const int Minor = 1;
    public const int Patch = 0;
    public const string Suffix = "alpha";
    public static readonly string VersionString = $"{Major}.{Minor}.{Patch}-{Suffix}";
    public static readonly DateTime BuildDate = new DateTime(2024, 1, 1);
}

public class EngineMetrics
{
    private readonly Stopwatch _frameTimer = new Stopwatch();
    private readonly Queue<double> _frameTimes = new Queue<double>();
    private const int FrameTimeHistorySize = 120;

    public long TotalFrames { get; private set; }

    // Seconds.
    public double TotalTime { get; private set; }

    // Seconds.
    public double LastFrameTime { get; private set; }

    public double AverageFrameTime { get; private set; }

    public double CurrentFPS => LastFrameTime > 0 ? 1.0 / LastFrameTime : 0;

    public double AverageFPS => AverageFrameTime > 0 ? 1.0 / AverageFrameTime : 0;

    // Bytes.
    public long PeakMemoryUsage { get; private set; }

    public long CurrentMemoryUsage => GC.GetTotalMemory(false);

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

public class Engine : IDisposable
{
    private static Engine _instance = null!;
    private static readonly object _instanceLock = new object();

    private EngineState _state = EngineState.NotInitialized;
    private readonly object _stateLock = new object();
    private bool _hostingLocalHome;
    private int? _localHomePort;
    private World _pendingUserSpaceSetup = null!;
    private readonly Dictionary<string, SubsystemStatus> _subsystemStatus = new Dictionary<string, SubsystemStatus>();
    private Exception _initializationError = null!;

    private readonly Stopwatch _engineTimer = new Stopwatch();
    private readonly EngineMetrics _metrics = new EngineMetrics();
    private double _fixedTimeAccumulator;
    private const double DefaultFixedTimestep = 1.0 / 60.0;

    public WorldManager WorldManager { get; private set; } = null!;
    public WorldLoadingService WorldLoadingService { get; private set; } = null!;
    public FocusManager FocusManager { get; private set; } = null!;
    public Input.InputInterface InputInterface { get; private set; } = null!;
    public AssetManager AssetManager { get; private set; } = null!;
    public GlobalCoroutineManager CoroutineManager { get; private set; } = null!;
    

    // The mixer everything audible goes through. The platform gives it a device after init.
    public Lumora.Core.Audio.AudioSystem AudioSystem { get; private set; } = null!;

    public LumoraClient? CDNClient { get; private set; }
    public ContentCache? ContentCache { get; private set; }

    private Action<Lumora.Nexus.Cloud.Cdn.RepresentedGroupInfo?>? _representedGroupChanged;
    private Action? _accountIdentityChanged;
    private int _accountRefresh;

    // The account this machine is signed into, as the accounts service shows it to everyone else. Null
    // while signed out, and until the first answer lands after a sign in.
    public AccountCard? LocalAccount { get; private set; }

    // The name this machine goes by: the account's when signed in, a guest name from the machine key when
    // not. Hosts work it out for themselves from what a joiner proves; this is what we show for our own.
    public string LocalUserName => LocalAccount?.Username
        ?? AccountCard.GuestName(Lumora.Core.Security.MachineIdentity.Local.MachineId);

    public LocalDB? LocalDB { get; set; }

    // Set automatically when a session is created or joined; cleared on dispose. AssetFetcher uses this to pull
    // remote local:// assets from peers.
    public Networking.Session.SessionAssetTransferer? ActiveSessionTransferer { get; set; }

    public string ResourceRoot { get; set; } = null!;

    // A disk path under ResourceRoot back to its res:// form, the reverse of how a res:// URL is
    // resolved. It works whether or not the file exists, and that is the point: in an export the
    // path was built on a root that holds no such file, and the res:// form is the only address the
    // resource pack answers to. Null for a path outside the root, which is a genuine external file.
    // -xlinka
    public string? TryGetResourcePath(string? diskPath)
    {
        var root = ResourceRoot;
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(diskPath))
            return null;

        static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');
        var path = Norm(diskPath);
        var prefix = Norm(root);
        if (path.Length <= prefix.Length + 1
            || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || path[prefix.Length] != '/')
            return null;

        var relative = path.Substring(prefix.Length + 1);
        return relative.Length == 0 ? null : "res://" + relative;
    }

    public event Action<EngineState> OnStateChanged = null!;

    public event Action<double> OnPreUpdate = null!;

    public event Action<double> OnPostUpdate = null!;

    public event Action<string, SubsystemStatus> OnSubsystemStatusChanged = null!;

    #region Static Properties

    public static Engine Instance
    {
        get
        {
            if (_instance == null)
                throw new InvalidOperationException("Engine not initialized. Call Engine.InitializeAsync() first.");
            return _instance;
        }
    }

    public static Engine Current => _instance;

    public static bool IsInitialized => _instance?._state == EngineState.Running;

    public static bool HasInstance => _instance != null;

    public static bool ShowDebug { get; set; } = true;

    public static bool IsDedicatedServer { get; set; } = false;

    public static string Platform => Environment.OSVersion.Platform.ToString();

    // What this build is actually RUNNING on, set once at startup by the platform layer.
    //
    // Environment.OSVersion.Platform above cannot answer this: it reports Unix for Linux AND for
    // Android, which is precisely the distinction anything platform-gated needs. The core has no way to
    // ask the renderer, so the renderer tells it, the same way the texture cache is told which block
    // formats this device has. Defaults to the desktop case so a harness with no platform layer behaves
    // as it always did. -xlinka
    public static Lumora.Core.Platform CurrentPlatform { get; set; } = Lumora.Core.Platform.Windows;

    public static bool IsMobilePlatform => CurrentPlatform == Lumora.Core.Platform.Android;

    // A mobile build that draws to its own screen (a phone) rather than handing every frame to a headset
    // compositor. Set by the platform layer with CurrentPlatform.
    public static bool IsHandheld { get; set; }

    // Linux on the headset is still Linux (Steam, file access and native libraries stay available).
    public static bool IsSteamFrameStandalone { get; set; }

    public static bool IsStandaloneHeadset => IsMobilePlatform && !IsHandheld || IsSteamFrameStandalone;

    public static bool UseConstrainedResources => IsMobilePlatform || IsStandaloneHeadset;

    // Running on Wine or Proton, which reports itself as Windows in every other way. CurrentPlatform
    // stays Windows on purpose, because the Win32 API this process is built against really is what it
    // is talking to; this is the separate question of whether the MACHINE underneath is one. See
    // WinePlatform for what changes on the answer.
    public static bool IsWine => WinePlatform.IsWine;

    public static bool IsProton => WinePlatform.IsProton;

    public static string? WineVersion => WinePlatform.Version;

    public static bool IsEditor { get; set; } = false;

    // Builds the platform layer still has queued for the main thread (textures, meshes, skins handed back
    // from worker threads). Read by the settle gate; the platform sets the provider.
    public static Func<int>? PlatformWorkPendingProvider { get; set; }

    public static int PlatformWorkPending => PlatformWorkPendingProvider?.Invoke() ?? 0;

    // ONE allowance a frame for load work, shared by every queue that is draining a backlog: sync-action
    // bursts, platform nodes behind a load scope, startup and change bursts, hook updates of a world still
    // draining its arrival, queued provider refreshes and the platform's main-thread builds.
    // Each queue's own constant budget, summed, let a join that filled them all at once take ~39 ms a
    // frame against an 11 ms VR frame. Those constants stay as ceilings; between them the queues now get
    // a quarter of the display's frame.
    //
    // Priority is how much of the allowance a queue may see spent before it stops. The decode backlog
    // stops early, so startups, changes and the hooks that put things on screen still get a share after
    // it in the same frame. Every queue keeps its own floor (its first item or its minimum count), so
    // nothing stalls once the allowance is gone. Apart from the platform's build drain, a queue that is
    // not in a burst never looks at it, which keeps the same-frame contract small queues have. The window
    // opens at the top of Update; the platform's drain runs before that in the frame, so it takes
    // whatever the worlds left. -xlinka
    public const double LoadShareActions = 0.4;
    public const double LoadShareHookCreations = 0.5;
    public const double LoadShareStartups = 0.6;
    public const double LoadShareChanges = 0.8;
    public const double LoadShareAll = 1.0;
    private const double LoadAllowanceFraction = 0.25;
    private const double MinLoadAllowanceMs = 2.0;
    private const double MaxLoadAllowanceMs = 6.0;
    private static long _loadAllowanceTicks = LoadTicksFor(60.0);
    private static long _loadSpentTicks;

    // A frame already far longer than the display's, under a heavy simulation say, gives load work the same
    // share of the frame it really is, up to six display frames' worth. Ten thousand spawned props starting at
    // their few milliseconds a frame, at ten frames a second, took half a minute to appear; each of those frames
    // grows by about a quarter instead. Every queue's own ceiling scales with it.
    private const double MaxLoadScale = 6.0;
    private static double _displayFrameMs = 1000.0 / 60.0;
    private static double _loadScale = 1.0;

    public static double LoadScale => _loadScale;

    public static double LoadAllowanceMs => _loadAllowanceTicks * 1000.0 / Stopwatch.Frequency;

    // The headset's rate in VR, the monitor's otherwise. Anything not a real rate is ignored.
    public static void SetDisplayRefreshRate(double hz)
    {
        if (hz > 1.0 && !double.IsNaN(hz) && !double.IsInfinity(hz))
        {
            Interlocked.Exchange(ref _loadAllowanceTicks, LoadTicksFor(hz));
            _displayFrameMs = 1000.0 / hz;
        }
    }

    private static long LoadTicksFor(double hz)
    {
        double ms = System.Math.Clamp(1000.0 / hz * LoadAllowanceFraction, MinLoadAllowanceMs, MaxLoadAllowanceMs);
        return (long)(ms * Stopwatch.Frequency / 1000.0);
    }

    // True once this frame's shared spend plus what the caller ran since startedTicks reaches its share.
    public static bool LoadAllowanceSpent(long startedTicks, double share)
        => Interlocked.Read(ref _loadSpentTicks) + (Stopwatch.GetTimestamp() - startedTicks)
           >= (long)(Interlocked.Read(ref _loadAllowanceTicks) * share * _loadScale);

    public static void ChargeLoad(long startedTicks)
        => Interlocked.Add(ref _loadSpentTicks, Stopwatch.GetTimestamp() - startedTicks);

    private static void OpenLoadWindow() => Interlocked.Exchange(ref _loadSpentTicks, 0L);

    #endregion

    #region Instance Properties

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

    public bool IsShuttingDown => State == EngineState.ShuttingDown;

    public bool IsRunning => State == EngineState.Running;

    // Seconds.
    public double TotalTime => _metrics.TotalTime;

    public long FrameCount => _metrics.TotalFrames;

    public EngineMetrics Metrics => _metrics;

    public double FixedTimestep { get; set; } = DefaultFixedTimestep;

    // Caps the fixed-step catch-up so a long frame can't spiral.
    public int MaxFixedUpdatesPerFrame { get; set; } = 8;

    public bool AutoHostLocalHome { get; set; } = true;

    public bool AutoConnectLocalHome { get; set; } = true;

    public Exception InitializationError => _initializationError;

    public IReadOnlyDictionary<string, SubsystemStatus> SubsystemStatuses => _subsystemStatus;

    #endregion

    #region Initialization

    public Engine()
    {
        lock (_instanceLock)
        {
            if (_instance != null)
                throw new InvalidOperationException("Engine instance already exists. Call Dispose() first.");
            _instance = this;
        }
    }

    // LumoraNexus sits below the engine and cannot see the logger or the version constant, so push both
    // down before anything in it runs. Without this, transport and cloud lines go to the console only and
    // the session directory advertises a placeholder version. -xlinka
    private static void WireNexus()
    {
        NexusRuntime.AppVersion = EngineVersion.VersionString;
        NexusLog.EnableDebug = LumoraLogger.EnableDebug;
        NexusLog.Sink = (level, message) =>
        {
            switch (level)
            {
                case NexusLogLevel.Warn: LumoraLogger.Warn(message); break;
                case NexusLogLevel.Error: LumoraLogger.Error(message); break;
                case NexusLogLevel.Debug: LumoraLogger.Debug(message); break;
                default: LumoraLogger.Log(message); break;
            }
        };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (State != EngineState.NotInitialized)
        {
            LumoraLogger.Warn($"Engine already in state {State}, skipping initialization.");
            return;
        }

        State = EngineState.Initializing;
        _engineTimer.Start();
        EngineWarmUp.Start();

        WireNexus();

        LumoraLogger.Log("=====================================");
        LumoraLogger.Log($"Lumora Engine v{EngineVersion.VersionString}");
        LumoraLogger.Log($"Platform: {Platform}");
        LumoraLogger.Log("=====================================");
        LumoraLogger.Log("Engine Initialization Starting");
        LumoraLogger.Log("=====================================");

        try
        {
            LumoraLogger.Log("Initializing core systems...");

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

            LumoraLogger.Log("Initializing asset systems...");

            await InitializeSubsystem("AssetManager", async () =>
            {
                AssetManager = new AssetManager(this);
                await AssetManager.InitializeAsync();
            }, cancellationToken);

            await InitializeSubsystem("ContentCache", async () =>
            {
                var deviceId = Environment.MachineName;
                CDNClient = new LumoraClient(deviceId, "LumoraVR", EngineVersion.VersionString);
                AccountSettings.Attach(CDNClient);
                InventorySync.Attach(CDNClient);
                OnlinePresence.Attach(CDNClient);
                var cachePath = Lumora.Core.Persistence.PathResolver.Resolve("Cache");
                ContentCache = new ContentCache(CDNClient, cachePath);
                AssetCacheBudget.Start();
                await Task.CompletedTask;
            }, cancellationToken);

            AudioSystem = new Lumora.Core.Audio.AudioSystem();


            // Physics is per-world and delegated to the platform engine (Godot/Jolt), accessed
            // through World.Physics - there is no engine-level physics subsystem to initialize.

            LumoraLogger.Log("Initializing world management...");

            await InitializeSubsystem("WorldManager", async () =>
            {
                WorldManager = new WorldManager();
                await WorldManager.InitializeAsync(this);
                WorldLoadingService = new WorldLoadingService(this);
            }, cancellationToken);

            // Wearing a different group has to show up on the nametag in every world this machine is in,
            // not just the one that happened to be focused when the button was pressed. Hooked here rather
            // than in the cloud client because the client knows nothing about worlds. -xlinka
            if (CDNClient != null)
            {
                _representedGroupChanged = OnRepresentedGroupChanged;
                CDNClient.RepresentedGroupChanged += _representedGroupChanged;
                _accountIdentityChanged = OnAccountIdentityChanged;
                CDNClient.AccountIdentityChanged += _accountIdentityChanged;
            }

            LumoraLogger.Log("Post-initialization setup...");
            ProcessStartupArguments();

            State = EngineState.Running;

            var initTime = _engineTimer.Elapsed.TotalSeconds;
            LumoraLogger.Log("=====================================");
            LumoraLogger.Log($"Engine Initialized Successfully in {initTime:F2}s");
            LumoraLogger.Log($"Subsystems: {_subsystemStatus.Count} initialized");
            LumoraLogger.Log("=====================================");
        }
        catch (OperationCanceledException)
        {
            LumoraLogger.Warn("Engine initialization was cancelled.");
            State = EngineState.Failed;
            _initializationError = new OperationCanceledException("Initialization cancelled");
            throw;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"Engine initialization failed: {ex.Message}");
            LumoraLogger.Error(ex.StackTrace ?? ex.Message);
            State = EngineState.Failed;
            _initializationError = ex;
            lock (_instanceLock) { _instance = null!; }
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
            LumoraLogger.Log($"  [{sw.ElapsedMilliseconds}ms] {name} initialized");
        }
        catch (Exception ex)
        {
            SetSubsystemStatus(name, SubsystemStatus.Failed);
            LumoraLogger.Error($"  [FAILED] {name}: {ex.Message}");
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

    public static string LocalHomeSavePath => Lumora.Core.Persistence.PathResolver.Resolve("home.lworld");

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

        // Load the saved home if one exists (build it into a blank world so the template's default
        // content isn't duplicated); otherwise build the default from the LocalHome template. The save's
        // read, decrypt, decompress and parse run on a task so startup doesn't freeze on them; only the
        // tree integration happens on the world thread, once the parse lands. -xlinka
        var savePath = LocalHomeSavePath;
        var world = File.Exists(savePath)
            ? WorldManager?.StartSessionDeferred(
                "LocalHome", (ushort)_localHomePort.Value, GetHostUserName(), "",
                SessionVisibility.Private, 16,
                () => WorldStorage.ReadTree(savePath),
                (w, tree) => WorldStorage.IntegrateTreeStaged(w, tree, savePath, loaded =>
                {
                    if (loaded)
                    {
                        Templates.LocalHomeWorldTemplate.RemoveRetired(w);
                        return;
                    }
                    LumoraLogger.Warn("Engine: LocalHome save failed to load; falling back to template.");
                    WorldTemplates.ApplyTemplate(w, "LocalHome");
                }))
            : WorldManager?.StartSession("LocalHome", (ushort)_localHomePort.Value, GetHostUserName(), "LocalHome", null!);
        if (world == null)
        {
            LumoraLogger.Error("Engine: Failed to start LocalHome session.");
            return;
        }

        _hostingLocalHome = true;
        LumoraLogger.Log($"Engine: LocalHome hosted on port {_localHomePort.Value}.");

        _pendingUserSpaceSetup = world;
    }

    private void SwitchToLocalHome()
    {
        var world = WorldManager?.GetWorldByName("LocalHome");
        if (world != null)
        {
            WorldManager!.SwitchToWorld(world);
            LumoraLogger.Log("Engine: Switched to LocalHome world.");
        }
        else
        {
            LumoraLogger.Warn("Engine: LocalHome world not found.");
        }
    }

    private string GetHostUserName() => LocalUserName;

    // Fires off the cloud client's thread after a sign in has its key published, or after a sign out.
    // The card is read back from the public record, the same answer any host gets about us, and then every
    // world hears it: where we host, our own user changes in place; where we joined, the host is asked to
    // check the new account. A newer change supersedes an older one still waiting on the service. -xlinka
    private void OnAccountIdentityChanged()
    {
        var client = CDNClient;
        if (client == null)
            return;
        int ticket = System.Threading.Interlocked.Increment(ref _accountRefresh);

        _ = Task.Run(async () =>
        {
            AccountCard? card = null;
            try
            {
                // Our own name needs no published key, only the account id, so a sign in whose key could
                // not be published still names us in the worlds we host.
                string? id = null;
                if (client.IsAuthenticated)
                {
                    var resolved = await client.ResolveAccountUserId();
                    id = resolved.Success ? resolved.Data : null;
                }
                if (!string.IsNullOrEmpty(id))
                {
                    var answer = await client.GetPlatformUserAsync(id);
                    if (answer.Success)
                        card = AccountCard.From(answer.Data);
                }
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"Engine: could not read our own account card ({ex.Message})");
            }
            if (ticket != System.Threading.Volatile.Read(ref _accountRefresh))
                return;

            LocalAccount = card;
            LumoraLogger.Log($"Engine: local identity is now '{LocalUserName}'{(card != null ? " (account)" : " (guest)")}");
            ApplyLocalAccountToWorlds();
        });
    }

    private void ApplyLocalAccountToWorlds()
    {
        var manager = WorldManager;
        if (manager == null)
            return;

        var card = LocalAccount;
        var name = LocalUserName;
        var machineKey = Lumora.Core.Security.MachineIdentity.Local.MachineId;
        var worlds = manager.Worlds;
        for (int i = 0; i < worlds.Count; i++)
        {
            var world = worlds[i];
            if (world == null || world.IsDestroyed)
                continue;
            world.RunSynchronously(() =>
            {
                var user = world.LocalUser;
                if (user == null || user.IsDestroyed)
                    return;
                if (world.IsAuthority)
                {
                    user.ApplyAccount(card, machineKey);
                    if (world.Session?.Metadata is { } metadata)
                        metadata.HostUsername = name;
                }
                else
                {
                    world.Session?.Connections?.SendAccountChange();
                }
            });
        }
    }

    // The card is the host's to write from the account's public record, so wearing a different group is
    // the same as signing in again: our own card is read back, worlds we host change in place, and every
    // host we joined is asked to check the account and read the record anew. -xlinka
    private void OnRepresentedGroupChanged(Lumora.Nexus.Cloud.Cdn.RepresentedGroupInfo? group)
        => OnAccountIdentityChanged();

    public World GetPendingUserSpaceSetup() => _pendingUserSpaceSetup;

    public void ClearPendingUserSpaceSetup() => _pendingUserSpaceSetup = null!;

    public void JoinLocalHome() => SwitchToLocalHome();

    public void JoinServer(string address, int port, string worldName = "RemoteWorld")
    {
        JoinThroughLoader(worldName, new UriBuilder("lnl", address, port).Uri);
    }

    // Joins that do not start in the world browser come through here, so they get the same join
    // indicator and the same focus hold as one that does. Main thread.
    public void JoinThroughLoader(string worldName, Uri uri)
    {
        var loader = WorldLoadingService;
        if (loader == null)
        {
            WorldManager?.JoinSession(worldName, uri);
            return;
        }
        if (loader.JoinSessionAsync(worldName, uri, focusWhenReady: true) == null)
            LumoraLogger.Warn($"Engine: join of {uri} not started, another join is still in progress");
    }

    // Join via NAT punch-through: punch a hole to the session host through the relay
    // server, then connect directly to the resolved endpoint.
    public void JoinNatServer(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            LumoraLogger.Warn("Engine: NAT join called with an empty session identifier.");
            return;
        }

        var addr = Networking.Session.Session.SessionServerAddress;
        var port = Networking.Session.Session.SessionServerPort;
        LumoraLogger.Log($"Engine: NAT punch join for session '{identifier}' via {addr}:{port}");

        var client = new SessionServerClient(addr, port);
        var joined = false;

        client.OnNATPunchSuccess += ep =>
        {
            if (joined) return;
            joined = true;

            LumoraLogger.Log($"Engine: NAT punch succeeded -> {ep}; connecting directly.");
            var wm = WorldManager;

            // The punch callback runs on the client's poll task; hop to the main
            // update thread to create/join the world.
            var punched = new UriBuilder("lnl", ep.Address.ToString(), ep.Port).Uri;
            var hop = wm?.FocusedWorld ?? wm?.UserspaceWorld;
            if (hop != null && !hop.IsDestroyed)
                hop.RunSynchronously(() => JoinThroughLoader("RemoteWorld", punched));
            else
                JoinThroughLoader("RemoteWorld", punched);

            // Hold the punch socket open briefly so the NAT mapping stays warm while
            // the direct connection establishes, then release it. (Hard NATs that
            // can't reuse the hole for a fresh socket fall back to relay.)
            _ = Task.Delay(5000).ContinueWith(_ => client.Dispose(), TaskScheduler.Default);
        };

        _ = client.RequestNATPunchAsync(identifier).ContinueWith(t =>
        {
            if (t.IsFaulted)
                LumoraLogger.Warn($"Engine: NAT punch request failed: {t.Exception?.GetBaseException().Message}");
        }, TaskScheduler.Default);
    }

    // Join via relay server: tunnel the session through the relay when a direct or
    // punched path isn't available.
    public void JoinNatServerRelay(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            LumoraLogger.Warn("Engine: Relay join called with an empty session identifier.");
            return;
        }

        // Make sure the relay transport is available (Register dedups by type).
        NetworkManagerRegistry.Register(new Networking.RelayNetworkManager());

        var addr = Networking.Session.Session.SessionServerAddress;
        var port = Networking.Session.Session.SessionServerPort;
        LumoraLogger.Log($"Engine: relay join for session '{identifier}' via {addr}:{port}");

        var relayUri = new Uri($"{Networking.RelayNetworkManager.SCHEME}://relay/{Uri.EscapeDataString(identifier)}");
        JoinThroughLoader("RemoteWorld", relayUri);
    }

    #endregion

    #region Update Loop

    public void Update(double delta)
    {
        if (State != EngineState.Running)
            return;

        _metrics.BeginFrame();
        _loadScale = System.Math.Clamp(delta * 1000.0 / _displayFrameMs, 1.0, MaxLoadScale);
        OpenLoadWindow();
        OnPreUpdate?.Invoke(delta);

        try
        {
            // Engine-level phase timing. The first capture showed 31% of the frame - 9 ms of a 30 ms
            // frame - sitting in NO phase at all, because everything the world profile measures happens
            // inside WorldManager.Update and this method does four other things around it. An unmeasured
            // third of the frame is not a profiler. -xlinka
            long _ts = System.Diagnostics.Stopwatch.GetTimestamp();
            double _mspt = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double Lap() { long n = System.Diagnostics.Stopwatch.GetTimestamp(); double ms = (n - _ts) * _mspt; _ts = n; return ms; }

            InputInterface?.ProcessInput(delta);
            double msInput = Lap();

            CoroutineManager?.Update((float)delta);
            double msCoroutines = Lap();

            // Before the worlds: an output folds the group volumes into its own gain while it builds
            // its parameters, so those numbers have to be this frame's.
            AudioSystem?.Update();

            WorldManager?.Update(delta);
            double msWorlds = Lap();

            ProcessFixedUpdates(delta);
            InputInterface?.SyncTrackingSpaceToFocusedLocalUser();
            double msFixed = Lap();

            AssetManager?.Update((float)delta);
            double msAssets = Lap();

            LastEnginePhases = new EnginePhaseProfile(msInput, msCoroutines, msWorlds, msFixed, msAssets);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"Engine Update error: {ex.Message}");
            if (ShowDebug) LumoraLogger.Error(ex.StackTrace ?? ex.Message);
        }

        OnPostUpdate?.Invoke(delta);
        GcPacer.EndFrame();
        _metrics.EndFrame(delta);
    }

    // What Engine.Update spends its time on, outside any single world. Published every frame.
    //
    // InputMs is the input pass itself (driver polling, action evaluation, and QUEUEING the receiver
    // callbacks) - the receivers' actual execution is charged to the world's sync phase, because
    // RunSynchronously defers them there.
    public readonly struct EnginePhaseProfile
    {
        public readonly double InputMs;
        public readonly double CoroutinesMs;
        public readonly double WorldsMs;
        public readonly double FixedMs;
        public readonly double AssetsMs;

        public EnginePhaseProfile(double input, double coroutines, double worlds, double fixedUpdates, double assets)
        {
            InputMs = input;
            CoroutinesMs = coroutines;
            WorldsMs = worlds;
            FixedMs = fixedUpdates;
            AssetsMs = assets;
        }

        public double TotalMs => InputMs + CoroutinesMs + WorldsMs + FixedMs + AssetsMs;
    }

    public EnginePhaseProfile LastEnginePhases { get; private set; }

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
        WorldManager?.FixedUpdate(fixedDelta);
    }

    // Called automatically by Update; can also be driven manually for a custom fixed step.
    public void FixedUpdate(double fixedDelta)
    {
        if (State != EngineState.Running)
            return;

        FixedUpdateInternal(fixedDelta);
    }

    public void LateUpdate(double delta)
    {
        if (State != EngineState.Running)
            return;

        WorldManager?.LateUpdate(delta);
    }

    #endregion

    #region Shutdown

    public void RequestShutdown()
    {
        if (State != EngineState.Running)
            return;

        LumoraLogger.Log("Engine: Shutdown requested.");
        Dispose();
    }

    // Raised by RequestQuit. The platform layer subscribes to close the app
    // (which tears the engine down through the normal exit path).
    public event Action? QuitRequested;

    // Request the application quit. Fires QuitRequested rather than disposing
    // inline, so it's safe to call from UI during a world update.
    public void RequestQuit() => QuitRequested?.Invoke();

    public void Dispose()
    {
        if (State == EngineState.Disposed || State == EngineState.NotInitialized)
            return;

        State = EngineState.ShuttingDown;

        LumoraLogger.Log("=====================================");
        LumoraLogger.Log("Engine Shutdown Starting...");
        LumoraLogger.Log("=====================================");

        if (CDNClient != null && _representedGroupChanged != null)
        {
            CDNClient.RepresentedGroupChanged -= _representedGroupChanged;
            _representedGroupChanged = null;
        }
        if (CDNClient != null && _accountIdentityChanged != null)
        {
            CDNClient.AccountIdentityChanged -= _accountIdentityChanged;
            _accountIdentityChanged = null;
        }

        // Dispose in reverse initialization order
        DisposeSubsystem("WorldManager", () => { WorldManager?.Dispose(); WorldManager = null!; });
        DisposeSubsystem("AudioSystem", () => { AudioSystem?.Dispose(); AudioSystem = null!; });
        // The queue stops before the client it sends through goes; what it had not sent waits for next run.
        DisposeSubsystem("InventorySync", () => InventorySync.Use(null));
        DisposeSubsystem("OnlinePresence", OnlinePresence.Stop);
        DisposeSubsystem("ContentCache", () => { AssetCacheBudget.Stop(); ContentCache?.Dispose(); ContentCache = null; CDNClient?.Dispose(); CDNClient = null; });
        DisposeSubsystem("AssetManager", () => { AssetManager?.Dispose(); AssetManager = null!; });
        DisposeSubsystem("CoroutineManager", () => { CoroutineManager?.Dispose(); CoroutineManager = null!; });
        DisposeSubsystem("InputInterface", () => { InputInterface?.Dispose(); InputInterface = null!; });
        DisposeSubsystem("FocusManager", () => { FocusManager = null!; });

        _engineTimer.Stop();
        var runTime = _engineTimer.Elapsed.TotalSeconds;

        State = EngineState.Disposed;

        lock (_instanceLock)
        {
            if (_instance == this)
                _instance = null!;
        }

        LumoraLogger.Log("=====================================");
        LumoraLogger.Log($"Engine Shutdown Complete");
        LumoraLogger.Log($"Total runtime: {runTime:F2}s, Frames: {_metrics.TotalFrames}");
        LumoraLogger.Log("=====================================");
    }

    private void DisposeSubsystem(string name, Action disposer)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            disposer();
            sw.Stop();
            SetSubsystemStatus(name, SubsystemStatus.Disposed);
            LumoraLogger.Log($"  [{sw.ElapsedMilliseconds}ms] {name} disposed");
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"  [ERROR] {name} disposal: {ex.Message}");
        }
    }

    #endregion

    #region Utility

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
