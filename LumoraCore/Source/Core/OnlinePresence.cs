// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core;

public enum OnlinePresenceState
{
    // Nothing asked yet.
    Waiting,
    Counting,
    // The services answer but keep no count (an older backend).
    NoCount,
    Unreachable
}

// This client telling the services it is running, once a minute, and the services answering with how many
// clients are running and on what. The id is made fresh for every launch and says nothing about the person or
// the machine; the platform is the only other thing sent. Signed in or not, every running client counts.
//
// The counts come back on the same request, so the dash card reads what the last report brought home. A count
// older than two missed reports is no longer the services' answer and is not shown as one. -xlinka
public static class OnlinePresence
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FirstReport = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(150);
    private static readonly string ClientId = Guid.NewGuid().ToString();

    private sealed record Answer(OnlineCounts Counts, DateTime At);

    private static LumoraClient? _client;
    private static Timer? _timer;
    private static int _busy;
    private static volatile Answer? _latest;

    public static OnlinePresenceState State { get; private set; } = OnlinePresenceState.Waiting;

    // The services' last count, while it is still theirs; null otherwise.
    public static OnlineCounts? Counts
    {
        get
        {
            var latest = _latest;
            return latest != null && DateTime.UtcNow - latest.At < Stale ? latest.Counts : null;
        }
    }

    public static void Attach(LumoraClient client)
    {
        if (_client != null)
            return;
        _client = client;
        _timer = new Timer(_ => _ = ReportAsync(), null, FirstReport, Interval);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _client = null;
    }

    private static async Task ReportAsync()
    {
        var client = _client;
        if (client == null || Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            var response = await client.ReportPresence(ClientId, PlatformName(Engine.CurrentPlatform)).ConfigureAwait(false);
            if (response.Success && response.Data != null)
            {
                _latest = new Answer(response.Data, DateTime.UtcNow);
                State = OnlinePresenceState.Counting;
            }
            else
            {
                State = response.Status == HttpStatusCode.NotFound
                    ? OnlinePresenceState.NoCount
                    : OnlinePresenceState.Unreachable;
            }
        }
        catch
        {
            State = OnlinePresenceState.Unreachable;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // The same names go out with the account's presence.
    internal static string PlatformName(Platform platform) => platform switch
    {
        Platform.Windows => "windows",
        Platform.Linux => "linux",
        Platform.Android => "android",
        _ => "other",
    };
}
