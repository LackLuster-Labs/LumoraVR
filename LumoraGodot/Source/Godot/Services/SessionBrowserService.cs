using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Lumora.Core;
using Lumora.Core.Components.Network;
using Lumora.Core.Networking.Session;
using Lumora.Godot.UI;
using AquaLogger = Lumora.Core.Logging.Logger;
using LumoraEngine = Lumora.Core.Engine;
using HttpClient = System.Net.Http.HttpClient;

namespace Aquamarine.Source.Godot.Services;

/// <summary>
/// Service that bridges SessionBrowser (core) with WorldBrowser (UI).
/// Handles both LAN discovery and public session server queries.
/// </summary>
public partial class SessionBrowserService : Node
{
	// Session server configuration
	[Export] public string SessionServerUrl { get; set; } = "http://localhost:8040";
	[Export] public float PublicRefreshInterval { get; set; } = 10f; // seconds

	// References
	private WorldBrowser _worldBrowser;
	private SessionBrowser _sessionBrowser;
	private HttpClient _httpClient;

	// State
	private float _publicRefreshTimer;
	private bool _isScanning;
	private readonly Dictionary<string, WorldBrowser.WorldInfo> _discoveredWorlds = new();

	// Events
	public event Action<string> OnJoinStarted;
	public event Action<World> OnJoinSuccess;
	public event Action<string> OnJoinFailed;

	public override void _Ready()
	{
		_httpClient = new HttpClient();
		_httpClient.Timeout = TimeSpan.FromSeconds(5);

		AquaLogger.Log("SessionBrowserService: Initialized");
	}

	public override void _ExitTree()
	{
		StopScanning();
		_httpClient?.Dispose();
	}

	/// <summary>
	/// Connect this service to a WorldBrowser UI.
	/// </summary>
	public void ConnectToUI(WorldBrowser browser)
	{
		_worldBrowser = browser;

		if (_worldBrowser != null)
		{
			_worldBrowser.WorldSelected += OnWorldSelected;
			AquaLogger.Log("SessionBrowserService: Connected to WorldBrowser UI");
		}
	}

	/// <summary>
	/// Start scanning for sessions (LAN + public server).
	/// </summary>
	public void StartScanning()
	{
		if (_isScanning)
			return;

		_isScanning = true;
		_worldBrowser?.ClearWorlds();
		_discoveredWorlds.Clear();

		// Start LAN discovery via SessionBrowser component
		StartLANDiscovery();

		// Immediately query public server
		_ = RefreshPublicSessionsAsync();

		AquaLogger.Log("SessionBrowserService: Started scanning");
	}

	/// <summary>
	/// Stop scanning for sessions.
	/// </summary>
	public void StopScanning()
	{
		if (!_isScanning)
			return;

		_isScanning = false;

		// Stop LAN discovery
		if (_sessionBrowser != null)
		{
			_sessionBrowser.OnSessionFound -= OnLANSessionFound;
			_sessionBrowser.OnSessionLost -= OnLANSessionLost;
			_sessionBrowser.OnSessionUpdated -= OnLANSessionUpdated;
			_sessionBrowser.StopScanning();
		}

		AquaLogger.Log("SessionBrowserService: Stopped scanning");
	}

	public override void _Process(double delta)
	{
		if (!_isScanning)
			return;

		// Periodic public server refresh
		_publicRefreshTimer += (float)delta;
		if (_publicRefreshTimer >= PublicRefreshInterval)
		{
			_publicRefreshTimer = 0;
			_ = RefreshPublicSessionsAsync();
		}
	}

	#region LAN Discovery

