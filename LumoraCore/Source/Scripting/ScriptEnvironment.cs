// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Script;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Scripting;

// The engine's side of Loom's setup: which datamodel types scripts may hold as handles, the node
// registry over the core nodes and the engine's own, and a compile cache keyed by graph hash, so a
// world full of copies of one scripted object compiles it once. Every fresh compile is timed and kept
// with its result, so a diagnostics panel can say what a graph cost to compile on this machine.
//
// Scripts compile on a worker: CompileAsync runs one compile per graph however many hosts ask while it
// runs, and leaves the result in the cache for the world thread to link. The cache lets go of its
// oldest compile when it is full. -xlinka
public static class ScriptEnvironment
{
    private const int MaxCachedCompiles = 256;

    private static readonly object _lock = new();
    private static readonly object _registryLock = new();
    private static readonly Dictionary<string, CompileResult> _compiled = new();
    private static readonly Dictionary<string, ScriptCompileRecord> _records = new();
    private static readonly Queue<string> _order = new();
    private static readonly Dictionary<string, Task<CompileResult>> _running = new();
    private static readonly List<Assembly> _nodeAssemblies = new();
    private static volatile NodeRegistry? _registry;
    private static int _warming;
    private static double _registryScanMs;
    private static int _compiles;
    private static int _hits;
    private static double _compileMs;
    private static double _slowestMs;
    private static ScriptCompileRecord _last;

    // Read once and never changed after, so a worker's compile and the world thread's editor share it.
    public static NodeRegistry Registry
    {
        get
        {
            var registry = _registry;
            if (registry != null)
                return registry;
            lock (_registryLock)
            {
                if (_registry != null)
                    return _registry;

                long start = Stopwatch.GetTimestamp();
                RegisterTypes();
                var assemblies = new List<Assembly> { typeof(ScriptNode).Assembly, typeof(ScriptEnvironment).Assembly };
                assemblies.AddRange(_nodeAssemblies);
                var scanned = NodeRegistry.Scan(assemblies);
                _registryScanMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                foreach (var problem in scanned.Problems)
                    LumoraLogger.Warn($"Loom: node left out: {problem}");
                _registry = scanned;
                return scanned;
            }
        }
    }

