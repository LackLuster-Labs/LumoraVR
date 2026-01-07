using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Networking.Session;
using Lumora.Core.Networking.Sync;
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

	private readonly HashSet<IWorldElement> _dirtyElements = new();
	private readonly Dictionary<string, List<Slot>> _slotsByTag = new();
	private readonly List<Slot> _rootSlots = new();
	private readonly List<User> _users = new();
	private readonly List<User> _joinedUsers = new();
	private readonly List<User> _leftUsers = new();
	private readonly List<IWorldEventReceiver>[] _worldEventReceivers;
	private WorldState _state = WorldState.Created;
	private Session _session;
	private HookManager _hookManager;
	private TrashBin _trashBin;
	private RefIDAllocator _refIDAllocator;
	private WorldFocus _focus = WorldFocus.Background;
	private HookTypeRegistry _hookTypes;
	private UpdateManager _updateManager;
	private Queue<Action> _synchronousActions = new Queue<Action>();
	private object _syncLock = new object();

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
	/// Current state of the World.
	/// </summary>
	public WorldState State => _state;

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
	/// Synchronization controller for this world.
	/// </summary>
	public SyncController SyncController { get; private set; }

	/// <summary>
	/// Reference controller for object lookup and async resolution.
	/// </summary>
	public ReferenceController ReferenceController { get; private set; }

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
		world._state = WorldState.Running;
		AquaLogger.Log($"Local world '{name}' created and started");

		return world;
	}

	/// <summary>
	/// Start a hosted session (authority/server).
	/// </summary>
	public static World StartSession(Engine engine, string name, ushort port, string hostUserName = null, Action<World> init = null)
	{
		var world = new World();
		world.WorldName.Value = name;
		world.SessionID.Value = "S-" + Guid.NewGuid().ToString();
		world.AuthorityID = 0; // This instance is authority
		world.LocalID = 0;
		world.IsDestroyed = false;
		world.IsDisposed = false;

		// Initialize world
		world.Initialize();

		// Start session network (creates LNL listener) but don't create user yet
		world._state = WorldState.InitializingNetwork;
		world.StartSessionNetwork(port);

		// Run initialization callback first so event receivers (like SimpleUserSpawn) are registered
		init?.Invoke(world);

		// Now create the host user (triggers OnUserJoined after SimpleUserSpawn is ready)
		world.CreateHostUser(hostUserName);

		// Start running
		world._state = WorldState.Running;
		AquaLogger.Log($"Session '{name}' started on port {port}");

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

		// Initialize world
		world.Initialize();

		// Join session (connects to LNL server)
		world._state = WorldState.InitializingNetwork;
		world.JoinSession(address);

		// World will transition to Running when connection succeeds
		AquaLogger.Log($"Joining session at {address}");

		return world;
	}

	/// <summary>
	/// Initialize the World and create the root Slot.
	/// </summary>
	public void Initialize()
	{
		if (_state != WorldState.Created) return;

		_state = WorldState.InitializingDataModel;
		AquaLogger.Log("World entering InitializingDataModel stage");

		// Create reference controller BEFORE anything else
		ReferenceController = new ReferenceController(this);

		// Create root Slot (uses ReferenceController)
		RootSlot = new Slot();
		RootSlot.SlotName.Value = "Root";
		RootSlot.Initialize(this);
		RegisterSlot(RootSlot);

		// Create sync controller
		SyncController = new SyncController(this);
		AquaLogger.Log("SyncController initialized");

		// Note: Godot scene attachment handled by WorldDriver wrapper
		// World itself is pure C# and doesn't use AddChild

		// Network session is started via StartSession() or JoinSession()
		// No automatic network initialization

		_state = WorldState.Running;
		AquaLogger.Log($"World '{WorldName.Value}' initialized successfully - now Running");
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

		ReferenceController?.RegisterObject(slot);

		if (!string.IsNullOrEmpty(slot.Tag.Value))
		{
			if (!_slotsByTag.TryGetValue(slot.Tag.Value, out var slots))
			{
				slots = new List<Slot>();
				_slotsByTag[slot.Tag.Value] = slots;
			}
			slots.Add(slot);
		}

		if (slot.Parent == null)
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
		ReferenceController?.RegisterObject(component);
	}

	/// <summary>
	/// Unregister a Component from the World.
	/// </summary>
	internal void UnregisterComponent(Component component)
	{
		if (component == null) return;
		ReferenceController?.UnregisterObject(component.ReferenceID);
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
				ReferenceController?.RegisterObject(user);
				AquaLogger.Log($"User added to world: {user.UserName.Value}");
				
				// Trigger user joined event
				TriggerUserJoinedEvent(user);
			}
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
			AquaLogger.Log($"User removed from world: {user.UserName.Value}");
			
			// Trigger user left event
			TriggerUserLeftEvent(user);
		}
	}

	/// <summary>
	/// Set the local user (client's own user).
	/// </summary>
	public void SetLocalUser(User user)
	{
		LocalUser = user;
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
	private void StartSessionNetwork(ushort port)
	{
		if (_session != null)
		{
			AquaLogger.Warn("Session already started");
			return;
		}

		try
		{
			_state = WorldState.InitializingNetwork;
			AquaLogger.Log("World entering InitializingNetwork stage (host)");

			_session = Session.NewSession(this, port);
			AuthorityID = -1; // This is the host

			_refIDAllocator.Reset();

			// Host doesn't need to wait for join grant, go straight to running
			if (_state == WorldState.InitializingNetwork)
			{
				_state = WorldState.Running;
			}

			AquaLogger.Log($"Started session network on port {port}");
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"Failed to start session network: {ex.Message}");
			_state = WorldState.Failed;
		}
	}

	/// <summary>
	/// Create the host user for a locally hosted session.
	/// </summary>
	public User CreateHostUser(string userName = null)
	{
		var (rangeStart, rangeEnd) = _refIDAllocator.GetAuthorityIDRange();

		// Allocate a unique RefID for the User via ReferenceController to avoid collisions
		var userRefId = ReferenceController.AllocateID();
		var hostUser = new User(this, userRefId);
		var resolvedName = string.IsNullOrWhiteSpace(userName) ? System.Environment.MachineName : userName;

		hostUser.UserName.Value = resolvedName;
		hostUser.UserID.Value = userRefId.ToString();
		hostUser.AllocationIDStart.Value = rangeStart;
		hostUser.AllocationIDEnd.Value = rangeEnd;
		hostUser.IsPresent.Value = true;
		hostUser.IsSilenced.Value = false;

		SetLocalUser(hostUser);
		AddUser(hostUser); // Add user to world and trigger OnUserJoined
		AquaLogger.Log($"Created host user '{resolvedName}' with RefID {userRefId}");
		return hostUser;
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
			_state = WorldState.InitializingNetwork;
			AquaLogger.Log("World entering InitializingNetwork stage (client)");

			_session = Session.JoinSession(this, new[] { address });

			// Client now waits for JoinGrant from authority
			_state = WorldState.WaitingForJoinGrant;
			AquaLogger.Log($"Joined session at {address} - waiting for JoinGrant");
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"Failed to join session: {ex.Message}");
			_state = WorldState.Failed;
		}
	}

	/// <summary>
	/// Called when client receives JoinGrant from authority.
	/// Transitions from WaitingForJoinGrant to Running.
	/// </summary>
	public void OnJoinGrantReceived()
	{
		if (_state == WorldState.WaitingForJoinGrant)
		{
			_state = WorldState.Running;
			AquaLogger.Log("World received JoinGrant - now Running");
		}
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
					AquaLogger.Error($"World: Error in synchronous action: {ex.Message}");
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

		var scaledDelta = delta * TimeScale;
		TotalTime += scaledDelta;
		LastDelta = (float)scaledDelta;

		// Stage 1: Process synchronous actions (immediate state changes)
		ProcessSynchronousActions();

		// Stage 2: Process world events (user joined/left, focus changes)
		RunWorldEvents();

		// Stage 3: Process input for this world (if focused)
		if (_focus == WorldFocus.Focused)
		{
			ProcessInput((float)scaledDelta);
		}

		// Stage 4: Update coroutines
		UpdateCoroutines((float)scaledDelta);

		// Stage 5: Update components (main update)
		UpdateComponents((float)scaledDelta);

		// Stage 5.5: Sync slot hooks so transforms propagate to the platform scene graph
		UpdateSlotHooks(RootSlot);

		// Stage 6: Process changed elements
		ProcessChangedElements();

		// Stage 7: Process destructions
		ProcessDestructions();

		// Stage 8: Update hooks (sync with platform layer)
		_updateManager?.ProcessHookUpdates((float)scaledDelta);

		// Stage 9: Clean up trash bin
		_trashBin?.Update();

		// Stage 10: Signal sync manager
		if (_session?.Sync != null)
		{
			_session.Sync.SignalWorldUpdateFinished();
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
		// Update all components that need updating
		UpdateSlotsRecursive(RootSlot, delta);
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
		// Process any pending component/slot destructions
		// This ensures destructions happen at a consistent time
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
					AquaLogger.Error($"World: Error in disposal synchronous action: {ex.Message}");
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