	private void StartLANDiscovery()
	{
		// Get or create SessionBrowser from focused world
		var world = LumoraEngine.Current?.WorldManager?.FocusedWorld;
		if (world == null)
		{
			AquaLogger.Warn("SessionBrowserService: No focused world for LAN discovery");
			return;
		}

		// Reuse existing SessionBrowser if we already have one and it's valid
		if (_sessionBrowser != null && _sessionBrowser.IsScanning.Value)
		{
			AquaLogger.Log("SessionBrowserService: LAN discovery already running");
			return;
		}

		// Find existing or create new SessionBrowser
		if (_sessionBrowser == null)
		{
			_sessionBrowser = world.RootSlot?.GetComponentInChildren<SessionBrowser>();
			if (_sessionBrowser == null)
			{
				var browserSlot = world.RootSlot?.AddSlot("SessionBrowser");
				_sessionBrowser = browserSlot?.AttachComponent<SessionBrowser>();
			}

			// Only attach events once when we first get the browser
			if (_sessionBrowser != null)
			{
				_sessionBrowser.OnSessionFound += OnLANSessionFound;
				_sessionBrowser.OnSessionLost += OnLANSessionLost;
				_sessionBrowser.OnSessionUpdated += OnLANSessionUpdated;
			}
		}

		if (_sessionBrowser != null && !_sessionBrowser.IsScanning.Value)
		{
			_sessionBrowser.StartScanning();
			AquaLogger.Log("SessionBrowserService: LAN discovery started");
		}
	}

	private void OnLANSessionFound(SessionListEntry entry)
	{
		var worldInfo = ConvertToWorldInfo(entry, "lan");
		_discoveredWorlds[entry.SessionId] = worldInfo;
		CallDeferred(nameof(UpdateUIDeferred), worldInfo.Id);
	}

	private void OnLANSessionLost(string sessionId)
	{
		if (_discoveredWorlds.Remove(sessionId))
		{
			CallDeferred(nameof(RefreshUIDeferred));
		}
	}

	private void OnLANSessionUpdated(SessionListEntry entry)
	{
		var worldInfo = ConvertToWorldInfo(entry, "lan");
		_discoveredWorlds[entry.SessionId] = worldInfo;
		CallDeferred(nameof(UpdateUIDeferred), worldInfo.Id);
	}

	#endregion

	#region Public Session Server

