using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Networking.LNL;
using Lumora.Core.Networking.Sync;
using LegacyJoinGrantData = Lumora.Core.Networking.Messages.JoinGrantData;
using LegacyJoinRequestData = Lumora.Core.Networking.Messages.JoinRequestData;
using AquaLogger = Lumora.Core.Logging.Logger;

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
            AquaLogger.Error("Failed to start listener");
            return false;
        }

        Listener.PeerConnected += OnPeerConnected;
        Listener.PeerDisconnected += OnPeerDisconnected;

        AquaLogger.Log($"Listener started on port {port}");
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
            AquaLogger.Error("No addresses provided");
            return false;
        }

        AquaLogger.Log($"Connecting to {uri}");

        var connection = new LNLConnection(
            LNLManager.APP_ID,
            uri,
            dontRoute: false,
            bindIP: System.Net.IPAddress.Any
        );

        var taskCompletionSource = new TaskCompletionSource<bool>();

        connection.Connected += (c) =>
        {
            AquaLogger.Log($"Connected to {c.Address}");
            taskCompletionSource.TrySetResult(true);
        };

        connection.ConnectionFailed += (c) =>
        {
            AquaLogger.Error($"Connection failed: {c.FailReason}");
            taskCompletionSource.TrySetResult(false);
        };

        connection.Closed += (c) =>
        {
            AquaLogger.Warn($"Connection closed: {c.FailReason}");
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
            AquaLogger.Warn($"Connection timed out: {uri}");
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
            AquaLogger.Error("SendJoinRequest: No host connection");
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

        AquaLogger.Log($"Sent JoinRequest to host - UserName='{requestData.UserName}'");
    }

    private void OnPeerConnected(LNLPeer peer)
    {
        AquaLogger.Log($"Peer connected: {peer.Identifier}");

        peer.DataReceived += (data, length) => OnConnectionDataReceived(peer, data, length);

        // Add to pending connections - will send JoinGrant when JoinRequest is received
        lock (_lock)
        {
            _pendingConnections.Add(peer);
        }
        AquaLogger.Log($"Peer {peer.Identifier} added to pending - waiting for JoinRequest");
    }

    private void OnPeerDisconnected(LNLPeer peer)
    {
        AquaLogger.Log($"Peer disconnected: {peer.Identifier}");

        lock (_lock)
        {
            _pendingConnections.Remove(peer);
            if (_connectionToUser.TryGetValue(peer, out var user))
            {
                _connectionToUser.Remove(peer);
                _userToConnection.Remove(user);
                RemoveUser(user);
            }
        }
    }

    /// <summary>
    /// Handle incoming JoinRequest from client.
    /// </summary>
    public void HandleJoinRequest(IConnection connection, LegacyJoinRequestData requestData)
    {
        bool isPending;
        lock (_lock)
        {
            isPending = _pendingConnections.Remove(connection);
        }

        if (!isPending)
        {
            AquaLogger.Warn($"HandleJoinRequest: Connection {connection.Identifier} was not pending");
            return;
        }

        AquaLogger.Log($"HandleJoinRequest: Received from {connection.Identifier} - UserName='{requestData.UserName}'");
        SendJoinGrant(connection, requestData.UserName, requestData.MachineID);
    }

    private void SendJoinGrant(IConnection connection, string userName, string machineId)
    {
        if (!World.IsAuthority)
        {
            AquaLogger.Error("Only authority can send JoinGrant");
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
            MaxUsers = World.RefIDAllocator.MaxUserCount,
            WorldTime = World.TotalTime,
            StateVersion = World.StateVersion
        };

        var controlMessage = new ControlMessage(ControlMessage.Message.JoinGrant)
        {
            Payload = grantData.Encode()
        };

        byte[] encoded = controlMessage.Encode();
        connection.Send(encoded, encoded.Length, reliable: true, background: false);

        AquaLogger.Log($"Sent JoinGrant to {connection.Identifier} - UserID: {userID}, UserName: '{userName}'");

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
        AquaLogger.Log($"SendJoinGrant: Added connection-user mapping for '{userName}'");

        World.AddUserToBag(user, userRefID, isNewlyCreated: true);
        AquaLogger.Log($"SendJoinGrant: User '{userName}' added to world");

        if (Session.Sync != null)
        {
            AquaLogger.Log($"SendJoinGrant: Calling QueueUserForInitialization for '{userName}'");
            Session.Sync.QueueUserForInitialization(user);
        }
        else
        {
            AquaLogger.Error("SendJoinGrant: Session.Sync is NULL - cannot queue user for initialization!");
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

    public void Dispose()
    {
        Listener?.Dispose();
        HostConnection?.Close();

        lock (_lock)
        {
            _connectionToUser.Clear();
            _userToConnection.Clear();
        }
    }
}
