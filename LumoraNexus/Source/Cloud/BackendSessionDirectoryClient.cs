// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Cloud;

// Publishes this host's session to the backend directory so it shows up in other people's world browser,
// then keeps it alive with a heartbeat until the session ends.
//
// The directory is a LIVENESS store, not a database: an entry dies about three missed heartbeats after we
// go quiet. That shapes everything here. One loop owns register and heartbeat, so a directory restart, a
// dropped request or an expired entry all recover the same way (heartbeat comes back 404, we register
// again on the next tick) instead of leaving the session silently unlisted for the rest of its run. -xlinka
public sealed class BackendSessionDirectoryClient : IDisposable
{
    // How often we tell the directory we are still here. The backend expires an entry at 3x this, so
    // changing it here means changing SessionDirectoryService.SessionTimeout to match.
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    // Retry delay after a failed register or heartbeat. Short enough to stay inside the expiry window.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    // Floor between the out-of-band pushes fired by user count changes. A busy session can churn users
    // several times a second and the directory does not need to hear about every one of them.
    private static readonly TimeSpan MinPushInterval = TimeSpan.FromSeconds(2);

    // Mirror of the directory's own validation limits. Trimming here rather than letting the backend 400
    // matters because a rejected register or heartbeat is not a one-off: it repeats every interval for the
    // life of the session, so one over-long world name would silently keep the session unlisted forever.
    // A trimmed name is honest (it is still the name, shortened); an unlisted session is not. -xlinka
    private const int MaxNameLength = 128;
    private const int MaxDescriptionLength = 2048;
    private const int MaxHashLength = 128;
    private const int MaxThumbnailUrlLength = 512;
    private const int MaxTagLength = 64;
    private const int MaxTags = 32;
    private const int MaxUsersCap = 256;
    private const int MaxUserListEntries = 64;
    private const int MaxSessionUrls = 8;
    private const int MaxUserIdLength = 64;
    private const string UserIdPrefix = "U-";

    private readonly string _apiBaseUrl;
    private readonly Func<string?> _tokenProvider;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    // One in-flight request at a time. The loop and the user-count push both write the same entry, and
    // letting them overlap means the directory can land them out of order.
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;
    private SessionMetadata? _metadata;
    private Func<string[]>? _getUserList;
    private Func<string[]>? _getUserIds;

    private volatile bool _running;
    private volatile bool _registered;

    // Set when the backend refuses in a way retrying cannot fix (session id owned by another account).
    private volatile bool _rejected;

    private DateTime _lastPushUtc = DateTime.MinValue;

    // Made up for this session and sent with every call. A host that is not signed in is listed as a guest,
    // and this is what makes the listing theirs: the directory only lets the holder of the key change or
    // remove it. A signed-in host sends it too; the account decides there.
    private readonly string _hostKey = NewHostKey();

    public bool IsRegistered => _registered;

    public BackendSessionDirectoryClient(string apiBaseUrl, Func<string?> tokenProvider)
    {
        _apiBaseUrl = NormalizeBaseUrl(apiBaseUrl);
        _tokenProvider = tokenProvider;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    internal static string NormalizeBaseUrl(string apiBaseUrl)
    {
        return string.IsNullOrWhiteSpace(apiBaseUrl)
            ? "http://localhost:5178/api"
            : apiBaseUrl.TrimEnd('/');
    }

    // Returns false only when there is no session to publish. A backend that is down or refusing right now
    // still returns true, because the loop keeps trying and the session gets listed as soon as it comes back.
    // Signed in or not, it publishes: without a token the session is listed as a guest's.
    // getUserIds gives the account ids of the signed-in users in the session, which is how the directory tells a
    // friend which session someone is in; guests have none and are left out.
    public async Task<bool> StartAsync(SessionMetadata metadata, Func<string[]> getUserList, Func<string[]>? getUserIds = null)
    {
        if (_running)
            return true;

        if (metadata == null || string.IsNullOrEmpty(metadata.SessionId))
        {
            LumoraLogger.Warn("BackendSessionDirectoryClient: Cannot publish a session with no id");
            return false;
        }

        _metadata = metadata;
        _getUserList = getUserList;
        _getUserIds = getUserIds;
        _running = true;
        _cts = new CancellationTokenSource();

        // First attempt inline so the caller's log line reflects what actually happened.
        await RegisterAsync(_cts.Token).ConfigureAwait(false);

        _heartbeatTask = Task.Run(() => RunAsync(_cts.Token));
        return true;
    }

    // Pushes the user count out of band, on top of the timed heartbeat. Throttled, and a no-op before
    // registration succeeds (the loop handles that case). Null userIds leaves the directory's list as it was.
    public void SendHeartbeat(int activeUsers, string[] userList, string[]? userIds = null)
    {
        if (!_running || !_registered || _metadata == null)
            return;

        var now = DateTime.UtcNow;
        if (now - _lastPushUtc < MinPushInterval)
            return;
        _lastPushUtc = now;

        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => HeartbeatAsync(activeUsers, userList, userIds, token));
    }

