// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Management;
using Lumora.Nexus.Cloud;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Social;

// Where this account is, told to the cloud for its friends while it is signed in.
//
// The service forgets a report after two and a half minutes, so one goes every minute whatever happens, and
// another soon after anything in it changes: the focused world, its access, whether it has room, the status
// mode. The report is built on the update, where the world may be read, and sent off it. A change right after
// a send waits for the next look, a few seconds at most, so a session filling and emptying cannot hammer the
// route. -xlinka
public sealed class PresenceReporter : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromSeconds(15);

    private readonly ISocialBackend _backend;
    private readonly Func<PresenceReportBody> _build;
    private readonly Action<Action> _post;
    private readonly object _gate = new();
    private Timer? _timer;
    private int _run;
    private int _looking;
    private bool _sending;
    private bool _quietFailure;
    private PresenceReportBody? _sent;
    private DateTime _sentAt = DateTime.MinValue;
    private DateTime _failedAt = DateTime.MinValue;

    // build runs on the update; post hands an action to it.
    public PresenceReporter(ISocialBackend backend, Func<PresenceReportBody> build, Action<Action> post)
    {
        _backend = backend;
        _build = build;
        _post = post;
    }

    // What the service last took, null before the first report lands.
    public PresenceReportBody? LastSent
    {
        get
        {
            lock (_gate)
                return _sent;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_timer != null)
                return;
            _run++;
            _sent = null;
            _sentAt = DateTime.MinValue;
            _failedAt = DateTime.MinValue;
            _timer = new Timer(_ => Look(), null, TimeSpan.Zero, LookEvery);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _run++;
            _sent = null;
        }
    }

    // Something the report reads changed: look now rather than at the next tick.
    public void Poke() => Look();

    // The service dropped what we last sent (the live link went and the server cleared us when it did): send
    // the next report whether or not it changed.
    public void Resend()
    {
        lock (_gate)
            _sent = null;
        Look();
    }

    public void Dispose() => Stop();

    private void Look()
    {
        int run;
        lock (_gate)
        {
            if (_timer == null)
                return;
            run = _run;
        }
        if (Interlocked.Exchange(ref _looking, 1) == 1)
            return;
        _post(() =>
        {
            Interlocked.Exchange(ref _looking, 0);
            PresenceReportBody body;
            try
            {
                body = _build();
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"PresenceReporter: could not read where we are: {ex.Message}");
                return;
            }
            Decide(body, run);
        });
    }

    private void Decide(PresenceReportBody body, int run)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (run != _run || _sending)
                return;
            bool changed = _sent == null || !_sent.Equals(body);
            if (!changed && now - _sentAt < Every)
                return;
            if (now - _sentAt < Floor || now - _failedAt < AfterFailure)
                return;
            _sending = true;
        }
        _ = SendAsync(body, run);
    }

    private async Task SendAsync(PresenceReportBody body, int run)
    {
        ApiResponse answer;
        try
        {
            answer = await _backend.ReportPresenceAsync(body).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            answer = ApiResponse.FromException(ex);
        }

        bool warn;
        lock (_gate)
        {
            _sending = false;
            if (run != _run)
                return;
            if (answer.Success)
            {
                _sent = body;
                _sentAt = DateTime.UtcNow;
                _quietFailure = false;
                return;
            }
            _failedAt = DateTime.UtcNow;
            warn = !_quietFailure;
            _quietFailure = true;
        }
        // Once until it works again: a service that is down is down for every report.
        if (warn)
            LumoraLogger.Warn($"PresenceReporter: could not report where we are: {answer.Message ?? answer.Status.ToString()}");
    }

    // The report for one world: the focused one, or null in none. Only a session a friend could find (Anyone
    // or Friends, as the directory names them Public or Contacts) carries its id and name at all; the service
    // hides the rest anyway, and what is never sent cannot leak. Joinable also asks that the door is open,
    // there is room, and a friend who is not in the group would get past a members-only group world.
    // The userspace is never a place. The home is a hosted session like any other, private unless its access
    // was opened.
    public static PresenceReportBody Build(World? world, string? mode, string platform)
    {
        if (world == null || world.IsDestroyed || world.IsDisposed || world.IsUserspace)
        {
            return new PresenceReportBody
            {
                Mode = mode,
                AccessLevel = SessionVisibility.Private.ToString(),
                Platform = platform,
            };
        }

        var settings = world.RootSlot?.GetComponent<WorldSettings>();
        var level = settings?.AccessLevel.Value ?? World.WorldAccessLevel.Private;
        bool forGroup = !string.IsNullOrEmpty(settings?.HostGroupId.Value);
        var visibility = WorldSettings.ToVisibility(level, forGroup);
        bool findable = visibility == SessionVisibility.Public || visibility == SessionVisibility.Contacts;

        var session = world.Session;
        string? sessionId = session != null && !session.IsDisposed ? session.SessionId : null;
        if (sessionId != null && !SessionIdentifier.IsValid(sessionId))
            sessionId = null;

        bool membersOnly = forGroup && level == World.WorldAccessLevel.GroupMembers;
        bool joinable = findable && sessionId != null && settings != null && !membersOnly
            && settings.AllowJoin.Value && world.UserCount < System.Math.Max(1, settings.MaxUsers.Value);

        return new PresenceReportBody
        {
            Mode = mode,
            SessionId = findable ? sessionId : null,
            WorldName = findable ? DisplayName(world) : null,
            AccessLevel = visibility.ToString(),
            Joinable = joinable,
            Platform = platform,
        };
    }

    private static string DisplayName(World world)
    {
        if (WorldManager.IsLocalHome(world))
            return "Local";
        var name = world.WorldName?.Value;
        return string.IsNullOrWhiteSpace(name) ? world.Name : name.Trim();
    }
}
