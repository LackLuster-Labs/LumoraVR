// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

// The debug screen's asset reload: every address-loaded asset a world uses, let go of, dropped from the share
// and asked for again, so it is read and decoded from scratch. A plain web link is downloaded again too; a
// local record, a built-in file or a content-hashed blob is read again from where it is, since those bytes
// cannot have changed under their name. An asset another world still holds stays as it is there and here.
// A part the service said it did not have is asked for again too: the reload is how a person says try again.
// Runs on the world's own thread; nothing on the normal path calls it.
//
// A few milliseconds of it a frame, one address at a time. In one call a hosted world with a few guests froze
// for over a second, pressed exactly when something was already wrong. Every provider of one address still
// takes each step before any takes the next, so a shared asset is let go of by all of them before it is
// dropped. -xlinka
public static class AssetReload
{
    public readonly record struct Report(int Providers, int Reloaded, int Kept, int Downloads);

    private const double BudgetMs = 3.0;

    public static void Run(World world, Action<Report>? done = null)
    {
        if (world == null || world.IsDestroyed)
            return;
        world.RunSynchronously(() => new Staged(world, done).Begin());
    }

    private sealed class Staged
    {
        private readonly World _world;
        private readonly Action<Report>? _done;
        private readonly List<(Uri Url, List<IAssetReload> Providers)> _groups = new();
        private int _next;
        private int _providers, _reloaded, _kept, _downloads;

        public Staged(World world, Action<Report>? done)
        {
            _world = world;
            _done = done;
        }

        public void Begin()
        {
            var byUrl = new Dictionary<Uri, List<IAssetReload>>();
            foreach (var component in _world.RootSlot.GetComponentsInChildren<Component>())
            {
                if (component is not IAssetReload provider || component.IsDestroyed || provider.Held is not { } url)
                    continue;
                if (!byUrl.TryGetValue(url, out var list))
                {
                    byUrl[url] = list = new List<IAssetReload>();
                    _groups.Add((url, list));
                }
                list.Add(provider);
            }
            Step();
        }

        private void Step()
        {
            if (_world.IsDestroyed)
                return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var cache = Engine.Current?.ContentCache;
            while (_next < _groups.Count)
            {
                var (url, providers) = _groups[_next++];
                bool letGo = false;
                foreach (var provider in providers)
                {
                    if (provider.LetGo() != null)
                    {
                        letGo = true;
                        _providers++;
                    }
                }
                if (letGo)
                {
                    bool dropped = true;
                    foreach (var provider in providers)
                        dropped &= provider.Drop();
                    if (dropped)
                    {
                        _reloaded++;
                        if (cache != null && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
                        {
                            cache.ForgetMissing(url);
                            if (cache.ForgetLink(url))
                                _downloads++;
                        }
                        CloudVariants.Forget(url);
                    }
                    else
                        _kept++;
                }
                foreach (var provider in providers)
                    provider.AskAgain();
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= BudgetMs)
                    break;
            }
            if (_next < _groups.Count)
            {
                _world.RunInUpdates(1, Step);
                return;
            }
            var report = new Report(_providers, _reloaded, _kept, _downloads);
            LumoraLogger.Log($"AssetReload: '{_world.WorldName?.Value}': {report.Providers} providers, {report.Reloaded} assets loading again, "
                + $"{report.Kept} kept (held elsewhere), {report.Downloads} links downloading again");
            _done?.Invoke(report);
        }
    }

    // The same three steps for the holders of one failed instance whose bytes are here now (LateCloudParts),
    // whichever worlds they are in. The instance is shared engine wide: dropped while one world still held it,
    // or let go of in one world at a time, the others would ask again straight back onto the failed one. Each
    // world lets go and asks again on its own thread; Pump, from the engine's asset update, takes the step
    // between once every world has let go, and does not wait for a world that went meanwhile. -xlinka
    internal sealed class Landed
    {
        private sealed class Held
        {
            public readonly World World;
            public readonly List<IAssetReload> Providers = new();
            public volatile bool LetGo;

            public Held(World world) => World = world;
        }

        private readonly Uri _url;
        private readonly List<Held> _held = new();

        public bool Finished { get; private set; }
        public int Providers { get; }
        public int Worlds => _held.Count;
        public int Kept { get; private set; }

        public Landed(Uri url, IReadOnlyList<IAssetReload> providers)
        {
            _url = url;
            foreach (var provider in providers)
            {
                if (provider is not Component { IsDestroyed: false, World: { } world })
                    continue;
                var held = _held.Find(h => h.World == world);
                if (held == null)
                    _held.Add(held = new Held(world));
                held.Providers.Add(provider);
                Providers++;
            }
            foreach (var held in _held)
            {
                held.World.RunSynchronously(() =>
                {
                    foreach (var provider in held.Providers)
                    {
                        if (provider is Component { IsDestroyed: false })
                            provider.LetGo();
                    }
                    held.LetGo = true;
                });
            }
        }

        public void Pump()
        {
            if (Finished)
                return;
            foreach (var held in _held)
            {
                if (!held.LetGo && !Gone(held.World))
                    return;
            }
            foreach (var held in _held)
            {
                foreach (var provider in held.Providers)
                {
                    if (!provider.Drop())
                        Kept++;
                }
            }
            CloudVariants.Forget(_url);
            foreach (var held in _held)
            {
                if (Gone(held.World))
                    continue;
                held.World.RunSynchronously(() =>
                {
                    foreach (var provider in held.Providers)
                        provider.AskAgain();
                });
            }
            Finished = true;
        }

        private static bool Gone(World world) => world.IsDestroyed || world.IsDisposed;
    }
}
