// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Diagnostics;

namespace Lumora.Nexus.Cloud.Cdn;

// Who owns what, as far as Avatar Protection goes, remembered for a while: an account's public keys for an hour,
// its name for an hour. A room full of sealed avatars checks every seal on every machine, and asking the service
// once per seal per peer would be the whole room hammering one route on join. Safe from any thread.
//
// A key id the remembered set lacks only counts as missing when that set was fetched after the seal was first
// seen here. An account whose key was replaced signs with an id a list fetched earlier never held, and a seal
// read as broken gets the object taken out of the world, so an older list is asked for again first. -xlinka
public sealed class ProtectionDirectory
{
    public static readonly TimeSpan KeyLife = TimeSpan.FromHours(1);
    public static readonly TimeSpan NameLife = TimeSpan.FromHours(1);
    // How long an "unknown account" answer is trusted before asking again.
    public static readonly TimeSpan UnknownLife = TimeSpan.FromMinutes(1);

    public enum KeyStatus
    {
        Found,
        // The service answered and there is no such account.
        Unknown,
        // No answer worth trusting: offline, timed out, a broken server.
        Unreachable,
    }

    public enum Verdict
    {
        Verified,
        Broken,
        Unreachable,
    }

    // FetchedAt is when the ask behind it was sent.
    public readonly record struct KeyAnswer(KeyStatus Status, ProtectionKeySet? Keys, DateTime FetchedAt);

    private sealed class KeyEntry
    {
        public ProtectionKeySet? Keys;
        public KeyStatus Status;
        public DateTime FetchedAt;
    }

