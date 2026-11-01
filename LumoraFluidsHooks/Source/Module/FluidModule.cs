// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading;
using Lumora.Core;
using Lumora.Core.Localization;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids.Hooks;

// The module's boot, called by the game's module initializer (FluidsLink). In an exported game that initializer
// runs inside Godot's own .NET entry point, before Godot's function pointers are set, so a Godot call here would be
// an access violation on every launch; and the data folder is not pinned yet, so it logs nothing either. It only
// fills static registries (hook types, Loom enums and nodes, Create New, the locale feed, the GPU seam), which
// also loads both module assemblies before the protocol schema hash is first computed. The Godot half waits for
// the first fluid hook (FluidGodotStart).
//
// A failure here must not take the launch down. It is kept and logged once the engine has its log, at the first
// locale change, which the runner raises at start. -xlinka
public static class FluidModule
{
    private static int _booted;
    private static Exception? _bootError;
    private static bool _reported;

    public static bool Booted => Volatile.Read(ref _booted) != 0 && _bootError == null;

    public static int HookCount { get; private set; }

    public static void Boot()
    {
        if (Interlocked.Exchange(ref _booted, 1) != 0)
            return;
        try
        {
            LocaleManager.Changed += ReportOnce;
            HookCount = World.HookTypes.RegisterFromAssembly(typeof(FluidModule).Assembly);
            FluidModuleData.Register();
            FluidGpu.Current = FluidGpuDevice.Instance;
        }
        catch (Exception ex)
        {
            _bootError = ex;
        }
    }

    private static void ReportOnce()
    {
        if (_reported)
            return;
        _reported = true;
        LocaleManager.Changed -= ReportOnce;
        if (_bootError != null)
            LumoraLogger.Error($"Fluids: module boot failed: {_bootError}");
        foreach (var problem in FluidModuleData.Problems)
            LumoraLogger.Warn($"Fluids: {problem}");
    }
}
