// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Nexus.Discovery;
using Lumora.Nexus.Transport.LNL;
using Lumora.Core.Networking.Streams;
using Lumora.Nexus.Cloud;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking.Session;

public class Session : IDisposable
{
    public World World { get; private set; }
    public SessionConnectionManager Connections { get; private set; }
    public SessionSyncManager Sync { get; private set; } = null!;
    public SessionAssetTransferer AssetTransferer { get; private set; } = null!;

    public SessionMetadata Metadata { get; private set; } = null!;

    public bool IsDisposed { get; private set; }
    public TimeSpan Latency { get; set; }

    // The session's directory id: the host's own, for a joiner the one the address it came in on carried,
    // and otherwise (a punched or typed address) the one the host seeds into the world's settings.
    public string? SessionId => Metadata?.SessionId ?? _joinedSessionId ?? SeededId();

    private string? SeededId()
    {
        var id = World?.Configuration?.SessionId.Value;
        return id != null && SessionIdentifier.IsValid(id) ? id : null;
    }

    private string? _joinedSessionId;

    private LANAnnouncer _lanAnnouncer = null!;
    private BackendSessionDirectoryClient? _directoryClient;
    private LNLListener? _natListener;

    // The listener holding this session on a NAT node, while it is listed; null otherwise.
    public LNLListener? NatListener => _natListener;

    public event Action OnDisconnected = null!;

    // Runs on the sync thread - keep the callback fast (push to a lock-free queue and return). The
    public delegate void RawFrameHandler(User sender, RefID streamRefID, ushort sequence, ReadOnlyMemory<byte> payload);

    public event RawFrameHandler? RawFrameReceived;

    // Guid.Empty when not hosting or not announcing.
    public Guid LANAnnouncerId => _lanAnnouncer?.AnnouncerId ?? Guid.Empty;

    // Deployment endpoints are sourced from the config store (Settings) so a build can point at another
    // server without code edits; unset, they follow ServiceConfig. An explicit setter still wins (host
    // bootstrap / tests). -xlinka
    private const string KeyBackendUrl = "Network.BackendDirectory.Url";

    private static string? _backendSessionDirectoryUrl;

    // Read from config key Network.BackendDirectory.Url. The fallback follows the SERVICE CONFIG rather
    // than a hardcoded localhost: everything else in the client already talks to the configured API host,
    // and a directory client that quietly points somewhere else means a shipped build registers with a
    // dev box that is not there and nobody's session is ever listed. -xlinka
    public static string BackendSessionDirectoryUrl
    {
        get => _backendSessionDirectoryUrl ??= Settings.ReadValue(KeyBackendUrl,
            Lumora.Nexus.Cloud.Cdn.ServiceConfig.Current.ApiBase.TrimEnd('/') + "/api");
        set => _backendSessionDirectoryUrl = value;
    }

    // Provides the current backend auth token for host-owned session registration. Nothing assigned this,
    // so every backend directory registration bailed at the null check below and no session ever reached
    // the internet listing. It now falls back to the signed-in account's token, which is the only token
    // that exists; the setter stays for host bootstrap and tests. -xlinka
    public static Func<string?> BackendAuthTokenProvider { get; set; } = DefaultBackendAuthToken;

    private static string? DefaultBackendAuthToken()
        => Engine.Current?.CDNClient?.CurrentSession?.Token;

    private Session(World world)
    {
        World = world;
        Connections = new SessionConnectionManager(this);
        // Wired here, for a joined session as much as a hosted one. Only the hosting path used to wire it,
        // and that is the one side whose host link never closes: a guest heard its host go and stayed in
        // the dead world, because nothing passed the word on to the world manager. -xlinka
        Connections.OnHostDisconnected += () => OnDisconnected?.Invoke();
    }

    public static Session NewSession(World world, ushort port = 7777)
    {
        var metadata = new SessionMetadata
        {
            Name = world.Name,
            Visibility = SessionVisibility.Private,
            MaxUsers = 16
        };

        return NewSession(world, port, metadata);
    }

