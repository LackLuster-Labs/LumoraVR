// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Warden;

// Per-peer denial score, host-side. One refused write proves nothing - clients lose grabs, lose link
// claims, and keep retrying writes into a frozen world for entirely honest reasons. What is worth acting
// on is the SHAPE of the refusals over time, so each kind carries a weight and the total decays, and only
// a peer that keeps doing the expensive kinds climbs anywhere.
//
// Keyed by durable identity so a peer cannot shed its score by reconnecting, and so a rejoin after a kick
// starts from where it left off rather than from zero. -xlinka
public sealed class PermissionLedger
{
    private sealed class Entry
    {
        public double Score;
        public double LastTouch;
        public double LastWarn = double.NegativeInfinity;
        public int Count;
        public bool Kicked;
        public bool Banned;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private PermissionEscalationSettings _settings = new();

    // Warnings are for the host's console, so they get a floor between them. The kick/ban latches are
    // what stop a repeat action; this only stops the log line repeating every frame.
    private const double WarnCooldownSeconds = 15.0;

    public PermissionEscalationSettings Settings
    {
        get => _settings;
        set => _settings = value ?? new PermissionEscalationSettings();
    }

    public int TrackedPeers => _entries.Count;

    public void Clear() => _entries.Clear();

    public static double Weight(PermissionDenialKind kind) => kind switch
    {
        // Contention. Two honest clients produce these all day; they must never be what gets someone
        // removed, but a peer generating thousands of them is still worth a look, hence not zero.
        PermissionDenialKind.GrabContention => 1.0,
        PermissionDenialKind.LinkRace => 1.0,
        PermissionDenialKind.LockedWorld => 1.0,

        // Editing someone else's object. Wrong, but a stale client or a mis-authored component does it
        // by accident.
        PermissionDenialKind.ForeignWrite => 3.0,

        // Destroying what you do not own, or writing into the element registry. No correct client ever
        // sends one of these, so a handful inside a minute is a decision, not a bug.
        PermissionDenialKind.Ownership => 10.0,

        // Forging a member only the host may author - identity, allocation byte, permission config.
        // Same weight for the same reason.
        PermissionDenialKind.HostOnly => 10.0,

        // Refused all the same, but the sender could not have known.
        PermissionDenialKind.StaleView => 0.0,

        _ => 1.0
    };

    // The score this peer stands at right now, decayed to `now`. Read-only: does not touch the ledger.
    public double ScoreFor(string identity, double now)
    {
        if (identity == null || !_entries.TryGetValue(identity, out var entry))
            return 0.0;
        return Decay(entry.Score, entry.LastTouch, now);
    }

    public int CountFor(string identity)
        => identity != null && _entries.TryGetValue(identity, out var entry) ? entry.Count : 0;

    public void Forget(string identity)
    {
        if (identity != null)
            _entries.Remove(identity);
    }

    // Records one denial and returns what the host should now do about this peer. Never acts twice for
    // the same escalation: the latch clears only once the score has decayed back below the threshold, so
    // a peer that is kicked and rejoins clean is not kicked again on its first stray write.
    public PermissionViolationResponse Record(string identity, PermissionDenialKind kind, double now, out double score, out bool warn)
    {
        score = 0.0;
        warn = false;
        if (string.IsNullOrEmpty(identity))
            return PermissionViolationResponse.None;

        if (!_entries.TryGetValue(identity, out var entry))
        {
            entry = new Entry { LastTouch = now };
            _entries[identity] = entry;
        }

        entry.Score = Decay(entry.Score, entry.LastTouch, now) + Weight(kind);
        entry.LastTouch = now;
        entry.Count++;
        score = entry.Score;

        var settings = _settings;

        if (entry.Kicked && score < settings.KickScore)
            entry.Kicked = false;
        if (entry.Banned && score < settings.TempBanScore)
            entry.Banned = false;

        if (settings.Response == PermissionViolationResponse.TempBan
            && score >= settings.TempBanScore && !entry.Banned)
        {
            entry.Banned = true;
            entry.Kicked = true;
            return PermissionViolationResponse.TempBan;
        }

        if (settings.Response != PermissionViolationResponse.None
            && score >= settings.KickScore && !entry.Kicked)
        {
            entry.Kicked = true;
            return PermissionViolationResponse.Kick;
        }

        if (score >= settings.WarnScore && now - entry.LastWarn >= WarnCooldownSeconds)
        {
            entry.LastWarn = now;
            warn = true;
        }

        return PermissionViolationResponse.None;
    }

    private double Decay(double score, double lastTouch, double now)
    {
        double elapsed = now - lastTouch;
        if (elapsed <= 0.0 || score <= 0.0)
            return score;

        double halfLife = _settings.HalfLifeSeconds;
        if (halfLife <= 0.0)
            return 0.0;

        return score * Math.Pow(0.5, elapsed / halfLife);
    }
}