    private readonly IProtectionRoutes _routes;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, KeyEntry> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<KeyAnswer>> _keyFetches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, DateTime At)> _names = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nameFetches = new(StringComparer.Ordinal);

    public ProtectionDirectory(IProtectionRoutes routes, Func<DateTime>? clock = null)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public IProtectionRoutes Routes => _routes;

    // The clock every time here is on; a seenSince handed in has to be read off it.
    public DateTime Now => _clock();

    public void Forget()
    {
        lock (_gate)
        {
            _keys.Clear();
            _names.Clear();
        }
    }

    // KEYS

    // seenSince: a remembered set that lacks mustHaveKeyId only answers when it was fetched at or after this time.
    public Task<KeyAnswer> KeysAsync(string ownerId, string? mustHaveKeyId = null, DateTime? seenSince = null)
    {
        if (string.IsNullOrEmpty(ownerId))
            return Task.FromResult(new KeyAnswer(KeyStatus.Unknown, null, _clock()));
        var now = _clock();
        lock (_gate)
        {
            if (_keys.TryGetValue(ownerId, out var entry))
            {
                bool fresh = now - entry.FetchedAt < (entry.Status == KeyStatus.Found ? KeyLife : UnknownLife);
                bool lacks = mustHaveKeyId != null && !Holds(entry.Keys, mustHaveKeyId);
                bool settled = !lacks || (seenSince is { } seen && entry.FetchedAt >= seen);
                if (fresh && settled)
                    return Task.FromResult(new KeyAnswer(entry.Status, entry.Keys, entry.FetchedAt));
            }
            if (_keyFetches.TryGetValue(ownerId, out var running))
                return running;
            var fetch = FetchKeysAsync(ownerId);
            _keyFetches[ownerId] = fetch;
            return fetch;
        }
    }

    // Stamped with when it was sent, not when it came back: a list asked for before a key existed can arrive
    // after a seal made with that key was seen, and must not count as having looked for it.
    private async Task<KeyAnswer> FetchKeysAsync(string ownerId)
    {
        var sent = _clock();
        KeyAnswer answer;
        try
        {
            var response = await _routes.GetProtectionKeys(ownerId).ConfigureAwait(false);
            if (response.Success && response.Data != null)
                answer = new KeyAnswer(KeyStatus.Found, response.Data, sent);
            else if (response.Status == HttpStatusCode.NotFound)
                answer = new KeyAnswer(KeyStatus.Unknown, null, sent);
            else
                answer = new KeyAnswer(KeyStatus.Unreachable, null, sent);
        }
        catch (Exception ex)
        {
            NexusLog.Warn($"Protection: keys for {ownerId} did not come: {ex.Message}");
            answer = new KeyAnswer(KeyStatus.Unreachable, null, sent);
        }

        lock (_gate)
        {
            _keyFetches.Remove(ownerId);
            if (answer.Status != KeyStatus.Unreachable)
                _keys[ownerId] = new KeyEntry { Keys = answer.Keys, Status = answer.Status, FetchedAt = sent };
        }
        return answer;
    }

    private static bool Holds(ProtectionKeySet? keys, string keyId)
    {
        if (keys?.Keys == null)
            return false;
        foreach (var key in keys.Keys)
        {
            if (string.Equals(key.KeyId, keyId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Warms the cache, for the account signed in here: its own seals then check without a round trip.
    public void Prefetch(string ownerId)
    {
        if (!string.IsNullOrEmpty(ownerId))
            _ = KeysAsync(ownerId);
    }

    // SEALS

    // Every field as the item carries it, and when this machine first saw them as they are (on Now's clock).
    // Unreachable only when the keys could not be had; a seal whose shape is wrong, whose key is not the
    // account's, or whose signature does not check out is broken whatever the network is doing.
    public async Task<Verdict> CheckAsync(string ownerId, string keyId, string itemId, int rules, long issuedAt, string signature,
        DateTime seenSince)
    {
        if (string.IsNullOrEmpty(ownerId) || !SealSignature.IsKeyId(keyId) || !SealSignature.IsItemId(itemId)
            || !SealSignature.IsRules(rules) || issuedAt <= 0 || string.IsNullOrEmpty(signature))
            return Verdict.Broken;

        var answer = await KeysAsync(ownerId, keyId, seenSince).ConfigureAwait(false);
        // A fetch already under way when the seal was seen may have been sent before its key existed.
        if (answer.Status != KeyStatus.Unreachable && answer.FetchedAt < seenSince && !Holds(answer.Keys, keyId))
            answer = await KeysAsync(ownerId, keyId, seenSince).ConfigureAwait(false);
        if (answer.Status == KeyStatus.Unreachable)
            return Verdict.Unreachable;
        if (answer.FetchedAt < seenSince && !Holds(answer.Keys, keyId))
            return Verdict.Unreachable;
        if (answer.Status == KeyStatus.Unknown || answer.Keys?.Keys == null)
            return Verdict.Broken;

        foreach (var key in answer.Keys.Keys)
        {
            if (!string.Equals(key.KeyId, keyId, StringComparison.Ordinal))
                continue;
            byte[] spki;
            try
            {
                spki = Convert.FromBase64String(key.PublicKey);
            }
            catch (FormatException)
            {
                continue;
            }
            // The id is derived from the key, so a listed key under someone else's id is not this seal's key.
            if (!string.Equals(SealSignature.KeyIdOf(spki), keyId, StringComparison.Ordinal))
                continue;
            if (SealSignature.Verify(spki, ownerId, keyId, itemId, rules, issuedAt, signature))
                return Verdict.Verified;
        }
        return Verdict.Broken;
    }

    // NAMES

    // The account's name when it is known here, else null and a fetch starts; changed fires once it lands.
    public string? NameOf(string accountId, Action<string>? changed = null)
    {
        if (string.IsNullOrEmpty(accountId))
            return null;
        var now = _clock();
        lock (_gate)
        {
            if (_names.TryGetValue(accountId, out var known) && now - known.At < NameLife)
                return known.Name;
            if (!_nameFetches.Add(accountId))
                return _names.TryGetValue(accountId, out var stale) ? stale.Name : null;
        }
        _ = FetchNameAsync(accountId, changed);
        lock (_gate)
            return _names.TryGetValue(accountId, out var old) ? old.Name : null;
    }

    public void NoteName(string accountId, string name)
    {
        if (string.IsNullOrEmpty(accountId) || string.IsNullOrWhiteSpace(name))
            return;
        lock (_gate)
            _names[accountId] = (name, _clock());
    }

    private async Task FetchNameAsync(string accountId, Action<string>? changed)
    {
        string? name = null;
        try
        {
            var response = await _routes.GetPublicUser(accountId).ConfigureAwait(false);
            if (response.Success && !string.IsNullOrWhiteSpace(response.Data?.Username))
                name = response.Data!.Username.Trim();
        }
        catch (Exception ex)
        {
            NexusLog.Debug($"Protection: no name for {accountId}: {ex.Message}");
        }
        lock (_gate)
        {
            _nameFetches.Remove(accountId);
            if (name != null)
                _names[accountId] = (name, _clock());
        }
        if (name != null)
            changed?.Invoke(name);
    }
}
