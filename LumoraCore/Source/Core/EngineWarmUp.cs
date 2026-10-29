// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Diagnostics;
using System.Threading;
using Lumora.Core.Scripting;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core;

// What the first world open of a session otherwise does on the world thread, done once on a low priority
// thread while the engine comes up: the Loom and shader node scans and every worker type's member table.
// Each is a reflection read into a cache built under its own lock and never changed after, and a world that
// gets there first just builds its own entry. Measured on the shader gallery: 248 ms of node scans and the
// member tables of every type it uses left its first open. -xlinka
public static class EngineWarmUp
{
    private static int _started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        var thread = new Thread(Run)
        {
            Name = "Lumora warm-up",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    private static void Run()
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            _ = ScriptEnvironment.Registry;
            _ = ShaderGraphLanguage.Instance.Registry;
            int types = WorkerInitializer.WarmUp(typeof(Worker).Assembly);
            LumoraLogger.Log($"Warm-up: node registries and {types} worker types ready in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms");
        }
        catch (Exception ex)
        {
            // A static constructor or a reflection call fails as "Exception has been thrown by the target of an
            // invocation", which names nothing; the inner one says what broke and where (seen on a Frame 10-30).
            var cause = ex;
            while (cause is System.Reflection.TargetInvocationException or TypeInitializationException && cause.InnerException != null)
                cause = cause.InnerException;
            LumoraLogger.Warn($"Warm-up stopped early: {cause.GetType().Name}: {cause.Message}\n{cause.StackTrace}");
        }
    }
}