    // A module's own nodes, scanned after the two fixed assemblies. The registry is built once and shared by
    // every compile, so this only counts before that build: a later call is left out and named in the log.
    public static void AddNodeAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (_registryLock)
        {
            if (_registry != null)
            {
                LumoraLogger.Warn($"Loom: nodes of {assembly.GetName().Name} came after the registry was built and are left out");
                return;
            }
            if (!_nodeAssemblies.Contains(assembly))
                _nodeAssemblies.Add(assembly);
        }
    }

    // Starts the node scan on a worker, so the first graph a world checks or runs does not wait for it on
    // the world thread.
    public static void WarmUp()
    {
        if (_registry == null && Interlocked.Exchange(ref _warming, 1) == 0)
            Task.Run(() => _ = Registry);
    }

    public static ScriptCompileStats CompileStats
    {
        get
        {
            lock (_lock)
                return new ScriptCompileStats(_compiles, _hits, _compiled.Count, _compileMs, _slowestMs, _last, _registryScanMs);
        }
    }

    // How many compiles are on workers right now.
    public static int Compiling
    {
        get
        {
            lock (_lock)
                return _running.Count;
        }
    }

    // The datamodel types a graph may name. Before the first registry scan, since node ports use them.
    public static void RegisterTypes()
    {
        ScriptTypes.RegisterHandle("slot", typeof(Slot));
        ScriptTypes.RegisterHandle("user", typeof(User));
        ScriptTypes.RegisterHandle("component", typeof(Component));
        ScriptTypes.RegisterGenericHandle("field", typeof(IField<>));
        // Any field at all, for what only watches one: On Value Changed's source.
        ScriptTypes.RegisterHandle("anyfield", typeof(IField));
        ScriptTypes.RegisterHandle("audioclip", typeof(IAssetProvider<AudioClip>));
        ScriptTypes.RegisterHandle("texture", typeof(IAssetProvider<TextureAsset>));
        ScriptTypes.RegisterHandle("material", typeof(IAssetProvider<MaterialAsset>));
        ScriptTypes.RegisterHandle("mesh", typeof(IAssetProvider<MeshDataAsset>));
        ScriptTypes.RegisterHandle("font", typeof(IAssetProvider<FontSet>));
        ScriptTypes.RegisterHandle("animation", typeof(IAssetProvider<AnimationAsset>));
        ScriptTypes.RegisterHandle("cubemap", typeof(IAssetProvider<CubemapAsset>));
        ScriptTypes.RegisterHandle("camera", typeof(Camera));
        ScriptTypes.RegisterHandle("probe", typeof(ReflectionProbe));
        // A reference field, whatever it points at: Reference Target reads it as the type asked for.
        ScriptTypes.RegisterHandle("reference", typeof(ISyncRef));
        // Someone asking to join, as a join check hears them; never a world object.
        ScriptTypes.RegisterHandle("joinrequest", typeof(JoinRequest));
        ScriptTypes.RegisterEnum("httpmethod", typeof(Lumora.Core.Networking.Web.WebMethod));
        ScriptTypes.RegisterEnum("slotAxis", typeof(SlotAxis));
        // What the user and body nodes read that no component keeps in a field. One already known under
        // its name keeps it.
        foreach (var (name, type) in new[]
        {
            ("fingerSegmentType", typeof(Lumora.Core.Input.FingerSegmentType)),
            ("headOutputDevice", typeof(HeadOutputDevice)),
            ("platform", typeof(Platform)),
            ("desktopViewMode", typeof(DesktopViewMode)),
        })
        {
            try
            {
                ScriptTypes.RegisterEnum(name, type);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
            }
        }
        // No component keeps it in a field, so the scan below would not find it.
        ScriptTypes.RegisterEnum("locomotionKind", typeof(LocomotionKind));
        // A live connection a script opened, held like any handle and kept in a Stored Value to send through.
        ScriptTypes.RegisterHandle("websocket", typeof(Lumora.Core.Networking.Web.ScriptWebSocket));
        // A reverb carried as one value, and the two components that keep one.
        ScriptTypes.RegisterStruct("reverbSettings", typeof(Lumora.Audio.ReverbSettings));
        ScriptTypes.RegisterHandle("reverbEffect", typeof(Components.Audio.ReverbEffect));
        ScriptTypes.RegisterHandle("reverbZone", typeof(Components.Audio.ReverbZone));
        // The audio analyser, and the two shapes its spectrum and the Window and Spectrum Of nodes are cut by.
        ScriptTypes.RegisterHandle("audioAnalyzer", typeof(Components.Audio.AudioAnalyzer));
        ScriptTypes.RegisterEnum("windowShape", typeof(Lumora.Audio.WindowShape));
        ScriptTypes.RegisterEnum("frequencyScale", typeof(Lumora.Audio.FrequencyScale));

        // Every enum a component keeps in a field, by its name with the first letter lowered, so Get Field
        // and Set Field reach those fields too. Two enums of one name keep the first.
        foreach (var type in GenericComponentTypes.EnumTypes)
        {
            var name = char.ToLowerInvariant(type.Name[0]) + type.Name[1..];
            try
            {
                ScriptTypes.RegisterEnum(name, type);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
            }
        }
        ScriptAssetEnums.Register();
    }

    public static CompileResult Compile(ScriptGraph graph) => Compile(graph, out _, out _);

    // On the calling thread. Cached says the result came from the cache; the record is the compile that
    // made it either way.
    public static CompileResult Compile(ScriptGraph graph, out ScriptCompileRecord record, out bool cached)
    {
        var hash = graph.ComputeHash();
        if (TryGetCached(hash, out var hit, out record))
        {
            cached = true;
            return hit;
        }
        cached = false;
        return CompileFresh(graph, hash, out record);
    }

    public static bool TryGetCached(string hash, out CompileResult result) => TryGetCached(hash, out result, out _);

    public static bool TryGetCached(string hash, out CompileResult result, out ScriptCompileRecord record)
    {
        lock (_lock)
        {
            if (_compiled.TryGetValue(hash, out result!))
            {
                _hits++;
                record = _records.GetValueOrDefault(hash);
                return true;
            }
        }
        record = default;
        return false;
    }

    // On a worker. The task never faults, and it lands in the cache before it completes, so whoever it
    // wakes finds it there.
    public static Task<CompileResult> CompileAsync(ScriptGraph graph, string? hash = null)
    {
        hash ??= graph.ComputeHash();
        lock (_lock)
        {
            if (_compiled.TryGetValue(hash, out var hit))
            {
                _hits++;
                return Task.FromResult(hit);
            }
            if (_running.TryGetValue(hash, out var running))
                return running;
            var key = hash;
            var task = Task.Run(() => CompileOnWorker(graph, key));
            _running[hash] = task;
            return task;
        }
    }

    private static CompileResult CompileOnWorker(ScriptGraph graph, string hash)
    {
        try
        {
            var result = CompileFresh(graph, hash, out _);
            // What linking will ask for, made here instead of on the world thread, and the module's native
            // code with it.
            foreach (var overload in result.Imports)
                _ = overload.Invoker;
            foreach (var entry in result.Events)
                ScriptContext.WarmCause(entry.PayloadType);
            result.PrepareNative();
            return result;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Loom: a compile could not run: {ex}");
            return CompileResult.Unrun(hash, "the compiler could not run: " + ex.Message);
        }
        finally
        {
            lock (_lock)
                _running.Remove(hash);
        }
    }

    private static CompileResult CompileFresh(ScriptGraph graph, string hash, out ScriptCompileRecord record)
    {
        var registry = Registry;
        long start = Stopwatch.GetTimestamp();
        var result = ScriptCompiler.Compile(graph, registry);
        record = new ScriptCompileRecord(hash, Stopwatch.GetElapsedTime(start).TotalMilliseconds, result.Module?.Length ?? 0,
            graph.Nodes.Count, result.Success, Thread.CurrentThread.IsThreadPoolThread);
        lock (_lock)
        {
            if (!_compiled.ContainsKey(hash))
            {
                while (_compiled.Count >= MaxCachedCompiles && _order.Count > 0)
                {
                    var oldest = _order.Dequeue();
                    _compiled.Remove(oldest);
                    _records.Remove(oldest);
                }
                _order.Enqueue(hash);
            }
            _compiled[hash] = result;
            _records[hash] = record;
            _compiles++;
            _compileMs += record.Milliseconds;
            if (record.Milliseconds > _slowestMs)
                _slowestMs = record.Milliseconds;
            _last = record;
        }
        return result;
    }

    public static bool TryGetCompileRecord(string hash, out ScriptCompileRecord record)
    {
        lock (_lock)
            return _records.TryGetValue(hash, out record);
    }
}
