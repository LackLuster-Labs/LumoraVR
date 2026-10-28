// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Networking.Session;
using Lumora.Nexus.Assets;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Networking;

// Supported URI schemes
//   local://machineId/hash - served from LocalDB; if not held locally, requested
//                             from the active session peer via SessionAssetTransferer.
//   file:///abs/path - direct disk read.
//   http:// / https:// - HTTP download.
//   builtin://... - built-in engine assets.
//   test://... - local test assets (editor/dev only).
public static class AssetFetcher
{
    private static readonly HttpClient Http = new();

    // In-flight tasks keyed by URI string: (task, pending callbacks)
    private static readonly Dictionary<string, (Task<byte[]> task, List<Action<byte[]>> callbacks)> _active = new();

    // _active is written from WORKER threads (FetchAsset/FetchLocalAsset run inside each asset's LoadSelf Task.Run)
    // and read+removed from the WORLD thread (ProcessQueue). A plain Dictionary mutated from multiple threads
    // silently corrupts/drops entries - which intermittently dropped one of two model textures (white body), and
    // got worse once more concurrent loads (the import indicator's font/text textures) were in flight. Serialize
    // every access through this lock; callbacks are invoked OUTSIDE the lock (a TCS continuation can run inline and
    // re-enter FetchAsset). -xlinka
    private static readonly object _lock = new();

    // Installed by the platform layer. A res:// path in, the bytes its file holds in the resource pack
    // out, or null when the pack has nothing under that path in any form. Left null there is no pack
    // to ask (tests, tools without a platform) and a missing file is simply missing. -xlinka
    public static Func<string, byte[]?>? ResourcePackReader { get; set; }

    // Calls callback with the raw bytes on the calling thread once the fetch task completes (via ProcessQueue).
    // Returns immediately; duplicate requests share a single in-flight task.
    public static void FetchAsset(string uri, Action<byte[]> callback)
    {
        // local:// - may need peer-to-peer transfer
        if (uri.StartsWith("local://", StringComparison.Ordinal))
        {
            FetchLocalAsset(uri, callback);
            return;
        }

        lock (_lock)
        {
            if (_active.TryGetValue(uri, out var existing))
            {
                existing.callbacks.Add(callback);
                return;
            }

            var task = Task.Run(() => FetchSync(uri));
            _active[uri] = (task, [callback]);
        }
    }

    // Gathers still running, for background work that should keep off the disk while something loads.
    public static int InFlight
    {
        get
        {
            lock (_lock)
                return _active.Count;
        }
    }

    // Must be called every frame by the platform driver update loop.
    public static void ProcessQueue()
    {
        if (_active.Count == 0)
            return;

        // Collect completed fetches + remove them UNDER the lock, then fire callbacks OUTSIDE it (a callback is a
        // TCS completion whose continuation can run inline and re-enter FetchAsset -> deadlock if held). -xlinka
        List<(Action<byte[]> cb, byte[] result, string uri)> ready = null!;
        lock (_lock)
        {
            if (_active.Count == 0)
                return;

            List<string> done = null!;
            foreach (var (uri, (task, callbacks)) in _active)
            {
                if (!task.IsCompleted)
                    continue;

                byte[] result = task.IsCompletedSuccessfully ? task.Result : null!;
                ready ??= new List<(Action<byte[]>, byte[], string)>();
                foreach (var cb in callbacks)
                    ready.Add((cb, result, uri));
                (done ??= new List<string>()).Add(uri);
            }

            if (done != null)
                foreach (var uri in done)
                    _active.Remove(uri);
        }

        if (ready != null)
            foreach (var (cb, result, uri) in ready)
            {
                try { cb(result); }
                catch (Exception ex) { LumoraLogger.Error($"AssetFetcher: callback exception for '{uri}': {ex.Message}"); }
            }
    }

