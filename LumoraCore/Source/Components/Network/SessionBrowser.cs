// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Nexus.Discovery;
using Lumora.Core.Networking.Session;
using Lumora.Nexus.Cloud;
using Lumora.Core.Security.Integrity;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Network;

[ComponentCategory("Network")]
public class SessionBrowser : Component
{
    private LANDiscovery _discovery = null!;
    private BackendSessionDirectoryQuery _backendQuery = null!;

    // Single aggregate of every session we know about, keyed by SessionId (lowercased). LAN discovery, the
    // backend directory and our own hosted session all funnel through ONE upsert path (Upsert) into this one
    // SessionId-keyed collection - so the same session can never list twice and an entry seen from two angles
    // just merges. -xlinka
    private readonly Dictionary<string, SessionListEntry> _sessions = new();
    private readonly object _sessionsLock = new();

    // Keys currently held by the backend directory. The directory sends a full snapshot every poll, so this
    // is how we tell "the host stopped advertising" from "we never heard about it on LAN": anything in here
    // that falls out of a snapshot goes, anything sourced from LAN is left alone. -xlinka
    private readonly HashSet<string> _backendKeys = new();

    // Tracks the key of our own hosted session so we can keep it fresh and drop it when we stop hosting.
    private string? _ownSessionKey;

    // Re-publish our own session into the list on a light timer (the host re-announces continuously; we mirror
    // that by re-reading live metadata instead of relying on our own filtered-out broadcast). -xlinka
    private float _ownRefreshTimer;
    private const float OwnRefreshInterval = 1f; // seconds

    public readonly Sync<bool> IsScanning;

    public int SessionCount
    {
        get
        {
            lock (_sessionsLock)
            {
                return _sessions.Count;
            }
        }
    }

    public event Action<SessionListEntry> OnSessionFound = null!;

    public event Action<string> OnSessionLost = null!;

    public event Action<SessionListEntry> OnSessionUpdated = null!;

    public SessionBrowser()
    {
        IsScanning = new Sync<bool>(this, false);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        GameIntegrity.Changed += OnBuildChanged;
    }

    public override void OnDestroy()
    {
        GameIntegrity.Changed -= OnBuildChanged;
        StopScanning();
        base.OnDestroy();
    }

    // Only sessions on this game's own build are listed: another release, or other game files on either side,
    // could never be joined. Waits for the startup integrity check on the feed threads that call it.
    private static bool Listable(SessionListEntry entry) => BuildIdentity.Matches(entry.VersionHash);

    // Something was loaded into this game: what was listed belongs to the old build, so start the list over.
    // LAN hosts re-announce within seconds and the directory is asked again now.
    private void OnBuildChanged()
    {
        lock (_sessionsLock)
        {
            _sessions.Clear();
            _backendKeys.Clear();
        }
        _ownSessionKey = null;
        _backendQuery?.RequestRefresh();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (!IsScanning.Value)
            return;

        // Keep our own hosted session live in the list (user count, name changes, and removing it the moment we
        // stop hosting). This is the local-update equivalent of the host re-announcing every tick. -xlinka
        _ownRefreshTimer += delta;
        if (_ownRefreshTimer >= OwnRefreshInterval)
        {
            _ownRefreshTimer = 0f;
            RefreshOwnHostedSession();
        }
    }

    // also false when polling never started; check IsScanning alongside it
    public bool BackendUnreachable => _backendQuery?.IsUnreachable ?? true;

    // LAN discovery is already continuous; this only nudges the backend poll off its interval
    public void RequestRefresh() => _backendQuery?.RequestRefresh();

