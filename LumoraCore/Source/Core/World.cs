using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core.Networking.Session;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Components;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

/// <summary>
/// Represents a World instance containing Slots, Components, and Users.
/// </summary>
public class World
{
	public enum WorldState
	{
		/// <summary>
		/// World has been created but not initialized yet.
		/// </summary>
		Created,

		/// <summary>
		/// Setting up network connections and listeners.
		/// </summary>
		InitializingNetwork,

		/// <summary>
		/// Waiting for authority to grant join permission (client only).
		/// </summary>
		WaitingForJoinGrant,

		/// <summary>
		/// Initializing data model (slots, components, users).
		/// </summary>
		InitializingDataModel,

		/// <summary>
		/// World is fully initialized and running.
		/// </summary>
		Running,

		/// <summary>
		/// World failed to initialize or encountered fatal error.
		/// </summary>
		Failed,

		/// <summary>
		/// World has been destroyed and cleaned up.
		/// </summary>
		Destroyed
	}

	public enum InitializationState
	{
		Created,
		InitializingNetwork,
		WaitingForJoinGrant,
		InitializingDataModel,
		Finished,
		Failed
	}

	public enum WorldFocus
	{
		Background,
		Focused,
		Overlay,
		PrivateOverlay
	}

	public enum WorldEvent
	{
		OnFocusChanged,
		OnUserJoined,
		OnUserLeft,
		OnWorldDestroy
	}

	/// <summary>
	/// World statistics and metrics.
	/// </summary>
	public class WorldMetrics
	{
		/// <summary>Total slots in this world.</summary>
		public int SlotCount { get; internal set; }

		/// <summary>Total components in this world.</summary>
		public int ComponentCount { get; internal set; }

		/// <summary>Total sync elements (networked).</summary>
		public int SyncElementCount { get; internal set; }

		/// <summary>Total RefIDs allocated.</summary>
		public long RefIDsAllocated { get; internal set; }

		/// <summary>Network messages sent.</summary>
		public long MessagesSent { get; internal set; }

		/// <summary>Network messages received.</summary>
		public long MessagesReceived { get; internal set; }

		/// <summary>Total bytes sent.</summary>
		public long BytesSent { get; internal set; }

		/// <summary>Total bytes received.</summary>
		public long BytesReceived { get; internal set; }

		/// <summary>Updates processed.</summary>
		public long UpdatesProcessed { get; internal set; }

		/// <summary>Average update time in ms.</summary>
		public double AverageUpdateTimeMs { get; internal set; }

		/// <summary>Peak update time in ms.</summary>
		public double PeakUpdateTimeMs { get; internal set; }

		/// <summary>Render time in ms (from engine).</summary>
		public double RenderTimeMs { get; set; }

		/// <summary>Physics time in ms.</summary>
		public double PhysicsTimeMs { get; set; }

		/// <summary>Video memory usage in bytes.</summary>
		public long VideoMemoryBytes { get; set; }

		/// <summary>Total Godot objects.</summary>
		public int GodotObjectCount { get; set; }

		/// <summary>Total Godot nodes.</summary>
		public int GodotNodeCount { get; set; }

		public override string ToString()
		{
			return $"Slots: {SlotCount}, Components: {ComponentCount}, Users: N/A, " +
			       $"Messages: {MessagesSent}/{MessagesReceived}, Updates: {UpdatesProcessed}";
		}
	}

	/// <summary>
	/// World configuration settings.
	/// </summary>
	public class WorldConfiguration
	{
		/// <summary>Maximum users allowed in this world.</summary>
		public int MaxUsers { get; set; } = 32;

		/// <summary>Whether to allow new users to join.</summary>
		public bool AllowJoin { get; set; } = true;

		/// <summary>Whether the world is publicly visible.</summary>
		public bool IsPublic { get; set; } = false;

		/// <summary>World description.</summary>
		public string Description { get; set; } = "";

		/// <summary>World tags for discovery.</summary>
		public List<string> Tags { get; } = new List<string>();

		/// <summary>Whether to persist world state.</summary>
		public bool EnablePersistence { get; set; } = false;

		/// <summary>Auto-save interval in seconds (0 = disabled).</summary>
		public float AutoSaveInterval { get; set; } = 0;

		/// <summary>Maximum world size in MB.</summary>
		public int MaxWorldSizeMB { get; set; } = 512;
	}

	private readonly HashSet<IWorldElement> _dirtyElements = new();
	private readonly Dictionary<string, List<Slot>> _slotsByTag = new();
	private readonly List<Slot> _rootSlots = new();
	private readonly List<User> _users = new();
	private readonly List<User> _joinedUsers = new();
	private readonly List<User> _leftUsers = new();

	// Network replicators for world structure synchronization
	private Networking.Sync.SlotBag? _slotBag;
	private Networking.Sync.UserBag? _userBag;
	private readonly List<IWorldEventReceiver>[] _worldEventReceivers;
	private WorldState _state = WorldState.Created;
	private InitializationState _initState = InitializationState.Created;
	private Session _session;
	private HookManager _hookManager;
	private TrashBin _trashBin;
	private RefIDAllocator _refIDAllocator;
	private WorldFocus _focus = WorldFocus.Background;
	private HookTypeRegistry _hookTypes;
	private UpdateManager _updateManager;
	private Queue<Action> _synchronousActions = new Queue<Action>();
	private object _syncLock = new object();
	private readonly WorldMetrics _metrics = new WorldMetrics();
	private readonly WorldConfiguration _configuration = new WorldConfiguration();

	// Static global hook type registry (shared across all worlds)
	private static HookTypeRegistry _staticHookTypes = new HookTypeRegistry();

	// Platform hook for world rendering
	public IWorldHook Hook { get; set; }

	// Godot scene access - set by WorldHook
	public object GodotSceneRoot { get; set; }

	// Reference to the WorldManager that owns this World
	public Management.WorldManager WorldManager { get; internal set; }

	private static int _worldEventTypeCount = Enum.GetValues(typeof(WorldEvent)).Length;

	/// <summary>
	/// Global hook type registry (static, shared across all worlds).
	/// </summary>
	public static HookTypeRegistry HookTypes => _staticHookTypes;

	/// <summary>
	/// Event fired when world state changes.
	/// </summary>
	public event Action<WorldState, WorldState>? OnStateChanged;

	/// <summary>
	/// Current state of the World.
	/// </summary>
	public WorldState State => _state;

	/// <summary>
	/// Detailed initialization state for the World.
	/// </summary>
	public InitializationState InitState => _initState;

	/// <summary>
	/// The root Slot of this World.
	/// </summary>
	public Slot RootSlot { get; private set; }

	/// <summary>
	/// Display name of this World.
	/// </summary>
	public Sync<string> WorldName { get; private set; }

