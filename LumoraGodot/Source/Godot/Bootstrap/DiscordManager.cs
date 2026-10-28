// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using DiscordRPC;
using Godot;
using Newtonsoft.Json;
using LumoraLogger = Lumora.Core.Logging.Logger;
using LumoraWorld = Lumora.Core.World;

namespace Lumora.Source.Godot.Bootstrap;

public static class DiscordManager
{
    // Called on the main thread. False keeps the invite pending while another world is loading.
    public static Func<Uri, bool>? JoinRequested;
    public static bool ExitAfterHandoff { get; private set; }
    public static bool Initialized => _client is { IsDisposed: false, IsInitialized: true };

    private static DiscordRpcClient? _client;
    private static DiscordLaunchRelay? _launchRelay;
    private static DateTime _sessionStart = DateTime.UtcNow;
    private static LumoraWorld? _presenceWorld;
    private static string? _presenceSession;
    private static string _lastPresenceKey = string.Empty;
    private static bool _joinRequestsAllowed;
    private static readonly DiscordInviteQueue Invites = new();

    // Runs in the first autoload, before Lumora opens its stores or starts the world bootstrap.
    public static bool PrepareLaunch(string dataDirectory)
    {
        if (!OS.HasFeature("pc") || LaunchArgs.HasFlag("--Lumora-DebugConsole") || LaunchArgs.HasFlag("--headless"))
            return true;
        string? activation = DiscordJoinLink.LaunchActivation(LaunchArgs.All());
        try
        {
            _launchRelay = DiscordLaunchRelay.Start(dataDirectory, activation ?? string.Empty);
            if (_launchRelay == null)
            {
                ExitAfterHandoff = true;
                LumoraLogger.Log("DiscordManager: launch handed to the running client");
                return false;
            }
            if (DiscordJoinLink.TryParse(activation, out var uri)) Invites.Enqueue(uri);
            return true;
        }
        catch (Exception ex)
        {
            ExitAfterHandoff = true;
            GD.PushError($"Could not hand the launch to the running Lumora client: {ex.Message}");
            return false;
        }
    }

    public static void StopLaunchRelay()
    {
        _launchRelay?.Dispose();
        _launchRelay = null;
    }

    public static void Initialize()
    {
        if (Initialized || !OS.HasFeature("pc") || LaunchArgs.HasFlag("--headless")) return;
        try
        {
            // Event delivery is owned by Poll, so world state and join callbacks stay on the main thread.
            _client = new DiscordRpcClient(DiscordJoinLink.AppId, autoEvents: false);
            string command = DiscordJoinLink.LaunchCommand(OS.GetExecutablePath(),
                OS.HasFeature("editor") ? ProjectSettings.GlobalizePath("res://") : null,
                OperatingSystem.IsWindows(), HeadsetDeviceMode.IsSteamFrame);
            try
            {
                if (!_client.RegisterUriScheme(executable: command))
                    LumoraLogger.Warn("DiscordManager: URI registration failed; presence remains available without invites");
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"DiscordManager: URI registration unavailable; presence continues without invites: {ex.Message}");
            }
            _client.OnReady += (_, _) =>
            {
                _lastPresenceKey = string.Empty;
                if (_client?.HasRegisteredUriScheme == true)
                    _client.SetSubscription(EventType.Join | EventType.JoinRequest);
                LumoraLogger.Log("DiscordManager: connected");
            };
            _client.OnError += (_, e) =>
            {
                _lastPresenceKey = string.Empty;
                LumoraLogger.Warn($"DiscordManager: error {e.Code}: {e.Message}");
            };
            _client.OnJoin += (_, e) =>
            {
                if (DiscordJoinLink.TryParse(e.Secret, out var uri)) Invites.Enqueue(uri);
                else LumoraLogger.Warn("DiscordManager: ignored an invalid join secret");
            };
            _client.OnJoinRequested += (_, e) => _client?.Respond(e, _joinRequestsAllowed);
            if (!_client.Initialize()) throw new InvalidOperationException("RPC connection could not start");
            _lastPresenceKey = string.Empty;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"DiscordManager: initialization failed: {ex.Message}");
            CloseClient();
        }
    }

    public static void Poll()
    {
        while (_launchRelay?.TryRead(out var activation) == true)
        {
            if (DiscordJoinLink.TryParse(activation, out var uri)) Invites.Enqueue(uri);
            else
            {
                // Some Discord launch URIs carry no secret. Reconnect the existing process so Discord
                // can deliver its pending ACTIVITY_JOIN after the new RPC handshake.
                CloseClient();
                Initialize();
            }
            if (!HeadsetDeviceMode.IsHeadset) DisplayServer.WindowMoveToForeground();
        }
        try { if (Initialized) _client!.Invoke(); }
        catch (Exception ex) { LumoraLogger.Warn($"DiscordManager: callback failed: {ex.Message}"); }
        try { Invites.Dispatch(JoinRequested); }
        catch (Exception ex) { LumoraLogger.Warn($"DiscordManager: could not start the invited session: {ex.Message}"); }
    }

    public static void UpdatePresence(LumoraWorld? world)
    {
        if (!Initialized) return;
        try
        {
            if (world?.IsDisposed == true || world?.IsUserspace == true) world = null;
            string? session = world?.SessionID?.Value;
            if (!ReferenceEquals(world, _presenceWorld) || session != _presenceSession)
            {
                _presenceWorld = world;
                _presenceSession = session;
                _sessionStart = DateTime.UtcNow;
            }
            var settings = world?.Configuration;
            var state = world == null ? null : new DiscordPresenceState(session ?? string.Empty,
                world.WorldName?.Value ?? world.Name, world.Mode.ToString(), world.UserCount,
                settings?.MaxUsers.Value ?? 1, settings?.IsPublic.Value ?? false,
                settings?.AllowJoin.Value ?? false, world.Session != null, world.SessionURLs,
                Closed: settings == null || settings.AccessLevel.Value == LumoraWorld.WorldAccessLevel.Private);
            var presence = DiscordPresence.Build(state, _sessionStart, Lumora.Core.EngineVersion.VersionString);
            if (!_client!.HasRegisteredUriScheme) presence.Secrets = null;
            _joinRequestsAllowed = presence.Secrets != null && presence.Party.Privacy == Party.PrivacySetting.Public;
            // Include session identity, address and timestamps: equally named worlds can be different sessions.
            string key = JsonConvert.SerializeObject(presence);
            if (key == _lastPresenceKey) return;
            _client.SetPresence(presence);
            _lastPresenceKey = key;
        }
        catch (Exception ex) { LumoraLogger.Warn($"DiscordManager: presence update failed: {ex.Message}"); }
    }

    private static void CloseClient()
    {
        var client = _client;
        _client = null;
        _joinRequestsAllowed = false;
        _lastPresenceKey = string.Empty;
        try
        {
            if (client is { IsDisposed: false })
            {
                if (client.IsInitialized) client.ClearPresence();
                client.Dispose();
            }
        }
        catch (Exception ex) { LumoraLogger.Warn($"DiscordManager: shutdown failed: {ex.Message}"); }
    }

    public static void Shutdown()
    {
        CloseClient();
        JoinRequested = null;
        Invites.Clear();
        _presenceWorld = null;
        _presenceSession = null;
    }
}
