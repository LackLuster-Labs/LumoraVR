// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids;

// Makes the stick mode follow the account, the one fluid setting that does.
//
// The core's account sync follows a fixed list of keys and steps round every other one, both ways, so this key is
// carried here instead, with the same rule: the value lives in the settings store, its change time sits in a key of
// its own next to it, and the newer side wins. On sign in the account's copy is pulled and whichever is newer is
// kept; a change made here goes up a couple of seconds later, a removal as a tombstone. Nothing happens on sign out.
// It costs one request per sign in and one per change.
//
// It attaches itself the first time anything asks for the mode, because the module boots before the engine has a
// cloud client: anyone reading StickMode in a world with fluid has started it, and a sign in already made by then is
// synced at once. -xlinka
public static class FluidAccountSync
{
    private static readonly TimeSpan PushDelay = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object _lock = new();

    private static LumoraClient? _client;
    private static Timer? _pushTimer;
    private static bool _quietFailure;
    private static int _version;
    // A change made here that the account has not taken yet.
    private static volatile bool _waiting;
    private static int _pulls;
    private static int _pushes;

    // Our own writes into the store, which must not read as a new local change.
    [ThreadStatic]
    private static bool _writing;

    // The mode changed, here or from the account. Raised on whatever thread did it, which for the account is a worker:
    // read StickMode again on your own thread, or watch Version.
    public static event Action? Changed;

    // Moves on every change, for readers that poll from the world thread.
    public static int Version => Volatile.Read(ref _version);

    // Requests made to the account, this session.
    public static int Pulls => Volatile.Read(ref _pulls);

    public static int Pushes => Volatile.Read(ref _pushes);

    public static bool Attached
    {
        get
        {
            lock (_lock)
                return _client != null;
        }
    }

    public static FluidStickMode StickMode
    {
        get
        {
            EnsureAttached();
            return ReadMode();
        }
    }

    // Unix milliseconds of the last change this machine knows of; 0 when it never had one.
    public static long ChangedAt => ReadStamp();

    public static void SetStickMode(FluidStickMode mode)
    {
        EnsureAttached();
        if (!Enum.IsDefined(mode))
            mode = FluidStickMode.Sticky;
        bool changed;
        _writing = true;
        try
        {
            changed = !Settings.HasValue(FluidSettingKeys.StickMode) || ReadMode() != mode;
            if (changed)
            {
                Settings.WriteValue(FluidSettingKeys.StickModeChanged, Now().ToString(CultureInfo.InvariantCulture));
                Settings.WriteValue(FluidSettingKeys.StickMode, ((int)mode).ToString(CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            _writing = false;
        }
        if (changed)
            LocalChange();
    }

    public static bool EnsureAttached() => Attach(Engine.Current?.CDNClient);

    public static bool Attach(LumoraClient? client)
    {
        if (client == null)
            return false;
        lock (_lock)
        {
            if (_client != null)
                return ReferenceEquals(_client, client);
            _client = client;
        }
        client.Authenticated += _ => Start(SyncAsync);
        Settings.Changed += OnSettingChanged;
        if (client.IsAuthenticated)
            Start(SyncAsync);
        return true;
    }

    // Sends a waiting change now, for as long as the caller can spare.
    public static void Flush(TimeSpan timeout)
    {
        var client = Client;
        if (client?.IsAuthenticated != true || !_waiting)
            return;
        _pushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            Task.Run(PushAsync).Wait(timeout);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Fluids: last stick mode push failed: {ex.Message}");
        }
    }

    private static LumoraClient? Client
    {
        get
        {
            lock (_lock)
                return _client;
        }
    }

    private static FluidStickMode ReadMode()
    {
        int raw = Settings.ReadValue(FluidSettingKeys.StickMode, (int)FluidStickMode.Sticky);
        return Enum.IsDefined(typeof(FluidStickMode), raw) ? (FluidStickMode)raw : FluidStickMode.Sticky;
    }

    private static long ReadStamp()
    {
        var raw = Settings.ReadValue(FluidSettingKeys.StickModeChanged, "");
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long at) ? at : 0;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // The Self ring or anything else may write the key straight into the store; that is a change made now all the same.
    private static void OnSettingChanged(string key, bool fromAccount)
    {
        if (_writing || key != FluidSettingKeys.StickMode)
            return;
        _writing = true;
        try
        {
            Settings.WriteValue(FluidSettingKeys.StickModeChanged, Now().ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            _writing = false;
        }
        LocalChange();
    }

    private static void LocalChange()
    {
        _waiting = true;
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
        if (Client?.IsAuthenticated != true)
            return;
        lock (_lock)
            (_pushTimer ??= new Timer(_ => Start(PushAsync))).Change(PushDelay, Timeout.InfiniteTimeSpan);
    }

    private static void Start(Func<Task> work) => Task.Run(work);

    private static async Task SyncAsync()
    {
        var client = Client;
        if (client?.IsAuthenticated != true)
            return;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _pulls);
            var pulled = await client.GetAccountSettings().ConfigureAwait(false);
            if (!pulled.Success || pulled.Data == null)
            {
                Failed(pulled.Message);
                return;
            }
            _quietFailure = false;
            var remote = Find(pulled.Data.Entries);
            long local = ReadStamp();
            if (remote != null && remote.Changed > local)
                Take(remote);
            else if (local > 0 && (remote == null || local > remote.Changed))
                await PushLockedAsync(client).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task PushAsync()
    {
        var client = Client;
        if (client?.IsAuthenticated != true || !_waiting)
            return;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await PushLockedAsync(client).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    // The answer is the account's whole copy after the merge; another computer's newer change can be in it.
    private static async Task PushLockedAsync(LumoraClient client)
    {
        long stamp = ReadStamp();
        if (stamp <= 0)
            return;
        bool present = Settings.HasValue(FluidSettingKeys.StickMode);
        var entry = new AccountSettingEntry
        {
            Key = FluidSettingKeys.StickMode,
            Value = present ? ((int)ReadMode()).ToString(CultureInfo.InvariantCulture) : null,
            Changed = stamp,
            Removed = !present,
        };
        Interlocked.Increment(ref _pushes);
        var merged = await client.MergeAccountSettings(new[] { entry }).ConfigureAwait(false);
        if (!merged.Success || merged.Data == null)
        {
            Failed(merged.Message);
            return;
        }
        _quietFailure = false;
        if (stamp == ReadStamp())
            _waiting = false;
        if (Find(merged.Data.Entries) is { } remote && remote.Changed > ReadStamp())
            Take(remote);
    }

    private static AccountSettingEntry? Find(List<AccountSettingEntry>? entries)
    {
        if (entries == null)
            return null;
        foreach (var entry in entries)
        {
            if (entry.Key == FluidSettingKeys.StickMode)
                return entry;
        }
        return null;
    }

    private static void Take(AccountSettingEntry remote)
    {
        _writing = true;
        try
        {
            if (remote.Removed || remote.Value == null)
            {
                Settings.DeleteValue(FluidSettingKeys.StickMode);
            }
            else if (int.TryParse(remote.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw)
                     && Enum.IsDefined(typeof(FluidStickMode), raw))
            {
                Settings.WriteValue(FluidSettingKeys.StickMode, raw.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                return;
            }
            Settings.WriteValue(FluidSettingKeys.StickModeChanged, remote.Changed.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            _writing = false;
        }
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    // Once until it works again: a server that is down is down for every push.
    private static void Failed(string? reason)
    {
        if (_quietFailure)
            return;
        _quietFailure = true;
        LumoraLogger.Warn($"Fluids: could not reach the account's settings for the stick mode: {reason ?? "no reason given"}");
    }
}