	/// <summary>
	/// Session ID for network identification.
	/// </summary>
	public Sync<string> SessionID { get; private set; }

	/// <summary>
	/// The authority ID (host) that validates all changes.
	/// -1 means local-only world.
	/// </summary>
	public int AuthorityID { get; set; } = -1;

	/// <summary>
	/// Whether this instance is the authority (host).
	/// Local ID for checking authority status.
	/// </summary>
	public int LocalID { get; set; } = -1;

	/// <summary>
	/// Whether this instance is the authority (host).
	/// </summary>
	public bool IsAuthority => AuthorityID == -1 || AuthorityID == LocalID;

	/// <summary>
	/// Session for networking.
	/// </summary>
	public Session Session => _session;

	/// <summary>
	/// URLs that can be used to connect to this session.
	/// </summary>
	public IReadOnlyList<Uri> SessionURLs => (IReadOnlyList<Uri>)_session?.Metadata?.SessionURLs ?? Array.Empty<Uri>();

	/// <summary>
	/// Synchronization controller for this world.
	/// </summary>
	public SyncController SyncController { get; private set; }

	/// <summary>
	/// Reference controller for object lookup and async resolution.
	/// </summary>
	public ReferenceController ReferenceController { get; private set; }

	/// <summary>
	/// Worker manager for type encoding/decoding during sync.
	/// </summary>
	public WorkerManager Workers { get; private set; }

	/// <summary>
	/// Thread-safe hook manager for world modifications.
	/// </summary>
	public HookManager HookManager => _hookManager;

	/// <summary>
	/// Trash bin for temporarily holding deleted objects.
	/// </summary>
	public TrashBin TrashBin => _trashBin;

	/// <summary>
	/// RefID allocator for preventing ID conflicts between users.
	/// </summary>
	public RefIDAllocator RefIDAllocator => _refIDAllocator;

	/// <summary>
	/// Hook type registry for mapping components to hooks (instance property for compatibility).
	/// </summary>
	public HookTypeRegistry InstanceHookTypes => _hookTypes;

	/// <summary>
	/// Update manager for coordinating hook updates.
	/// </summary>
	public UpdateManager UpdateManager => _updateManager;

	/// <summary>
	/// Local user (client's own user).
	/// </summary>
	public User LocalUser { get; private set; }

	/// <summary>
	/// Time scale for the World simulation.
	/// </summary>
	public float TimeScale { get; set; } = 1.0f;

	/// <summary>
	/// Total time this World has been running (in seconds).
	/// </summary>
	public double TotalTime { get; private set; }

	/// <summary>
	/// Local sync tick counter for ordering messages.
	/// Incremented every sync cycle.
	/// </summary>
	public ulong SyncTick { get; private set; }

	/// <summary>
	/// Authority state version for conflict detection.
	/// Incremented whenever the authority makes a state change.
	/// </summary>
	public ulong StateVersion { get; private set; }

	/// <summary>
	/// Whether this world has been destroyed.
	/// </summary>
	public bool IsDestroyed { get; internal set; }

	/// <summary>
	/// Whether this world has been disposed.
	/// </summary>
	public bool IsDisposed { get; private set; }

	/// <summary>
	/// Last frame delta time (seconds).
	/// </summary>
	public float LastDelta { get; private set; }

	/// <summary>
	/// Convenience property for WorldName.Value.
	/// </summary>
	public string Name => WorldName?.Value ?? "Unknown";

	/// <summary>
	/// Whether this world is currently focused.
	/// </summary>
	public bool IsFocused => _focus == WorldFocus.Focused;

	/// <summary>
	/// Number of users in the world.
	/// </summary>
	public int UserCount
	{
		get
		{
			lock (_users)
			{
				return _users.Count;
			}
		}
	}

	/// <summary>
	/// Current focus mode of this world.
	/// </summary>
	public WorldFocus Focus
	{
		get => _focus;
		set
		{
			if (_focus == value) return;
			_focus = value;

			// Notify world hook of focus change
			if (Hook is IWorldHook worldHook)
			{
				worldHook.ChangeFocus(value);
			}
		}
	}

	/// <summary>
	/// World statistics and metrics.
	/// </summary>
	public WorldMetrics Metrics => _metrics;

	/// <summary>
	/// World configuration settings.
	/// </summary>
	public WorldConfiguration Configuration => _configuration;

