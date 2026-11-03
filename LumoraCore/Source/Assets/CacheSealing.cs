// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Logging;
using Lumora.Core.Persistence;
using Lumora.Nexus.Storage;

namespace Lumora.Core.Assets;

// The pass that seals cache files written before sealing: the asset database, the download cache and the
// renderer's own caches, one file at a time. It waits a while after startup so the first world loads first,
// and paces itself to a disk rate a load never notices. Something plain arriving later (an adoption on an old
// path) asks for one more pass. -xlinka
public static class CacheSealing
{
    private const double BytesPerSecond = 16.0 * 1024 * 1024;
    // How long the pass stands aside for loads at most, per file, so a world that streams for ever still gets
    // sealed eventually.
    private static readonly TimeSpan LongestYield = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartAfter = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AgainAfter = TimeSpan.FromSeconds(20);

    public delegate Task<(int Files, long Bytes)> Pass(Func<long, Task> pace, CancellationToken ct);

    private static readonly object Gate = new();
    private static readonly List<(string Name, Pass Run)> Passes = new();
    private static CancellationTokenSource _stop = new();
    private static bool _started;
    private static bool _running;
    private static bool _again;

    // A cache outside the engine (the renderer's) adds its folder's pass here at startup.
    public static void Add(string name, Pass pass)
    {
        lock (Gate)
            Passes.Add((name, pass));
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_started)
                return;
            _started = true;
            _stop = new CancellationTokenSource();
        }
        Schedule(StartAfter);
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _started = false;
            _stop.Cancel();
        }
    }

    public static void Nudge()
    {
        lock (Gate)
        {
            if (!_started)
                return;
            if (_running)
            {
                _again = true;
                return;
            }
        }
        Schedule(AgainAfter);
    }

    // The disk time a file cost, then out of the way while anything is gathering: the pass is never what a
    // load waits on.
    public static async Task Pace(long bytes)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(10 + bytes / BytesPerSecond * 1000.0)).ConfigureAwait(false);
        var until = DateTime.UtcNow + LongestYield;
        while (Networking.AssetFetcher.InFlight > 0 && DateTime.UtcNow < until)
            await Task.Delay(250).ConfigureAwait(false);
    }

    private static void Schedule(TimeSpan after)
    {
        CancellationToken ct;
        lock (Gate)
            ct = _stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(after, ct).ConfigureAwait(false);
                await RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Warn($"CacheSealing: pass stopped: {ex.Message}");
            }
        }, ct);
    }

    private static async Task RunAsync(CancellationToken ct)
    {
        lock (Gate)
        {
            if (_running)
            {
                _again = true;
                return;
            }
            _running = true;
            _again = false;
        }
        try
        {
            var engine = Engine.Current;
            var all = new List<(string Name, Pass Run)>();
            if (engine?.LocalDB is { } db)
            {
                all.Add(("assets", db.SealOlderFilesAsync));
                all.Add(("gpu", (pace, token) => SealFolderAsync(db.GetGpuCachePath(), TextureGpuCache.LegacyExtension,
                    TextureGpuCache.NameScope, pace, token)));
            }
            if (engine?.ContentCache is { } cache)
                all.Add(("downloads", cache.SealOlderFilesAsync));
            lock (Gate)
                all.AddRange(Passes);

            foreach (var (name, run) in all)
            {
                ct.ThrowIfCancellationRequested();
                var (files, bytes) = await run(Pace, ct).ConfigureAwait(false);
                if (files > 0)
                    Logger.Log($"CacheSealing: sealed {files} older {name} file(s), {bytes / (1024.0 * 1024.0):0.#} MB");
            }
        }
        finally
        {
            bool again;
            lock (Gate)
            {
                _running = false;
                again = _again && _started;
                _again = false;
            }
            if (again)
                Schedule(AgainAfter);
        }
    }

    // A flat folder of files named "<key><legacyExtension>" from before sealing: each one sealed (its key
    // wrapped into it) under the name the scope and key give, then the old one deleted. Readers look for the
    // sealed name first and the old one second, so a file is readable at every step.
    public static async Task<(int Files, long Bytes)> SealFolderAsync(string? folder, string legacyExtension, string scope,
        Func<long, Task> pace, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return (0, 0);
        LocalEncryption.WireSealedStorage();
        int count = 0;
        long bytes = 0;
        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*" + legacyExtension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string key = Path.GetFileNameWithoutExtension(file);
            string target = Path.Combine(folder, SealedKeys.NameFor(scope, key));
            try
            {
                if (File.Exists(target) || SealedFile.IsSealed(file))
                {
                    File.Delete(file);
                    continue;
                }
                var plain = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
                string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await SealedFile.WriteAllBytesAsync(temp, plain, null, ct).ConfigureAwait(false);
                    File.Move(temp, target, overwrite: false);
                }
                catch (IOException) when (File.Exists(target))
                {
                }
                finally
                {
                    try { File.Delete(temp); } catch { }
                }
                File.Delete(file);
                count++;
                bytes += plain.LongLength;
                await pace(plain.LongLength).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return (count, bytes);
    }
}
