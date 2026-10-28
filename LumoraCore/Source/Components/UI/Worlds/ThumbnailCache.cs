// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.UI.Worlds;

// Turns the pictures the browser can get hold of into textures it can show, without re-decoding the
// same picture every time discovery re-announces it.
//
// Two sources, one path. A live session carries its thumbnail as base64 inside the announcement; a
// saved world has a JPEG sidecar on disk beside it. Both end up as bytes -> LocalDB.SaveAssetAsync
// -> an ImageProvider on a hidden slot -> RawImage.Texture.
//
// Keyed by whatever the caller calls the thing (session id, file path) and gated on a cheap content
// hash, because discovery re-reports every listed session about once a second: same hash, we do
// nothing at all. A NEW hash saves the bytes and swaps the provider, and the OLD provider is
// destroyed on the way out. Without that last part the dash grows one live component per poll for as
// long as the browser is open. -xlinka
internal sealed class ThumbnailCache
{
    private sealed class Entry
    {
        public string Hash = string.Empty;
        public ImageProvider? Provider;
        public bool Loading;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Slot _host;
    private readonly Worker _owner;

    // The slot the providers live on. Its own slot so clearing the cache is one DestroyChildren, and
    // so the browser's UI tree never has an asset component sitting in the middle of a layout.
    public ThumbnailCache(Worker owner, Slot host)
    {
        _owner = owner;
        _host = host;
    }

    public ImageProvider? Get(string key)
    {
        if (_entries.TryGetValue(key, out var entry) && entry.Provider != null && !entry.Provider.IsDestroyed)
            return entry.Provider;
        return null;
    }

    // A live session's inline picture. onReady fires on the world thread once a NEW picture is
    // resident, so the caller can point its RawImage at it.
    public void OfferBase64(string key, string? base64, Action? onReady)
    {
        if (string.IsNullOrEmpty(base64))
            return;
        // The base64 IS the content, so its own hash is the content hash. Cheaper than decoding.
        Offer(key, base64!.Length.ToString() + ":" + base64.GetHashCode().ToString("x8"),
            () => Task.FromResult<byte[]?>(DecodeBase64(base64!)), onReady);
    }

    // A saved world's sidecar. Keyed on size + write time so an unchanged file is never re-read.
    public void OfferFile(string key, string path, Action? onReady)
    {
        long length;
        long ticks;
        try
        {
            var info = new System.IO.FileInfo(path);
            if (!info.Exists)
                return;
            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return;
        }
        Offer(key, length.ToString() + ":" + ticks.ToString(), async () =>
        {
            try
            {
                return await AssetFiles.ReadAllBytesAsync(path).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }, onReady);
    }

    public void Forget(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
            return;
        _entries.Remove(key);
        Destroy(entry.Provider);
    }

    public void OfferUrl(string key, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;
        string hash = "url:" + uri.AbsoluteUri;
        if (_entries.TryGetValue(key, out var existing) && (existing.Hash == hash || existing.Loading))
            return;
        var provider = _host.AddSlot("Thumb").AttachComponent<ImageProvider>();
        provider.URL.Value = uri;
        provider.GenerateMipmaps.Value = false;
        provider.MaxSizeOverride.Value = -1;
        _entries[key] = new Entry { Hash = hash, Provider = provider };
        Destroy(existing?.Provider);
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values)
            Destroy(entry.Provider);
        _entries.Clear();
    }

    private void Offer(string key, string hash, Func<Task<byte[]?>> load, Action? onReady)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            _entries[key] = entry;
        }
        if (entry.Loading || string.Equals(entry.Hash, hash, StringComparison.Ordinal))
            return;

        entry.Loading = true;
        // Decode and the disk write go to the background; the URL write and the swap come back to the
        // world thread, because an ImageProvider is a datamodel component like any other.
        _owner.StartTask(async () =>
        {
            string? url = null;
            try
            {
                await WorldContext.ToBackground();
                var bytes = await load().ConfigureAwait(false);
                var db = Engine.Current?.LocalDB;
                if (bytes != null && bytes.Length > 0 && db != null)
                    url = await db.SaveAssetAsync(bytes, ".jpg").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LumoraLogger.Warn($"WorldsScreen: couldn't prepare a thumbnail: {ex.Message}");
            }

            await WorldContext.ToWorld();
            entry.Loading = false;
            if (_host == null || _host.IsDestroyed || string.IsNullOrEmpty(url))
                return;
            if (!_entries.TryGetValue(key, out var live) || !ReferenceEquals(live, entry))
                return;   // the key was forgotten (or the cache cleared) while we were away

            var replaced = entry.Provider;
            var provider = _host.AddSlot("Thumb").AttachComponent<ImageProvider>();
            provider.URL.Value = new Uri(url!);
            // Card art is small and never sampled at an angle, so the mip chain is dead weight, and the
            // texture cap has nothing to do with a 256x144 picture.
            provider.GenerateMipmaps.Value = false;
            provider.MaxSizeOverride.Value = -1;
            entry.Provider = provider;
            entry.Hash = hash;
            Destroy(replaced);
            onReady?.Invoke();
        });
    }

    private static void Destroy(ImageProvider? provider)
    {
        if (provider == null || provider.IsDestroyed)
            return;
        // One provider per slot, so the slot goes with it rather than accumulating empty children.
        provider.Slot?.Destroy();
    }

    private static byte[]? DecodeBase64(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;   // a peer announced something that is not base64; nothing to show
        }
    }
}