    public void StartScanning()
    {
        if (_discovery != null)
            return;

        _discovery = new LANDiscovery();
        _discovery.SessionFound += OnDiscoveryFound;
        _discovery.SessionLost += OnDiscoveryLost;
        _discovery.SessionUpdated += OnDiscoveryUpdated;
        // Local network discovery off in the network settings: the browser only shows what the directory lists.
        bool listenLocally = EngineSettings.LanDiscovery;

        // If we're hosting, ignore our own announcer so the discovery side doesn't surface our broadcast copy.
        // We list our own session directly from live metadata instead (RefreshOwnHostedSession). -xlinka
        Guid? ignoreId = null;
        if (World?.Session != null)
        {
            var announcerId = World.Session.LANAnnouncerId;
            if (announcerId != Guid.Empty)
            {
                ignoreId = announcerId;
                LumoraLogger.Debug($"SessionBrowser: Filtering out own announcer ID: {announcerId}");
            }
        }

        if (listenLocally)
            _discovery.StartDiscovery(ignoreId);

        // Second source: sessions published to the backend directory by hosts we will never hear on the
        // local network. It polls on its own thread and reports through the same upsert path.
        _backendQuery = new BackendSessionDirectoryQuery(Lumora.Core.Networking.Session.Session.BackendSessionDirectoryUrl,
            () => BuildIdentity.TryGetHash(out var build) ? build : null);
        _backendQuery.OnResults += OnBackendResults;
        _backendQuery.Start();

        IsScanning.Value = true;

        // Show our own hosted session immediately, then OnUpdate keeps it fresh.
        _ownRefreshTimer = 0f;
        RefreshOwnHostedSession();

        LumoraLogger.Debug("SessionBrowser: Started scanning for sessions");
    }

    public void StopScanning()
    {
        if (_backendQuery != null)
        {
            _backendQuery.OnResults -= OnBackendResults;
            _backendQuery.Dispose();
            _backendQuery = null!;
        }

        if (_discovery == null)
        {
            IsScanning.Value = false;
            return;
        }

        _discovery.SessionFound -= OnDiscoveryFound;
        _discovery.SessionLost -= OnDiscoveryLost;
        _discovery.SessionUpdated -= OnDiscoveryUpdated;
        _discovery.StopDiscovery();
        _discovery.Dispose();
        _discovery = null!;

        IsScanning.Value = false;

        LumoraLogger.Debug("SessionBrowser: Stopped scanning");
    }

    public void ClearSessions()
    {
        lock (_sessionsLock)
        {
            _sessions.Clear();
            _backendKeys.Clear();
        }
        _ownSessionKey = null;
        _discovery?.ClearSessions();
    }

    public List<SessionListEntry> GetSessions()
    {
        lock (_sessionsLock)
        {
            return _sessions.Values.ToList();
        }
    }

    public SessionListEntry GetSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null!;

