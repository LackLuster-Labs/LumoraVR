// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids;

// Where fluid events meet Loom. Whoever learns of one (the wearer's summary for touches, snaps and stuck limbs, the
// keeper's report for props, the release ring for releases) raises it here with its world; an event node's Listen
// subscribes, filters by its world and source, and unsubscribes in its hearing's Until. Raised on the world thread
// inside that world's update, so a hearing never sees another thread.
//
// A handler that throws is logged once per kind and never stops the others: one broken script must not cost
// everyone else their events. -xlinka
public static class FluidEvents
{
    private static bool _touchedFailed;
    private static bool _snappedFailed;
    private static bool _stuckFailed;
    private static bool _releasedFailed;

    public static event Action<World, FluidTouched>? Touched;

    public static event Action<World, StrandSnapped>? Snapped;

    public static event Action<World, FluidStuck>? Stuck;

    public static event Action<World, FluidReleased>? Released;

    public static void RaiseTouched(World world, in FluidTouched e) => Raise(Touched, world, e, ref _touchedFailed, "touch");

    public static void RaiseSnapped(World world, in StrandSnapped e) => Raise(Snapped, world, e, ref _snappedFailed, "strand snap");

    public static void RaiseStuck(World world, in FluidStuck e) => Raise(Stuck, world, e, ref _stuckFailed, "stuck");

    public static void RaiseReleased(World world, in FluidReleased e) => Raise(Released, world, e, ref _releasedFailed, "release");

    private static void Raise<T>(Action<World, T>? handlers, World world, in T e, ref bool failed, string kind)
    {
        if (handlers == null || world == null || world.IsDestroyed)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<World, T>)handler)(world, e);
            }
            catch (Exception ex)
            {
                if (failed)
                    continue;
                failed = true;
                LumoraLogger.Warn($"Fluids: a {kind} event handler threw: {ex.Message}");
            }
        }
    }
}