	private async Task RefreshPublicSessionsAsync()
	{
		try
		{
			var url = $"{SessionServerUrl}/sessions";
			var response = await _httpClient.GetStringAsync(url);

			var sessions = JsonSerializer.Deserialize<List<PublicSessionInfo>>(response,
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

			if (sessions != null)
			{
				foreach (var session in sessions)
				{
					var worldInfo = ConvertToWorldInfo(session);
					_discoveredWorlds[session.SessionIdentifier] = worldInfo;
				}

				CallDeferred(nameof(RefreshUIDeferred));
				AquaLogger.Log($"SessionBrowserService: Found {sessions.Count} public sessions");
			}
		}
		catch (HttpRequestException ex)
		{
			// Server not available - this is normal if no public server is running
			AquaLogger.Log($"SessionBrowserService: Public server not available ({ex.Message})");
		}
		catch (TaskCanceledException)
		{
			// Timeout
		}
		catch (Exception ex)
		{
			AquaLogger.Warn($"SessionBrowserService: Error fetching public sessions: {ex.Message}");
		}
	}

	#endregion

	#region UI Updates (must run on main thread)

	private void UpdateUIDeferred(string worldId)
	{
		if (_worldBrowser != null && _discoveredWorlds.TryGetValue(worldId, out var info))
		{
			_worldBrowser.UpdateWorld(info);
		}
	}

	private void RefreshUIDeferred()
	{
		if (_worldBrowser == null)
			return;

		_worldBrowser.ClearWorlds();
		foreach (var world in _discoveredWorlds.Values)
		{
			_worldBrowser.UpdateWorld(world);
		}
	}

	#endregion

	#region Join Flow

	private SessionServerClient _natPunchClient;

	private void OnWorldSelected(WorldBrowser.WorldInfo world)
	{
		if (world == null)
		{
			AquaLogger.Warn("SessionBrowserService: OnWorldSelected called with null world");
			return;
		}
		AquaLogger.Log($"SessionBrowserService: User selected world '{world.Name}'");
		JoinWorld(world);
	}

	/// <summary>
	/// Join a world/session.
	/// </summary>
	public void JoinWorld(WorldBrowser.WorldInfo world)
	{
		if (world == null)
			return;

		// Prevent duplicate joins - check if already loading
		var loadingService = LumoraEngine.Current?.WorldLoadingService;
		if (loadingService?.IsLoading == true)
		{
			AquaLogger.Warn("SessionBrowserService: Already loading a world, ignoring join request");
			return;
		}

		// Check if already in this world
		var worldManager = LumoraEngine.Current?.WorldManager;
		if (worldManager != null)
		{
			foreach (var existingWorld in worldManager.Worlds)
			{
				// Check by session ID or address match
				if (existingWorld?.Session?.Metadata?.SessionId == world.Id)
				{
					AquaLogger.Warn($"SessionBrowserService: Already connected to world '{world.Name}'");
					OnJoinFailed?.Invoke("Already connected to this world");
					return;
				}
			}
		}

		OnJoinStarted?.Invoke(world.Name);

		// Check if this is a LAN session with direct connection info
		var lanEntry = _sessionBrowser?.GetSession(world.Id);
		if (lanEntry?.JoinUrl != null)
		{
			// Direct LAN connection
			_ = JoinDirectAsync(world.Name, lanEntry.JoinUrl);
			return;
		}

		// Check if it's a public session (need NAT punch)
		if (world.Category == "featured" || world.Category == "active")
		{
			// Use NAT punch for public sessions
			_ = JoinViaNATPunchAsync(world.Name, world.Id);
			return;
		}

		// Try to parse as direct URL
		if (Uri.TryCreate(world.Id, UriKind.Absolute, out var joinUri))
		{
			_ = JoinDirectAsync(world.Name, joinUri);
			return;
		}

		OnJoinFailed?.Invoke("No valid connection method available");
	}

	/// <summary>
	/// Join directly using a connection URL (LAN or direct IP).
	/// Uses WorldLoadingService for progress tracking.
	/// </summary>
	private async Task JoinDirectAsync(string worldName, Uri joinUri)
	{
		try
		{
			var loadingService = LumoraEngine.Current?.WorldLoadingService;
			if (loadingService == null)
			{
				// Fall back to direct join if loading service not available
				await JoinDirectLegacyAsync(worldName, joinUri);
				return;
			}

			AquaLogger.Log($"SessionBrowserService: Direct join to {joinUri} via WorldLoadingService");

			// Use loading service for background loading with progress
			var operation = loadingService.JoinSessionAsync(worldName, joinUri, focusWhenReady: true);
			if (operation == null)
			{
				OnJoinFailed?.Invoke("Already loading another world");
				return;
			}

			// Wait for completion
			var world = await operation.Task;

			if (world != null)
			{
				OnJoinSuccess?.Invoke(world);
				AquaLogger.Log($"SessionBrowserService: Successfully joined '{worldName}'");
			}
			else if (operation.IsCancelled)
			{
				OnJoinFailed?.Invoke("Join cancelled");
			}
			else
			{
				OnJoinFailed?.Invoke(operation.ErrorMessage ?? "Failed to join session");
			}
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"SessionBrowserService: Direct join failed: {ex.Message}");
			OnJoinFailed?.Invoke(ex.Message);
		}
	}

	/// <summary>
	/// Legacy direct join without loading indicator.
	/// </summary>
	private async Task JoinDirectLegacyAsync(string worldName, Uri joinUri)
	{
		var worldManager = LumoraEngine.Current?.WorldManager;
		if (worldManager == null)
		{
			OnJoinFailed?.Invoke("WorldManager not available");
			return;
		}

		var host = joinUri.Host;
		var port = (ushort)(joinUri.Port > 0 ? joinUri.Port : 7777);

		AquaLogger.Log($"SessionBrowserService: Legacy direct join to {host}:{port}");

		var joinedWorld = await worldManager.JoinSessionAsync(worldName, host, port);

		if (joinedWorld != null)
		{
			worldManager.FocusWorld(joinedWorld);
			OnJoinSuccess?.Invoke(joinedWorld);
		}
		else
		{
			OnJoinFailed?.Invoke("Failed to join session");
		}
	}