    public static Session NewSession(World world, ushort port, SessionMetadata metadata)
    {
        if (world.IsUserspace)
            throw new InvalidOperationException("Private userspace cannot be networked.");
        var session = new Session(world);

        session.Metadata = metadata ?? new SessionMetadata();
        session.Metadata.SessionId = SessionIdentifier.Generate();
        session.Metadata.StartTime = DateTime.UtcNow;
        session.Metadata.LastUpdate = DateTime.UtcNow;
        session.Metadata.HostUsername ??= Environment.UserName;
        session.Metadata.ActiveUsers = 1;
        // What browsers match against their own build before they list it.
        session.Metadata.VersionHash = Lumora.Core.Security.Integrity.BuildIdentity.Hash;

        LumoraLogger.Debug($"[lnl] Creating new session '{metadata!.Name}' on port {port}");
        LumoraLogger.Log($"[lnl] Session ID: {session.Metadata.SessionId}");

        if (!session.Connections.StartListener(port))
        {
            throw new Exception($"Failed to start listener on port {port}");
        }

        // Advertise a URL per transport now that the listeners are up. The LAN/direct lnl:// URLs (one per local
        // interface IP) come from the URL builder, because the LNL listener's own URI is a 0.0.0.0 wildcard; every
        // OTHER transport (e.g. Steam) contributes its concrete dialable GlobalUri (steam://<id>/...). LAN URLs go
        // first so same-subnet joiners use cheap UDP and discovery's source-IP match still finds an lnl URL. -xlinka
        var urls = new List<Uri>(SessionUrlBuilder.GetLocalSessionUrls(port, session.Metadata.SessionId));
        foreach (var listener in session.Connections.StartedListeners)
        {
            var globalUri = listener.GlobalUri;
            if (globalUri == null)
                continue;
            // The concrete LAN IPs are already added above; skip the LNL wildcard (lnl://0.0.0.0:port).
            if (string.Equals(globalUri.Scheme, "lnl", StringComparison.OrdinalIgnoreCase))
                continue;
            urls.Add(globalUri);
        }
        session.Metadata.SessionURLs = urls;

        session.Sync = new SessionSyncManager(session);
        session.AssetTransferer = new SessionAssetTransferer(session);
        session.Sync.Start();
        if (Engine.Current is { } e)
            e.ActiveSessionTransferer = session.AssetTransferer;

        if (metadata.Visibility == SessionVisibility.LAN ||
            metadata.Visibility == SessionVisibility.Public)
        {
            session.StartLANAnnouncer();
        }

        if (ShouldList(metadata.Visibility))
        {
            // Detached on purpose - don't stall host creation on a public-server round-trip. The method
            // logs its own failures; the continuation catches anything that escapes so it can't go silent.
            _ = session.StartPublicServerRegistration().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    LumoraLogger.Warn($"[lnl] Session: Public server registration faulted - {t.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
        }

        LumoraLogger.Debug($"[lnl] Session created as host with {session.Metadata.SessionURLs.Count} URLs");
        return session;
    }

    public static Session JoinSession(World world, IEnumerable<Uri> addresses)
    {
        var session = JoinSessionAsync(world, addresses).GetAwaiter().GetResult();
        if (session == null)
        {
            throw new Exception("Failed to connect to session");
        }

        return session;
    }

    public static async Task<Session?> JoinSessionAsync(World world, IEnumerable<Uri> addresses)
    {
        if (world.IsUserspace)
            throw new InvalidOperationException("Private userspace cannot join a network session.");
        var session = new Session(world);
        foreach (var address in addresses)
        {
            if (SessionUrlBuilder.TryParseSessionId(address, out var sessionId))
            {
                session._joinedSessionId = sessionId;
                break;
            }
        }

        LumoraLogger.Debug($"[lnl] Joining session at {string.Join(", ", addresses)}");

        // Run the ENTIRE join - socket connect, handshake, and starting the sync threads - on a background
        // thread, never the main/update thread. Our network is polled from the engine update loop, so doing
        // ANY of this on the main thread (even the synchronous connect call) risks freezing the client mid-join.
        // ConfigureAwait(false) keeps every continuation off the main thread too. -xlinka
        return await Task.Run(async () =>
        {
            bool connected;
            try
            {
                connected = await session.Connections.ConnectToAsync(addresses).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"[lnl] Failed to connect to session: {ex.Message}");
                connected = false;
            }

            if (!connected)
            {
                session.Dispose();
                return (Session?)null;
            }

            session.Sync = new SessionSyncManager(session);
            session.AssetTransferer = new SessionAssetTransferer(session);
            session.Sync.Start();
            if (Engine.Current is { } e)
                e.ActiveSessionTransferer = session.AssetTransferer;

            LumoraLogger.Log("[lnl] Session joined as client");
            return (Session?)session;
        }).ConfigureAwait(false);
    }

