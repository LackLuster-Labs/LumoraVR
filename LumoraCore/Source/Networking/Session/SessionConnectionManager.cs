using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Networking.LNL;
using Lumora.Core.Networking.Sync;
using LegacyJoinGrantData = Lumora.Core.Networking.Messages.JoinGrantData;
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

    public Session Session { get; private set; }
    public World World => Session?.World;

    public LNLListener Listener { get; private set; }
    public IConnection HostConnection { get; private set; }

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
            taskCompletionSource.SetResult(true);
        };

        connection.ConnectionFailed += (c) =>
        {
            AquaLogger.Error($"Connection failed: {c.FailReason}");
            taskCompletionSource.SetResult(false);
        };

        connection.Closed += (c) =>
        {
            AquaLogger.Warn($"Connection closed: {c.FailReason}");
            taskCompletionSource.TrySetResult(false);
        };

        connection.DataReceived += (data, length) => OnConnectionDataReceived(connection, data, length);
        connection.Connect(null);

        bool success = await taskCompletionSource.Task;
        if (success)
        {
            HostConnection = connection;
        }

        return success;
    }

    private void OnPeerConnected(LNLPeer peer)
    {
        AquaLogger.Log($"Peer connected: {peer.Identifier}");

        peer.DataReceived += (data, length) => OnConnectionDataReceived(peer, data, length);

        // Send JoinGrant
        SendJoinGrant(peer);
    }

    private void OnPeerDisconnected(LNLPeer peer)
    {
        AquaLogger.Log($"Peer disconnected: {peer.Identifier}");

        lock (_lock)
        {
            if (_connectionToUser.TryGetValue(peer, out var user))
            {
                _connectionToUser.Remove(peer);
                _userToConnection.Remove(user);
                RemoveUser(user);
            }
        }
    }

    private void SendJoinGrant(IConnection connection)
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

        AquaLogger.Log($"Sent JoinGrant to {connection.Identifier} - UserID: {userID}");

        // Create user
        var user = new User(World, userRefID);
        user.UserID.Value = userID.ToString();
        user.AllocationIDStart.Value = (ulong)allocStart;
        user.AllocationIDEnd.Value = (ulong)allocEnd;

        lock (_lock)
        {
            _connectionToUser[connection] = user;
            _userToConnection[user] = connection;
        }

        World.AddUser(user);
        Session.Sync?.QueueUserForInitialization(user);
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
