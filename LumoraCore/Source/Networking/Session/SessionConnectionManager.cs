// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Networking;
using Lumora.Core.Networking.LNL;
using Lumora.Core.Networking.Sync;
using LegacyJoinGrantData = Lumora.Core.Networking.Messages.JoinGrantData;
using LegacyJoinRequestData = Lumora.Core.Networking.Messages.JoinRequestData;
using LegacyJoinRejectData = Lumora.Core.Networking.Messages.JoinRejectData;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

/// <summary>
/// Manages connections and maps them to users.
/// 
/// </summary>
public class SessionConnectionManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<IConnection, User> _connectionToUser = new();
    private readonly Dictionary<User, IConnection> _userToConnection = new();
    private readonly HashSet<IConnection> _pendingConnections = new(); // Connections waiting for JoinRequest
    private readonly Dictionary<string, int> _pendingByIp = new();     // IP → count of pending connections

    public Session Session { get; private set; }
    public World World => Session?.World;

    public LNLListener Listener { get; private set; }
    public IConnection HostConnection { get; private set; }

    /// <summary>
    /// Event triggered when host connection is lost (client side).
    /// </summary>
    public event Action OnHostDisconnected;

    public SessionConnectionManager(Session session)
    {
        Session = session;
    }

    /// <summary>
    /// Start listening for connections (host only).
    /// </summary>
    public bool StartListener(ushort port)
    {
        if (Listener != null)
        {
            throw new InvalidOperationException("Listener already started");
        }

        Listener = new LNLListener(LNLManager.APP_ID, port, System.Net.IPAddress.Any);

        if (!Listener.IsInitialized)
        {
            LumoraLogger.Error("Failed to start listener");
            return false;
        }

        Listener.PeerConnected += OnPeerConnected;
        Listener.PeerDisconnected += OnPeerDisconnected;

        LumoraLogger.Log($"Listener started on port {port}");
        return true;
    }

    /// <summary>
    /// Connect to host as client.
    /// </summary>
    public async Task<bool> ConnectToAsync(IEnumerable<Uri> addresses)
    {
        var uri = addresses.FirstOrDefault();
        if (uri == null)
        {
            LumoraLogger.Error("No addresses provided");
            return false;
        }

        LumoraLogger.Log($"Connecting to {uri}");

        var connection = new LNLConnection(
            LNLManager.APP_ID,
            uri,
            dontRoute: false,
            bindIP: System.Net.IPAddress.Any
        );

        var taskCompletionSource = new TaskCompletionSource<bool>();

        connection.Connected += (c) =>
        {
            LumoraLogger.Log($"Connected to {c.Address}");
            taskCompletionSource.TrySetResult(true);
        };

        connection.ConnectionFailed += (c) =>
        {
            LumoraLogger.Error($"Connection failed: {c.FailReason}");
            taskCompletionSource.TrySetResult(false);
        };

        connection.Closed += (c) =>
        {
            LumoraLogger.Warn($"Connection closed: {c.FailReason}");
            taskCompletionSource.TrySetResult(false);
            
            // Trigger host disconnected event if this was the host connection
            if (HostConnection == c)
            {
                OnHostDisconnected?.Invoke();
            }
        };

        connection.DataReceived += (data, length) => OnConnectionDataReceived(connection, data, length);
        connection.Connect(null);

        var timeoutTask = Task.Delay(10000);
        var completedTask = await Task.WhenAny(taskCompletionSource.Task, timeoutTask);
        bool success = completedTask == taskCompletionSource.Task && taskCompletionSource.Task.Result;

        if (success)
        {
            HostConnection = connection;

            // Send JoinRequest to host with our username
            SendJoinRequest();
            return true;
        }

        if (completedTask == timeoutTask)
        {
            LumoraLogger.Warn($"Connection timed out: {uri}");
        }

        connection.Close();
        return false;
    }

    /// <summary>
    /// Send JoinRequest to host (client only).
    /// </summary>
    private void SendJoinRequest()
    {
        if (HostConnection == null)
        {
            LumoraLogger.Error("SendJoinRequest: No host connection");
            return;
        }

        var requestData = new LegacyJoinRequestData
        {
            UserName = Environment.MachineName,
            MachineID = Environment.MachineName,
            UserID = "",
            HeadDevice = (byte)(Engine.Current?.InputInterface?.CurrentHeadOutputDevice ?? HeadOutputDevice.Screen)
        };

        var controlMessage = new ControlMessage(ControlMessage.Message.JoinRequest)
        {
            Payload = requestData.Encode()
        };

        byte[] encoded = controlMessage.Encode();
        HostConnection.Send(encoded, encoded.Length, reliable: true, background: false);

        LumoraLogger.Log($"Sent JoinRequest to host - UserName='{requestData.UserName}'");
    }

    private void OnPeerConnected(LNLPeer peer)
    {
        var ip = GetIpKey(peer);

        // Cap both total pending connections and pending per source IP. Without
        // this, a single attacker can open thousands of LNL connections that sit
        // forever before sending JoinRequest and exhaust host memory/sockets.
        bool admit;
        lock (_lock)
        {
            if (_pendingConnections.Count >= NetworkLimits.MaxPendingConnections)
            {
                admit = false;
            }
            else if (_pendingByIp.TryGetValue(ip, out var perIp) && perIp >= NetworkLimits.MaxPendingPerIP)
            {
                admit = false;
            }
            else
            {
                admit = true;
                _pendingConnections.Add(peer);
                _pendingByIp[ip] = _pendingByIp.TryGetValue(ip, out var n) ? n + 1 : 1;
            }
        }

        if (!admit)
        {
            LumoraLogger.Warn($"Peer {peer.Identifier} from {ip} rejected: pending-connection limit reached.");
            peer.Close();
            return;
        }

        LumoraLogger.Log($"Peer connected: {peer.Identifier}");
        peer.DataReceived += (data, length) => OnConnectionDataReceived(peer, data, length);
        LumoraLogger.Log($"Peer {peer.Identifier} added to pending - waiting for JoinRequest");
    }

    private void OnPeerDisconnected(LNLPeer peer)
    {
        LumoraLogger.Log($"Peer disconnected: {peer.Identifier}");

        Session.AssetTransferer?.ConnectionClosed(peer);

        lock (_lock)
        {
            RemovePendingLocked(peer);
            if (_connectionToUser.TryGetValue(peer, out var user))
            {
                _connectionToUser.Remove(peer);
                _userToConnection.Remove(user);
                RemoveUser(user);
            }
        }
    }

    /// <summary>Remove a connection from the pending set and decrement its per-IP count. Caller must hold _lock.</summary>
    private bool RemovePendingLocked(IConnection connection)
    {
        if (!_pendingConnections.Remove(connection))
            return false;

        var ip = GetIpKey(connection);
        if (_pendingByIp.TryGetValue(ip, out var n))
        {
            if (n <= 1) _pendingByIp.Remove(ip);
            else _pendingByIp[ip] = n - 1;
        }
        return true;
    }

    private static string GetIpKey(IConnection connection)
        => connection?.IP?.ToString() ?? connection?.Identifier ?? "<unknown>";

    /// <summary>
    /// Handle incoming JoinRequest from client.
    /// </summary>
    public void HandleJoinRequest(IConnection connection, LegacyJoinRequestData requestData)
    {
        bool isPending;
        lock (_lock)
        {
            isPending = RemovePendingLocked(connection);
        }

        if (!isPending)
        {
            LumoraLogger.Warn($"HandleJoinRequest: Connection {connection.Identifier} was not pending");
            SendJoinReject(connection, "Duplicate or stale join request");
            return;
        }

        LumoraLogger.Log($"HandleJoinRequest: Received from {connection.Identifier} - UserName='{requestData.UserName}'");
        var rejectReason = ValidateJoinRequest(connection, requestData);
        if (rejectReason != null)
        {
            LumoraLogger.Warn($"HandleJoinRequest: Rejected {connection.Identifier} - {rejectReason}");
            SendJoinReject(connection, rejectReason);
            return;
        }

        SendJoinGrant(connection, requestData.UserName, requestData.MachineID);
    }

    private string? ValidateJoinRequest(IConnection connection, LegacyJoinRequestData requestData)
    {
        if (connection == null || !connection.IsOpen)
            return "Connection is not open";

        if (World == null || World.IsDisposed || World.IsDestroyed)
            return "World is not available";

        if (!World.IsAuthority)
            return "Only the authority can approve joins";

        if (!World.Configuration.AllowJoin)
            return "This world is not accepting joins";

        var maxUsers = GetEffectiveMaxUsers();
        if (World.UserCount >= maxUsers)
            return $"Session is full ({World.UserCount}/{maxUsers})";

        if (!World.RefIDAllocator.CanAllocateMoreUsers)
            return "Session has no remaining user allocation slots";

        if (!string.IsNullOrWhiteSpace(requestData.UserName) && requestData.UserName.Length > 32)
            return "Username is too long";

        if (!string.IsNullOrWhiteSpace(requestData.MachineID) && requestData.MachineID.Length > 128)
            return "Machine identifier is too long";

        if (!string.IsNullOrWhiteSpace(requestData.UserID))
        {
            lock (_lock)
            {
                if (_connectionToUser.Values.Any(u => u.UserID?.Value == requestData.UserID))
                    return "User is already connected";
            }
        }

        return null;
    }

    private int GetEffectiveMaxUsers()
    {
        var metadataMax = Session?.Metadata?.MaxUsers ?? 0;
        var configMax = World?.Configuration?.MaxUsers ?? 0;
        var allocatorMax = World?.RefIDAllocator?.MaxUserCount ?? 1;
        var requestedMax = metadataMax > 0 ? metadataMax : configMax;
        return global::System.Math.Clamp(requestedMax > 0 ? requestedMax : allocatorMax, 1, allocatorMax);
    }

    private void SendJoinReject(IConnection connection, string reason)
    {
        if (connection == null)
            return;

        var rejectData = new LegacyJoinRejectData
        {
            Reason = string.IsNullOrWhiteSpace(reason) ? "Join rejected" : reason
        };

        var controlMessage = new ControlMessage(ControlMessage.Message.JoinReject)
        {
            Payload = rejectData.Encode()
        };

        var encoded = controlMessage.Encode();
        connection.Send(encoded, encoded.Length, reliable: true, background: false);

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            connection.Close();
        });
    }

    private void SendJoinGrant(IConnection connection, string userName, string machineId)
    {
        if (!World.IsAuthority)
        {
            LumoraLogger.Error("Only authority can send JoinGrant");
            return;
        }

        // Allocate ID range using RefIDAllocator
        var (allocStart, allocEnd) = World.RefIDAllocator.AllocateUserIDRange();

        // Use the start of the range as the user's RefID
        RefID userRefID = allocStart;
        ulong userID = (ulong)userRefID;

        var grantData = new LegacyJoinGrantData
        {
            AssignedUserID = userID,
            AllocationIDStart = (ulong)allocStart,
            AllocationIDEnd = (ulong)allocEnd,
            MaxUsers = GetEffectiveMaxUsers(),
            WorldTime = World.TotalTime,
            StateVersion = World.StateVersion
        };

        var controlMessage = new ControlMessage(ControlMessage.Message.JoinGrant)
        {
            Payload = grantData.Encode()
        };

        byte[] encoded = controlMessage.Encode();
        connection.Send(encoded, encoded.Length, reliable: true, background: false);

        LumoraLogger.Log($"Sent JoinGrant to {connection.Identifier} - UserID: {userID}, UserName: '{userName}'");

        // Create user instance, populate fields, then add to the user bag for initialization
        var user = new User();
        user.UserID.Value = userID.ToString();
        user.UserName.Value = !string.IsNullOrEmpty(userName) ? userName : $"Guest {userRefID.GetUserByte()}";
        user.MachineID.Value = machineId ?? "";
        user.AllocationIDStart.Value = (ulong)allocStart;
        user.AllocationIDEnd.Value = (ulong)allocEnd;
        user.AllocationID.Value = userRefID.GetUserByte();
        user.IsPresent.Value = true;
        user.PresentInWorld.Value = true;

        lock (_lock)
        {
            _connectionToUser[connection] = user;
            _userToConnection[user] = connection;
        }
        LumoraLogger.Log($"SendJoinGrant: Added connection-user mapping for '{userName}'");

        World.AddUserToBag(user, userRefID, isNewlyCreated: true);
        LumoraLogger.Log($"SendJoinGrant: User '{userName}' added to world");

        if (Session.Sync != null)
        {
            LumoraLogger.Log($"SendJoinGrant: Calling QueueUserForInitialization for '{userName}'");
            Session.Sync.QueueUserForInitialization(user);
        }
        else
        {
            LumoraLogger.Error("SendJoinGrant: Session.Sync is NULL - cannot queue user for initialization!");
        }
    }

    private void RemoveUser(User user)
    {
        World.RemoveUser(user);
        user.Dispose();
    }

    private void OnConnectionDataReceived(IConnection connection, byte[] data, int length)
    {
        if (Session?.Sync == null)
            return;

        var raw = new RawInMessage
        {
            Data = data,
            Offset = 0,
            Length = length,
            Sender = connection
        };

        Session.Sync.QueueRawIncoming(raw);
    }

    /// <summary>
    /// Get user for connection.
    /// </summary>
    public bool TryGetUser(IConnection connection, out User user)
    {
        lock (_lock)
        {
            return _connectionToUser.TryGetValue(connection, out user);
        }
    }

    /// <summary>
    /// Get connection for user.
    /// </summary>
    public bool TryGetConnection(User user, out IConnection connection)
    {
        lock (_lock)
        {
            return _userToConnection.TryGetValue(user, out connection);
        }
    }

    /// <summary>
    /// Get all connections for broadcasting.
    /// </summary>
    public List<IConnection> GetAllConnections()
    {
        lock (_lock)
        {
            return _connectionToUser.Keys.ToList();
        }
    }

    /// <summary>
    /// Broadcast data to specified connections.
    /// </summary>
    public void Broadcast(byte[] data, List<IConnection> targets, bool reliable)
    {
        foreach (var target in targets)
        {
            target.Send(data, data.Length, reliable, background: false);
        }
    }

    /// <summary>
    /// Poll network events for this session's connections and listeners.
    /// Must be called every frame by the world update loop.
    /// </summary>
    public void Poll()
    {
        Listener?.Poll();
        (HostConnection as LNLConnection)?.Poll();
    }

    public void Dispose()
    {
        Listener?.Dispose();
        HostConnection?.Close();

        lock (_lock)
        {
            _pendingConnections.Clear();
            _pendingByIp.Clear();
            _connectionToUser.Clear();
            _userToConnection.Clear();
        }
    }
}