    private void StartLANAnnouncer()
    {
        // Turned off in the network settings: nothing goes out on this network about the session.
        if (_lanAnnouncer != null || !EngineSettings.LanDiscovery)
            return;

        _lanAnnouncer = new LANAnnouncer();
        _lanAnnouncer.StartAnnouncing(Metadata);
    }

    private void StopLANAnnouncer()
    {
        _lanAnnouncer?.StopAnnouncing();
        _lanAnnouncer?.Dispose();
        _lanAnnouncer = null!;
    }

    // Returns a Task so callers can observe faults; it runs detached (callers must not block session setup on a
    // network round-trip).
    private async Task StartPublicServerRegistration()
    {
        await StartDirectoryAsync(StartNatNode());
    }

    private static readonly TimeSpan NatAddressWait = TimeSpan.FromSeconds(3);

    // Listed for others to find. The cloud directory takes Public sessions, and Friends ones so the host's
    // friends can find and join them, which needs a signed-in host to have any friends at all. Whatever is
    // listed is held on a NAT node too, so it can be reached from outside this network. -xlinka
    private static bool ShouldList(SessionVisibility visibility)
        => visibility == SessionVisibility.Public
           || (visibility == SessionVisibility.Contacts && !string.IsNullOrWhiteSpace(BackendAuthTokenProvider?.Invoke()));

    // A Private or LAN world is listed nowhere until its host invites someone. Then it goes on the directory
    // hidden, with its NAT node: the service only carries an invite to a session it knows, and the node is the
    // way in for an invited account outside this network. Nobody browsing ever sees it, and it stays listed that
    // way for the rest of the session. -xlinka
    private volatile bool _listedForInvites;

    private bool CanList(SessionVisibility visibility)
        => ShouldList(visibility)
           || (_listedForInvites && visibility is SessionVisibility.Private or SessionVisibility.LAN
               && !string.IsNullOrWhiteSpace(BackendAuthTokenProvider?.Invoke()));

    // Whether the directory has this session now.
    public bool IsListed => _directoryClient?.IsRegistered == true;

    // Host side: list this session for invites if nothing has listed it yet. Answers at once; IsListed says
    // when the directory has it.
    public void ListForInvites()
    {
        if (IsDisposed || Metadata == null || World == null || !World.IsAuthority)
            return;
        _listedForInvites = true;
        if (_directoryClient != null || !CanList(Metadata.Visibility))
            return;
        _ = StartPublicServerRegistration().ContinueWith(t =>
        {
            if (t.IsFaulted)
                LumoraLogger.Warn($"[lnl] Session: listing for invites faulted - {t.Exception?.GetBaseException().Message}");
        }, TaskScheduler.Default);
    }

    // The session's own LNL listener registers on the nearest official NAT node, so a joiner punches to, or is
    // relayed into, the socket this session already accepts on, and comes in as any other peer. The same
    // listener asks the router to forward its port, which lets people outside connect to it directly.
    private LNLListener? StartNatNode()
    {
        var listener = Connections.StartedListeners.OfType<LNLListener>().FirstOrDefault();
        if (listener == null)
            return null;
        if (Interlocked.CompareExchange(ref _natListener, listener, null) != null)
            return _natListener;
        listener.NatUrisChanged += OnNatUrisChanged;
        listener.PortMappingChanged += RebuildReachUrls;
        EngineSettings.Changed += OnNetworkSettingsChanged;
        listener.StartNodeLink();
        if (EngineSettings.PortForwarding)
            listener.StartPortMapping();
        _streamReach = new SessionStreamReach(Connections.StartedListeners, listener, RebuildReachUrls);
        _streamReach.Start(EngineSettings.PortForwarding);
        _ipv6Listed = EngineSettings.Ipv6;
        return listener;
    }

