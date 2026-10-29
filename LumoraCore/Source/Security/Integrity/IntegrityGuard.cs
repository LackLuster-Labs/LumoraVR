// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Components.Interaction;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Security.Integrity;

// What happens when the integrity verdict changes while the game runs, which only ever means something was loaded
// into it. Every session it is in was joined or hosted under the old build identity: a guest leaves for its home, a
// host sends its guests home and lists the session under the new identity, and the person is told why once. The
// account key goes out again with the new identity, so hosts asking the account service hear about it too. -xlinka
public static class IntegrityGuard
{
    private static readonly TimeSpan NoticeDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Burst = TimeSpan.FromSeconds(1);
    private const string GuestReason = "the host's game files changed while running";

    private static Engine? _engine;
    private static int _reacting;

    public static void Attach(Engine engine)
    {
        bool first = _engine == null;
        _engine = engine;
        if (!first)
            return;
        GameIntegrity.Settled += OnSettled;
        GameIntegrity.Changed += OnChanged;
        if (GameIntegrity.IsReady)
            OnSettled();
    }

    private static void OnSettled()
    {
        LumoraLogger.Log($"[integrity] build identity {BuildIdentity.Hash} (release {BuildIdentity.Release}, "
            + $"{(BuildIdentity.IsModified ? "modified: " + GameIntegrity.Describe() : "clean")})");
        _ = _engine?.CDNClient?.RepublishBuildAsync();
    }

    // A patcher arrives as several assemblies at once (itself, the mod, what it builds in memory): one reaction for the
    // burst, a moment after the first of it.
    private static void OnChanged()
    {
        var engine = _engine;
        if (engine == null || Interlocked.Exchange(ref _reacting, 1) == 1)
            return;
        _ = Task.Delay(Burst).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _reacting, 0);
            React(engine);
        }, TaskScheduler.Default);
    }

    private static void React(Engine engine)
    {
        string what = GameIntegrity.Describe();
        LumoraLogger.Warn($"[integrity] game files changed while running ({what}); build is now {BuildIdentity.Short(BuildIdentity.Hash)}, leaving sessions on the old one");
        _ = engine.CDNClient?.RepublishBuildAsync();
        engine.RunOnUpdate(() => LeaveSessions(engine, what));
    }

    private static void LeaveSessions(Engine engine, string what)
    {
        var manager = engine.WorldManager;
        if (manager == null)
            return;

        List<World> worlds;
        try
        {
            worlds = new List<World>(manager.Worlds);
        }
        catch (InvalidOperationException)
        {
            // the list moved under us; the next change (or the handshake on any new join) still holds the line
            return;
        }

        bool left = false, sentHome = false;
        foreach (var world in worlds)
        {
            var session = world?.Session;
            if (world == null || world.IsDestroyed || session == null || session.IsDisposed)
                continue;
            if (world.IsAuthority)
            {
                sentHome |= world.GetAllUsers().Count > 1;
                world.RunSynchronously(() => SendGuestsHome(world));
            }
            else
            {
                LumoraLogger.Warn($"[integrity] leaving '{world.WorldName.Value}': joined under the old build");
                manager.LeaveWorld(world);
                left = true;
            }
        }

        if (!left && !sentHome)
            return;
        var title = IntegrityLocale.ChangedTitle.Resolve();
        var text = (left ? IntegrityLocale.ChangedLeft(what) : IntegrityLocale.ChangedHosting(what)).Resolve();
        _ = Task.Delay(NoticeDelay).ContinueWith(_ => engine.RunOnUpdate(() =>
        {
            var focused = engine.WorldManager?.FocusedWorld;
            if (focused != null && !focused.IsDestroyed)
                focused.RunSynchronously(() => ToolNotice.Say(focused, title, text));
        }), TaskScheduler.Default);
    }

    private static void SendGuestsHome(World world)
    {
        var session = world.Session;
        if (world.IsDestroyed || session == null || session.IsDisposed)
            return;
        session.RefreshBuild();
        foreach (var user in world.GetAllUsers())
        {
            if (user != null && user != world.LocalUser && !user.IsHost)
                session.Connections.DisconnectUser(user, GuestReason);
        }
    }
}