    // Safe to call more than once. Callers on the world thread should await this rather than relying on
    // Dispose, which has to block.
    public async Task StopAsync()
    {
        if (!_running)
            return;
        _running = false;

        _cts?.Cancel();

        var task = _heartbeatTask;
        _heartbeatTask = null;
        if (task != null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"BackendSessionDirectoryClient: Heartbeat loop ended with {ex.Message}");
            }
        }

        _cts?.Dispose();
        _cts = null;

        // Unregister on a fresh token: the loop's is cancelled, and leaving the entry behind means everyone
        // browsing sees a dead session for up to a minute.
        if (_registered)
            await UnregisterAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_rejected)
        {
            var delay = _registered ? HeartbeatInterval : RetryInterval;

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                if (_registered)
                {
                    var users = _getUserList?.Invoke() ?? Array.Empty<string>();
                    await HeartbeatAsync(_metadata?.ActiveUsers ?? users.Length, users, _getUserIds?.Invoke(), cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await RegisterAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"BackendSessionDirectoryClient: Directory loop failed - {ex.Message}");
            }
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        var metadata = _metadata;
        if (metadata == null || _rejected)
            return;

        if (!await _requestGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var payload = BuildRegistration(metadata);
            using var request = CreateRequest(HttpMethod.Post, $"{_apiBaseUrl}/sessions/register", payload);
            if (request == null)
                return;

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                if (!_registered)
                    LumoraLogger.Log($"BackendSessionDirectoryClient: Listed session {metadata.SessionId} on the directory");
                _registered = true;
                return;
            }

            _registered = false;

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // Someone else owns this session id. Retrying cannot change that, so stop hammering.
                _rejected = true;
                LumoraLogger.Warn($"BackendSessionDirectoryClient: Session id {metadata.SessionId} is claimed by another account, not listing");
                return;
            }

            LumoraLogger.Warn($"BackendSessionDirectoryClient: Register failed ({(int)response.StatusCode}), retrying");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _registered = false;
            LumoraLogger.Warn($"BackendSessionDirectoryClient: Register failed - {ex.Message}");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task HeartbeatAsync(int activeUsers, string[] userList, string[]? userIds, CancellationToken cancellationToken)
    {
        var metadata = _metadata;
        if (metadata == null || !_registered)
            return;

        if (!await _requestGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var users = NormalizeUsers(userList);
            var payload = new HeartbeatPayload
            {
                ActiveUsers = Math.Clamp(Math.Max(activeUsers, users.Length), 0, MaxUsersCap),
                UserList = users,
                UserIds = userIds == null ? null : NormalizeUserIds(userIds, metadata.MaxUsers),
                SessionUrls = BuildSessionUrls(metadata),
                Name = Clamp(metadata.Name, MaxNameLength),
                Tags = NormalizeTags(metadata.Tags)
            };

            var url = $"{_apiBaseUrl}/sessions/{Uri.EscapeDataString(metadata.SessionId)}/heartbeat";
            using var request = CreateRequest(HttpMethod.Patch, url, payload);
            if (request == null)
                return;

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // The directory forgot us: it expired the entry while we were unreachable, or it restarted.
                // Drop back to registering so the session reappears instead of heartbeating into a void.
                _registered = false;
                LumoraLogger.Log("BackendSessionDirectoryClient: Directory no longer has our session, re-registering");
                return;
            }

            LumoraLogger.Warn($"BackendSessionDirectoryClient: Heartbeat failed ({(int)response.StatusCode})");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"BackendSessionDirectoryClient: Heartbeat failed - {ex.Message}");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    // The access level or the build changed while listed (Anyone to Friends and back, something loaded into the
    // game): the entry is updated where it stands. Taking it down and putting it back would race its own removal,
    // the same session id going both ways.
    public void PushListing()
    {
        if (!_running || !_registered || _metadata == null)
            return;
        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => UpdateListingAsync(token));
    }

    private async Task UpdateListingAsync(CancellationToken cancellationToken)
    {
        var metadata = _metadata;
        if (metadata == null)
            return;

        try
        {
            var url = $"{_apiBaseUrl}/sessions/{Uri.EscapeDataString(metadata.SessionId)}";
            using var request = CreateRequest(HttpMethod.Patch, url, new ListingPayload
            {
                AccessLevel = metadata.Visibility.ToString(),
                VersionHash = Clamp(metadata.VersionHash, MaxHashLength)
            });
            if (request == null)
                return;

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // gone from the directory: registering again carries the new level and build
                _registered = false;
                return;
            }
            if (!response.IsSuccessStatusCode)
                LumoraLogger.Warn($"BackendSessionDirectoryClient: Listing update failed ({(int)response.StatusCode})");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"BackendSessionDirectoryClient: Listing update failed - {ex.Message}");
        }
    }

    private sealed class ListingPayload
    {
        public string AccessLevel { get; set; } = "";
        public string VersionHash { get; set; } = "";
    }

    private async Task UnregisterAsync()
    {
        var metadata = _metadata;
        if (metadata == null)
            return;

        try
        {
            var url = $"{_apiBaseUrl}/sessions/{Uri.EscapeDataString(metadata.SessionId)}";
            using var request = CreateRequest(HttpMethod.Delete, url, null);
            if (request == null)
                return;

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            _registered = false;

            // A 404 here is the outcome we wanted anyway.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                LumoraLogger.Warn($"BackendSessionDirectoryClient: Unregister failed ({(int)response.StatusCode})");
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"BackendSessionDirectoryClient: Unregister failed - {ex.Message}");
        }
    }

    // Build a request with the bearer token attached to THIS message. The token used to be written into
    // HttpClient.DefaultRequestHeaders, which the heartbeat loop and the user-count push both raced on.
    // -xlinka
    private HttpRequestMessage? CreateRequest(HttpMethod method, string url, object? payload)
    {
        var request = new HttpRequestMessage(method, url);
        var token = _tokenProvider.Invoke();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Host-Key", _hostKey);

        if (payload != null)
        {
            var json = JsonSerializer.Serialize(payload, payload.GetType(), _jsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static string NewHostKey()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Everything the directory needs and nothing it does not. Host identity is deliberately absent: the
    // backend takes the host user id and username from our token, so sending them here would only be an
    // invitation to spoof. Fields we do not actually have (region, relay/NAT availability, transport
    // priorities) are absent too rather than being sent as plausible-looking defaults. -xlinka
    private RegistrationPayload BuildRegistration(SessionMetadata metadata)
    {
        var users = NormalizeUsers(_getUserList?.Invoke());
        var maxUsers = Math.Clamp(metadata.MaxUsers, 1, MaxUsersCap);
        return new RegistrationPayload
        {
            SessionId = metadata.SessionId,
            Name = string.IsNullOrWhiteSpace(metadata.Name) ? "Unnamed Session" : Clamp(metadata.Name, MaxNameLength),
            Description = Clamp(metadata.Description, MaxDescriptionLength),
            SessionUrls = BuildSessionUrls(metadata),
            AccessLevel = metadata.Visibility.ToString(),
            HideFromListing = metadata.HideFromListing,
            MaxUsers = maxUsers,
            ActiveUsers = Math.Clamp(Math.Max(metadata.ActiveUsers, users.Length), 0, MaxUsersCap),
            UserList = users,
            UserIds = _getUserIds == null ? null : NormalizeUserIds(_getUserIds.Invoke(), maxUsers),
            Tags = NormalizeTags(metadata.Tags),
            IsHeadless = metadata.IsHeadless,
            AppVersion = NexusRuntime.AppVersion,
            // The host's build identity; browsers list only sessions on their own.
            CompatibilityHash = Clamp(metadata.VersionHash, MaxHashLength),
            ThumbnailUrl = string.IsNullOrWhiteSpace(metadata.ThumbnailUrl) || metadata.ThumbnailUrl.Length > MaxThumbnailUrlLength
                ? null
                : metadata.ThumbnailUrl
        };
    }

    private static string[] BuildSessionUrls(SessionMetadata metadata)
    {
        if (metadata.SessionURLs == null || metadata.SessionURLs.Count == 0)
            return Array.Empty<string>();

        var urls = metadata.SessionURLs
            .Where(uri => uri != null)
            .Select(uri => uri.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (urls.Count <= MaxSessionUrls)
            return urls.ToArray();

        // Over the cap, private network addresses give way first: a host with many network cards would otherwise
        // push out its NAT node, forwarded, IPv6 and Steam addresses, the ones a joiner elsewhere can use.
        int direct = Math.Max(0, MaxSessionUrls - urls.Count(u => !IsPrivateDirect(u)));
        var kept = new List<string>();
        foreach (var url in urls)
        {
            if (!IsPrivateDirect(url) || direct-- > 0)
                kept.Add(url);
        }
        return kept.Take(MaxSessionUrls).ToArray();
    }

    private static bool IsPrivateDirect(string url)
        => url.StartsWith("lnl://", StringComparison.OrdinalIgnoreCase)
           && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && IPAddress.TryParse(uri.DnsSafeHost, out var ip)
           && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
           && !Lumora.Nexus.Transport.PortMapping.RouterAddress.IsPublic(ip);

    private static string[] NormalizeTags(List<string>? tags)
    {
        if (tags == null || tags.Count == 0)
            return Array.Empty<string>();

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => Clamp(tag, MaxTagLength))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags)
            .ToArray();
    }

    private static string Clamp(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
    }

    private static string[] NormalizeUsers(string[]? users)
    {
        if (users == null || users.Length == 0)
            return Array.Empty<string>();

        return users
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Select(user => user.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxUserListEntries)
            .ToArray();
    }

    // The directory refuses a list with anything but account ids in it, and a refused heartbeat repeats every
    // interval, so anything else is dropped here rather than costing the session its listing.
    private static string[] NormalizeUserIds(string[]? ids, int maxUsers)
    {
        if (ids == null || ids.Length == 0)
            return Array.Empty<string>();

        return ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Where(id => id.StartsWith(UserIdPrefix, StringComparison.Ordinal) && id.Length <= MaxUserIdLength)
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Clamp(maxUsers, 1, MaxUsersCap))
            .ToArray();
    }

    public void Dispose()
    {
        // Dispose is the fallback path (Session.Dispose runs on the world thread). StopAsync is the one to
        // await when the caller can. Budget is deliberately tight so a dead backend cannot stall teardown.
        try
        {
            StopAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"BackendSessionDirectoryClient: Shutdown failed - {ex.Message}");
        }

        _cts?.Dispose();
        _cts = null;
        _requestGate.Dispose();
        _httpClient.Dispose();
    }

    private sealed class RegistrationPayload
    {
        public string SessionId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string[] SessionUrls { get; set; } = Array.Empty<string>();
        public string AccessLevel { get; set; } = "Private";
        public bool HideFromListing { get; set; }
        public int MaxUsers { get; set; }
        public int ActiveUsers { get; set; }
        public string[] UserList { get; set; } = Array.Empty<string>();
        public string[]? UserIds { get; set; }
        public string[] Tags { get; set; } = Array.Empty<string>();
        public bool IsHeadless { get; set; }
        public string AppVersion { get; set; } = "";
        public string CompatibilityHash { get; set; } = "";
        public string? ThumbnailUrl { get; set; }
    }

    private sealed class HeartbeatPayload
    {
        public int ActiveUsers { get; set; }
        public string[] UserList { get; set; } = Array.Empty<string>();
        public string[]? UserIds { get; set; }
        public string[] SessionUrls { get; set; } = Array.Empty<string>();
        public string Name { get; set; } = "";
        public string[] Tags { get; set; } = Array.Empty<string>();
    }
}