    private void StopNatNode()
    {
        var listener = Interlocked.Exchange(ref _natListener, null);
        if (listener == null)
            return;
        listener.NatUrisChanged -= OnNatUrisChanged;
        listener.PortMappingChanged -= RebuildReachUrls;
        EngineSettings.Changed -= OnNetworkSettingsChanged;
        listener.StopNodeLink();
        listener.StopPortMapping();
        Interlocked.Exchange(ref _streamReach, null)?.Stop();
        RebuildReachUrls(listener);
    }

    // TCP and QUIC reach of a listed session: through a node over TCP, and their router forwards.
    private SessionStreamReach? _streamReach;
    private List<Uri> _streamUrls = new();

    internal SessionStreamReach? StreamReach => _streamReach;

    private bool _ipv6Listed;

    // Turning router forwarding or IPv6 on or off reaches a listed session at once. Taking a forward back
    // waits on the router, so it runs off the thread the setting was changed on.
    private void OnNetworkSettingsChanged()
    {
        var listener = _natListener;
        if (listener == null)
            return;
        if (EngineSettings.PortForwarding && listener.PortMapper == null)
            listener.StartPortMapping();
        else if (!EngineSettings.PortForwarding && listener.PortMapper != null)
            _ = Task.Run(listener.StopPortMapping);
        _streamReach?.Forwarding(EngineSettings.PortForwarding);
        if (_ipv6Listed != EngineSettings.Ipv6)
        {
            _ipv6Listed = EngineSettings.Ipv6;
            RebuildReachUrls();
        }
    }

    private void OnNatUrisChanged(IReadOnlyList<Uri> uris)
    {
        foreach (var uri in uris)
            LumoraLogger.Log($"[lnl] Session reachable from outside at {uri}");
        RebuildReachUrls();
    }

    private void RebuildReachUrls() => RebuildReachUrls(_natListener);

    // Off the main thread. The address list is swapped whole, never edited where someone may be reading it.
    // What can change while the session runs is put back in place each time: IPv6 direct addresses as the
    // setting stands, the router-forwarded address, and the node addresses, after the direct ones and ahead
    // of Steam.
    private void RebuildReachUrls(LNLListener? listener)
    {
        var metadata = Metadata;
        if (metadata == null || IsDisposed)
            return;
        bool held = listener != null && listener == _natListener;
        var mapped = held && listener!.PortMapper is { State: Lumora.Nexus.Transport.PortMapping.PortMapState.Mapped, External: { } external }
            ? SessionUrlBuilder.BuildLNLUrl(external.Address.ToString(), external.Port, metadata.SessionId)
            : null;
        var direct = new List<Uri>();
        var rest = new List<Uri>();
        foreach (var url in metadata.SessionURLs ?? new List<Uri>())
        {
            if (url == null || string.Equals(url.Scheme, LNLNetworkManager.NAT_SCHEME, StringComparison.OrdinalIgnoreCase)
                || string.Equals(url.Scheme, Lumora.Nexus.Transport.LNT.LNTNetworkManager.NAT_SCHEME, StringComparison.OrdinalIgnoreCase)
                || Equals(url, _mappedUrl) || IsIpv6Direct(url) || _streamUrls.Contains(url))
                continue;
            (string.Equals(url.Scheme, LNLNetworkManager.SCHEME, StringComparison.OrdinalIgnoreCase) ? direct : rest).Add(url);
        }
        int port = listener?.LocalUri?.Port ?? direct.FirstOrDefault()?.Port ?? 0;
        if (EngineSettings.Ipv6 && port > 0)
        {
            foreach (var address in Lumora.Nexus.Transport.HostAddresses.GlobalIpv6)
                direct.Add(SessionUrlBuilder.BuildLNLUrl(address.ToString(), port, metadata.SessionId));
        }
        if (mapped != null)
        {
            direct.Add(mapped);
            if (!Equals(mapped, _mappedUrl))
                LumoraLogger.Log($"[lnl] Session reachable through the router at {mapped}");
        }
        _mappedUrl = mapped;
        if (held)
            direct.AddRange(listener!.NatUris);
        _streamUrls = held && _streamReach != null ? _streamReach.Urls(metadata.SessionId) : new List<Uri>();
        direct.AddRange(_streamUrls);
        direct.AddRange(rest);
        metadata.SessionURLs = direct;
        metadata.LastUpdate = DateTime.UtcNow;
        _lanAnnouncer?.UpdateMetadata(metadata);
        _directoryClient?.SendHeartbeat(metadata.ActiveUsers, GetUserList(), GetUserIds());
    }

