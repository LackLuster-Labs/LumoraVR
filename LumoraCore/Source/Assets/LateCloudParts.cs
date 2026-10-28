// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Nexus.Cloud.Cdn;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

// LATE CLOUD PARTS
//
// A cloud part the service answered "not there" for is often only late. The wearer's own game puts back the
// parts the service lacks (a save, the background repair), and that upload can land a second or two after
// another player first asked for the part. The failed load was final: the avatar stayed a "Couldn't load"
// stand-in for the rest of the session while its part sat on the service.
//
// Every cloud address whose load failed for want of its bytes ("not there", or the service could not be asked)
// is watched while some failed instance of it is still asked for. While anything references a holder of it, the
// part is read again each time the download cache's schedule for its hash allows (ContentCache.WaitAfter; a
// read that failed some other way waits the same steps here, and nothing is read while offline), so the
// requests for one part follow that one schedule however many worlds, copies and rejoins want it, and a part
// nothing references costs nothing. The person wearing it is asked for it too, once. Once the blob is here,
// every holder of the failed instance loads again through AssetReload: meshes, textures and the surfaces
// drawing them, clips, anything a static provider loads, and an avatar held on the stand-in arrives. Driven
// from the engine's asset update; with nothing watched a frame costs one read of a counter. -xlinka
internal static class LateCloudParts
{
    private const double LookEverySeconds = 0.5;

    // A part that is here and fails again when loaded from it is broken rather than late: a few goes, then
    // the failure stands.
    private const int MostReloads = 3;

    private sealed class Watch
    {
        public readonly string Hash;
        public readonly HashSet<AssetID> Ids = new();
        public readonly long Since = Stopwatch.GetTimestamp();
        public volatile bool Arrived;
        public bool CheckHere = true;
        public bool AskedWearer;
        public int Touched;
        public Task? Read;
        public int Reads;
        public long NextRead;
        public AssetReload.Landed? Reload;
        public int Reloads;

        public Watch(string hash) => Hash = hash;
    }