    private static void FetchLocalAsset(string uri, Action<byte[]> callback)
    {
        var engine = Engine.Current;
        var localDB = engine?.LocalDB;

        // 1. Served from our own LocalDB?
        if (localDB != null)
        {
            if (localDB.Exists(uri))
            {
                lock (_lock)
                {
                    // Coalesce: a blind overwrite here would drop a prior requester's callback for the same URI.
                    if (_active.TryGetValue(uri, out var existing))
                    {
                        existing.callbacks.Add(callback);
                        return;
                    }
                    // ASYNC IO, NOT A POOL THREAD HOLDING A BLOCKING READ.
                    //
                    // Task.Run(File.ReadAllBytes) occupies a thread-pool thread for the whole read.
                    // An import asks for well over a hundred assets at once, every one of them also
                    // owning a pool thread for its own ProcessUpdate and another later for its decode,
                    // and the pool only grows past its minimum at a rate of about one thread a second.
                    // So the reads queued behind each other and the whole pipeline moved at a trickle:
                    // measured 67 texture loads started, 34 reads finished, 13 uploaded in 44 seconds.
                    // ReadAllBytesAsync holds no thread while the disk works. -xlinka
                    //
                    // The file is sealed: each batch is decrypted on the thread its read completes on, straight
                    // into the array the decoder gets, so the plaintext never touches the disk.
                    var task = ReadHeld(localDB, uri);
                    _active[uri] = (task, [callback]);
                }
                return;
            }
        }

        // 2. Request from session peer. The read goes off the thread that finished the transfer, which is
        // the session's sync thread holding the data model: reading a large file there held every world's
        // frame. -xlinka
        GatherFromSession(uri, cachedPath =>
        {
            if (cachedPath == null)
            {
                callback(null!);
                return;
            }
            _ = Task.Run(async () =>
            {
                byte[] data;
                try
                {
                    data = localDB != null
                        ? (await localDB.ReadAssetBytesAsync(uri).ConfigureAwait(false))!
                        : await AssetFiles.ReadAllBytesAsync(cachedPath).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LumoraLogger.Error($"AssetFetcher: error reading received asset '{cachedPath}': {ex.Message}");
                    data = null!;
                }
                callback(data);
            });
        });
    }

    private static async Task<byte[]> ReadHeld(LocalDB db, string uri)
        => (await db.ReadAssetBytesAsync(uri).ConfigureAwait(false))!;