    private Uri? _mappedUrl;

    private static bool IsIpv6Direct(Uri url)
        => string.Equals(url.Scheme, LNLNetworkManager.SCHEME, StringComparison.OrdinalIgnoreCase)
           && url.HostNameType == UriHostNameType.IPv6;

    private async Task StartDirectoryAsync(LNLListener? nat)
    {
        if (_directoryClient != null)
            return;

        BackendSessionDirectoryClient? client = null;
        try
        {
            var backendUrl = BackendSessionDirectoryUrl;
            // Auth tokens ride this URL; warn on plain http unless it's an explicit loopback dev target.
            if (backendUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !backendUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                && !backendUrl.Contains("127.0.0.1", StringComparison.Ordinal))
            {
                LumoraLogger.Warn($"[lnl] Session: backend directory URL '{backendUrl}' is plain http; use https in deployment.");
            }

            // Listed as a guest when nobody is signed in, and it stays a guest's listing for the life of the
            // session: signing in halfway would present a different owner for a listing the guest key holds.
            bool asGuest = string.IsNullOrWhiteSpace(BackendAuthTokenProvider?.Invoke());
            client = new BackendSessionDirectoryClient(
                backendUrl,
                asGuest ? () => null : () => BackendAuthTokenProvider?.Invoke());
            _directoryClient = client;
            if (asGuest)
                LumoraLogger.Log("[lnl] Session: not signed in, listing the session as a guest");

            // The NAT node's address is what lets a joiner outside this network in, so the first listing waits
            // a moment for it. A node that answers later joins the listing on a heartbeat.
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (nat != null && nat.NatUris.Count == 0 && _natListener == nat && waited.Elapsed < NatAddressWait)
                await Task.Delay(100);
            if (_directoryClient != client)
                return;

            // True means the publisher is running, not that the first register succeeded - it keeps retrying
            // and re-registers itself if the directory expires us. Only a missing token gives up here.
            if (await client.StartAsync(Metadata, GetUserList, GetUserIds))
            {
                LumoraLogger.Log($"[lnl] Session publishing to backend directory at {BackendSessionDirectoryUrl}");
                return;
            }
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"[lnl] Session: Backend directory registration failed - {ex.Message}");
        }
        if (client == null)
            return;
        // only our own: a flip to Private and back may already have handed the slot to a newer client
        if (Interlocked.CompareExchange(ref _directoryClient, null, client) == client)
            client.Dispose();
    }

    private string[] GetUserList()
    {
        if (World == null)
            return new[] { Metadata?.HostUsername ?? "Host" };

        var users = World.GetAllUsers();
        if (users == null || users.Count == 0)
            return new[] { Metadata?.HostUsername ?? "Host" };

        var usernames = new List<string>();
        foreach (var user in users)
        {
            var name = user.UserName?.Value;
            if (!string.IsNullOrEmpty(name))
                usernames.Add(name);
        }

        return usernames.Count > 0 ? usernames.ToArray() : new[] { Metadata?.HostUsername ?? "Host" };
    }

    // The account ids of the signed-in users here, which is how the directory tells a friend where someone is.
    // Guests have none and are left out.
    private string[] GetUserIds()
    {
        var world = World;
        if (world == null)
            return Array.Empty<string>();

        var ids = new List<string>();
        foreach (var user in world.GetAllUsers())
        {
            var id = user?.AccountId?.Value;
            if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
                ids.Add(id);
        }
        return ids.ToArray();
    }

    private void StopPublicServerRegistration()
    {
        StopNatNode();

        var directory = _directoryClient;
        _directoryClient = null;
        if (directory == null)
            return;

        // Taking the session OUT of the directory is a network round-trip, and this runs on the world thread
        // (world close, or flipping visibility off Public). Hand it off so a slow or dead backend cannot
        // stall teardown; the client stays alive until its own task disposes it. Skipping the unregister
        // entirely would leave a dead session in everyone's browser for a full expiry window. -xlinka
        _ = Task.Run(async () =>
        {
            try
            {
                await directory.StopAsync();
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"[lnl] Session: Backend directory unregister failed - {ex.Message}");
            }
            finally
            {
                directory.Dispose();
            }
        });
    }

    public void Poll()
    {
        Connections.Poll();
    }

    // The frame is routed through the same authority/relay path as StreamMessage, so sender-identity validation
    // still applies. Sequence is opaque to the framework; use it for jitter buffering downstream. Returns false
    // if the stream is not local, the payload is over the cap, or there are no eligible targets.
    public bool SendRawFrame(Stream stream, ushort sequence, ReadOnlySpan<byte> payload)
    {
        if (Sync == null || IsDisposed) return false;
        return Sync.EnqueueRawFrame(stream, sequence, payload);
    }

    // Called by SessionSyncManager on the sync thread.
    internal void HandleIncomingRawFrame(User sender, RefID streamRefID, ushort sequence, ReadOnlyMemory<byte> payload)
    {
        RawFrameReceived?.Invoke(sender, streamRefID, sequence, payload);
    }

    public void UpdateMetadata(Action<SessionMetadata> update)
    {
        if (Metadata == null)
            return;

        update?.Invoke(Metadata);
        Metadata.LastUpdate = DateTime.UtcNow;

        _lanAnnouncer?.UpdateMetadata(Metadata);
    }

    internal void OnUserCountChanged(int count)
    {
        if (Metadata == null)
            return;

        Metadata.ActiveUsers = count;
        Metadata.LastUpdate = DateTime.UtcNow;

        _lanAnnouncer?.UpdateMetadata(Metadata);

        _directoryClient?.SendHeartbeat(count, GetUserList(), GetUserIds());
    }

    public void SetVisibility(SessionVisibility visibility)
    {
        if (Metadata == null)
            return;
        // The home is never listed or announced, whoever asks. -xlinka
        if (visibility != SessionVisibility.Private && Lumora.Core.Management.WorldManager.IsLocalHome(World))
        {
            LumoraLogger.Warn($"Session: the local world stays private, visibility {visibility} refused");
            visibility = SessionVisibility.Private;
        }

        var oldVisibility = Metadata.Visibility;
        Metadata.Visibility = visibility;
        Metadata.LastUpdate = DateTime.UtcNow;

        bool shouldAnnounceLAN = visibility == SessionVisibility.LAN ||
                                 visibility == SessionVisibility.Public;

        bool wasAnnouncingLAN = oldVisibility == SessionVisibility.LAN ||
                                oldVisibility == SessionVisibility.Public;

        if (shouldAnnounceLAN && !wasAnnouncingLAN)
        {
            StartLANAnnouncer();
        }
        else if (!shouldAnnounceLAN && wasAnnouncingLAN)
        {
            StopLANAnnouncer();
        }
        else if (shouldAnnounceLAN)
        {
            _lanAnnouncer?.UpdateMetadata(Metadata);
        }

        if (!CanList(visibility))
        {
            if (_directoryClient != null || _natListener != null)
                StopPublicServerRegistration();
            return;
        }

        if (_directoryClient == null)
        {
            _ = StartPublicServerRegistration().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    LumoraLogger.Warn($"[lnl] Session: Public server registration faulted - {t.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
            return;
        }

        // Still listed, Anyone to Friends or back: the directory entry takes the new level where it stands, and
        // the NAT node keeps holding the session either way.
        if (oldVisibility != visibility)
            _directoryClient.PushListing();
        StartNatNode();
    }

    // The game's build identity changed while hosting (something was loaded into it): listings take the new one,
    // so browsers on the old build drop the session and ones on the new build can find it.
    public void RefreshBuild()
    {
        if (Metadata == null || IsDisposed)
            return;
        Metadata.VersionHash = Lumora.Core.Security.Integrity.BuildIdentity.Hash;
        Metadata.LastUpdate = DateTime.UtcNow;
        _lanAnnouncer?.UpdateMetadata(Metadata);
        _directoryClient?.PushListing();
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        LumoraLogger.Log("[lnl] Disposing session");

        StopLANAnnouncer();

        StopPublicServerRegistration();

        AssetTransferer?.Dispose();
        if (Engine.Current?.ActiveSessionTransferer == AssetTransferer)
            Engine.Current!.ActiveSessionTransferer = null!;

        Sync?.Dispose();
        Connections?.Dispose();

        World = null!;
        Metadata = null!;
    }
}
