// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Security.Integrity;

public enum IntegrityMode
{
    // A stamped build: every managed assembly loaded has to be a file the signed release list names.
    Release,
    // A build from the source tree: only known patchers count, since nothing lists what a dev build should hold.
    Development,
    // Assemblies that do not sit on disk on their own (Android): only known patchers count.
    Packaged,
}

// Whether this game runs the managed code it shipped with. Every assembly loaded into the process is looked at once,
// the ones there at startup on a worker and every later one as it arrives, plus a sweep every half minute for anything
// that got past the load event. Native libraries never count: overlays, capture tools, antivirus hooks and the XR
// runtime load those into every game, and none of them is managed code. Assemblies made in memory by
// Reflection.Emit count as nothing either, since scripts compiled to IL, serializers and regex all make them; only
// the code that makes them is judged, and that comes from a file.
//
// What it does NOT try: checking machine code at method entries for detours. On this runtime a method's entry is a
// stub the runtime itself rewrites as code moves between tiers, so a byte check either flags clean games or is easy
// to slip past, and anything able to detour can patch this check too. Every patcher has to arrive as a managed
// assembly first, which is what this catches. -xlinka
public static class GameIntegrity
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(20);

    // Libraries that exist to change other code at run time, or to load code nobody shipped.
    private static readonly string[] Patchers =
    {
        "0Harmony", "Harmony", "Lib.Harmony", "MonoMod", "Mono.Cecil", "BepInEx", "MelonLoader", "Doorstop",
        "Reloaded.Hooks", "EasyHook", "MinHook", "Dobby",
    };

    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<Assembly, object> Seen = new();
    private static readonly SortedSet<string> Findings = new(StringComparer.Ordinal);
    private static readonly SortedSet<string> Names = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> FileHashes = new(StringComparer.Ordinal);
    private static readonly ManualResetEventSlim ReadyEvent = new(false);

    private static bool _started;
    private static IntegrityMode _mode;
    private static ReleaseManifest? _manifest;
    private static string _manifestProblem = string.Empty;
    private static volatile bool _listRead;
    private static int _revision;
    private static int _checked;
    private static Timer? _sweep;

    // Raised once, off the main thread, when the startup look is done.
    public static event Action? Settled;

    // Raised off the main thread whenever a later look finds something new.
    public static event Action? Changed;

    public static IntegrityMode Mode { get { Start(); return _mode; } }

    public static bool IsReady => ReadyEvent.IsSet;

    // Bumped on every new finding, so callers can tell a verdict they hold from the current one.
    public static int Revision { get { lock (Gate) return _revision; } }

    public static bool IsModified
    {
        get
        {
            WaitReady();
            lock (Gate) return Findings.Count > 0;
        }
    }

    // "clean", or "modified <sha-256 of every finding>". Two games with the same changes give the same text, so
    // they still find each other.
    public static string Verdict
    {
        get
        {
            WaitReady();
            lock (Gate)
            {
                if (Findings.Count == 0)
                    return "clean";
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", Findings)));
                return "modified " + Convert.ToHexString(digest).ToLowerInvariant();
            }
        }
    }

    // The names behind the verdict for people to read: "0Harmony, LiteNetLib". Empty when clean.
    public static string Describe()
    {
        lock (Gate)
            return string.Join(", ", Names);
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_started)
                return;
            _started = true;
            _mode = PickMode(out var home);
            // Before the first look, so nothing loaded between the two goes unseen.
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            _ = Task.Run(() => FirstLook(home));
        }
    }

    // Blocks until the startup look is done (it normally is long before anyone hosts or joins).
    public static void WaitReady()
    {
        Start();
        if (!ReadyEvent.IsSet && !ReadyEvent.Wait(ReadyWait))
            LumoraLogger.Warn("[integrity] the startup check is still running; answering with what it has so far");
    }

    private static IntegrityMode PickMode(out string home)
    {
        home = string.Empty;
        try
        {
            home = Path.GetDirectoryName(typeof(GameIntegrity).Assembly.Location) ?? string.Empty;
        }
        catch
        {
            home = string.Empty;
        }
        if (OperatingSystem.IsAndroid() || home.Length == 0)
            return IntegrityMode.Packaged;
        return BuildIdentity.IsRelease ? IntegrityMode.Release : IntegrityMode.Development;
    }

    private static void FirstLook(string home)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            if (_mode == IntegrityMode.Release)
            {
                var manifest = ReleaseManifest.Load(Path.Combine(home, ReleaseManifest.FileName), BuildInfo.Version, out var problem);
                lock (Gate)
                {
                    _manifest = manifest;
                    _manifestProblem = problem;
                }
                if (manifest == null)
                    Add(ReleaseManifest.FileName, ReleaseManifest.FileName + "|" + problem, raise: false);
            }
            _listRead = true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                Look(assembly, raise: false);
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"[integrity] startup check failed: {ex.Message}");
            if (_mode == IntegrityMode.Release && !_listRead)
                Add(ReleaseManifest.FileName, ReleaseManifest.FileName + "|check failed", raise: false);
        }
        finally
        {
            _listRead = true;
            ReadyEvent.Set();
        }

        string summary;
        lock (Gate)
        {
            summary = Findings.Count == 0
                ? "clean"
                : "MODIFIED: " + string.Join("; ", Findings);
        }
        string against = _mode switch
        {
            IntegrityMode.Release => _manifest != null
                ? $"against the signed list of release {_manifest.Version} ({_manifest.FileCount} files)"
                : $"with no usable release list ({_manifestProblem})",
            IntegrityMode.Development => "as a development build (patchers only)",
            _ => "as a packaged build (patchers only)",
        };
        LumoraLogger.Log($"[integrity] {_checked} assemblies checked {against} in {watch.ElapsedMilliseconds} ms: {summary}");

        _sweep = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
        try { Settled?.Invoke(); }
        catch (Exception ex) { LumoraLogger.Warn($"[integrity] settled handler threw: {ex.Message}"); }
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        var assembly = args.LoadedAssembly;
        if (assembly == null || assembly.IsDynamic)
            return;
        // Hashing a file has no place on whatever thread happened to load it.
        ThreadPool.QueueUserWorkItem(_ => Look(assembly, raise: true));
    }

    private static void Sweep()
    {
        if (!ReadyEvent.IsSet)
            return;
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                Look(assembly, raise: true);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"[integrity] sweep failed: {ex.Message}");
        }
    }

    private static void Look(Assembly assembly, bool raise)
    {
        // Loaded before the list was read: the first look's own pass over every assembly comes after, and takes it.
        if (!_listRead)
            return;
        lock (Gate)
        {
            if (Seen.TryGetValue(assembly, out _))
                return;
            Seen.Add(assembly, Gate);
            _checked++;
        }

        string? finding;
        try
        {
            finding = Judge(assembly);
        }
        catch (Exception ex)
        {
            finding = $"{SafeName(assembly)}|unreadable {ex.GetType().Name}";
        }
        if (finding != null)
            Add(SafeName(assembly), finding, raise && ReadyEvent.IsSet);
    }

    // Null when the assembly is what this build ships, else "name|evidence" where the evidence is the file's hash,
    // or "image" for one loaded from memory with no file behind it.
    private static string? Judge(Assembly assembly)
    {
        if (assembly.IsDynamic)
            return null;

        string name = SafeName(assembly);
        bool patcher = IsPatcher(name);
        string location = string.Empty;
        try
        {
            location = assembly.Location;
        }
        catch
        {
            location = string.Empty;
        }

        switch (_mode)
        {
            case IntegrityMode.Release:
            {
                ReleaseManifest? manifest;
                lock (Gate)
                    manifest = _manifest;
                // Without a usable list the verdict already says so; listing every file on top would only make two
                // copies of the same build disagree over which assemblies each happened to load first.
                if (manifest == null)
                    return patcher ? $"{name}|{Evidence(location)}" : null;
                if (location.Length == 0)
                    return $"{name}|image";
                var hash = HashFile(location);
                if (hash == null)
                    return $"{name}|unreadable file";
                return manifest.Allows(Path.GetFileName(location), hash) ? null : $"{name}|{hash}";
            }
            default:
                return patcher ? $"{name}|{Evidence(location)}" : null;
        }
    }

    private static void Add(string name, string finding, bool raise)
    {
        lock (Gate)
        {
            if (!Findings.Add(finding))
                return;
            Names.Add(name);
            _revision++;
        }
        LumoraLogger.Warn($"[integrity] not part of this build: {finding}");
        if (!raise)
            return;
        try { Changed?.Invoke(); }
        catch (Exception ex) { LumoraLogger.Warn($"[integrity] change handler threw: {ex.Message}"); }
    }

    private static bool IsPatcher(string name)
    {
        foreach (var patcher in Patchers)
        {
            if (name.StartsWith(patcher, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // A file's hash; an image loaded from memory goes by its name alone, since patchers build those at run time with
    // a fresh module id every launch (two games with the same mod would never match otherwise), and whatever built it
    // was loaded from a file that is judged by its own hash.
    private static string Evidence(string location)
        => (location.Length > 0 ? HashFile(location) : null) ?? "image";

    private static string SafeName(Assembly assembly)
    {
        try { return assembly.GetName().Name ?? "unnamed"; }
        catch { return "unnamed"; }
    }

    private static string? HashFile(string path)
    {
        lock (FileHashes)
        {
            if (FileHashes.TryGetValue(path, out var known))
                return known;
        }
        string? hash;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1 << 16, FileOptions.SequentialScan);
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            hash = null;
        }
        lock (FileHashes)
            FileHashes[path] = hash;
        return hash;
    }
}
