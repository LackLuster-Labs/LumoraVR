// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Core.Networking;
using Lumora.Core.Networking.Sync;
using Lumora.Nexus.Transport;
using Lumora.Nexus.Protocol;
using Lumora.Nexus.Transport.LNL;
using Lumora.Core.Security.Integrity;
using LegacyJoinGrantData = Lumora.Core.Networking.Messages.JoinGrantData;
using LegacyJoinRequestData = Lumora.Core.Networking.Messages.JoinRequestData;
using LegacyJoinRejectData = Lumora.Core.Networking.Messages.JoinRejectData;
using LegacyJoinChallengeData = Lumora.Core.Networking.Messages.JoinChallengeData;
using LegacyJoinAuthenticateData = Lumora.Core.Networking.Messages.JoinAuthenticateData;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

public partial class SessionConnectionManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<IConnection, User> _connectionToUser = new();
    private readonly Dictionary<User, IConnection> _userToConnection = new();
    private readonly HashSet<IConnection> _pendingConnections = new(); // Connections waiting for JoinRequest
    private readonly Dictionary<string, int> _pendingByIp = new();     // IP -> count of pending connections

    // Connections that sent a valid JoinRequest and got a challenge, now waiting on the signed answer.
    // Holds the original request + the nonce we asked them to sign. Cleaned on grant, reject, or
    // disconnect, and counted against the pending cap so it can't be used to dodge the DoS limit. -xlinka
    private readonly Dictionary<IConnection, PendingJoin> _pendingAuth = new();

    private sealed class PendingJoin
    {
        public LegacyJoinRequestData Request;
        public byte[] Nonce = null!;
        // When we sent the challenge. If they never sign it, we reap the entry after a TTL so it can't sit
        // forever holding a pending slot. -xlinka
        public DateTime CreatedAt = DateTime.UtcNow;
    }

    // Domain-separation labels so a signature made for one step of the handshake can't be replayed as
    // another (host-proving vs joiner-proving vs account-proving). Both sides build payloads with these. -xlinka
    private const string JoinContextHost = "lumora-join-host";
    private const string JoinContextUserMachine = "lumora-join-user-machine";
    private const string JoinContextUserAccount = "lumora-join-user-account";

    // Client side: the nonce we sent in our JoinRequest for the host to sign, so we can verify the host is
    // who it claims before we hand over our own signature. -xlinka
    private byte[]? _hostVerificationToken;

    public Session Session { get; private set; }
    public World World => (Session?.World) ?? null!;

    // A host listens on EVERY available transport at once (LNL for LAN/direct, Steam for friends, ...), so a world
    // is reachable over all of them and advertises a URL per transport. All listeners are polled + disposed
    // together. 'Listener' stays as a back-compat shim returning the first. -xlinka
    private readonly List<IListener> _listeners = new();
    public IListener Listener => _listeners.Count > 0 ? _listeners[0] : null!;
    public IReadOnlyList<IListener> StartedListeners => _listeners;
    public IConnection HostConnection { get; private set; } = null!;

    // This machine's traffic in the session, wire bytes both ways: a guest's one connection to the host, or
    // the host's connections to every guest it has now.
    public (ulong Received, ulong Sent) TrafficTotals()
    {
        lock (_lock)
        {
            if (HostConnection != null)
                return (HostConnection.ReceivedBytes, HostConnection.SentBytes);
            ulong received = 0, sent = 0;
            foreach (var connection in _connectionToUser.Keys)
            {
                received += connection.ReceivedBytes;
                sent += connection.SentBytes;
            }
            return (received, sent);
        }
    }

    // The round trip to the host in milliseconds: 0 on the host itself, -1 while a guest's is not known yet.
    public int PingToHost => HostConnection?.Ping ?? 0;

    public event Action OnHostDisconnected = null!;

    public SessionConnectionManager(Session session)
    {
        Session = session;
    }

    // Start listening for connections (host only). Picks the
    // highest-priority registered INetworkManager as the
    // transport. Override the scheme with preferredScheme
    // to pin a specific transport (e.g. "lnl" for direct UDP). - xlinka
    public bool StartListener(ushort port, string preferredScheme = null!)
    {
        if (_listeners.Count > 0)
        {
            throw new InvalidOperationException("Listeners already started");
        }

        var sessionId = Session?.Metadata?.SessionId;

        // Open a listener on EVERY registered transport (LNL for LAN/direct, Steam for friends, ...) so the world
        // is reachable over all of them and can advertise a URL for each (Session.NewSession aggregates them).
        // This replaced a "pick the highest-priority manager" selection, which made a Steam-enabled host listen
        // ONLY on Steam while still advertising an lnl:// LAN URL - so LAN joiners hit a dead port. A transport
        // that can't start (e.g. Steam not initialized) is skipped, never fatal: LAN keeps working regardless.
        // preferredScheme, when given, restricts to that single transport. -xlinka
        IEnumerable<INetworkManager> managers;
        if (preferredScheme != null)
        {
            var only = NetworkManagerRegistry.FindForScheme(preferredScheme);
            managers = only != null ? new[] { only } : System.Array.Empty<INetworkManager>();
        }
        else
        {
            managers = NetworkManagerRegistry.Managers;
        }

        foreach (var manager in managers)
        {
            IListener listener;
            try
            {
                listener = manager.CreateListener(port, sessionId!);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] StartListener: {manager.GetType().Name} could not start a listener - skipping ({ex.Message})");
                continue;
            }

            if (listener == null || !listener.IsActive)
            {
                LumoraLogger.Warn($"[lnl] StartListener: {manager.GetType().Name} listener not active on port {port} - skipping");
                continue;
            }

            listener.PeerConnected += OnPeerConnected;
            listener.PeerDisconnected += OnPeerDisconnected;
            _listeners.Add(listener);

            LumoraLogger.Log($"[lnl] Listener started ({manager.GetType().Name}, port {port}, sessionId={sessionId ?? "(none)"})");
        }

        if (_listeners.Count == 0)
        {
            LumoraLogger.Error($"[lnl] StartListener: no transport could open a listener on port {port}");
            return false;
        }

        return true;
    }

    public async Task<bool> ConnectToAsync(IEnumerable<Uri> addresses)
    {
        // Try every advertised address, nearest first (see SessionAddressOrder), falling through to the next
        // on failure instead of giving up after the first. Returns on the first that connects. -xlinka
        int attempts = 0;
        foreach (var address in SessionAddressOrder.Order(addresses))
        {
            if (address == null)
                continue;
            attempts++;
            LumoraLogger.Log($"[lnl] Connect attempt {attempts}: {address}");
            if (await TryConnectAsync(address))
                return true;
            LumoraLogger.Warn($"[lnl] Address failed, trying next: {address}");
        }

        LumoraLogger.Error(attempts == 0
            ? "[lnl] No addresses provided"
            : $"[lnl] All {attempts} address(es) failed to connect");
        return false;
    }

    private async Task<bool> TryConnectAsync(Uri uri)
    {
        // Same-machine join ("two clients on one PC"): if the host address is one of THIS machine's own IPs,
        // dial it over loopback (127.0.0.1) instead. Connecting to the host's LAN IP egresses the OS
        // default-route interface, which on a dev box is often a Hyper-V/WSL/VPN virtual adapter - so the packet
        // leaves the box and never reaches the host process running on the same machine (the "it just won't
        // connect" case). 127.0.0.1 is always delivered locally, and the host listens on 0.0.0.0 so it accepts
        // it. Untouched for real two-machine joins (the target isn't a local IP there). -xlinka
        if (IsLocalAddress(uri.Host))
        {
            var loopback = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
            LumoraLogger.Log($"[lnl] ConnectToAsync: '{uri.Host}' is this machine - dialing same-PC host over loopback {loopback}");
            uri = loopback;
        }

        LumoraLogger.Debug($"[lnl] Connecting to {uri}");

        var manager = NetworkManagerRegistry.FindForUri(uri);
        if (manager == null)
        {
            LumoraLogger.Error($"[lnl] ConnectToAsync: no registered network manager handles scheme '{uri.Scheme}'");
            return false;
        }

        var connection = manager.CreateConnection(uri);

        // RunContinuationsAsynchronously: the Connected/Failed/Closed callbacks fire from inside PollEvents on the
        // MAIN thread (network is polled from the engine update loop). Without this, everything awaiting this TCS -
        // the rest of the join handshake AND Sync.Start()'s blocking init wait - runs INLINE on the main thread,
        // right inside the poll, which freezes the joining client. This pushes the post-connect continuation onto
        // the thread pool so the main thread stays free to keep pumping the network. -xlinka
        var taskCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.Connected += (c) =>
        {
            LumoraLogger.Log($"[lnl] Connected to {c.Address}");
            taskCompletionSource.TrySetResult(true);
        };

        connection.ConnectionFailed += (c) =>
        {
            LumoraLogger.Error($"[lnl] Connection failed: {c.FailReason}");
            taskCompletionSource.TrySetResult(false);
        };

        connection.Closed += (c) =>
        {
            LumoraLogger.Warn($"[lnl] Connection closed: {c.FailReason}");
            taskCompletionSource.TrySetResult(false);
            
            if (HostConnection == c)
            {
                OnHostDisconnected?.Invoke();
            }
        };

        connection.DataReceived += (data, length) => OnConnectionDataReceived(connection, data, length);
        connection.Connect(null!);

        var timeoutTask = Task.Delay(SessionAddressOrder.ConnectTimeoutMs(uri));
        var completedTask = await Task.WhenAny(taskCompletionSource.Task, timeoutTask);
        bool success = completedTask == taskCompletionSource.Task && taskCompletionSource.Task.Result;

        if (success)
        {
            HostConnection = connection;

            SendJoinRequest();
            return true;
        }

        if (completedTask == timeoutTask)
        {
            LumoraLogger.Warn($"[lnl] Connection timed out: {uri}");
        }

        connection.Close();
        return false;
    }

    // True if the given host string is an IPv4 address that belongs to THIS machine (loopback or any of its
    // own interface addresses) - i.e. the join target is a session hosted on the same PC. -xlinka
    private static bool IsLocalAddress(string host)
    {
        // IPv4 only: an IPv6 address is dialled as it is, so a join over IPv6 stays one.
        if (string.IsNullOrEmpty(host) || !IPAddress.TryParse(host, out var target) || target.AddressFamily != AddressFamily.InterNetwork)
            return false;

        if (IPAddress.IsLoopback(target))
            return true;

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ua.Address.Equals(target))
                        return true;
                }
            }
        }
        catch
        {
            // Interface enumeration failing just means we don't redirect - the normal LAN-IP dial still runs.
        }

        return false;
    }

    private void SendJoinRequest()
    {
        if (HostConnection == null)
        {
            LumoraLogger.Error("[lnl] SendJoinRequest: No host connection");
            return;
        }

        var identity = Lumora.Core.Security.MachineIdentity.Local;
        var cdn = Engine.Current?.CDNClient;
        bool hasAccount = cdn != null && cdn.HasAccountIdentity;

        // Our challenge to the host: it must sign this so we know we're talking to the real host. We hold
        // onto it to check the host's signature in HandleJoinChallenge. -xlinka
        var hostVerificationToken = RandomNumberGenerator.GetBytes(32);
        _hostVerificationToken = hostVerificationToken;

        var requestData = new LegacyJoinRequestData
        {
            // Only for the host's log: the host names us from what we prove, never from this.
            UserName = Engine.Current?.LocalUserName ?? Environment.MachineName,
            // MachineID is now the self-certifying hash of our public key, not a free-text string. We
            // prove we hold the matching private key when the host challenges us. -xlinka
            MachineID = identity.MachineId,
            MachinePublicKey = identity.PublicKey,
            UserID = "",
            HeadDevice = (byte)(Engine.Current?.InputInterface?.CurrentHeadOutputDevice ?? HeadOutputDevice.Screen),
            // If we're signed into an account, name it + the login session so the host can fetch our
            // cloud-published key and verify we own it. Empty = guest (machine key only). -xlinka
            AccountUserId = hasAccount ? cdn!.AccountUserId! : "",
            AccountSessionId = hasAccount ? cdn!.AccountSessionId! : "",
            HostVerificationToken = hostVerificationToken
        };

        var controlMessage = new ControlMessage(ControlMessage.Message.JoinRequest)
        {
            Payload = requestData.Encode()
        };

        byte[] encoded = controlMessage.Encode();
        HostConnection.Send(encoded, encoded.Length, reliable: true, background: false);

        LumoraLogger.Debug($"[lnl] Sent JoinRequest to host - UserName='{requestData.UserName}'");
    }

    private void OnPeerConnected(IConnection peer)
    {
        // From here on what changes may have to go to someone: the sync loop decides again on its next cycle.
        if (World?.SyncController is { } sync)
            sync.DropFieldDeltas = false;
        var ip = GetIpKey(peer);

        // Cap both total pending connections and pending per source IP, so a flood of
        // connections that never send a JoinRequest can't exhaust host memory/sockets.
        bool admit;
        List<IConnection>? reclaimed;
        lock (_lock)
        {
            // A peer this session already has, told again: a second handler on it would run every message
            // twice, and a second count against its address would never be paid back. -xlinka
            if (_pendingConnections.Contains(peer) || _pendingAuth.ContainsKey(peer) || _connectionToUser.ContainsKey(peer))
            {
                LumoraLogger.Debug($"[lnl] Peer {peer.Identifier} announced again; already known");
                return;
            }

            // First reclaim any join challenges that timed out, so a flood of unanswered ones can't be what's
            // holding the cap against a legit peer trying to connect right now. -xlinka
            reclaimed = ReclaimStalePendingAuthLocked();

            // Count connections still mid-handshake (waiting to authenticate) too, so the pending slot
            // can't be freed and reused to slip past the cap. -xlinka
            if (_pendingConnections.Count + _pendingAuth.Count >= NetworkLimits.MaxPendingConnections)
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

        RejectStalePendingAuth(reclaimed);

        if (!admit)
        {
            LumoraLogger.Warn($"[lnl] Peer {peer.Identifier} from {ip} rejected: pending-connection limit reached.");
            peer.Close();
            return;
        }

        LumoraLogger.Log($"[lnl] Peer connected: {peer.Identifier}");
        peer.DataReceived += (data, length) => OnConnectionDataReceived(peer, data, length);
        LumoraLogger.Debug($"[lnl] Peer {peer.Identifier} added to pending - waiting for JoinRequest");
    }

    private void OnPeerDisconnected(IConnection peer)
    {
        LumoraLogger.Log($"[lnl] Peer disconnected: {peer.Identifier}");

        Session.AssetTransferer?.ConnectionClosed(peer);
        // Drop this link's delta cursor. A reconnect arrives as a new IConnection and therefore a new
        // cursor, which is what stops it resuming a delta run across the stretch it was gone for - it
        // has to start from the tagged full batch the join path sends. -xlinka
        Session.Sync?.OnConnectionClosed(peer);

        lock (_lock)
        {
            RemovePendingLocked(peer);
            _pendingAuth.Remove(peer); // drop any half-finished handshake so it can't leak. -xlinka
            _pendingClaims.Remove(peer);
            _lastClaimAt.Remove(peer);
            if (_connectionToUser.TryGetValue(peer, out var user))
            {
                _connectionToUser.Remove(peer);
                _userToConnection.Remove(user);
                RemoveUser(user);
            }
        }
    }

    // Caller must hold _lock.
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

    // How long a joiner has to answer the signed challenge before we give up on them. -xlinka
    private static readonly TimeSpan PendingAuthTtl = TimeSpan.FromSeconds(20);

    // Caller must hold _lock. Pulls out any pending-auth entries that have sat past the challenge TTL without
    // answering. Those slots count against MaxPendingConnections (and the joiner already freed its per-IP slot
    // when it left _pendingConnections), so without this a single source can open valid join requests, never
    // sign the challenge, and park every pending slot - locking real players out. Returns the dropped
    // connections so the caller can reject + close them AFTER releasing the lock. -xlinka
    private List<IConnection>? ReclaimStalePendingAuthLocked()
    {
        var cutoff = DateTime.UtcNow - PendingAuthTtl;
        List<IConnection>? stale = null;
        foreach (var kvp in _pendingAuth)
        {
            if (kvp.Value.CreatedAt < cutoff)
                (stale ??= new List<IConnection>()).Add(kvp.Key);
        }
        if (stale != null)
        {
            foreach (var c in stale)
                _pendingAuth.Remove(c);
        }
        return stale;
    }

    private void RejectStalePendingAuth(List<IConnection>? stale)
    {
        if (stale == null)
            return;
        foreach (var c in stale)
        {
            LumoraLogger.Warn($"[lnl] PendingAuth: dropping {c.Identifier} - never answered the join challenge in time");
            SendJoinReject(c, "Join challenge timed out");
        }
    }

    // A relayed peer shares the node's address with every other relayed peer, so it counts by its own endpoint.
    private static string GetIpKey(IConnection connection)
        => (connection?.ViaRelay == true ? connection.Identifier : connection?.IP?.ToString())
           ?? connection?.Identifier ?? "<unknown>";

    public void HandleJoinRequest(IConnection connection, LegacyJoinRequestData requestData)
    {
        bool isPending;
        lock (_lock)
        {
            isPending = RemovePendingLocked(connection);
        }

        if (!isPending)
        {
            LumoraLogger.Warn($"[lnl] HandleJoinRequest: Connection {connection.Identifier} was not pending");
            SendJoinReject(connection, "Duplicate or stale join request");
            return;
        }

        LumoraLogger.Debug($"[lnl] HandleJoinRequest: Received from {connection.Identifier} - UserName='{requestData.UserName}'");
        var rejectReason = ValidateJoinRequest(connection, requestData);
        if (rejectReason != null)
        {
            LumoraLogger.Warn($"[lnl] HandleJoinRequest: Rejected {connection.Identifier} - {rejectReason}");
            SendJoinReject(connection, rejectReason);
            return;
        }

        // The request checks out (machine id matches its key, not banned, room to join). Don't grant yet:
        // make them prove they actually hold the private key by signing a fresh random nonce. Only after
        // that signature verifies do we mint a User. This is what stops someone just claiming an id. -xlinka
        var nonce = RandomNumberGenerator.GetBytes(32);
        lock (_lock)
        {
            _pendingAuth[connection] = new PendingJoin { Request = requestData, Nonce = nonce };
        }
        SendJoinChallenge(connection, nonce, requestData.HostVerificationToken);
    }

    private string? ValidateJoinRequest(IConnection connection, LegacyJoinRequestData requestData)
    {
        if (connection == null || !connection.IsOpen)
            return "Connection is not open";

        if (World == null || World.IsDisposed || World.IsDestroyed)
            return "World is not available";

        if (!World.IsAuthority)
            return "Only the authority can approve joins";

        if (!(World.Configuration?.AllowJoin.Value ?? true))
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

        // The MachineID has to be the self-certifying hash of the public key the client sent. Ownership
        // of the matching PRIVATE key is proven a step later via the signed challenge; this just rejects a
        // mismatched/forged id up front, so the ban check below keys on something that can't be faked. -xlinka
        if (requestData.MachinePublicKey == null || requestData.MachinePublicKey.Length == 0)
            return "Missing machine public key";
        if (!Lumora.Core.Security.MachineIdentity.IsMachineIdForKey(requestData.MachineID, requestData.MachinePublicKey))
            return "Machine identity does not match its key";

        // Bounce banned identities HERE, before we mint a User or sync anything. The ban check used to
        // only fire later in AddUser, by which point the user was already in the collection and synced,
        // so a banned user ended up half-joined with the connection still open. This is the gate, so the
        // ban belongs at the gate. Caveat: it's only as strong as what it keys on, and right now MachineID
        // is client-supplied and the account UserID isn't verified, so it can still be evaded until we have
        // full join verification. Still beats leaving the door open. -xlinka
        if (Lumora.Core.Security.BanManager.IsBanned(requestData.UserID, requestData.MachineID, World.WorldName?.Value))
            return "You are banned from this world";

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
        var configMax = World?.Configuration?.MaxUsers.Value ?? 0;
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

        // Give the reject message time to flush before we tear the connection down. Run detached, but
        // log faults instead of letting them vanish - Close() can throw on an already-disposed peer. -xlinka
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250);
                connection.Close();
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] SendJoinReject: deferred close failed: {ex.Message}");
            }
        });
    }

    private void SendJoinGrant(IConnection connection, AccountCard? account, string machineId, bool forceSilenced = false, string? joinRole = null)
    {
        var userName = account?.Username ?? AccountCard.GuestName(machineId);
        if (!World.IsAuthority)
        {
            LumoraLogger.Error("[lnl] Only authority can send JoinGrant");
            return;
        }

        var (allocStart, allocEnd) = World.RefIDAllocator.AllocateUserIDRange();

        // Defensively clear any stale objects still sitting on this byte BEFORE we build the new user on it.
        // A recycled byte SHOULD already be clean (RemoveUser purges on leave), but if a disconnect was missed
        // or raced a fast leave->rejoin, leftovers here would collide during user init and the join would be
        // rejected ("fails to authenticate"). Purging first makes rejoin bulletproof - a genuinely fresh byte
        // has nothing to purge. -xlinka
        World.ReferenceController?.PurgeUserByte(allocStart.GetUserByte());
        World.ReferenceController?.SetScriptFence(allocStart.GetUserByte(), RefIDConstants.ScriptRangeStart(allocStart.GetPosition()));

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

        LumoraLogger.Debug($"[lnl] Sent JoinGrant to {connection.Identifier} - UserID: {userID}, UserName: '{userName}'");

        // Create user instance, populate fields, then add to the user collection for initialization.
        // Wrapped: if initializing the user worker throws (e.g. a RefID collision), tear the half-built user
        // back down so its byte recycles CLEAN (RemoveUser now purges the whole byte + hands it back), then
        // rethrow so the caller rejects. Without this, every failed attempt left this byte's objects
        // registered and the next attempt collided ("RefID collision! User[NNN]:... already registered"). -xlinka
        var user = new User();
        user.UserID.Value = userID.ToString();
        user.MachineID.Value = machineId ?? "";
        // The name, colour, supporter and staff marks and the group card all come from the account the
        // joiner proved, or from its proven machine key for a guest. Nothing the joiner says about itself is
        // read. -xlinka
        user.ApplyAccount(account, machineId);
        user.IsSilenced.Value = forceSilenced; // platform mute/spectator ban forces silence on entry. -xlinka
        user.AllocationIDStart.Value = (ulong)allocStart;
        user.AllocationIDEnd.Value = (ulong)allocEnd;
        user.AllocationID.Value = userRefID.GetUserByte();
        user.IsPresent.Value = true;
        user.PresentInWorld.Value = true;

        lock (_lock)
        {
            // Gone while its grant waited for the world: its disconnect has been and found no user to take down,
            // so one made now would stand in the world for good.
            if (!connection.IsOpen)
            {
                LumoraLogger.Log($"[lnl] SendJoinGrant: {connection.Identifier} left before its grant - not adding '{userName}'");
                World.RefIDAllocator?.ReleaseUserAllocation(userRefID.GetUserByte());
                return;
            }
            _connectionToUser[connection] = user;
            _userToConnection[user] = connection;
        }
        LumoraLogger.Debug($"[lnl] SendJoinGrant: Added connection-user mapping for '{userName}'");

        try
        {
            World.AddUserToCollection(user, userRefID, isNewlyCreated: true);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"[lnl] SendJoinGrant: failed to initialize user '{userName}' on byte {userRefID.GetUserByte()} - cleaning up and rejecting ({ex.Message})");
            lock (_lock)
            {
                _connectionToUser.Remove(connection);
                _userToConnection.Remove(user);
            }
            try { World.RemoveUser(user); } catch { /* best-effort teardown */ }
            // Belt and suspenders: purge + release directly too, in case the user never reached the lists
            // RemoveUser cleans. Both are idempotent. -xlinka
            World.ReferenceController?.PurgeUserByte(userRefID.GetUserByte());
            World.RefIDAllocator?.ReleaseUserAllocation(userRefID.GetUserByte());
            throw;
        }
        LumoraLogger.Log($"[lnl] SendJoinGrant: User '{userName}' added to world");
        if (joinRole != null)
            Lumora.Core.Scripting.ScriptJoinChecks.GiveRole(World, user, joinRole);

        if (Session.Sync != null)
        {
            LumoraLogger.Debug($"[lnl] SendJoinGrant: Calling QueueUserForInitialization for '{userName}'");
            Session.Sync.QueueUserForInitialization(user);
        }
        else
        {
            LumoraLogger.Error("[lnl] SendJoinGrant: Session.Sync is NULL - cannot queue user for initialization!");
        }
    }

    private void SendJoinChallenge(IConnection connection, byte[] nonce, byte[] hostVerificationToken)
    {
        var hostIdentity = Lumora.Core.Security.MachineIdentity.Local;

        // Prove WE are the real host by signing the joiner's token with our machine key. The joiner checks
        // this before handing over its own signature, so nothing in between can pose as the host. -xlinka
        byte[] hostSignature = Array.Empty<byte>();
        if (hostVerificationToken != null && hostVerificationToken.Length > 0)
        {
            try
            {
                hostSignature = hostIdentity.SignChallenge(
                    Lumora.Core.Security.MachineIdentity.BuildJoinPayload(hostVerificationToken, JoinContextHost));
            }
            catch (Exception ex) { LumoraLogger.Warn($"[lnl] SendJoinChallenge: host self-sign failed ({ex.Message})"); }
        }

        var challenge = new LegacyJoinChallengeData
        {
            Nonce = nonce,
            HostMachineId = hostIdentity.MachineId,
            HostMachinePublicKey = hostIdentity.PublicKey,
            HostMachineSignature = hostSignature,
            HostBuild = BuildIdentity.Hash,
            HostModified = BuildIdentity.IsModified
        };
        var controlMessage = new ControlMessage(ControlMessage.Message.JoinChallenge)
        {
            Payload = challenge.Encode()
        };

        byte[] encoded = controlMessage.Encode();
        connection.Send(encoded, encoded.Length, reliable: true, background: false);
        LumoraLogger.Debug($"[lnl] Sent JoinChallenge to {connection.Identifier}");
    }

    public void HandleJoinChallenge(IConnection connection, LegacyJoinChallengeData challenge)
    {
        if (challenge.Nonce == null || challenge.Nonce.Length == 0)
        {
            LumoraLogger.Warn("[lnl] HandleJoinChallenge: empty nonce, ignoring");
            return;
        }

        // Verify the HOST first: its MachineId has to hash to the key it sent, and it has to have signed
        // the token we put in our JoinRequest. If that doesn't check out we're not talking to the real host,
        // so bail without ever handing over our own signature. -xlinka
        var token = _hostVerificationToken;
        bool hostOk =
            token != null && token.Length > 0
            && Lumora.Core.Security.MachineIdentity.IsMachineIdForKey(challenge.HostMachineId, challenge.HostMachinePublicKey)
            && Lumora.Core.Security.MachineIdentity.VerifyRsaSignature(
                challenge.HostMachinePublicKey,
                Lumora.Core.Security.MachineIdentity.BuildJoinPayload(token, JoinContextHost),
                challenge.HostMachineSignature);

        if (!hostOk)
        {
            LumoraLogger.Error("[lnl] HandleJoinChallenge: host identity verification FAILED - aborting join");
            (connection ?? HostConnection)?.Close();
            _hostVerificationToken = null;
            return;
        }

        // Other game files on either side: stop here and say which, rather than answering into the host's refusal.
        // An older host sends no build and gets the answer; its own check turns us away. -xlinka
        if (!string.IsNullOrEmpty(challenge.HostBuild) && !BuildIdentity.Matches(challenge.HostBuild))
        {
            string refusal = BuildIdentity.IsModified ? BuildRefusals.Modified
                : challenge.HostModified ? BuildRefusals.HostModified
                : BuildRefusals.OtherVersion;
            LumoraLogger.Warn($"[lnl] HandleJoinChallenge: the host runs build {BuildIdentity.Short(challenge.HostBuild)}{(challenge.HostModified ? " (modified)" : string.Empty)}, "
                + $"this game {BuildIdentity.Short(BuildIdentity.Hash)}{(BuildIdentity.IsModified ? " (modified: " + GameIntegrity.Describe() + ")" : string.Empty)} - not joining");
            _hostVerificationToken = null;
            World?.InitializationFailed(refusal);
            (connection ?? HostConnection)?.Close();
            return;
        }

        byte[] signature;
        try
        {
            signature = Lumora.Core.Security.MachineIdentity.Local.SignChallenge(
                Lumora.Core.Security.MachineIdentity.BuildJoinPayload(challenge.Nonce, JoinContextUserMachine));
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"[lnl] HandleJoinChallenge: failed to sign challenge ({ex.Message})");
            return;
        }

        // If signed into an account, also sign the nonce (account context) with our account key so the host
        // can verify our account against the key we published to the cloud. Best-effort, guests skip. -xlinka
        byte[] accountSignature = Array.Empty<byte>();
        var cdn = Engine.Current?.CDNClient;
        if (cdn != null && cdn.HasAccountIdentity)
        {
            try
            {
                accountSignature = cdn.SignWithAccountKey(
                    Lumora.Core.Security.MachineIdentity.BuildJoinPayload(challenge.Nonce, JoinContextUserAccount)) ?? Array.Empty<byte>();
            }
            catch (Exception ex) { LumoraLogger.Warn($"[lnl] HandleJoinChallenge: account sign failed ({ex.Message})"); }
        }

        // Hash our build identity with the host's nonce so the host can confirm we run the same build, files
        // included, before granting the join. -xlinka
        byte[] compatHash;
        try
        {
            compatHash = Lumora.Core.Networking.Sync.ProtocolCompatibility.ComputeChallengeResponse(challenge.Nonce);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"[lnl] HandleJoinChallenge: failed to compute compatibility hash ({ex.Message})");
            compatHash = Array.Empty<byte>();
        }

        var auth = new LegacyJoinAuthenticateData
        {
            Signature = signature,
            AccountSignature = accountSignature,
            CompatHash = compatHash,
            Modified = BuildIdentity.IsModified
        };
        var controlMessage = new ControlMessage(ControlMessage.Message.JoinAuthenticate)
        {
            Payload = auth.Encode()
        };

        byte[] encoded = controlMessage.Encode();
        var target = connection ?? HostConnection;
        target.Send(encoded, encoded.Length, reliable: true, background: false);
        _hostVerificationToken = null; // one-shot; done with this join's host challenge. -xlinka
        LumoraLogger.Debug("[lnl] Sent JoinAuthenticate to host");
    }

    // Host side: verify the joiner's signed challenge. Only if the signature proves they hold the
    // private key behind their claimed MachineID do we actually grant the join. Otherwise reject. -xlinka
    public async Task HandleJoinAuthenticate(IConnection connection, LegacyJoinAuthenticateData auth)
    {
        if (connection == null || !World.IsAuthority)
            return;

        PendingJoin? pending = null;
        lock (_lock)
        {
            _pendingAuth.TryGetValue(connection, out pending);
            if (pending != null)
                _pendingAuth.Remove(connection);
        }

        if (pending == null)
        {
            LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: no pending auth for {connection.Identifier} - ignoring");
            SendJoinReject(connection, "No pending authentication");
            return;
        }

        try
        {
            // The machine key is always required: prove ownership of the claimed MachineID.
            if (!Lumora.Core.Security.MachineIdentity.VerifyChallenge(
                    pending.Request.MachineID, pending.Request.MachinePublicKey,
                    Lumora.Core.Security.MachineIdentity.BuildJoinPayload(pending.Nonce, JoinContextUserMachine),
                    auth.Signature))
            {
                LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: machine signature FAILED for {connection.Identifier} - rejecting");
                SendJoinReject(connection, "Machine authentication failed");
                return;
            }

            // Build compatibility: the joiner must report the same build identity we do (release, schema and
            // whether the game files are the shipped ones, salted with this join's nonce). A mismatch is another
            // release or modified files on one side, so reject before attempting a doomed join, saying which. -xlinka
            if (!Lumora.Core.Networking.Sync.ProtocolCompatibility.Verify(pending.Nonce, auth.CompatHash))
            {
                string refusal = auth.Modified ? BuildRefusals.Modified
                    : BuildIdentity.IsModified ? BuildRefusals.HostModified
                    : BuildRefusals.OtherVersion;
                LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: build mismatch for {connection.Identifier}"
                    + $"{(auth.Modified ? " (joiner says its files are modified)" : string.Empty)}{(BuildIdentity.IsModified ? " (this host is modified)" : string.Empty)} - rejecting");
                SendJoinReject(connection, refusal);
                return;
            }

            // The account check only runs when the joiner CLAIMS an account. We fail closed: if
            // they claim one and we can't verify it (no key published, bad signature, platform-banned, or
            // the backend is unreachable), reject. A guest just doesn't claim an account. -xlinka
            var claimedAccount = pending.Request.AccountUserId;
            if (!string.IsNullOrEmpty(claimedAccount))
            {
                string? refusal = null;
                var verified = await VerifyAccountAsync(
                    claimedAccount, pending.Request.AccountSessionId, pending.Nonce, auth.AccountSignature, JoinContextUserAccount,
                    said => refusal = said);

                if (verified == null)
                {
                    LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: account verification FAILED for {connection.Identifier} (claimed '{claimedAccount}') - rejecting{(refusal != null ? ", the account service held it back (" + refusal + ")" : string.Empty)}");
                    SendJoinReject(connection, refusal ?? EmailJoinGate.Unverifiable);
                    return;
                }

                var (verifiedCard, forceSilenced) = verified.Value;
                var verifiedAccountId = verifiedCard.AccountId;

                // We're on a threadpool continuation after the HTTP await, so marshal the grant (which
                // mutates world state: creates + registers the User) back onto the world's sync thread.
                // The guest path below skips this because it never awaited and is still on-thread. -xlinka
                var world = World;
                if (world == null)
                {
                    SendJoinReject(connection, "World no longer available");
                    return;
                }
                var conn = connection;
                var req = pending.Request;

                // Now that the account is actually verified, re-check world bans keyed on the ACCOUNT id. The
                // gate in ValidateJoinRequest only had the (client-supplied) machine id to go on - the account
                // wasn't proven yet - so a host's account-scoped ban would otherwise slip straight through to a
                // grant. This is the point where we finally know who they really are. -xlinka
                if (Lumora.Core.Security.BanManager.IsBanned(verifiedAccountId, req.MachineID, world.WorldName?.Value))
                {
                    LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: {connection.Identifier} (account '{verifiedAccountId}') is banned from this world - rejecting");
                    SendJoinReject(connection, "You are banned from this world");
                    return;
                }

                // The group door, on the VERIFIED id and nowhere earlier. ValidateJoinRequest runs before
                // anyone has proved anything, so a group check there would be keyed on a name the joiner
                // typed. -xlinka
                var groupRefusal = CheckGroupDoor(verifiedAccountId);
                if (groupRefusal != null)
                {
                    LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: {connection.Identifier} (account '{verifiedAccountId}') refused by the group door - {groupRefusal}");
                    SendJoinReject(connection, groupRefusal);
                    return;
                }
                var friendsRefusal = CheckFriendsDoor(verifiedAccountId);
                if (friendsRefusal != null)
                {
                    LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: {connection.Identifier} (account '{verifiedAccountId}') refused by the friends door - {friendsRefusal}");
                    SendJoinReject(connection, friendsRefusal);
                    return;
                }
                var privateRefusal = CheckPrivateDoor(verifiedAccountId);
                if (privateRefusal != null)
                {
                    LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: {connection.Identifier} (account '{verifiedAccountId}') refused by the private door");
                    SendJoinReject(connection, privateRefusal);
                    return;
                }

                // Last, the world's own join checks, which the host switched on for this session if any. -xlinka
                var verdict = await Lumora.Core.Scripting.ScriptJoinChecks.AskAsync(world, verifiedAccountId, verifiedCard.Username,
                    req.MachineID, Engine.Current?.Social?.IsFriend(verifiedAccountId) == true, forceSilenced);
                if (verdict.Refused)
                {
                    LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: {connection.Identifier} (account '{verifiedAccountId}') turned away by a join check - {verdict.Reason}");
                    SendJoinReject(connection, verdict.Reason ?? "Join rejected");
                    return;
                }

                LumoraLogger.Log($"[lnl] HandleJoinAuthenticate: {connection.Identifier} authenticated (account='{verifiedAccountId}', silenced={forceSilenced}) - granting");
                world.RunSynchronously(() => SendJoinGrant(conn, verifiedCard, req.MachineID, forceSilenced, verdict.Role));
                return;
            }

            // Guest path. A guest carries no account, so a members-only group world has nothing to check them against
            // and the answer is no. A group-public one lets them in like anyone else.
            var guestRefusal = CheckGroupDoor(string.Empty) ?? CheckFriendsDoor(string.Empty) ?? CheckPrivateDoor(string.Empty);
            if (guestRefusal != null)
            {
                LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: {connection.Identifier} (guest) refused at the door - {guestRefusal}");
                SendJoinReject(connection, guestRefusal);
                return;
            }

            // The world's join checks hear guests too. Asking them waits, so the grant then goes back onto the
            // world's thread as the account path's does.
            var guestWorld = World;
            var guestMachine = pending.Request.MachineID;
            var guestVerdict = await Lumora.Core.Scripting.ScriptJoinChecks.AskAsync(guestWorld, string.Empty,
                AccountCard.GuestName(guestMachine), guestMachine, false, false);
            if (guestVerdict.Refused)
            {
                LumoraLogger.Warn($"[lnl] HandleJoinAuthenticate: {connection.Identifier} (guest) turned away by a join check - {guestVerdict.Reason}");
                SendJoinReject(connection, guestVerdict.Reason ?? "Join rejected");
                return;
            }

            LumoraLogger.Log($"[lnl] HandleJoinAuthenticate: {connection.Identifier} authenticated (guest) - granting");
            var guestConnection = connection;
            guestWorld.RunSynchronously(() => SendJoinGrant(guestConnection, null, guestMachine, joinRole: guestVerdict.Role));
        }
        catch (Exception ex)
        {
            // Log the ROOT cause, not just the outer wrapper - worker init rethrows as a generic "Exception
            // during initializing Worker of type X" that hides the real reason (e.g. a RefID collision). -xlinka
            LumoraLogger.Error($"[lnl] HandleJoinAuthenticate: unexpected error for {connection.Identifier} ({ex.GetBaseException().Message}) - rejecting");
            SendJoinReject(connection, "Authentication error");
        }
    }

    // THE GROUP DOOR
    //
    // Runs on a VERIFIED account id, or on an empty string for a guest, and answers a refusal sentence or
    // null. Both facts it reads are the host's own: HostGroupId is host-only on WorldSettings, and the
    // roster came off the host's fetch through the permission gate. Nothing the joiner sent about itself
    // is consulted, so there is no claim a client can make that opens this.
    //
    // Order matters. A group ban refuses whatever tier the world is on, because being banned from the
    // group is about the person and not about this world's access setting. Membership is only asked once
    // the world is actually members-only. And an empty roster - the first seconds of a session, or a
    // fetch that has never succeeded - refuses, which is the whole point of holding it empty.
    //
    // The host never reaches here: this is the joining side of the handshake and the host does not join
    // its own session. -xlinka
    private string? CheckGroupDoor(string verifiedAccountId)
    {
        var world = World;
        var config = world?.Configuration;
        string groupId = config?.HostGroupId.Value ?? string.Empty;
        if (world == null || config == null || string.IsNullOrEmpty(groupId))
            return null;

        var permissions = world.DataModelPermissions;
        bool haveAccount = !string.IsNullOrEmpty(verifiedAccountId);

        if (permissions != null && haveAccount && permissions.IsGroupBanned(verifiedAccountId))
            return "You are banned from this group's worlds";

        if (config.AccessLevel.Value != Lumora.Core.World.WorldAccessLevel.GroupMembers)
            return null;

        if (permissions != null && haveAccount && permissions.IsGroupMember(verifiedAccountId))
            return null;
        // on the host's invite book, written by the host alone (SessionInvites)
        if (haveAccount && SessionInvites.Admits(world, verifiedAccountId))
            return null;

        // The short tag if the host published one, else the id: a refusal that names nothing tells the
        // person nothing about which door they are standing at.
        GroupSessionTags.TryRead(world.Session?.Metadata?.Tags, out _, out var shortTag);
        return $"This world is for members of {(shortTag.Length > 0 ? shortTag : groupId)}";
    }

    // THE FRIENDS DOOR
    //
    // Same rules of evidence as the group door: a verified account id or an empty string for a guest, read
    // against the host's own lists off its signed-in social service. Someone the host blocked stays out
    // whatever the tier. A Friends world takes the host's friends and the host's own account on another
    // machine. Friends+ waits on friends of friends, so until then it takes friends only. Lists that have
    // not arrived, or a host signed out, take nobody: there is no list to be on. An account on the host's
    // invite book gets in for as long as its line lasts, whatever the lists say. -xlinka
    private string? CheckFriendsDoor(string verifiedAccountId)
    {
        var config = World?.Configuration;
        if (config == null)
            return null;
        var social = Engine.Current?.Social;
        bool haveAccount = !string.IsNullOrEmpty(verifiedAccountId);

        // worded like any closed door, so a refusal never tells anyone they were blocked
        if (haveAccount && social != null && social.IsBlocked(verifiedAccountId))
            return "You can't join this world";

        var level = config.AccessLevel.Value;
        if (level != Lumora.Core.World.WorldAccessLevel.Contacts && level != Lumora.Core.World.WorldAccessLevel.ContactsPlus)
            return null;
        if (!haveAccount)
            return "This world is for the host's friends. Sign in to join";
        if (SessionInvites.Admits(World, verifiedAccountId))
            return null;
        if (social == null || !social.IsReady)
            return "The host's friends list hasn't loaded yet. Try again in a moment";
        if (verifiedAccountId == social.SelfId || social.IsFriend(verifiedAccountId))
            return null;
        return "This world is for the host's friends";
    }

    public const string PrivateRefusal = "This world is private. Ask the host for an invite.";
    public const string LocalRefusal = "This is the host's local world. Nobody else can join it.";

    // THE PRIVATE DOOR
    //
    // A Private world takes the host's own account on another machine, its co-hosts (an Admin role pinned to
    // their account in the host's permission config) and accounts with a live line on the host's invite book.
    // Nobody else: no guest, and nobody who only has its address. Every fact is the host's: its own user, its
    // config, its book. LAN is not this door's business; it stays open to whoever reaches it on the network.
    // -xlinka
    private string? CheckPrivateDoor(string verifiedAccountId)
    {
        var world = World;
        var config = world?.Configuration;
        // The home answers as Private whatever its config says, and only to the host's own account. -xlinka
        bool home = Lumora.Core.Management.WorldManager.IsLocalHome(world);
        if (world == null || config == null || (!home && config.AccessLevel.Value != Lumora.Core.World.WorldAccessLevel.Private))
            return null;
        if (string.IsNullOrEmpty(verifiedAccountId))
            return home ? LocalRefusal : PrivateRefusal;
        string host = world.LocalUser?.AccountId?.Value ?? string.Empty;
        if (verifiedAccountId == host || verifiedAccountId == Engine.Current?.Social?.SelfId)
            return null;
        if (home)
            return LocalRefusal;
        // found, never made: this runs off the world's thread, and asking World.PermissionConfig would attach one
        var permissions = world.RootSlot?.GetComponent<WorldPermissionConfig>();
        if (permissions?.FindAssignment(null, verifiedAccountId) is { } assigned
            && string.Equals(assigned.Role.Value, SessionInvites.CoHostRole, StringComparison.Ordinal))
            return null;
        return SessionInvites.Admits(world, verifiedAccountId) ? null : PrivateRefusal;
    }

    // Verify a joiner's CLAIMED account: fetch the public key that account published to the cloud for the
    // given login session, verify the account signature over the challenge nonce against THAT key (so the
    // joiner can't present a key of their choosing), then check platform moderation ban status. Returns
    // the verified account id, or null if anything fails. World bans are a separate, host-side check. refused hears
    // the words to turn the joiner away with when the service held the account back on purpose. -xlinka
    private async Task<(AccountCard card, bool forceSilenced)?> VerifyAccountAsync(string accountUserId, string accountSessionId, byte[] nonce, byte[] accountSignature, string context,
        Action<string>? refused = null)
    {
        var cdn = Engine.Current?.CDNClient;
        if (cdn == null)
        {
            LumoraLogger.Warn("[lnl] VerifyAccount: no CDN client available; cannot verify a claimed account.");
            return null;
        }
        if (string.IsNullOrEmpty(accountSessionId) || accountSignature == null || accountSignature.Length == 0)
            return null;

        // The session's own id lets the account service hold the key back from a session registered under another
        // build than the one this login reported.
        var (publishedKey, keyRefusal) = await cdn.GetSessionPublicKeyAsync(accountUserId, accountSessionId, World?.Session?.Metadata?.SessionId);
        if (publishedKey == null)
        {
            if (keyRefusal == AccountEmail.NotVerifiedCode)
            {
                LumoraLogger.Warn($"[lnl] VerifyAccount: the account service holds {accountUserId} back until its email is verified.");
                refused?.Invoke(EmailJoinGate.Refusal);
            }
            else if (keyRefusal == BuildRefusals.ServiceCode)
            {
                LumoraLogger.Warn($"[lnl] VerifyAccount: the account service says {accountUserId} reported other game files than this session was listed with.");
                refused?.Invoke(BuildRefusals.Modified);
            }
            else
            {
                LumoraLogger.Warn($"[lnl] VerifyAccount: no published key for {accountUserId}/{accountSessionId}.");
            }
            return null;
        }

        if (!Lumora.Core.Security.MachineIdentity.VerifyRsaSignature(
                publishedKey,
                Lumora.Core.Security.MachineIdentity.BuildJoinPayload(nonce, context),
                accountSignature))
        {
            LumoraLogger.Warn($"[lnl] VerifyAccount: account signature did not verify for {accountUserId}.");
            return null;
        }

        // Platform moderation bans. Public/account ban blocks the join outright. Mute/spectator are softer:
        // they restrict rather than block. We force-silence a mute-banned user; spectator-ban should force
        // spectator-only, but there's no spectator mode yet, so we silence + flag the gap. -xlinka
        bool forceSilenced = false;
        var userInfo = await cdn.GetPlatformUserAsync(accountUserId);

        // Fail CLOSED. If we can't reach the moderation backend (offline / timeout / non-2xx), we cannot
        // confirm this account isn't banned, so we refuse the join. The old code only checked bans when the
        // fetch SUCCEEDED, which meant a platform-banned account sailed in during any backend outage. The
        // machine signature is already verified by this point; this gate is purely about ban status. -xlinka
        if (!userInfo.Success || userInfo.Data == null)
        {
            LumoraLogger.Warn($"[lnl] VerifyAccount: could not fetch platform status for {accountUserId} (backend unreachable?); failing closed and rejecting.");
            return null;
        }

        var info = userInfo.Data;
        if (info.IsPublicBanned || info.IsAccountBanned)
        {
            LumoraLogger.Warn($"[lnl] VerifyAccount: {accountUserId} is platform-banned; rejecting.");
            return null;
        }
        if (info.IsMuteBanned)
            forceSilenced = true;
        if (info.IsSpectatorBanned)
        {
            forceSilenced = true;
            LumoraLogger.Warn($"[lnl] VerifyAccount: {accountUserId} is spectator-banned, but there's no spectator mode yet, so silencing as a partial measure. -xlinka");
        }

        // The card is built from the service's record for the id the key was published under, and that
        // record has to be the same account: a reply about anybody else names nobody. -xlinka
        var card = AccountCard.From(info);
        if (card == null || !string.Equals(card.AccountId, accountUserId, StringComparison.Ordinal))
        {
            LumoraLogger.Warn($"[lnl] VerifyAccount: the service's record for {accountUserId} carries no usable name; rejecting.");
            return null;
        }

        return (card, forceSilenced);
    }

    // A user leaves on the world's own thread, inside its update, whichever thread lost the link. Every transport
    // raises its disconnects on a network thread of its own, and the removal from there wrote the user list, the
    // reference registry and the leave events while the world's update or a sync cycle was in the middle of them.
    // -xlinka
    private void RemoveUser(User user)
    {
        var world = World;
        if (world == null || user == null)
            return;
        world.RunSynchronously(() =>
        {
            world.RemoveUser(user);
            user.Dispose();
        });
    }

    // Cut a peer off and take their user down with them. Host only; the host cannot remove itself. Used by
    // the permission gate's violation escalation, which decides WHETHER someone goes and leaves the HOW
    // here - the gate has no idea what a connection is, by design. Closing the socket is the part that
    // matters: dropping the user without it leaves the peer connected and re-authoring itself back in.
    // -xlinka
    public void DisconnectUser(User user, string reason)
    {
        if (user == null || World == null || !World.IsAuthority || user.IsHost)
            return;

        LumoraLogger.Warn($"[lnl] Disconnecting '{user.UserName.Value}': {reason}");

        IConnection? connection = null;
        lock (_lock)
        {
            if (_userToConnection.TryGetValue(user, out var found))
            {
                connection = found;
                _userToConnection.Remove(user);
                _connectionToUser.Remove(found);
            }
        }

        try { connection?.Close(); }
        catch (Exception ex) { LumoraLogger.Debug($"[lnl] DisconnectUser: closing the connection threw ({ex.Message})"); }

        RemoveUser(user);
    }

    // What arrived before the session's sync manager was there to take it. A joiner sends its join request the moment
    // its link is up and starts its sync manager after, and a host quick enough to challenge in between had its
    // challenge dropped and the join waiting for good. Kept here in order until the sync manager starts. -xlinka
    private readonly Queue<RawInMessage> _early = new();
    private volatile bool _earlyTaken;
    private const int MaxEarlyMessages = 256;

    private void OnConnectionDataReceived(IConnection connection, byte[] data, int length)
    {
        var raw = new RawInMessage
        {
            Data = data,
            Offset = 0,
            Length = length,
            Sender = connection,
            Arrived = System.Diagnostics.Stopwatch.GetTimestamp(),
        };

        if (!_earlyTaken)
        {
            lock (_early)
            {
                if (!_earlyTaken)
                {
                    if (_early.Count < MaxEarlyMessages)
                        _early.Enqueue(raw);
                    return;
                }
            }
        }

        Session?.Sync?.QueueRawIncoming(raw);
    }

    // The sync manager, as it starts: everything held so far, then everything as it arrives.
    internal void TakeEarlyMessages(SessionSyncManager sync)
    {
        lock (_early)
        {
            while (_early.TryDequeue(out var raw))
                sync.QueueRawIncoming(raw);
            _earlyTaken = true;
        }
    }

    public bool TryGetUser(IConnection connection, out User user)
    {
        lock (_lock)
        {
            if (_connectionToUser.TryGetValue(connection, out var found))
            {
                user = found;
                return true;
            }
            user = default!;
            return false;
        }
    }

    public bool TryGetConnection(User user, out IConnection connection)
    {
        lock (_lock)
        {
            if (_userToConnection.TryGetValue(user, out var found))
            {
                connection = found;
                return true;
            }
            connection = default!;
            return false;
        }
    }

    public int ConnectionCount
    {
        get
        {
            lock (_lock)
            {
                return _connectionToUser.Count;
            }
        }
    }

    // The same list written into one the caller keeps, so a sender asking per message makes none.
    public void CopyAllConnections(List<IConnection> into)
    {
        into.Clear();
        lock (_lock)
        {
            foreach (var connection in _connectionToUser.Keys)
                into.Add(connection);
        }
    }

    public List<IConnection> GetAllConnections()
    {
        lock (_lock)
        {
            return _connectionToUser.Keys.ToList();
        }
    }

    public void Broadcast(byte[] data, List<IConnection> targets, bool reliable, bool background = false, bool bulk = false)
        => Broadcast(data, data.Length, targets, reliable, background, bulk);

    // The first length bytes of data. Every link copies what it is handed before it returns, so the caller may
    // write its next frame into the same buffer.
    public void Broadcast(byte[] data, int length, List<IConnection> targets, bool reliable, bool background = false, bool bulk = false)
    {
        // One link refusing a send must not cost every target after it the same message. -xlinka
        foreach (var target in targets)
        {
            try
            {
                if (bulk)
                    target.SendBulk(data, length);
                else
                    target.Send(data, length, reliable, background);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] Broadcast: sending {length} bytes to {target?.Identifier} failed: {ex.Message}");
            }
        }
    }

    // Must be called every frame by the world update loop.
    public void Poll()
    {
        // Periodic reap of join challenges nobody answered (host side; no-op for a client). The contention
        // path in OnPeerConnected reclaims too, so this still works even if Poll runs irregularly. -xlinka
        List<IConnection>? reclaimed;
        lock (_lock)
        {
            reclaimed = ReclaimStalePendingAuthLocked();
        }
        RejectStalePendingAuth(reclaimed);

        // Per-session poll. Most transports also have a global poll driven by
        // NetworkManagerRegistry.UpdateAll() from the engine update loop, which
        // covers any listeners/connections created outside the session. - xlinka
        foreach (var l in _listeners)
        {
            if (l is LNLListener lnlListener) lnlListener.Poll();
        }
        HostConnection?.Poll();
    }

    public void Dispose()
    {
        // Close + unsubscribe EVERY listener (LNL, Steam, ...). Unsubscribe before Close so a disposed listener's
        // callback can't fire into the cleared maps, and so Steam channel sockets don't leak. -xlinka
        foreach (var l in _listeners)
        {
            try
            {
                l.PeerConnected -= OnPeerConnected;
                l.PeerDisconnected -= OnPeerDisconnected;
                l.Close();
                l.Dispose();
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] Dispose: error closing {l.GetType().Name} - {ex.Message}");
            }
        }
        _listeners.Clear();
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