	/// <summary>
	/// Join a session via NAT punch through the public server.
	/// </summary>
	private async Task JoinViaNATPunchAsync(string worldName, string sessionId)
	{
		try
		{
			AquaLogger.Log($"SessionBrowserService: Initiating NAT punch for session {sessionId}");

			// Create NAT punch client
			_natPunchClient?.Dispose();
			_natPunchClient = new SessionServerClient(
				Session.SessionServerAddress,
				Session.SessionServerPort);

			System.Net.IPEndPoint punchedEndpoint = null;
			var punchCompleted = new TaskCompletionSource<bool>();

			_natPunchClient.OnNATPunchSuccess += (endpoint) =>
			{
				punchedEndpoint = endpoint;
				punchCompleted.TrySetResult(true);
			};

			// Request NAT punch
			if (!await _natPunchClient.RequestNATPunchAsync(sessionId))
			{
				OnJoinFailed?.Invoke("Failed to initiate NAT punch");
				return;
			}

			// Wait for NAT punch to complete (timeout 10 seconds)
			var timeoutTask = Task.Delay(10000);
			var completedTask = await Task.WhenAny(punchCompleted.Task, timeoutTask);

			if (completedTask == timeoutTask || punchedEndpoint == null)
			{
				AquaLogger.Warn("SessionBrowserService: NAT punch timeout");
				OnJoinFailed?.Invoke("NAT punch timeout - host may be unreachable");
				_natPunchClient?.Dispose();
				_natPunchClient = null;
				return;
			}

			AquaLogger.Log($"SessionBrowserService: NAT punch succeeded, connecting to {punchedEndpoint}");

			// Connect to the punched endpoint using WorldLoadingService
			var joinUri = new Uri($"lnl://{punchedEndpoint.Address}:{punchedEndpoint.Port}");
			await JoinDirectAsync(worldName, joinUri);

			// Clean up NAT punch client
			_natPunchClient?.Dispose();
			_natPunchClient = null;
		}
		catch (Exception ex)
		{
			AquaLogger.Error($"SessionBrowserService: NAT punch join failed: {ex.Message}");
			OnJoinFailed?.Invoke(ex.Message);
			_natPunchClient?.Dispose();
			_natPunchClient = null;
		}
	}

	#endregion

	#region Conversion Helpers

	private static WorldBrowser.WorldInfo ConvertToWorldInfo(SessionListEntry entry, string category)
	{
		var info = new WorldBrowser.WorldInfo
		{
			Id = entry.SessionId,
			Name = entry.Name ?? "Unknown Session",
			Host = entry.HostUsername ?? "Unknown",
			UserCount = entry.ActiveUsers,
			MaxUsers = entry.MaxUsers,
			ThumbnailUrl = entry.ThumbnailUrl ?? "",
			Category = category
		};

		// Decode base64 thumbnail if available
		if (!string.IsNullOrEmpty(entry.ThumbnailBase64))
		{
			info.Thumbnail = DecodeBase64Thumbnail(entry.ThumbnailBase64);
		}

		return info;
	}

	private static WorldBrowser.WorldInfo ConvertToWorldInfo(PublicSessionInfo session)
	{
		return new WorldBrowser.WorldInfo
		{
			Id = session.SessionIdentifier,
			Name = session.Name ?? "Unknown Session",
			Host = "Public Server",
			UserCount = 0, // Not provided by basic API
			MaxUsers = 16,
			ThumbnailUrl = "",
			Category = session.Direct ? "active" : "featured"
		};
	}

	/// <summary>
	/// Decode a base64 thumbnail string into a Godot Texture2D.
	/// </summary>
	private static Texture2D DecodeBase64Thumbnail(string base64)
	{
		try
		{
			var bytes = Convert.FromBase64String(base64);
			var image = new Image();

			// Try JPEG first (our capture format), then PNG
			var error = image.LoadJpgFromBuffer(bytes);
			if (error != Error.Ok)
			{
				error = image.LoadPngFromBuffer(bytes);
			}

			if (error != Error.Ok)
			{
				AquaLogger.Warn("SessionBrowserService: Failed to decode thumbnail image");
				return null;
			}

			return ImageTexture.CreateFromImage(image);
		}
		catch (Exception ex)
		{
			AquaLogger.Warn($"SessionBrowserService: Thumbnail decode error - {ex.Message}");
			return null;
		}
	}

	#endregion
}

/// <summary>
/// Session info from public session server API.
/// </summary>
public class PublicSessionInfo
{
	public string Name { get; set; }
	public string SessionIdentifier { get; set; }
	public string WorldIdentifier { get; set; }
	public bool Direct { get; set; }
}