        lock (_sessionsLock)
        {
            return _sessions.TryGetValue(sessionId.ToLowerInvariant(), out var entry) ? entry : null!;
        }
    }

    // A session the list may not hold: a friend's Friends session is only ever looked up by id, under this
    // machine's sign-in, and stays out of the list after. Whatever is listed already answers first.
    // Completes off the update. -xlinka
    public async System.Threading.Tasks.Task<SessionListEntry?> FindSessionAsync(string sessionId)
    {
        var listed = GetSession(sessionId);
        if (listed != null)
            return listed;
        var query = _backendQuery;
        if (query == null || string.IsNullOrEmpty(sessionId))
            return null;
        var dto = await query.GetSessionAsync(sessionId, Lumora.Core.Networking.Session.Session.BackendAuthTokenProvider?.Invoke())
            .ConfigureAwait(false);
        return dto == null ? null : BuildEntry(dto);
    }

    // --- Single choke point every source funnels through -------------------------------------------------

    // Insert or update a session in the aggregate, keyed by SessionId. Fires OnSessionFound for a brand-new
    // session and OnSessionUpdated for one we already had, so the same session never lists twice no matter how
    // many sources report it. -xlinka
    private void Upsert(SessionListEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.SessionId))
            return;

        string key = entry.SessionId.ToLowerInvariant();
        bool isNew;
        lock (_sessionsLock)
        {
            isNew = !_sessions.ContainsKey(key);
            _sessions[key] = entry;

            // A session we can now see locally is no longer the directory's to remove: LAN sees it live,
            // and the directory listing lags by up to a poll.
            if (entry.Source == SessionSource.Local)
                _backendKeys.Remove(key);
        }

        if (isNew)
        {
            LumoraLogger.Debug($"SessionBrowser: Found session '{entry.Name}' ({entry.ActiveUsers}/{entry.MaxUsers} users)");
            OnSessionFound?.Invoke(entry);
        }
        else
        {
            OnSessionUpdated?.Invoke(entry);
        }
    }

    private void Remove(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        string key = sessionId.ToLowerInvariant();
        SessionListEntry? removed = null;
        lock (_sessionsLock)
        {
            if (_sessions.TryGetValue(key, out removed))
                _sessions.Remove(key);
            _backendKeys.Remove(key);
        }

        if (removed != null)
        {
            LumoraLogger.Debug($"SessionBrowser: Lost session '{removed.Name}'");
            OnSessionLost?.Invoke(sessionId);
        }
    }

    // --- LAN discovery feed ------------------------------------------------------------------------------

    private void OnDiscoveryFound(DiscoveredSession discovered) => UpsertLocal(CreateEntry(discovered));

    private void OnDiscoveryUpdated(DiscoveredSession discovered) => UpsertLocal(CreateEntry(discovered));

    private void UpsertLocal(SessionListEntry entry)
    {
        if (Listable(entry))
            Upsert(entry);
        else
            Remove(entry.SessionId);
    }

    private void OnDiscoveryLost(string sessionId) => Remove(sessionId);

    // --- Backend directory feed --------------------------------------------------------------------------

    // Fold a directory snapshot into the aggregate. Three rules, in order:
    //
    //   1. Our own hosted session never comes back in through here. RefreshOwnHostedSession lists it from
    //      live metadata; the directory copy is up to a heartbeat stale and would fight it every poll.
    //   2. A session LAN discovery can see wins. Same session, and the LAN copy carries the address the
    //      announcer was actually reached on, which beats whatever the host advertised to the internet.
    //   3. Anything the previous snapshot gave us that is missing from this one is gone. That is the only
    //      "lost" signal the directory has - hosts stop heartbeating and the backend expires them.
    //
    // An empty snapshot (backend down, offline, nothing listed) is a legitimate answer and clears every
    // backend-sourced row. It never leaves stale entries behind and never invents one. -xlinka
    private void OnBackendResults(IReadOnlyList<SessionListingDto> listings)
    {
        var seen = new HashSet<string>();
        var fresh = new List<SessionListEntry>(listings.Count);

        for (int i = 0; i < listings.Count; i++)
        {
            var entry = BuildEntry(listings[i]);
            if (entry == null)
                continue;   // no session id or no dialable URL: nothing we could join
            if (!Listable(entry))
                continue;   // another build: just as unjoinable, and dropped like a listing that went away

            string key = entry.SessionId.ToLowerInvariant();
            if (key == _ownSessionKey)
                continue;

            if (!seen.Add(key))
                continue;   // duplicate id in one snapshot

            fresh.Add(entry);
        }

        var stale = new List<string>();
        var updates = new List<SessionListEntry>(fresh.Count);

        lock (_sessionsLock)
        {
            foreach (var key in _backendKeys)
            {
                if (!seen.Contains(key))
                    stale.Add(key);
            }

            for (int i = 0; i < fresh.Count; i++)
            {
                string key = fresh[i].SessionId.ToLowerInvariant();
                if (_sessions.TryGetValue(key, out var existing) && existing.Source == SessionSource.Local)
                    continue;   // LAN copy wins
                updates.Add(fresh[i]);
            }
        }

        // Upsert/Remove take the lock themselves and raise events, so they run outside it.
        for (int i = 0; i < stale.Count; i++)
            Remove(stale[i]);

        for (int i = 0; i < updates.Count; i++)
        {
            Upsert(updates[i]);
            lock (_sessionsLock)
            {
                _backendKeys.Add(updates[i].SessionId.ToLowerInvariant());
            }
        }
    }

    // --- Own hosted session feed -------------------------------------------------------------------------

    private void RefreshOwnHostedSession()
    {
        var session = World?.Session;
        var m = session?.Metadata;

        // Not hosting (or metadata not ready) - drop our own entry if we had one listed.
        if (session == null || session.LANAnnouncerId == Guid.Empty || m == null || string.IsNullOrEmpty(m.SessionId))
        {
            if (_ownSessionKey != null)
            {
                Remove(_ownSessionKey);
                _ownSessionKey = null;
            }
            return;
        }

        var entry = BuildEntry(m);
        _ownSessionKey = entry.SessionId.ToLowerInvariant();
        Upsert(entry);
    }

    // --- Entry builders ----------------------------------------------------------------------------------

    private static SessionListEntry CreateEntry(DiscoveredSession discovered)
    {
        var entry = BuildEntry(discovered.Metadata);
        entry.JoinUrl = discovered.GetConnectionUrl();
        if (entry.JoinUrl != null && !entry.JoinUrls.Contains(entry.JoinUrl))
            entry.JoinUrls.Insert(0, entry.JoinUrl);
        return entry;
    }

    private static SessionListEntry BuildEntry(SessionMetadata m)
    {
        return new SessionListEntry
        {
            SessionId = m.SessionId,
            Name = m.Name,
            Description = m.Description,
            HostUsername = m.HostUsername,
            HostUserId = m.HostUserId ?? "",
            ActiveUsers = m.ActiveUsers,
            MaxUsers = m.MaxUsers,
            Visibility = m.Visibility,
            JoinUrl = (m.SessionURLs != null && m.SessionURLs.Count > 0) ? m.SessionURLs[0] : null!,
            JoinUrls = m.SessionURLs != null ? m.SessionURLs.Where(u => u != null).ToList() : new List<Uri>(),
            ThumbnailUrl = m.ThumbnailUrl,
            ThumbnailBase64 = m.ThumbnailBase64,
            Tags = m.Tags != null ? new List<string>(m.Tags) : new List<string>(),
            VersionHash = m.VersionHash ?? "",
            Source = SessionSource.Local
        };
    }

    // Map a directory listing onto the same entry the LAN path produces. Every field is copied straight
    // from what the backend sent; nothing is filled in from guesses, so ThumbnailBase64 stays null (the
    // directory carries no image data) and the description stays empty if the host never set one.
    // Returns null when the listing is unusable rather than surfacing an unjoinable row. -xlinka
    private static SessionListEntry? BuildEntry(SessionListingDto dto)
    {
        if (dto == null || string.IsNullOrEmpty(dto.SessionId))
            return null;

        var joinUrls = new List<Uri>();
        if (dto.SessionUrls != null)
        {
            foreach (var raw in dto.SessionUrls)
            {
                if (Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
                    joinUrls.Add(parsed);
            }
        }

        if (joinUrls.Count == 0)
            return null;
        var joinUrl = joinUrls[0];

        return new SessionListEntry
        {
            SessionId = dto.SessionId,
            Name = dto.Name ?? "",
            Description = dto.Description ?? "",
            HostUsername = dto.HostUsername ?? "",
            HostUserId = dto.HostUserId ?? "",
            ActiveUsers = dto.ActiveUsers,
            MaxUsers = dto.MaxUsers,
            Visibility = dto.AccessLevel,
            JoinUrl = joinUrl,
            JoinUrls = joinUrls,
            ThumbnailUrl = dto.ThumbnailUrl ?? "",
            ThumbnailBase64 = null,
            Tags = dto.Tags != null ? new List<string>(dto.Tags) : new List<string>(),
            VersionHash = dto.CompatibilityHash ?? "",
            Source = SessionSource.Internet
        };
    }
}