    private static readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);
    private static readonly List<Watch> _looking = new();
    private static readonly List<AssetID> _ids = new();
    private static readonly List<IAssetRequester> _holders = new();
    private static readonly List<IAssetReload> _reloading = new();
    private static readonly Action<string> _onContentReady = OnContentReady;
    private static ContentCache? _heard;
    private static int _count;
    private static long _nextLook;

    // A static asset's load failed. Kept only when it is a cloud blob that is not on this machine: the service
    // said it does not have it, or could not be asked (offline, a dropped line). Bytes that are here and still
    // failed are broken, not late.
    internal static void Failed(LoadableAsset asset)
    {
        var url = asset.AssetURL;
        var hash = ContentCache.ContentHashOf(url);
        var cache = Engine.Current?.ContentCache;
        if (url == null || hash == null || cache == null)
            return;
        if (!cache.IsMissing(hash) && (cache.Contains(hash) || Engine.Current?.LocalDB?.UriForHash(hash) != null))
            return;
        lock (_watches)
        {
            if (!_watches.TryGetValue(hash, out var watch))
            {
                _watches[hash] = watch = new Watch(hash);
                Volatile.Write(ref _count, _watches.Count);
            }
            watch.Ids.Add(new AssetID(url, asset.GetType()));
            watch.Touched++;
        }
    }

    internal static void Tick(AssetManager manager)
    {
        if (Volatile.Read(ref _count) == 0)
            return;
        long now = Stopwatch.GetTimestamp();
        if (now < _nextLook)
            return;
        _nextLook = now + (long)(LookEverySeconds * Stopwatch.Frequency);
        var cache = manager.Engine?.ContentCache;
        if (cache == null)
            return;
        if (!ReferenceEquals(cache, _heard))
        {
            if (_heard != null)
                _heard.ContentReady -= _onContentReady;
            cache.ContentReady += _onContentReady;
            _heard = cache;
        }

        _looking.Clear();
        lock (_watches)
            _looking.AddRange(_watches.Values);
        foreach (var watch in _looking)
        {
            bool keep;
            int touched;
            lock (_watches)
                touched = watch.Touched;
            try
            {
                keep = Look(watch, manager, cache);
            }
            catch (Exception ex)
            {
                LumoraLogger.Error($"LateCloudParts: {Short(watch.Hash)}: {ex.Message}");
                keep = false;
            }
            if (keep)
                continue;
            // Not when a load failed on it again meanwhile: that holder came after the look.
            lock (_watches)
            {
                if (watch.Touched != touched)
                    continue;
                _watches.Remove(watch.Hash);
                Volatile.Write(ref _count, _watches.Count);
            }
        }
        _looking.Clear();
    }

    // False once the watch is over.
    private static bool Look(Watch watch, AssetManager manager, ContentCache cache)
    {
        if (watch.Reload != null)
        {
            watch.Reload.Pump();
            if (watch.Reload.Finished)
                watch.Reload = null;
            return true;
        }

        _ids.Clear();
        lock (_watches)
            _ids.AddRange(watch.Ids);
        _holders.Clear();
        foreach (var id in _ids)
            manager.CollectFailed(id, _holders);
        if (_holders.Count == 0)
        {
            if (!watch.Arrived)
                LumoraLogger.Debug($"LateCloudParts: nothing asks for {Short(watch.Hash)} any more after {watch.Reads} read(s); no longer waiting for it");
            return false;
        }

        // A read that did not bring the part leaves it alone a while whatever went wrong: the service's
        // "not there" has its own wait in the cache, and an error or a dropped line waits here, on the same steps.
        if (watch.Read is { IsCompleted: true })
        {
            watch.Read = null;
            watch.CheckHere = true;
            watch.NextRead = Stopwatch.GetTimestamp() + (long)(ContentCache.WaitAfter(watch.Reads).TotalSeconds * Stopwatch.Frequency);
        }
        if (!watch.Arrived && watch.CheckHere)
        {
            watch.CheckHere = false;
            if (cache.Contains(watch.Hash))
                watch.Arrived = true;
        }

        if (watch.Arrived)
        {
            if (watch.Reloads >= MostReloads)
                return false;
            _reloading.Clear();
            foreach (var holder in _holders)
            {
                if (holder is IAssetReload provider && !_reloading.Contains(provider))
                    _reloading.Add(provider);
            }
            if (_reloading.Count == 0)
                return false;
            watch.Reloads++;
            watch.Reload = new AssetReload.Landed(_ids[0].Url, _reloading);
            LumoraLogger.Log($"LateCloudParts: {Short(watch.Hash)} is here {Stopwatch.GetElapsedTime(watch.Since).TotalSeconds:0.0} s after it failed "
                + $"({watch.Reads} read(s) since); loading it again for {watch.Reload.Providers} provider(s) in {watch.Reload.Worlds} world(s)");
            _reloading.Clear();
            return true;
        }

        if (!Wanted(_holders))
            return true;
        if (!watch.AskedWearer)
            watch.AskedWearer = AskWearer(watch, _holders);
        if (watch.Read != null || cache.IsMissing(watch.Hash) || !Connectivity.IsOnline || Stopwatch.GetTimestamp() < watch.NextRead)
            return true;
        watch.Read = cache.Prefetch(watch.Hash);
        watch.Reads++;
        return true;
    }

    // ASKING THE WEARER
    //
    // The service lacks the part, but the person wearing or holding it here, or who brought it in, usually
    // has it: it is their own import, or they loaded it before it went. It is asked of them once, by its hash as a local address on
    // their machine, the request any local part goes out as (the host relays it when it is not theirs to
    // serve), so no build needs anything new to answer. Kept only when the bytes hash to the name: a peer
    // can send anything for an address, and what it sends is filed by that hash. A part that fails before
    // its avatar is worn has nobody to ask yet, and is asked once somebody holds it. True once asked. -xlinka
    private static bool AskWearer(Watch watch, List<IAssetRequester> holders)
    {
        foreach (var holder in holders)
        {
            if (holder is not Component { IsDestroyed: false } component)
                continue;
            var user = component.Slot?.ActiveUser ?? BroughtBy(component);
            var transferer = component.World?.Session?.AssetTransferer;
            var machine = user?.MachineID?.Value;
            if (user == null || user.IsLocal || transferer == null || string.IsNullOrEmpty(machine))
                continue;
            var address = $"local://{machine}/{watch.Hash}";
            LumoraLogger.Log($"LateCloudParts: {Short(watch.Hash)} is not on the service; asking {user.UserName?.Value} for it");
            transferer.RequestAsset(new Uri(address), (_, path) => FromWearer(watch, address, path));
            return true;
        }
        return false;
    }

    // Whoever made it, for a holder under nobody's root: the person who brought it in.
    private static User? BroughtBy(Component component)
    {
        byte owner = component.ReferenceID.GetUserByte();
        if (!RefIDConstants.IsValidUserByte(owner) || component.World is not { } world)
            return null;
        foreach (var user in world.GetAllUsers())
        {
            if (user.HoldsByte(owner))
                return user;
        }
        return null;
    }

    // On the session's thread: the check reads the whole file, so it goes elsewhere.
    private static void FromWearer(Watch watch, string address, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            LumoraLogger.Debug($"LateCloudParts: the wearer does not have {Short(watch.Hash)} either");
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                // The transfer has filed it already; the file is sealed, so it is hashed through the store.
                string hash;
                using (var stream = Engine.Current?.LocalDB?.OpenRead(address) ?? AssetFiles.Open(path))
                    hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (hash != watch.Hash)
                {
                    LumoraLogger.Warn($"LateCloudParts: what the wearer sent for {Short(watch.Hash)} is not that part; not kept");
                    if (Engine.Current?.LocalDB is { } db)
                        _ = db.DeleteAssetAsync(address);
                    else
                        File.Delete(path);
                    return;
                }
                if (Engine.Current?.LocalDB?.AdoptGatheredAsset(address, path) == null)
                    return;
                watch.Arrived = true;
                LumoraLogger.Log($"LateCloudParts: {Short(watch.Hash)} came from its wearer");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LumoraLogger.Warn($"LateCloudParts: could not keep {Short(watch.Hash)} from its wearer: {ex.Message}");
            }
        });
    }

    // Something draws with it or otherwise points at it. A holder nothing references is not worth a request;
    // the watch stays, so a reference that comes back starts the reads again.
    private static bool Wanted(List<IAssetRequester> holders)
    {
        foreach (var holder in holders)
        {
            if (holder is IAssetProvider { AssetReferenceCount: > 0 } && holder is Component { IsDestroyed: false })
                return true;
        }
        return false;
    }

    private static void OnContentReady(string key)
    {
        lock (_watches)
        {
            if (_watches.TryGetValue(key, out var watch))
                watch.Arrived = true;
        }
    }

    private static string Short(string hash) => hash.Length > 12 ? hash.Substring(0, 12) : hash;
}
