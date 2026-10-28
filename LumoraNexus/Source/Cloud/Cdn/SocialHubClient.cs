// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using LumoraLogger = Lumora.Nexus.Diagnostics.NexusLog;

namespace Lumora.Nexus.Cloud.Cdn;

public enum SocialHubState
{
    Stopped,
    Connecting,
    Connected,
    Reconnecting,
}

// The cloud's live social feed: requests, friends coming and going, presence, profile edits and chat, pushed
// as they happen. Connected while an account is signed in, dropped on sign out.
//
// The service keeps nothing for a game that is not connected and replays nothing after a drop, so Connected
// fires on the first connect and again after every reconnect: whoever keeps lists fetches them again then.
// A connection that never comes up, or comes back refused, keeps trying on the same ladder, a minute apart
// at most, until it works or the account signs out. Every event is raised on the connection's own threads.
// -xlinka
public sealed class SocialHubClient : IDisposable
{
    public const string HubPath = "/hubs/social";

    private static readonly TimeSpan[] Ladder =
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    };

    private readonly Func<string?> _token;
    private readonly Func<string> _url;
    private readonly object _gate = new();
    private HubConnection? _connection;
    private CancellationTokenSource? _run;
    private LumoraClient? _client;
    private volatile SocialHubState _state = SocialHubState.Stopped;
    // the service turned a typing ping away as a call it does not have; asked again after the next connect
    private volatile bool _typingRefused;

    public SocialHubState State => _state;

    public event Action? Connected;
    // the connection dropped; it is already trying again
    public event Action? Disconnected;

    public event Action<FriendRequestInfo>? FriendRequestReceived;
    public event Action<string>? FriendRequestRemoved;
    public event Action<FriendInfo>? FriendAdded;
    public event Action<string>? FriendRemoved;
    public event Action<string, PresenceInfo>? PresenceChanged;
    public event Action<string>? ProfileChanged;
    public event Action<ChatMessageInfo>? MessageReceived;
    // byUserId read every message up to upTo
    public event Action<string, DateTime>? MessagesRead;
    // this account read its chat with withUserId up to upTo, here or on another machine
    public event Action<string, DateTime>? ChatRead;
    // fromUserId is typing to this account; one per ping, nothing when they stop
    public event Action<string>? Typing;
    // a message left one of this account's chats: hidden on one of its machines, or taken back by its sender
    public event Action<ChatMessageDeletedInfo>? MessageDeleted;

    // The token is asked for at every connect, so a renewed one is picked up by the next reconnect.
    public SocialHubClient(Func<string?> token, Func<string>? url = null)
    {
        _token = token;
        _url = url ?? (() => ServiceConfig.Current.ApiBase.TrimEnd('/') + HubPath);
    }

    // Follows the client's sign in. A second sign in starts over, since the connection belongs to the token it
    // was opened with.
    public static SocialHubClient For(LumoraClient client)
    {
        var hub = new SocialHubClient(() => client.CurrentSession?.Token);
        hub.Attach(client);
        return hub;
    }

    public void Attach(LumoraClient client)
    {
        if (_client != null)
            return;
        _client = client;
        client.Authenticated += OnAuthenticated;
        client.SignedOut += Stop;
        if (client.IsAuthenticated)
            Start();
    }

    private void OnAuthenticated(Session session)
    {
        Stop();
        Start();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_run != null)
                return;
            _run = new CancellationTokenSource();
            _connection = Build();
            _state = SocialHubState.Connecting;
            var connection = _connection;
            var token = _run.Token;
            _ = Task.Run(() => KeepConnectedAsync(connection, token));
        }
    }

    public void Stop()
    {
        HubConnection? connection;
        CancellationTokenSource? run;
        lock (_gate)
        {
            connection = _connection;
            run = _run;
            _connection = null;
            _run = null;
        }
        if (run == null)
            return;

        bool wasConnected = _state == SocialHubState.Connected;
        _state = SocialHubState.Stopped;
        run.Cancel();
        // Disposing waits on the socket closing, which is no reason to hold up a sign out.
        _ = Task.Run(async () =>
        {
            try
            {
                if (connection != null)
                    await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LumoraLogger.Debug($"SocialHubClient: closing the connection failed: {ex.Message}");
            }
            finally
            {
                run.Dispose();
            }
        });
        if (wasConnected)
            Raise(Disconnected);
    }

    public void Dispose()
    {
        var client = _client;
        _client = null;
        if (client != null)
        {
            client.Authenticated -= OnAuthenticated;
            client.SignedOut -= Stop;
        }
        Stop();
    }

    private HubConnection Build()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(_url(), options => options.AccessTokenProvider = () => Task.FromResult(_token()))
            .WithAutomaticReconnect(new Retry())
            // The service may name the payload fields either way; the DTOs read both.
            .AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true)
            .Build();

        connection.On<FriendRequestInfo>(SocialHubEvents.FriendRequestReceived, request => Raise(FriendRequestReceived, request));
        connection.On<string>(SocialHubEvents.FriendRequestRemoved, id => Raise(FriendRequestRemoved, id));
        connection.On<FriendInfo>(SocialHubEvents.FriendAdded, friend => Raise(FriendAdded, friend));
        connection.On<string>(SocialHubEvents.FriendRemoved, id => Raise(FriendRemoved, id));
        connection.On<string, PresenceInfo>(SocialHubEvents.PresenceChanged, (id, presence) => Raise(PresenceChanged, id, presence));
        connection.On<string>(SocialHubEvents.ProfileChanged, id => Raise(ProfileChanged, id));
        connection.On<ChatMessageInfo>(SocialHubEvents.MessageReceived, message => Raise(MessageReceived, message));
        connection.On<string, DateTime>(SocialHubEvents.MessagesRead, (id, upTo) => Raise(MessagesRead, id, upTo));
        connection.On<string, DateTime>(SocialHubEvents.ChatRead, (id, upTo) => Raise(ChatRead, id, upTo));
        connection.On<string>(SocialHubEvents.Typing, id => Raise(Typing, id));
        connection.On<ChatMessageDeletedInfo>(SocialHubEvents.MessageDeleted, deleted => Raise(MessageDeleted, deleted));

        connection.Reconnecting += _ =>
        {
            if (IsCurrent(connection))
            {
                _state = SocialHubState.Reconnecting;
                Raise(Disconnected);
            }
            return Task.CompletedTask;
        };
        connection.Reconnected += _ =>
        {
            if (IsCurrent(connection))
            {
                _state = SocialHubState.Connected;
                _typingRefused = false;
                Raise(Connected);
            }
            return Task.CompletedTask;
        };
        // The automatic reconnect never gives up, so this is the service closing on us for good or a reconnect
        // it refused outright. Start over from the first rung while the account is still signed in.
        connection.Closed += error =>
        {
            CancellationToken token;
            lock (_gate)
            {
                if (!ReferenceEquals(_connection, connection) || _run == null)
                    return Task.CompletedTask;
                token = _run.Token;
            }
            bool wasConnected = _state == SocialHubState.Connected;
            _state = SocialHubState.Reconnecting;
            if (error != null)
                LumoraLogger.Warn($"SocialHubClient: the social feed closed: {error.Message}");
            if (wasConnected)
                Raise(Disconnected);
            _ = Task.Run(() => KeepConnectedAsync(connection, token, Ladder[1]));
            return Task.CompletedTask;
        };
        return connection;
    }

    // Tells withUserId this account is typing to them. False when nothing went: no live connection, or a
    // service from before typing, which answers the call as one it does not have. That is taken as the
    // answer until the next connect, so an old service hears one ping per connection and no more. -xlinka
    public async Task<bool> SendTypingAsync(string withUserId)
    {
        HubConnection? connection;
        lock (_gate)
            connection = _connection;
        if (connection == null || _state != SocialHubState.Connected || _typingRefused || string.IsNullOrEmpty(withUserId))
            return false;
        try
        {
            await connection.InvokeAsync(SocialHubMethods.Typing, withUserId).ConfigureAwait(false);
            return true;
        }
        catch (HubException ex)
        {
            _typingRefused = true;
            LumoraLogger.Debug($"SocialHubClient: the social feed takes no typing pings ({ex.Message})");
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsCurrent(HubConnection connection)
    {
        lock (_gate)
            return ReferenceEquals(_connection, connection);
    }

    private async Task KeepConnectedAsync(HubConnection connection, CancellationToken token, TimeSpan firstWait = default)
    {
        int attempt = 0;
        var wait = firstWait;
        while (!token.IsCancellationRequested)
        {
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            string failure = "offline";
            if (Connectivity.IsOnline)
            {
                try
                {
                    await connection.StartAsync(token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || !IsCurrent(connection))
                        return;
                    _state = SocialHubState.Connected;
                    _typingRefused = false;
                    if (attempt > 0)
                        LumoraLogger.Log("SocialHubClient: connected to the social feed");
                    Raise(Connected);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }
            }

            // Once, then every tenth try: a service that is down is down for every attempt.
            if (attempt % 10 == 0)
                LumoraLogger.Warn($"SocialHubClient: could not reach the social feed ({failure}), trying again");
            wait = Delay(attempt);
            attempt++;
        }
    }

    private static TimeSpan Delay(int attempt)
    {
        var rung = Ladder[Math.Min(attempt + 1, Ladder.Length - 1)];
        // A little spread so every game a restart threw off does not knock at the same instant.
        return rung + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
    }

    private sealed class Retry : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context)
        {
            var rung = Ladder[(int)Math.Min(context.PreviousRetryCount, Ladder.Length - 1)];
            return rung + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
        }
    }

    private static void Raise(Action? handler)
    {
        try { handler?.Invoke(); }
        catch (Exception ex) { LumoraLogger.Warn($"SocialHubClient: a listener failed: {ex.Message}"); }
    }

    private static void Raise<T>(Action<T>? handler, T value)
    {
        try { handler?.Invoke(value); }
        catch (Exception ex) { LumoraLogger.Warn($"SocialHubClient: a listener failed: {ex.Message}"); }
    }

    private static void Raise<T1, T2>(Action<T1, T2>? handler, T1 first, T2 second)
    {
        try { handler?.Invoke(first, second); }
        catch (Exception ex) { LumoraLogger.Warn($"SocialHubClient: a listener failed: {ex.Message}"); }
    }
}
