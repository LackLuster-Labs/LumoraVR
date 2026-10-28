// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Cloud;

// Read side of the backend session directory: polls GET /api/sessions while something is watching and
// hands the raw listings to whoever subscribed. The register/heartbeat half lives in
// BackendSessionDirectoryClient.
//
// Deliberately dumb. It does not map, merge, de-dup or cache; that is the browser's job, which already
// owns one upsert path for every discovery source. The list poll stays anonymous, because the list is
// Public sessions only; a friend's session is looked up by id with the caller's token. -xlinka
public sealed class BackendSessionDirectoryQuery : IDisposable
{
    // How often we ask the directory for the list. Hosts heartbeat every 20s, so anything faster than
    // this just re-reads numbers that have not moved.
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly string _apiBaseUrl;
    private readonly Func<string?>? _build;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _refreshSignal = new(0);

    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    // Failure logging is edge-triggered. A backend that is simply not running would otherwise print a line
    // every 15 seconds for the whole session, and the honest answer (no internet sessions) is already
    // visible in the browser. -xlinka
    private bool _reportedFailure;

    // Raised after every completed poll. Empty list on failure.
    public event Action<IReadOnlyList<SessionListingDto>>? OnResults;

    // Default if no poll has ever reached the backend.
    public DateTime LastSuccessUtc { get; private set; }

    // Drives the browser's "offline" wording.
    public bool IsUnreachable { get; private set; }

    public bool IsRunning => _pollTask != null;

    // build gives this game's build identity once it is known; the directory then sends only sessions on it.
    public BackendSessionDirectoryQuery(string apiBaseUrl, Func<string?>? build = null)
    {
        _apiBaseUrl = BackendSessionDirectoryClient.NormalizeBaseUrl(apiBaseUrl);
        _build = build;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    public void Start()
    {
        if (_pollTask != null)
            return;

        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (_pollTask == null)
            return;

        _cts?.Cancel();
        _pollTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void RequestRefresh()
    {
        if (_pollTask == null)
            return;

        // Never let repeated clicks stack up more than one extra pass.
        if (_refreshSignal.CurrentCount == 0)
            _refreshSignal.Release();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        // First pass immediately, so opening the browser does not sit empty for a whole interval.
        await FetchAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _refreshSignal.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            await FetchAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var build = _build?.Invoke();
            using var response = await _httpClient
                .GetAsync(string.IsNullOrEmpty(build) ? $"{_apiBaseUrl}/sessions" : $"{_apiBaseUrl}/sessions?build={Uri.EscapeDataString(build)}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                ReportFailure($"directory returned {(int)response.StatusCode}");
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var listings = JsonSerializer.Deserialize<List<SessionListingDto>>(json, _jsonOptions);

            IsUnreachable = false;
            LastSuccessUtc = DateTime.UtcNow;
            if (_reportedFailure)
            {
                _reportedFailure = false;
                LumoraLogger.Log("BackendSessionDirectoryQuery: Session directory reachable again");
            }

            OnResults?.Invoke(listings ?? new List<SessionListingDto>());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportFailure(ex.Message);
        }
    }

    // One session by id, as the caller may see it. A Friends session never rides the public list and comes
    // back here only to the host's friends, so this one carries the caller's token. Null for not listed,
    // not for them, or no answer. -xlinka
    public async Task<SessionListingDto?> GetSessionAsync(string sessionId, string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiBaseUrl}/sessions/{Uri.EscapeDataString(sessionId)}");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SessionListingDto>(json, _jsonOptions);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LumoraLogger.Log($"BackendSessionDirectoryQuery: Session lookup failed ({ex.Message})");
            return null;
        }
    }

    // An unreachable or unhappy directory means NO internet sessions, never a placeholder row. Callers get
    // an empty result so anything they were showing from the backend ages out of the list.
    private void ReportFailure(string reason)
    {
        IsUnreachable = true;

        if (!_reportedFailure)
        {
            _reportedFailure = true;
            LumoraLogger.Log($"BackendSessionDirectoryQuery: Session directory unavailable ({reason}); listing local sessions only");
        }

        OnResults?.Invoke(Array.Empty<SessionListingDto>());
    }

    public void Dispose()
    {
        Stop();
        _refreshSignal.Dispose();
        _httpClient.Dispose();
    }
}
