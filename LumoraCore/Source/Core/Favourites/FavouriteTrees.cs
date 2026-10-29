// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Persistence;

namespace Lumora.Core;

// Favourites a menu reaches for again and again in one sitting (an inspector), fetched once and kept read, so
// every open after the first is a load from memory: no listing, no download, no parse. The fetch starts as
// soon as the favourite is known (a sign in, a new pick), so the first open usually finds it waiting. A kept
// copy is looked up again in the background now and then, which is how one saved over or deleted meanwhile is
// noticed; the open that notices still gets the copy it had. -xlinka
public static class FavouriteTrees
{
    public enum State
    {
        // No favourite of that kind.
        None,
        // On its way; WhenFetched hears it.
        Fetching,
        Kept,
        // The last fetch gave the reason instead of the item.
        Failed,
    }

    // Seconds a kept copy, or a failure, stands before the next open looks the item up again.
    private const double Recheck = 120.0;

    private sealed class Entry
    {
        public DataTreeDictionary? Tree;
        public string? Problem;
        public bool Told;
        public bool Fetching;
        public DateTime FetchedAt = DateTime.MinValue;
        public int Generation;
        public readonly List<Action<FavouriteFetched?>> Waiting = new();
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<FavouriteKind, Entry> Entries = new();
    private static FavouriteKind[] _kinds = Array.Empty<FavouriteKind>();

    public static void Attach(params FavouriteKind[] kinds)
    {
        _kinds = kinds;
        Favourites.Changed += OnChanged;
        Favourites.Ready += OnReady;
    }

    // What there is for kind right now. A kept tree is read by every open, so it is never changed: loading it
    // reads it. Kept or failed long enough ago starts a background look.
    public static State Peek(FavouriteKind kind, out DataTreeDictionary? tree, out string? problem)
    {
        tree = null;
        problem = null;
        if (!Favourites.Has(kind))
            return State.None;
        bool start;
        State state;
        lock (Gate)
        {
            var entry = EntryOf(kind);
            tree = entry.Tree;
            problem = entry.Problem;
            bool stale = (DateTime.UtcNow - entry.FetchedAt).TotalSeconds > Recheck;
            // Nothing kept and nothing failed means nothing asked yet, which this call asks.
            state = tree != null ? State.Kept : entry.Fetching || problem == null ? State.Fetching : State.Failed;
            start = !entry.Fetching && (tree == null && problem == null || stale);
        }
        if (start)
            Fetch(kind);
        return state;
    }

    // True once per failure: the notice that a favourite could not be had is said once, not at every open.
    public static bool Tell(FavouriteKind kind)
    {
        lock (Gate)
        {
            var entry = EntryOf(kind);
            if (entry.Problem == null || entry.Told)
                return false;
            entry.Told = true;
            return true;
        }
    }

    // A kept copy that turned out unusable once loaded: dropped, and the reason stands as the failure until the
    // next look, so the opens after this one go straight to the built-in.
    public static void Refuse(FavouriteKind kind, string problem)
    {
        lock (Gate)
        {
            var entry = EntryOf(kind);
            entry.Tree = null;
            if (!string.Equals(problem, entry.Problem, StringComparison.Ordinal))
                entry.Told = false;
            entry.Problem = problem;
            entry.FetchedAt = DateTime.UtcNow;
        }
    }

    // done hears the fetch out now, or, while one is out, when it lands, on whatever thread it lands on. Null
    // when there is no favourite.
    public static void WhenFetched(FavouriteKind kind, Action<FavouriteFetched?> done)
    {
        FavouriteFetched? now = null;
        lock (Gate)
        {
            var entry = EntryOf(kind);
            if (entry.Fetching)
            {
                entry.Waiting.Add(done);
                return;
            }
            if (Favourites.Get(kind) is { } item)
                now = new FavouriteFetched { Item = item, Tree = entry.Tree, Problem = entry.Problem };
        }
        done(now);
    }

    private static Entry EntryOf(FavouriteKind kind)
    {
        if (!Entries.TryGetValue(kind, out var entry))
            Entries[kind] = entry = new Entry();
        return entry;
    }

    private static bool Kept(FavouriteKind kind) => Array.IndexOf(_kinds, kind) >= 0;

    // A new pick, a reset, or another account: what was kept is someone else's now. The new one is fetched
    // straight away.
    private static void OnChanged(FavouriteKind kind)
    {
        if (!Kept(kind))
            return;
        List<Action<FavouriteFetched?>>? waiting = null;
        lock (Gate)
        {
            var old = EntryOf(kind);
            if (old.Waiting.Count > 0)
                waiting = new List<Action<FavouriteFetched?>>(old.Waiting);
            Entries[kind] = new Entry { Generation = old.Generation + 1 };
        }
        if (Favourites.Has(kind))
            Fetch(kind, waiting);
        else if (waiting != null)
        {
            foreach (var done in waiting)
                Hear(done, null);
        }
    }

    private static void OnReady()
    {
        foreach (var kind in _kinds)
            Peek(kind, out _, out _);
    }

    private static void Fetch(FavouriteKind kind, List<Action<FavouriteFetched?>>? carried = null)
    {
        int generation;
        lock (Gate)
        {
            var entry = EntryOf(kind);
            if (carried != null)
                entry.Waiting.AddRange(carried);
            if (entry.Fetching)
                return;
            entry.Fetching = true;
            generation = entry.Generation;
        }
        _ = Task.Run(async () =>
        {
            FavouriteFetched? fetched;
            try
            {
                fetched = await FavouriteFetch.FetchAsync(kind).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                fetched = Favourites.Get(kind) is { } item
                    ? new FavouriteFetched { Item = item, Problem = $"Your favourite {kind.Noun()} could not be fetched: {ex.Message}" }
                    : null;
            }
            Landed(kind, generation, fetched);
        });
    }

    private static void Landed(FavouriteKind kind, int generation, FavouriteFetched? fetched)
    {
        List<Action<FavouriteFetched?>> waiting;
        lock (Gate)
        {
            var entry = EntryOf(kind);
            if (entry.Generation != generation)
                return;
            entry.Fetching = false;
            entry.FetchedAt = DateTime.UtcNow;
            string? problem = fetched?.Tree == null ? fetched?.Problem ?? $"Your favourite {kind.Noun()} could not be read." : null;
            if (!string.Equals(problem, entry.Problem, StringComparison.Ordinal))
                entry.Told = false;
            entry.Tree = fetched?.Tree;
            entry.Problem = fetched == null ? null : problem;
            waiting = new List<Action<FavouriteFetched?>>(entry.Waiting);
            entry.Waiting.Clear();
        }
        if (fetched != null)
            Logging.Logger.Log(fetched.Tree != null
                ? $"Favourites: kept the favourite {kind.Noun()} \"{fetched.Item.Name}\""
                : $"Favourites: {fetched.Problem}");
        foreach (var done in waiting)
            Hear(done, fetched);
    }

    private static void Hear(Action<FavouriteFetched?> done, FavouriteFetched? fetched)
    {
        try
        {
            done(fetched);
        }
        catch (Exception ex)
        {
            Logging.Logger.Error($"Favourites: a wait on a fetch threw: {ex.Message}");
        }
    }
}