	/// <summary>
	/// Get a diagnostic summary of this world.
	/// </summary>
	public string GetDiagnostics()
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine($"World: {Name}");
		sb.AppendLine($"State: {State}");
		sb.AppendLine($"SessionID: {SessionID?.Value ?? "N/A"}");
		sb.AppendLine($"IsAuthority: {IsAuthority}");
		sb.AppendLine($"Users: {UserCount}");
		sb.AppendLine($"TotalTime: {TotalTime:F2}s");
		sb.AppendLine($"SyncTick: {SyncTick}");
		sb.AppendLine($"StateVersion: {StateVersion}");
		sb.AppendLine($"Metrics: {_metrics}");
		return sb.ToString();
	}

	/// <summary>
	/// Update world metrics (call periodically).
	/// </summary>
	internal void UpdateMetrics()
	{
		_metrics.SlotCount = RootSlot?.GetDescendants(true).Count() ?? 0;
		_metrics.ComponentCount = RootSlot?.GetDescendants(true).Sum(s => s.ComponentCount) ?? 0;
		_metrics.RefIDsAllocated = ReferenceController?.ObjectCount ?? 0;
	}

	/// <summary>
	/// Event triggered when a Slot is added to the World.
	/// </summary>
	public event Action<Slot> OnSlotAdded;

	/// <summary>
	/// Event triggered when a Slot is removed from the World.
	/// </summary>
	public event Action<Slot> OnSlotRemoved;

	public World()
	{
		WorldName = new Sync<string>(null, "New World");
		SessionID = new Sync<string>(null, Guid.NewGuid().ToString());
		_hookManager = new HookManager(this);
		_trashBin = new TrashBin(this);
		_refIDAllocator = new RefIDAllocator(this);
		_hookTypes = new HookTypeRegistry();
		_updateManager = new UpdateManager(this);

		// Initialize event receiver arrays
		int length = Enum.GetValues(typeof(WorldEvent)).Length;
		_worldEventReceivers = new List<IWorldEventReceiver>[length];
		for (int i = 0; i < length; i++)
		{
			_worldEventReceivers[i] = new List<IWorldEventReceiver>();
		}
	}

	/// <summary>
	/// Create a local-only world (single user, no networking).
	/// </summary>
	public static World LocalWorld(Engine engine, string name, Action<World> init = null)
	{
		var world = new World();
		world.WorldName.Value = name;
		world.AuthorityID = -1; // Local-only (no authority)
		world.LocalID = -1;
		world.IsDestroyed = false;
		world.IsDisposed = false;

		// Initialize world
		world.Initialize();

		// Run initialization callback first so event receivers (like SimpleUserSpawn) are registered
		init?.Invoke(world);

		// Create local user (this triggers OnUserJoined after SimpleUserSpawn is ready)
		world.CreateHostUser("LocalUser");

		// Start running
		world.StartRunning();
		AquaLogger.Log($"Local world '{name}' created and started");

		return world;
	}

	/// <summary>
	/// Start a hosted session (authority/server).
	/// </summary>
	public static World StartSession(Engine engine, string name, ushort port, string hostUserName = null, Action<World> init = null)
	{
		return StartSession(engine, name, port, hostUserName, SessionVisibility.Private, 16, init);
	}

	/// <summary>
	/// Start a hosted session (authority/server) with visibility settings.
	/// </summary>
	public static World StartSession(
		Engine engine,
		string name,
		ushort port,
		string hostUserName,
		SessionVisibility visibility,
		int maxUsers = 16,
		Action<World> init = null)
	{
		var world = new World();
		world.WorldName.Value = name;
		world.AuthorityID = 0; // This instance is authority
		world.LocalID = 0;
		world.IsDestroyed = false;
		world.IsDisposed = false;

		// Initialize world
		world.Initialize();

		// Build session metadata
		var metadata = new SessionMetadata
		{
			Name = name,
			HostUsername = hostUserName ?? Environment.MachineName,
			HostMachineId = Environment.MachineName,
			Visibility = visibility,
			MaxUsers = maxUsers
		};

		// Start session network (creates LNL listener) but don't create user yet
		world.StartSessionNetwork(port, metadata);

		// Set session ID from the generated metadata
		world.SessionID.Value = world._session?.Metadata?.SessionId ?? SessionIdentifier.Generate();

		// Run initialization callback first so event receivers (like SimpleUserSpawn) are registered
		init?.Invoke(world);

		// Now create the host user (triggers OnUserJoined after SimpleUserSpawn is ready)
		world.CreateHostUser(hostUserName);

		// Start running
		world.StartRunning();
		AquaLogger.Log($"Session '{name}' started on port {port} with visibility {visibility}");

		return world;
	}

	/// <summary>
	/// Join a remote session (client).
	/// </summary>
	public static World JoinSession(Engine engine, string name, Uri address)
	{
		var world = new World();
		world.WorldName.Value = name;
		world.SessionID.Value = "Unknown"; // Will be set by server
		world.AuthorityID = 0; // Server is authority
		world.LocalID = -1; // Will be assigned by server
		world.IsDestroyed = false;
		world.IsDisposed = false;

		// Initialize world as CLIENT (uses LOCAL RefID space to avoid collisions with host's Authority RefIDs)
		world.Initialize(isAuthority: false);

		// Join session (connects to LNL server)
		world.JoinSession(address);

		// World will transition to Running when connection succeeds
		AquaLogger.Log($"Joining session at {address}");

		return world;
	}

	/// <summary>
	/// Join a remote session (client) asynchronously.
	/// </summary>
	public static async Task<World?> JoinSessionAsync(Engine engine, string name, Uri address)
	{
		var world = new World();
		world.WorldName.Value = name;
		world.SessionID.Value = "Unknown"; // Will be set by server
		world.AuthorityID = 0; // Server is authority
		world.LocalID = -1; // Will be assigned by server
		world.IsDestroyed = false;
		world.IsDisposed = false;

		// Initialize world as CLIENT (uses LOCAL RefID space to avoid collisions with host's Authority RefIDs)
		world.Initialize(isAuthority: false);

		// Join session (connects to LNL server)
		var joined = await world.JoinSessionAsync(address);
		if (!joined)
		{
			return null;
		}

		AquaLogger.Log($"Joining session at {address}");
		return world;
	}

	/// <summary>
	/// Initialize the World and create the root Slot.
	/// </summary>
	/// <param name="isAuthority">True if this is the authority/host, false for clients.
	/// Clients use LOCAL RefID space to avoid conflicts with network-received Authority RefIDs.</param>
	public void Initialize(bool isAuthority = true)
	{
		if (_state != WorldState.Created) return;

		_initState = InitializationState.Created;
		AquaLogger.Log($"World initializing data model (isAuthority={isAuthority})");

		// Create reference controller BEFORE anything else
		ReferenceController = new ReferenceController(this);

		// Create sync controller first (doesn't need RefID)
		SyncController = new SyncController(this);
		AquaLogger.Log("SyncController initialized");

		// Create worker manager for type encoding/decoding during sync
		// Needs SyncController available so the type index table registers for replication.
		Workers = new WorkerManager(this);

		// Always create network replicators with consistent RefIDs
		// This ensures both client and host use the same RefIDs for replicators
		_slotBag = new Networking.Sync.SlotBag();
		_slotBag.Initialize(this, null);
		_userBag = new Networking.Sync.UserBag();
		_userBag.Initialize(this, null);
		AquaLogger.Log($"Network bags initialized: SlotBag={_slotBag.ReferenceID}, UserBag={_userBag.ReferenceID}");

		// Clients must create shared world structures (RootSlot) in authority RefID space
		// so incoming ParentSlotRef and other references resolve correctly.
		// Local-only structures should explicitly use LocalAllocationBlockBegin where created.
		if (!isAuthority)
		{
			AquaLogger.Log("Client: Using authority RefID space for shared world structures");
		}

		// Create root Slot (uses Authority RefID space on both host and client)
		RootSlot = new Slot();
		RootSlot.SlotName.Value = "Root";
		RootSlot.Initialize(this);
		RegisterSlot(RootSlot);

		// Note: Godot scene attachment handled by WorldDriver wrapper
		// World itself is pure C# and doesn't use AddChild

		// Network session is started via StartSession() or JoinSession()
		// No automatic network initialization

		// DON'T transition to Running here for clients!
		// Clients need to wait for full state download.
		// Only local/authority worlds can go to Running immediately.
		if (_slotBag != null && _slotBag.IsInInitPhase)
		{
			_slotBag.EndInitPhase();
		}
		if (_userBag != null && _userBag.IsInInitPhase)
		{
			_userBag.EndInitPhase();
		}
		AquaLogger.Log($"World '{WorldName.Value}' initialized successfully - state={_state}, initState={_initState}");
	}

	/// <summary>
	/// Get or create the Users container slot (NOT a UserRoot component!).
	/// This is just a container - each user gets their own UserRootComponent via SimpleUserSpawn.
	/// </summary>
	public Slot GetOrCreateUsersSlot()
	{
		// Try to find existing Users slot
		var usersSlot = FindSlotsByTag("UserRoot").FirstOrDefault();

		if (usersSlot == null)
		{
			// Create Users container slot under Root
			usersSlot = RootSlot.AddSlot("Users");
			usersSlot.Tag.Value = "UserRoot";
			AquaLogger.Log("Created Users container slot in world");
		}

		// NO UserRootComponent here! Users container is just a parent slot.
		// Each individual user gets UserRootComponent via SimpleUserSpawn!

		return usersSlot;
	}


	/// <summary>
	/// Register a Slot with the World.
	/// </summary>
	internal void RegisterSlot(Slot slot)
	{
		if (slot == null) return;

		// Only register if not already registered (InitializeFromReplicator may have already registered)
		if (!ReferenceController.ContainsObject(slot.ReferenceID))
		{
			ReferenceController?.RegisterObject(slot);
		}
		Metrics.SlotCount++;

		// Add to slot replicator for network sync (skip local-only slots)
		if (!slot.IsLocalElement && _slotBag != null && !_slotBag.ContainsKey(slot.ReferenceID))
		{
			_slotBag.Add(slot.ReferenceID, slot, isNewlyCreated: true, skipSync: false);
		}

		if (!string.IsNullOrEmpty(slot.Tag.Value))
		{
			if (!_slotsByTag.TryGetValue(slot.Tag.Value, out var slots))
			{
				slots = new List<Slot>();
				_slotsByTag[slot.Tag.Value] = slots;
			}
			slots.Add(slot);
		}

		if (slot.IsRootSlot)
		{
			_rootSlots.Add(slot);
		}

		OnSlotAdded?.Invoke(slot);
	}

	/// <summary>
	/// Unregister a Slot from the World.
	/// </summary>
	internal void UnregisterSlot(Slot slot)
	{
		if (slot == null) return;

		ReferenceController?.UnregisterObject(slot.ReferenceID);
		Metrics.SlotCount--;

		// Remove from slot replicator for network sync
		if (!slot.IsLocalElement)
		{
			_slotBag?.Remove(slot.ReferenceID);
		}

		if (!string.IsNullOrEmpty(slot.Tag.Value))
		{
			if (_slotsByTag.TryGetValue(slot.Tag.Value, out var slots))
			{
				slots.Remove(slot);
			}
		}

		_rootSlots.Remove(slot);
		OnSlotRemoved?.Invoke(slot);
	}

	/// <summary>
	/// Register a Component with the World.
	/// </summary>
	internal void RegisterComponent(Component component)
	{
		if (component == null) return;
		if (!ReferenceController.ContainsObject(component.ReferenceID))
		{
			ReferenceController?.RegisterObject(component);
		}
		Metrics.ComponentCount++;
	}

	/// <summary>
	/// Unregister a Component from the World.
	/// </summary>
	internal void UnregisterComponent(Component component)
	{
		if (component == null) return;
		ReferenceController?.UnregisterObject(component.ReferenceID);
		Metrics.ComponentCount--;
	}

	/// <summary>
	/// Mark an element as dirty (needs network synchronization).
	/// </summary>
	internal void MarkElementDirty(IWorldElement element)
	{
		if (element != null && !element.IsDestroyed)
		{
			_dirtyElements.Add(element);
		}
	}

	/// <summary>
	/// Get all elements that have been modified since last sync.
	/// </summary>
	public IEnumerable<IWorldElement> GetDirtyElements()
	{
		return _dirtyElements.ToArray();
	}

	/// <summary>
	/// Clear the dirty elements list (after synchronization).
	/// </summary>
	public void ClearDirtyElements()
	{
		_dirtyElements.Clear();
	}

	/// <summary>
	/// Get all elements in the world.
	/// </summary>
	public IEnumerable<KeyValuePair<RefID, IWorldElement>> GetAllElements()
	{
		return ReferenceController?.AllObjects ?? Array.Empty<KeyValuePair<RefID, IWorldElement>>();
	}

	/// <summary>
	/// Find a world element by its RefID.
	/// </summary>
	public IWorldElement FindElement(RefID refID)
	{
		return ReferenceController?.GetObjectOrNull(refID);
	}

	/// <summary>
	/// Find a world element by its RefID (legacy ulong overload).
	/// </summary>
	public IWorldElement FindElement(ulong refID)
	{
		return FindElement(new RefID(refID));
	}

	/// <summary>
	/// Try to retrieve a trashed object for the given tick and ID (used during confirmations).
	/// </summary>
	public IWorldElement TryRetrieveFromTrash(ulong tick, RefID id)
	{
		return ReferenceController?.TryRetrieveFromTrash(tick, id);
	}

	/// <summary>
	/// Find all Slots with the specified tag.
	/// </summary>
	public IEnumerable<Slot> FindSlotsByTag(string tag)
	{
		if (_slotsByTag.TryGetValue(tag, out var slots))
		{
			return slots.ToArray();
		}
		return Array.Empty<Slot>();
	}

	/// <summary>
	/// Find a Slot by name (searches entire hierarchy).
	/// </summary>
	public Slot FindSlotByName(string name)
	{
		return FindSlotByNameRecursive(RootSlot, name);
	}

	private Slot FindSlotByNameRecursive(Slot slot, string name)
	{
		if (slot.SlotName.Value == name)
			return slot;

		foreach (var child in slot.Children)
		{
			var found = FindSlotByNameRecursive(child, name);
			if (found != null) return found;
		}
		foreach (var child in slot.LocalChildren)
		{
			var found = FindSlotByNameRecursive(child, name);
			if (found != null) return found;
		}

		return null;
	}

	/// <summary>
	/// Destroy the World and all its contents.
	/// </summary>
	public void DestroyWorld()
	{
		if (_state == WorldState.Destroyed) return;

		AquaLogger.Log($"Destroying world '{WorldName.Value}'...");

		_state = WorldState.Destroyed;

		// Destroy all root slots (which will cascade to children)
		foreach (var slot in _rootSlots.ToArray())
		{
			slot.Destroy();
		}

		_dirtyElements.Clear();
		_slotsByTag.Clear();
		_rootSlots.Clear();
		_users.Clear();

		_hookManager?.Dispose();

		ReferenceController?.Reset();

		// Note: Godot scene cleanup handled by WorldDriver wrapper
	}

	/// <summary>
	/// Add a user to the world.
	/// </summary>
	public void AddUser(User user)
	{
		if (user == null) return;

		lock (_users)
		{
			if (!_users.Contains(user))
			{
				_users.Add(user);
				// Only register if not already registered (User constructor may have already registered)
				if (!ReferenceController.ContainsObject(user.ReferenceID))
				{
					ReferenceController?.RegisterObject(user);
				}

				// Add to user replicator for network sync (only if not already present)
				if (_userBag != null && !_userBag.ContainsKey(user.ReferenceID))
				{
					_userBag.Add(user.ReferenceID, user, isNewlyCreated: true, skipSync: false);
				}

				AquaLogger.Log($"User added to world: {user.UserName.Value}");

				// Trigger user joined event - ONLY on authority!
				// Only host fires OnUserJoined events. Host's SimpleUserSpawn creates
				// avatar slots in authority namespace, which are then synced to clients.
				// If clients also fired OnUserJoined, they'd create duplicate slots.
				if (IsAuthority)
				{
					TriggerUserJoinedEvent(user);
				}

				// Notify session of user count change
				_session?.OnUserCountChanged(_users.Count);
			}
		}
	}

	internal void AddUserToBag(User user, RefID id, bool isNewlyCreated)
	{
		if (user == null || _userBag == null)
		{
			return;
		}

		if (!_userBag.ContainsKey(id))
		{
			_userBag.Add(id, user, isNewlyCreated, skipSync: false);
		}
	}

	/// <summary>
	/// Remove a user from the world.
	/// </summary>
	public void RemoveUser(User user)
	{
		if (user == null) return;

		lock (_users)
		{
			_users.Remove(user);
			ReferenceController?.UnregisterObject(user.ReferenceID);

			// Remove from user replicator for network sync
			_userBag?.Remove(user.ReferenceID);

			AquaLogger.Log($"User removed from world: {user.UserName.Value}");

			// Trigger user left event - ONLY on authority!
			// Same reason as OnUserJoined - host handles user lifecycle events.
			if (IsAuthority)
			{
				TriggerUserLeftEvent(user);
			}

			// Notify session of user count change
			_session?.OnUserCountChanged(_users.Count);
		}
	}

	/// <summary>
	/// Register a user with the world (called by UserBag).
	/// Alias for AddUser.
	/// </summary>
	internal void RegisterUser(User user) => AddUser(user);

	/// <summary>
	/// Unregister a user from the world (called by UserBag).
	/// Alias for RemoveUser.
	/// </summary>
	internal void UnregisterUser(User user) => RemoveUser(user);

	/// <summary>
	/// Set the local user (client's own user).
	/// </summary>
	public void SetLocalUser(User user)
	{
		LocalUser = user;
		// Only configure streams if world is already Running.
		// For clients, streams are decoded AFTER SetLocalUser is called during FullBatch processing.
		// ConfigureLocalTrackingStreams will be called in StartRunning() instead.
		if (_state == WorldState.Running)
		{
			user?.ConfigureLocalTrackingStreams();
		}
		AddUser(user);
		AquaLogger.Log($"Local user set: {user.UserName.Value}");
	}

	/// <summary>
	/// Get all users in the world.
	/// </summary>
	public List<User> GetAllUsers()
	{
		lock (_users)
		{
			return new List<User>(_users);
		}
	}

	/// <summary>
	/// Start a new session as host (authority).
	/// </summary>
	public void StartSession(ushort port = 7777, string hostUserName = null)
	{
		StartSessionNetwork(port);
		CreateHostUser(hostUserName);
	}

	/// <summary>
	/// Start the session network without creating the host user.
	/// Used internally to allow init callback to run before user creation.
	/// </summary>
	private void StartSessionNetwork(ushort port, SessionMetadata metadata = null)
	{
		if (_session != null)
		{
			AquaLogger.Warn("Session already started");
			return;
		}

		try
		{
			NetworkInitStart();

			if (metadata != null)
			{
				_session = Session.NewSession(this, port, metadata);
			}
			else
			{
				_session = Session.NewSession(this, port);
			}
			AuthorityID = -1; // This is the host

			_refIDAllocator.Reset();

			// NOTE: State remains InitializingNetwork here!
			// The caller (StartSession factory) will set Running AFTER init callback completes.
			// This allows the init callback to modify the world before the DataModel lock check kicks in.

			AquaLogger.Log($"Started session network on port {port}");
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"Failed to start session network: {ex.Message}");
			InitializationFailed();
		}
	}

	/// <summary>
	/// Create the host user for a locally hosted session.
	/// </summary>
	public User CreateHostUser(string userName = null)
	{
		var (rangeStart, rangeEnd) = _refIDAllocator.GetAuthorityIDRange();

		// Reserve the next RefID without advancing the allocation cursor.
		// User.InitializeWorker will advance the cursor for the user and its sync members.
		var userRefId = ReferenceController.PeekID();
		var hostUser = new User();
		var resolvedName = string.IsNullOrWhiteSpace(userName) ? System.Environment.MachineName : userName;

		hostUser.UserName.Value = resolvedName;
		hostUser.UserID.Value = userRefId.ToString();
		hostUser.MachineID.Value = System.Environment.MachineName;
		hostUser.AllocationIDStart.Value = rangeStart;
		hostUser.AllocationIDEnd.Value = rangeEnd;
		hostUser.AllocationID.Value = userRefId.GetUserByte();
		hostUser.IsPresent.Value = true;
		hostUser.PresentInWorld.Value = true;
		hostUser.IsSilenced.Value = false;

		// Set head device type based on VR status
		var inputInterface = Engine.Current?.InputInterface;
		hostUser.HeadDevice.Value = inputInterface?.CurrentHeadOutputDevice ?? HeadOutputDevice.Screen;
		hostUser.VRActive.Value = inputInterface?.IsVRActive ?? false;

		// Set platform
		hostUser.UserPlatform.Value = GetCurrentPlatform();

		LocalUser = hostUser;
		AddUserToBag(hostUser, userRefId, isNewlyCreated: true);
		hostUser.ConfigureLocalTrackingStreams();
		AquaLogger.Log($"Created host user '{resolvedName}' with RefID {userRefId}");
		return hostUser;
	}

	/// <summary>
	/// Get the current platform type.
	/// </summary>
	private static Platform GetCurrentPlatform()
	{
		if (OperatingSystem.IsWindows())
			return Platform.Windows;
		if (OperatingSystem.IsLinux())
			return Platform.Linux;
		if (OperatingSystem.IsAndroid())
			return Platform.Android;
		return Platform.Other;
	}

	/// <summary>
	/// Join an existing session as client.
	/// </summary>
	public void JoinSession(Uri address)
	{
		if (_session != null)
		{
			AquaLogger.Warn("Session already active");
			return;
		}

		try
		{
			NetworkInitStart();

			_session = Session.JoinSession(this, new[] { address });

			// Client now waits for JoinGrant from authority
			WaitForJoinGrant();

			// Create loading indicator in current world
			CreateSessionJoinIndicator();
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"Failed to join session: {ex.Message}");
			InitializationFailed();
		}
	}

	/// <summary>
	/// Join an existing session as client (async).
	/// </summary>
	public async Task<bool> JoinSessionAsync(Uri address)
	{
		if (_session != null)
		{
			AquaLogger.Warn("Session already active");
			return false;
		}

		try
		{
			NetworkInitStart();

			_session = await Session.JoinSessionAsync(this, new[] { address });
			if (_session == null)
			{
				InitializationFailed();
				return false;
			}

			// Client now waits for JoinGrant from authority
			WaitForJoinGrant();
			return true;
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"Failed to join session: {ex.Message}");
			InitializationFailed();
			return false;
		}
	}

	/// <summary>
	/// Called when client receives JoinGrant from authority.
	/// Transitions from WaitingForJoinGrant to InitializingDataModel.
	/// Note: Allocation context switch is handled in SessionSyncManager before SetLocalUser.
	/// </summary>
	public void OnJoinGrantReceived()
	{
		if (_state == WorldState.WaitingForJoinGrant)
		{
			StartDataModelInit();
		}
	}

	/// <summary>
	/// Called when client receives full world state from authority.
	/// Transitions from InitializingDataModel to Running.
	/// </summary>
	public void OnFullStateReceived()
	{
		if (_state == WorldState.InitializingDataModel)
		{
			StartRunning();
		}
	}

	/// <summary>
	/// Enter network initialization stage.
	/// </summary>
	public void NetworkInitStart()
	{
		var oldState = _state;
		_initState = InitializationState.InitializingNetwork;
		_state = WorldState.InitializingNetwork;
		AquaLogger.Log("World entering network initialization");
		OnStateChanged?.Invoke(oldState, _state);
	}

	/// <summary>
	/// Enter join grant wait stage (clients only).
	/// </summary>
	public void WaitForJoinGrant()
	{
		if (IsAuthority)
		{
			AquaLogger.Warn("Authority cannot wait for join grant");
			return;
		}

		var oldState = _state;
		_initState = InitializationState.WaitingForJoinGrant;
		_state = WorldState.WaitingForJoinGrant;
		AquaLogger.Log("Waiting for join grant");
		OnStateChanged?.Invoke(oldState, _state);
	}

	/// <summary>
	/// Enter data model initialization stage (clients only).
	/// </summary>
	public void StartDataModelInit()
	{
		if (IsAuthority)
		{
			AquaLogger.Warn("Authority cannot enter data model init");
			return;
		}

		var oldState = _state;
		_initState = InitializationState.InitializingDataModel;
		_state = WorldState.InitializingDataModel;
		AquaLogger.Log("Starting data model initialization");
		OnStateChanged?.Invoke(oldState, _state);
	}

	/// <summary>
	/// Mark the world as running.
	/// </summary>
	public void StartRunning()
	{
		if (_state == WorldState.Destroyed)
			return;

		var oldState = _state;
		_initState = InitializationState.Finished;
		_state = WorldState.Running;
		AquaLogger.Log("World is now running");

		// For clients: configure local user's tracking streams now that all sync members are decoded.
		// This was deferred from SetLocalUser because streams weren't decoded yet during FullBatch processing.
		if (!IsAuthority && LocalUser != null)
		{
			LocalUser.ConfigureLocalTrackingStreams();
			AquaLogger.Log($"Configured tracking streams for local user '{LocalUser.UserName.Value}' on world Running");
		}

		OnStateChanged?.Invoke(oldState, _state);
	}

	/// <summary>
	/// Mark initialization failure.
	/// </summary>
	public void InitializationFailed()
	{
		var oldState = _state;
		_initState = InitializationState.Failed;
		_state = WorldState.Failed;
		AquaLogger.Log("World initialization failed");
		OnStateChanged?.Invoke(oldState, _state);
	}

	/// <summary>
	/// Queue action to run synchronously on next update.
	/// Thread-safe for cross-thread calls.
	/// </summary>
	public void RunSynchronously(Action action)
	{
		if (IsDisposed) return;

		lock (_syncLock)
		{
			_synchronousActions.Enqueue(action);
		}
	}

	/// <summary>
	/// Queue action to run after a number of update cycles.
	/// </summary>
	/// <param name="updateCount">Number of updates to wait</param>
	/// <param name="action">Action to execute</param>
	public void RunInUpdates(int updateCount, Action action)
	{
		if (IsDisposed || action == null) return;

		if (updateCount <= 0)
		{
			RunSynchronously(action);
			return;
		}

		// Wrap to count down updates
		int remaining = updateCount;
		void CountdownAction()
		{
			remaining--;
			if (remaining <= 0)
				action();
			else
				RunSynchronously(CountdownAction);
		}
		RunSynchronously(CountdownAction);
	}

	/// <summary>
	/// Process all queued synchronous actions.
	/// </summary>
	private void ProcessSynchronousActions()
	{
		lock (_syncLock)
		{
			while (_synchronousActions.Count > 0)
			{
				try
				{
					_synchronousActions.Dequeue()?.Invoke();
				}
				catch (Exception ex)
				{
					AquaLogger.Error($"World: Error in synchronous action: {ex}");
				}
			}
		}
	}

	/// <summary>
	/// Leave the current session.
	/// </summary>
	public void LeaveSession()
	{
		if (_session != null)
		{
			_session.Dispose();
			_session = null;
			AquaLogger.Log("Left session");
		}
	}

	/// <summary>
	/// Increment the sync tick counter.
	/// Called once per sync cycle by SessionSyncManager.
	/// </summary>
	public void IncrementSyncTick()
	{
		SyncTick++;
	}

	/// <summary>
	/// Increment the state version counter.
	/// Called whenever the authority makes a state change.
	/// </summary>
	public void IncrementStateVersion()
	{
		StateVersion++;
	}

	/// <summary>
	/// Set the state version (used when receiving authority updates).
	/// </summary>
	public void SetStateVersion(ulong version)
	{
		StateVersion = version;
	}

	/// <summary>
	/// Process the World update loop with comprehensive stages.
	/// Handles all update phases for the world simulation.
	/// Called by EngineDriver or similar wrapper.
	/// </summary>
	public void Update(double delta)
	{
		if (_state != WorldState.Running) return;

		// Acquire Implementer lock for main thread modifications
		_hookManager?.ImplementerLock(System.Threading.Thread.CurrentThread);
		try
		{
			var scaledDelta = delta * TimeScale;
			TotalTime += scaledDelta;
			LastDelta = (float)scaledDelta;

			// Stage 1: Process synchronous actions (immediate state changes)
			ProcessSynchronousActions();

			// Stage 1.5: Process completed asset fetch tasks
			Networking.AssetFetcher.ProcessQueue();

			// Stage 2: Process world events (user joined/left, focus changes)
			RunWorldEvents();

			// Stage 2.5: Register any newly used worker types (authority only)
			if (IsAuthority)
			{
				Workers?.RegisterTypes();
			}

			// Stage 3: Process input for this world (if focused)
			if (_focus == WorldFocus.Focused)
			{
				ProcessInput((float)scaledDelta);
			}

			// Stage 4: Update coroutines
			UpdateCoroutines((float)scaledDelta);

			// Stage 5: Update components (main update)
			UpdateComponents((float)scaledDelta);

			// Stage 5.5: Apply component changes (from sync field updates)
			_updateManager?.RunChangeApplications();

			// Stage 5.6: Sync slot hooks so transforms propagate to the platform scene graph
			UpdateSlotHooks(RootSlot);

			// Stage 5.7: Update hooks for orphaned slots (network-replicated slots not yet in tree)
			UpdateOrphanedSlotHooks();

			// Stage 6: Process changed elements
			ProcessChangedElements();

			// Stage 7: Process destructions
			ProcessDestructions();

			// Stage 8: Update hooks (sync with platform layer)
			_updateManager?.ProcessHookUpdates((float)scaledDelta);

			// Stage 9: Clean up trash bin
			_trashBin?.Update();

			// Stage 10: Signal sync manager that world refresh is complete
			// Sync thread waits for this before new-user initialization
			// This ensures OnUserJoined events have fired and avatars are created
			if (_session?.Sync != null)
			{
				_session.Sync.SignalRefreshFinished();
			}

			// Stage 11: Update local user stats (FPS)
			if (LocalUser != null && delta > 0)
			{
				LocalUser.FPS.Value = (float)(1.0 / delta);
			}
		}
		finally
		{
			_hookManager?.ImplementerUnlock();
		}
	}

	/// <summary>
	/// Fixed update for physics and deterministic operations.
	/// </summary>
	public void FixedUpdate(double fixedDelta)
	{
		if (_state != WorldState.Running) return;

		var scaledDelta = fixedDelta * TimeScale;

		// Update physics for all physics components
		UpdatePhysics((float)scaledDelta);
	}

	/// <summary>
	/// Late update for cameras and final positioning.
	/// </summary>
	public void LateUpdate(double delta)
	{
		if (_state != WorldState.Running) return;

		var scaledDelta = delta * TimeScale;

		// Update cameras and final transforms
		UpdateCameras((float)scaledDelta);
	}

	/// <summary>
	/// Process input for this world.
	/// </summary>
	private void ProcessInput(float delta)
	{
		// Process input through input manager
		// This would handle VR controllers, keyboard, mouse for this world
	}

	/// <summary>
	/// Update coroutines for this world.
	/// </summary>
	private void UpdateCoroutines(float delta)
	{
		// Update world-specific coroutines
	}

	/// <summary>
	/// Update all components in the world.
	/// </summary>
	private void UpdateComponents(float delta)
	{
		_updateManager?.RunStartups();
		_updateManager?.RunUpdates(delta);
	}

	/// <summary>
	/// Recursively apply slot hook updates so Node3D transforms stay in sync.
	/// </summary>
	private void UpdateSlotHooks(Slot slot)
	{
		if (slot == null)
			return;

		slot.Hook?.ApplyChanges();

		foreach (var child in slot.Children)
		{
			UpdateSlotHooks(child);
		}
		foreach (var child in slot.LocalChildren)
		{
			UpdateSlotHooks(child);
		}
	}

	/// <summary>
	/// Update hooks for orphaned slots (slots not in the tree hierarchy).
	/// This handles network-replicated slots whose parent hasn't resolved yet.
	/// </summary>
	private void UpdateOrphanedSlotHooks()
	{
		if (_slotBag == null)
			return;

		foreach (var kvp in _slotBag)
		{
			var slot = kvp.Value;
			// Only update slots that aren't in the tree (no parent and not RootSlot)
			if (slot != null && slot.Parent == null && slot != RootSlot && slot.Hook != null)
			{
				slot.Hook.ApplyChanges();
			}
		}
	}

	/// <summary>
	/// Recursively update slots and their components.
	/// </summary>
	private void UpdateSlotsRecursive(Slot slot, float delta)
	{
		if (slot == null || !slot.ActiveSelf)
			return;

		// Update components on this slot
		foreach (var component in slot.Components)
		{
			if (component.Enabled)
			{
				component.OnUpdate(delta);
			}
		}

		// Update child slots
		foreach (var child in slot.Children)
		{
			UpdateSlotsRecursive(child, delta);
		}
		foreach (var child in slot.LocalChildren)
		{
			UpdateSlotsRecursive(child, delta);
		}
	}

	/// <summary>
	/// Process elements that have changed this frame.
	/// </summary>
	private void ProcessChangedElements()
	{
		lock (_syncLock)
		{
			foreach (var element in _dirtyElements)
			{
				// Process changed elements for network sync
				if (_session != null && IsAuthority)
				{
					// Queue state changes for network broadcast
				}
			}
			_dirtyElements.Clear();
		}
	}

	/// <summary>
	/// Process pending destructions.
	/// </summary>
	private void ProcessDestructions()
	{
		_updateManager?.RunDestructions();
	}

	/// <summary>
	/// Update physics components.
	/// </summary>
	private void UpdatePhysics(float fixedDelta)
	{
		// Update all physics components with fixed timestep
		// This will be called by physics components that register for fixed updates
		UpdatePhysicsRecursive(RootSlot, fixedDelta);
	}

	/// <summary>
	/// Recursively update physics on slots.
	/// </summary>
	private void UpdatePhysicsRecursive(Slot slot, float fixedDelta)
	{
		if (slot == null || !slot.ActiveSelf)
			return;

		// Update physics components on this slot
		foreach (var component in slot.Components)
		{
			if (component.Enabled)
			{
				// Check if component has physics
				// For now, call a virtual method that components can override
				component.OnFixedUpdate(fixedDelta);
			}
		}

		// Update child slots
		foreach (var child in slot.Children)
		{
			UpdatePhysicsRecursive(child, fixedDelta);
		}
		foreach (var child in slot.LocalChildren)
		{
			UpdatePhysicsRecursive(child, fixedDelta);
		}
	}

	/// <summary>
	/// Update camera components.
	/// </summary>
	private void UpdateCameras(float delta)
	{
		// Update all camera components for final positioning
		UpdateCamerasRecursive(RootSlot, delta);
	}

	/// <summary>
	/// Recursively update cameras on slots.
	/// </summary>
	private void UpdateCamerasRecursive(Slot slot, float delta)
	{
		if (slot == null || !slot.ActiveSelf)
			return;

		// Update camera components on this slot
		foreach (var component in slot.Components)
		{
			if (component.Enabled)
			{
				// Call late update for cameras and final positioning
				component.OnLateUpdate(delta);
			}
		}

		// Update child slots
		foreach (var child in slot.Children)
		{
			UpdateCamerasRecursive(child, delta);
		}
		foreach (var child in slot.LocalChildren)
		{
			UpdateCamerasRecursive(child, delta);
		}
	}

	/// <summary>
	/// Register a component to receive world events.
	/// </summary>
	public void RegisterEventReceiver(IWorldEventReceiver receiver)
	{
		foreach (WorldEvent eventType in Enum.GetValues(typeof(WorldEvent)))
		{
			if (receiver.HasEventHandler(eventType))
			{
				_worldEventReceivers[(int)eventType].Add(receiver);
			}
		}
	}

	/// <summary>
	/// Unregister a component from receiving world events.
	/// </summary>
	public void UnregisterEventReceiver(IWorldEventReceiver receiver)
	{
		foreach (WorldEvent eventType in Enum.GetValues(typeof(WorldEvent)))
		{
			if (receiver.HasEventHandler(eventType))
			{
				_worldEventReceivers[(int)eventType].Remove(receiver);
			}
		}
	}

	/// <summary>
	/// Trigger user joined events to all registered receivers.
	/// Should be called from AddUser.
	/// </summary>
	private void TriggerUserJoinedEvent(User user)
	{
		_joinedUsers.Add(user);
	}

	/// <summary>
	/// Trigger user left events to all registered receivers.
	/// Should be called from RemoveUser.
	/// </summary>
	private void TriggerUserLeftEvent(User user)
	{
		_leftUsers.Add(user);
	}

	/// <summary>
	/// Process all pending world events and notify receivers.
	/// Called during world update cycle.
	/// </summary>
	private void RunWorldEvents()
	{
		// Process user joined events
		if (_joinedUsers.Count > 0)
		{
			foreach (var user in _joinedUsers)
			{
				foreach (var receiver in _worldEventReceivers[(int)WorldEvent.OnUserJoined])
				{
					try
					{
						receiver.OnUserJoined(user);
					}
					catch (Exception ex)
					{
						AquaLogger.Error($"Error in OnUserJoined handler: {ex.Message}");
					}
				}
			}
			_joinedUsers.Clear();
		}

		// Process user left events
		if (_leftUsers.Count > 0)
		{
			foreach (var user in _leftUsers)
			{
				foreach (var receiver in _worldEventReceivers[(int)WorldEvent.OnUserLeft])
				{
					try
					{
						receiver.OnUserLeft(user);
					}
					catch (Exception ex)
					{
						AquaLogger.Error($"Error in OnUserLeft handler: {ex.Message}");
					}
				}
			}
			_leftUsers.Clear();
		}
	}

	/// <summary>
	/// Add a slot to the world.
	/// </summary>
	public Slot AddSlot(string name = "Slot")
	{
		return RootSlot.AddSlot(name);
	}

	/// <summary>
	/// Create a SessionJoinIndicator in the current world.
	/// Shows a loading indicator in the currently focused world.
	/// </summary>
	private void CreateSessionJoinIndicator()
	{
		// Only create indicator for client worlds that have a local user
		if (IsAuthority || LocalUser == null || WorldManager?.FocusedWorld == null)
			return;

		try
		{
			// Create indicator in the currently focused world
			var currentWorld = WorldManager.FocusedWorld;
			
			RunSynchronously(() =>
			{
				SessionJoinIndicator.CreateIndicatorAsync(currentWorld, this, _session?.Sync, indicator =>
				{
					if (indicator != null)
					{
						AquaLogger.Log($"Created session join indicator in world '{currentWorld.Name}' for joining '{Name}'");
					}
					else
					{
						AquaLogger.Warn("Failed to create session join indicator");
					}
				});
			});
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"Error creating session join indicator: {ex.Message}");
		}
	}

	/// <summary>
	/// Dispose of the world and clean up resources.
	/// </summary>
	public void Dispose()
	{
		// 1. Check double-dispose
		if (IsDisposed)
		{
			AquaLogger.Warn($"World: Already disposed world '{WorldName.Value}'");
			return;
		}

		AquaLogger.Log($"World: Disposing world '{WorldName.Value}'");

		// 2. Mark as destroyed
		IsDestroyed = true;
		IsDisposed = true;

		// 3. Process remaining synchronous actions
		lock (_syncLock)
		{
			while (_synchronousActions.Count > 0)
			{
				try
				{
					_synchronousActions.Dequeue()?.Invoke();
				}
				catch (Exception ex)
				{
					AquaLogger.Error($"World: Error in disposal synchronous action: {ex}");
				}
			}
		}

		// 4. Dispose session
		try
		{
			_session?.Dispose();
			_session = null;
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"World: Error disposing session: {ex.Message}");
		}

		// Dispose sync controller
		try
		{
			SyncController?.Dispose();
			SyncController = null;
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"World: Error disposing sync controller: {ex.Message}");
		}

		// 5. Dispose all users
		foreach (var user in _users.ToList())
		{
			try
			{
				user?.Dispose();
			}
			catch (Exception ex)
			{
				AquaLogger.Error($"World: Error disposing user: {ex.Message}");
			}
		}

		// 8. Dispose all components in all slots
		// 9. Dispose all slots
		try
		{
			RootSlot?.Destroy();
			RootSlot = null;
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"World: Error disposing root slot: {ex.Message}");
		}

		// 10. Clear all collections
		_users?.Clear();
		_dirtyElements?.Clear();
		_slotsByTag?.Clear();
		_rootSlots?.Clear();
		_joinedUsers?.Clear();
		_leftUsers?.Clear();

		// Clear event receivers
		if (_worldEventReceivers != null)
		{
			for (int i = 0; i < _worldEventReceivers.Length; i++)
			{
				_worldEventReceivers[i]?.Clear();
			}
		}

		// 11. Dispose managers
		try
		{
			_hookManager?.Dispose();
			_hookManager = null;
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"World: Error disposing hook manager: {ex.Message}");
		}

		try
		{
			// TrashBin doesn't have Dispose, just clear reference
			_trashBin = null;
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"World: Error clearing trash bin: {ex.Message}");
		}

		try
		{
			// UpdateManager doesn't have Dispose, just clear reference
			_updateManager = null;
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"World: Error clearing update manager: {ex.Message}");
		}

		try
		{
			_refIDAllocator = null;
			_hookTypes = null;
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"World: Error clearing managers: {ex.Message}");
		}

		try
		{
			ReferenceController?.Dispose();
			ReferenceController = null;
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"World: Error disposing reference controller: {ex.Message}");
		}

		AquaLogger.Log($"World: Disposed world '{WorldName.Value}'");
	}
}
