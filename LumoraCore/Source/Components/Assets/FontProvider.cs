// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Lumora.Core.Assets;

namespace Lumora.Core.Components.Assets;

// A FontSet assembled from a primary font plus fallbacks. Each file is requested from the AssetManager
// as a shared, dedup'd FontAsset; this aggregates the loaded ones into the set it exposes. -xlinka
[ComponentCategory("Assets")]
public sealed class FontProvider : AssetProvider<FontSet>, IAssetRequester
{
    public readonly Sync<Uri> URL;
    public readonly SyncFieldList<Uri> FallbackURLs;

    private readonly FontSet _fontSet = new();
    private readonly object _lock = new();
    private readonly List<Uri> _requested = new();           // resolved file URLs, in render order
    private readonly Dictionary<Uri, FontAsset> _loaded = new();
    // local:// fonts asked of the session, each once: a font loads from a file, so one this machine does
    // not hold is fetched into the store first and the set rebuilt when it lands.
    private readonly HashSet<string> _fetched = new();
    private bool _setInitialized;

    public FontProvider()
    {
        URL = new Sync<Uri>(this, null!);
        FallbackURLs = new SyncFieldList<Uri>();
    }

    public override FontSet Asset => _fontSet.IsValid ? _fontSet : null!;
    public override bool IsAssetAvailable => _fontSet.IsValid;

    protected override void UpdateAsset()
    {
        if (!_setInitialized)
        {
            _fontSet.InitializeDynamic();
            _fontSet.SetOwner(this);
            _setInitialized = true;
        }

        var manager = Engine.Current?.AssetManager;
        if (manager == null)
            return;

        // Desired ordered set of resolved file URLs: primary first, then fallbacks, deduplicated.
        var desired = new List<Uri>();
        AddResolved(desired, URL.Value);
        foreach (var fallback in FallbackURLs)
            AddResolved(desired, fallback);

        List<Uri> toRelease, toRequest;
        lock (_lock)
        {
            toRelease = _requested.Where(u => !desired.Contains(u)).ToList();
            toRequest = desired.Where(u => !_requested.Contains(u)).ToList();
            _requested.Clear();
            _requested.AddRange(desired);
            foreach (var u in toRelease)
                _loaded.Remove(u);
        }

        // Request/release outside the lock; RequestAsset may call back into AssignAsset.
        foreach (var u in toRelease)
            manager.ReleaseAsset<FontAsset>(u, this);
        foreach (var u in toRequest)
            manager.RequestAsset<FontAsset>(u, this);

        RebuildFontSet();
    }

    protected override void FreeAsset()
    {
        var manager = Engine.Current?.AssetManager;
        List<Uri> toRelease;
        lock (_lock)
        {
            toRelease = new List<Uri>(_requested);
            _requested.Clear();
            _loaded.Clear();
        }
        foreach (var u in toRelease)
            manager?.ReleaseAsset<FontAsset>(u, this);

        _fontSet.SetFonts(Array.Empty<FontAsset>());
        AssetRemoved();
    }

    void IAssetRequester.AssignAsset(Asset asset)
    {
        var font = (FontAsset)asset;
        lock (_lock)
        {
            _loaded[font.AssetURL] = font;
        }
    }

    void IAssetRequester.AssetLoadStateUpdated(Asset asset) => RebuildFontSet();

    // A font is opened from a file, so only a resolved file goes in the set. A local:// address with no file
    // here is somebody else's import, and it used to be passed over for good: every font in a package one
    // person brought in stayed missing for everyone else, and text set in it drew nothing. -xlinka
    private void AddResolved(List<Uri> into, Uri raw)
    {
        if (raw == null)
            return;
        var resolved = ProcessURL(raw);
        if (resolved == null)
            return;
        bool local = string.Equals(resolved.Scheme, "local", StringComparison.OrdinalIgnoreCase);
        if (resolved.IsFile || (local && Engine.Current?.LocalDB?.Exists(resolved.OriginalString) == true))
        {
            if (!into.Contains(resolved))
                into.Add(resolved);
            return;
        }
        if (local)
            FetchFromSession(resolved.OriginalString);
    }

    private void FetchFromSession(string uri)
    {
        lock (_lock)
        {
            if (!_fetched.Add(uri))
                return;
        }

        var world = World;
        _ = Lumora.Core.Networking.AssetFetcher.EnsureLocalAsync(uri).ContinueWith(task =>
        {
            if (!task.Result || world == null || world.IsDisposed)
                return;
            world.RunSynchronously(() =>
            {
                if (!IsDestroyed && AssetReferenceCount > 0)
                    UpdateAsset();
            });
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    private void RebuildFontSet()
    {
        // Every font's load completes on its own thread, so two of them can rebuild at once. Build AND
        // apply under the lock: with the apply outside it the slower thread could publish the older
        // membership last and the set would sit one font short until something else poked it. -xlinka
        var fonts = new List<FontAsset>();
        lock (_lock)
        {
            foreach (var u in _requested)
            {
                if (_loaded.TryGetValue(u, out var font) &&
                    font.LoadState is AssetLoadState.PartiallyLoaded or AssetLoadState.FullyLoaded)
                {
                    fonts.Add(font);
                }
            }

            _fontSet.SetFonts(fonts);
        }

        if (_fontSet.IsValid)
        {
            AssetCreated();
            AssetUpdated();
        }
    }
}
