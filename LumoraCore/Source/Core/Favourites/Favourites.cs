// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;

namespace Lumora.Core;

// One favourite per kind, each an item of the inventory, kept in the settings store as
// Favourites.<account>.<Kind> and following the account like any other account setting. The account is in
// the key so that whoever signs in on this machine next neither sees nor sends someone else's. Nothing
// here works signed out: an item id means nothing without the inventory it is in. -xlinka
public static class Favourites
{
    public const string Prefix = "Favourites.";

    private static readonly object Gate = new();
    private static readonly Dictionary<FavouriteKind, FavouriteItem?> Cache = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static volatile string? _account;
    private static LumoraClient? _client;
    private static int _version;

    // A favourite changed: set, cleared, brought in from the account, or the account changed under it. Any thread.
    public static event Action<FavouriteKind>? Changed;

    // The settings sync has had its go after a sign in, so the account's favourites are as known as they get.
    // Any thread.
    public static event Action? Ready;

    // Moves whenever any favourite may read differently, for screens that poll.
    public static int Version => System.Threading.Volatile.Read(ref _version);

    public static string? Account => _account;
    public static bool SignedIn => _account != null && _client?.IsAuthenticated == true;

    public static void Attach(LumoraClient client)
    {
        if (_client != null)
            return;
        _client = client;
        Settings.Changed += OnSettingChanged;
        client.SignedOut += () => SetAccount(null);
        AccountSettings.Pulled += () => Ready?.Invoke();
    }

    // The settings sync asks before it pulls, so the account's own keys are known as its own when they land.
    internal static async Task KnowAccountAsync(LumoraClient client)
    {
        string? id = client.AccountUserId;
        if (string.IsNullOrEmpty(id))
        {
            var resolved = await client.ResolveAccountUserId().ConfigureAwait(false);
            id = resolved.Success ? resolved.Data : null;
        }
        if (!string.IsNullOrEmpty(id) && client.IsAuthenticated)
            SetAccount(id);
    }

    private static void SetAccount(string? account)
    {
        if (string.Equals(account, _account, StringComparison.Ordinal))
            return;
        lock (Gate)
        {
            _account = account;
            Cache.Clear();
        }
        System.Threading.Interlocked.Increment(ref _version);
        foreach (var kind in FavouriteKinds.All)
            Changed?.Invoke(kind);
    }

    // Only the signed in account's favourites travel; another account's stay on this machine, unsent.
    public static bool Follows(string key)
    {
        var account = _account;
        return account != null
            && key.Length > Prefix.Length + account.Length + 1
            && key.StartsWith(Prefix, StringComparison.Ordinal)
            && string.CompareOrdinal(key, Prefix.Length, account, 0, account.Length) == 0
            && key[Prefix.Length + account.Length] == '.';
    }

    private static string KeyOf(string account, FavouriteKind kind) => Prefix + account + "." + kind;

    public static FavouriteItem? Get(FavouriteKind kind)
    {
        var account = _account;
        if (account == null)
            return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(kind, out var known))
                return known;
        }
        var item = Parse(Settings.ReadValue(KeyOf(account, kind), string.Empty));
        lock (Gate)
        {
            if (string.Equals(account, _account, StringComparison.Ordinal))
                Cache[kind] = item;
        }
        return item;
    }

    public static bool Has(FavouriteKind kind) => Get(kind) != null;

    // The kinds this row's item is the favourite of, usually none. A share or a link counts as what it leads to.
    public static List<FavouriteKind> KindsOf(in InventoryEntry entry)
    {
        var kinds = new List<FavouriteKind>();
        if (entry.IsFolder || _account == null)
            return kinds;
        foreach (var kind in FavouriteKinds.All)
        {
            if (Get(kind) is { } item && item.Is(entry))
                kinds.Add(kind);
        }
        return kinds;
    }

    public static bool IsFavourite(FavouriteKind kind, in InventoryEntry entry) => Get(kind) is { } item && item.Is(entry);

    public static bool Set(FavouriteKind kind, in InventoryEntry entry)
    {
        var account = _account;
        if (account == null || !SignedIn || string.IsNullOrEmpty(entry.Id))
            return false;
        Settings.WriteValue(KeyOf(account, kind), Format(FavouriteItem.From(entry)));
        return true;
    }

    public static void Clear(FavouriteKind kind)
    {
        var account = _account;
        if (account != null)
            Settings.DeleteValue(KeyOf(account, kind));
    }

    // Where the item turned out to be the last time it was looked for. Written only when that moved, so a
    // spawn does not stamp the setting every time.
    internal static void Seen(FavouriteKind kind, in InventoryEntry entry)
    {
        var account = _account;
        if (account == null || Get(kind) is not { } stored || !stored.Is(entry))
            return;
        var now = FavouriteItem.From(entry);
        if (now == stored with { ItemKind = now.ItemKind })
            return;
        Settings.WriteValue(KeyOf(account, kind), Format(now with { ItemKind = string.IsNullOrEmpty(now.ItemKind) ? stored.ItemKind : now.ItemKind }));
    }

    private static void OnSettingChanged(string key, bool fromAccount)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal))
            return;
        int dot = key.LastIndexOf('.');
        if (dot < Prefix.Length || !Enum.TryParse(key.Substring(dot + 1), out FavouriteKind kind))
            return;
        lock (Gate)
            Cache.Remove(kind);
        System.Threading.Interlocked.Increment(ref _version);
        Changed?.Invoke(kind);
    }

    private sealed class Stored
    {
        public string? Id { get; set; }
        public string? Hash { get; set; }
        public string? Name { get; set; }
        public string? Folder { get; set; }
        public bool World { get; set; }
        public string? Kind { get; set; }
        public string? Owner { get; set; }
        public string? Via { get; set; }
        public string? ViaFolder { get; set; }
    }

    private static string Format(in FavouriteItem item)
        => JsonSerializer.Serialize(new Stored
        {
            Id = item.Id,
            Hash = item.Hash,
            Name = item.Name,
            Folder = item.Folder,
            World = item.IsWorld,
            Kind = item.ItemKind,
            Owner = item.Owner,
            Via = item.Via,
            ViaFolder = item.ViaFolder,
        }, Json);

    private static FavouriteItem? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(text, Json);
            if (stored == null || string.IsNullOrEmpty(stored.Id))
                return null;
            static string? Text(string? value) => string.IsNullOrEmpty(value) ? null : value;
            string? via = Text(stored.Via);
            return new FavouriteItem(stored.Id, stored.Hash ?? string.Empty, stored.Name ?? string.Empty,
                Text(stored.Folder) ?? (via == null ? Inventory.Root : null), stored.World, stored.Kind ?? string.Empty,
                Text(stored.Owner), via, Text(stored.ViaFolder));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
