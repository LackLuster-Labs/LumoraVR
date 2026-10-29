// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

// The settings that follow the account rather than the machine, kept on the server against the account
// and brought to every computer it signs in on.
//
// Each key is decided on its own and the newer change wins, so a site allowed at home and another one
// allowed at work both survive, and a removal travels like any change. On sign in the account's copy is
// pulled and merged, then whatever this machine has that is newer is pushed back. A change made here is
// pushed a moment later, and once more on the way out so the last one is not lost to a closing window.
//
// What follows the account is declared in one place: the preferences EngineSettings lists, every site
// decision of WebAccess, the signed in account's favourites and their model import answers. Everything else
// in the settings store stays on this machine, the saved sign-in above all. -xlinka
public static class AccountSettings
{
    public const string WebAccessSites = "WebAccess.Sites.";

    private static readonly TimeSpan PushDelay = TimeSpan.FromSeconds(2);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, long> ServerChanged = new(StringComparer.Ordinal);
    private static LumoraClient? _client;
    private static Timer? _pushTimer;
    private static bool _quietFailure;

    // A sync after a sign in has had its go, whether or not it reached the account. Off the main thread.
    public static event Action? Pulled;

    public static bool Follows(string key)
        => key.StartsWith(WebAccessSites, StringComparison.Ordinal) || EngineSettings.FollowsAccount(key) || Favourites.Follows(key)
           || Components.Import.ModelImportPreferences.Follows(key);

    public static void Attach(LumoraClient client)
    {
        if (_client != null)
            return;
        _client = client;
        client.Authenticated += _ => Start(SyncAsync);
        client.SignedOut += () =>
        {
            lock (ServerChanged)
                ServerChanged.Clear();
        };
        Settings.Changed += OnSettingChanged;
        if (client.IsAuthenticated)
            Start(SyncAsync);
    }

    // On the way out: whatever is still waiting goes now, for as long as the exit can spare.
    public static void Flush(TimeSpan timeout)
    {
        if (_client?.IsAuthenticated != true)
            return;
        _pushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            Task.Run(PushAsync).Wait(timeout);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"AccountSettings: last push failed: {ex.Message}");
        }
    }

    private static void OnSettingChanged(string key, bool fromAccount)
    {
        if (fromAccount || !Follows(key) || _client?.IsAuthenticated != true)
            return;
        (_pushTimer ??= new Timer(_ => Start(PushAsync))).Change(PushDelay, Timeout.InfiniteTimeSpan);
    }

    // Off whatever thread asked, and off the game's own synchronisation context, so nothing here ever
    // waits on the main thread.
    private static void Start(Func<Task> work) => Task.Run(work);

    // Pull everything, take what is newer, push what is newer here.
    private static async Task SyncAsync()
    {
        var client = _client;
        if (client?.IsAuthenticated != true)
            return;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Favourite keys carry the account, so it has to be known before they land or they are refused.
            await Favourites.KnowAccountAsync(client).ConfigureAwait(false);
            var pulled = await client.GetAccountSettings().ConfigureAwait(false);
            if (!pulled.Success || pulled.Data == null)
            {
                Failed(pulled.Message);
                return;
            }
            bool engine = Take(pulled.Data.Entries);
            engine |= await PushNewerAsync(client).ConfigureAwait(false);
            _quietFailure = false;
            if (engine)
                ReloadEngineSettings();
        }
        finally
        {
            Gate.Release();
            Pulled?.Invoke();
        }
    }

    private static async Task PushAsync()
    {
        var client = _client;
        if (client?.IsAuthenticated != true)
            return;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (await PushNewerAsync(client).ConfigureAwait(false))
                ReloadEngineSettings();
        }
        finally
        {
            Gate.Release();
        }
    }

    // Sends the keys this machine changed after the account last heard of them. The answer is the
    // account's whole copy after the merge, which can hold newer changes from other computers, and those
    // are taken. True when that touched an engine setting.
    private static async Task<bool> PushNewerAsync(LumoraClient client)
    {
        var outgoing = new List<AccountSettingEntry>();
        lock (ServerChanged)
        {
            foreach (var entry in Settings.AccountEntries())
            {
                if (ServerChanged.TryGetValue(entry.Key, out long known) && known >= entry.Changed)
                    continue;
                outgoing.Add(new AccountSettingEntry
                {
                    Key = entry.Key,
                    Value = entry.Value,
                    Changed = entry.Changed,
                    Removed = entry.Value == null,
                });
            }
        }
        if (outgoing.Count == 0)
            return false;
        var merged = await client.MergeAccountSettings(outgoing).ConfigureAwait(false);
        if (!merged.Success || merged.Data == null)
        {
            Failed(merged.Message);
            return false;
        }
        _quietFailure = false;
        return Take(merged.Data.Entries);
    }

    private static bool Take(List<AccountSettingEntry> entries)
    {
        bool engine = false;
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Key))
                continue;
            lock (ServerChanged)
                ServerChanged[entry.Key] = entry.Changed;
            if (Settings.ApplyAccountEntry(new Settings.AccountEntry(entry.Key, entry.Removed ? null : entry.Value ?? "", entry.Changed))
                && EngineSettings.FollowsAccount(entry.Key))
                engine = true;
        }
        return engine;
    }

    // EngineSettings holds its values in memory and its listeners reach into the windowing and the
    // renderer, so the re-read happens on the main thread.
    private static void ReloadEngineSettings()
    {
        var world = Engine.Current?.WorldManager?.UserspaceWorld;
        if (world != null)
            world.RunSynchronously(EngineSettings.Reload);
        else
            EngineSettings.Reload();
    }

    // Once until it works again: a server that is down is down for every push.
    private static void Failed(string? reason)
    {
        if (_quietFailure)
            return;
        _quietFailure = true;
        LumoraLogger.Warn($"AccountSettings: could not reach the account's settings: {reason ?? "no reason given"}");
    }
}