    // Fetch a local:// asset from the session into the store and answer its file, or null. Adopted under
    // the hash the URI already carries, before reading a byte: importing instead would re-read and re-hash
    // the whole file to rediscover an address we were handed, and it wrote the record asynchronously - so
    // the decoder that runs straight after could look the asset up, find no record, and be unable to tell
    // what format it just received. Adoption is synchronous and keeps the extension the sender declared,
    // which is exactly what that lookup needs. -xlinka
    private static void GatherFromSession(string uri, Action<string?> onFile, double deadline = 0)
    {
        var engine = Engine.Current;
        var localDB = engine?.LocalDB;
        var transferer = SessionAssetTransferer.ForLocalAsset(uri) ?? engine?.ActiveSessionTransferer;
        if (transferer == null)
        {
            LumoraLogger.Warn($"AssetFetcher: '{uri}' not in LocalDB and no active session - cannot fetch");
            onFile(null);
            return;
        }

        LumoraLogger.Debug($"AssetFetcher: requesting remote asset '{uri}' from session");
        transferer.RequestAsset(new Uri(uri), (_, localPath) =>
        {
            if (localPath == null)
            {
                bool derived = uri.IndexOf('~') >= 0;
                if (!derived && SessionAssetTransferer.OwnerLeft(uri) && (deadline == 0 || Now < deadline))
                {
                    Park(uri, onFile, deadline);
                    return;
                }
                if (derived)
                    LumoraLogger.Debug($"AssetFetcher: peer has no '{uri}'");
                else
                    LumoraLogger.Warn($"AssetFetcher: peer could not provide '{uri}'");
                onFile(null);
                return;
            }

            string? cachedPath;
            try
            {
                cachedPath = localDB?.AdoptGatheredAsset(uri, localPath) ?? localPath;
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"AssetFetcher: error adopting received asset '{localPath}': {ex.Message}");
                cachedPath = null;
            }
            onFile(cachedPath);
        });
    }

    // OWNER AWAY
    //
    // An asset whose owner was in the session and has gone (a dropped link, a crash, a rejoin) is not an
    // asset that does not exist. Failing it there left it broken on every machine for the rest of the
    // session, even after the owner came straight back. It waits instead, as a load still in progress, and
    // is asked for again the moment a session has its owner in it; only after the window does it fail. An
    // owner nobody has seen here is answered at once, so an address that names nobody still fails fast. -xlinka
    internal static double OwnerAwaySeconds = 600;

    private sealed class ParkedFetch
    {
        public readonly List<Action<string?>> Waiters = new();
        public double Deadline;
    }

    private static readonly Dictionary<string, ParkedFetch> _parked = new();
    private static System.Threading.Timer? _parkTimer;

    private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    // A fetch asked again after its owner seemed back, and lost again, keeps the deadline it first had: an
    // owner whose user lingers here a moment after leaving must not stretch the wait. -xlinka
    private static void Park(string uri, Action<string?> onFile, double deadline = 0)
    {
        lock (_parked)
        {
            if (!_parked.TryGetValue(uri, out var parked))
            {
                parked = new ParkedFetch { Deadline = deadline > 0 ? deadline : Now + OwnerAwaySeconds };
                _parked[uri] = parked;
                if (deadline == 0)
                    LumoraLogger.Log($"AssetFetcher: the owner of '{uri}' left the session; waiting up to {OwnerAwaySeconds:0}s for them to come back");
            }
            parked.Waiters.Add(onFile);
            _parkTimer ??= new System.Threading.Timer(_ => SweepParked(), null, 2000, 2000);
        }
    }

    private static void SweepParked()
    {
        List<(string Uri, List<Action<string?>> Waiters, bool Retry, double Deadline)> due = new();
        lock (_parked)
        {
            double now = Now;
            foreach (var (uri, parked) in _parked)
            {
                if (now >= parked.Deadline)
                    due.Add((uri, parked.Waiters, false, parked.Deadline));
                else if (SessionAssetTransferer.IsOwnerPresent(uri))
                    due.Add((uri, parked.Waiters, true, parked.Deadline));
            }
            foreach (var d in due)
                _parked.Remove(d.Uri);
        }

        foreach (var (uri, waiters, retry, deadline) in due)
        {
            if (!retry)
            {
                LumoraLogger.Warn($"AssetFetcher: the owner of '{uri}' did not come back; giving up on it");
                foreach (var w in waiters)
                    w(null);
                continue;
            }
            LumoraLogger.Debug($"AssetFetcher: the owner of '{uri}' is in the session; asking again");
            GatherFromSession(uri, file =>
            {
                foreach (var w in waiters)
                    w(file);
            }, deadline);
        }
    }

    // A local:// asset held in the store, fetched from the session into it first when it is not. For whatever
    // reads an asset as it goes rather than decoding it whole, a video above all, which can be gigabytes and
    // has no business passing through memory: it then opens the address through LocalDB.OpenRead. False when
    // nobody could provide it.
    public static Task<bool> EnsureLocalAsync(string uri)
    {
        var localDB = Engine.Current?.LocalDB;
        if (localDB != null && localDB.Exists(uri))
            return Task.FromResult(true);

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        GatherFromSession(uri, file => done.TrySetResult(file != null));
        return done.Task;
    }

    private static byte[] FetchSync(string uri)
    {
        try
        {
            if (BuiltinAssetHelper.ValidPath(uri))
                return BuiltinAssetHelper.GetBuiltinAssetData(uri);

            if (LocalTestAssetHelper.ValidPath(uri))
                return LocalTestAssetHelper.GetLocalTestAssetData(uri);

            var u = new Uri(uri);

            if (u.Scheme == Uri.UriSchemeFile)
                return ReadFile(u, uri);

            if (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            {
                // Route remote downloads through the shared content cache (concurrent-request
                // dedup, disk LRU, persistence). Only fall back to a direct, uncached download
                // if the cache isn't up yet (early init / tests).
                var cache = Engine.Current?.ContentCache;
                if (cache != null)
                    return cache.Get(u).GetAwaiter().GetResult()!;

                return Http.GetByteArrayAsync(u).GetAwaiter().GetResult();
            }

            LumoraLogger.Warn($"AssetFetcher: unsupported URI scheme '{u.Scheme}' in '{uri}'");
            return null!;
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"AssetFetcher: failed to fetch '{uri}': {ex.Message}");
            return null!;
        }
    }

    // A res:// asset arrives here already resolved to a disk path under ResourceRoot. In the editor that
    // file is real and the read is the whole story. An export has no such file: the pack holds either
    // the raw file (a file no importer claims, or one kept as-is) or only what the importer made of it,
    // and the platform reader gets bytes out of whichever is there. Both names go in the warning so
    // a miss can be found from either side. -xlinka
    private static byte[] ReadFile(Uri u, string uri)
    {
        var path = u.LocalPath;
        if (File.Exists(path))
            return AssetFiles.ReadAllBytes(path);

        var resPath = Engine.Current?.TryGetResourcePath(path);
        var reader = ResourcePackReader;
        if (resPath != null && reader != null)
        {
            var packed = reader(resPath);
            if (packed != null && packed.Length > 0)
            {
                LumoraLogger.Debug($"AssetFetcher: '{resPath}' served from the resource pack ({packed.Length} bytes)");
                return packed;
            }
        }

        LumoraLogger.Warn(resPath != null
            ? $"AssetFetcher: '{resPath}' is neither on disk at '{path}' nor in the resource pack"
            : $"AssetFetcher: file not found: '{uri}'");
        return null!;
    }
}
