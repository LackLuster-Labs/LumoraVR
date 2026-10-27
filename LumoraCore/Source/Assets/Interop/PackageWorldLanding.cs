// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Logging;
using Lumora.Core.Math;

namespace Lumora.Core.Assets.Interop;

// Where a package lands. An object's root position is wherever it stood in the world it was packed from and
// means nothing here, so an object lands on the import anchor. A world packed whole is different: its root
// position is part of the world, the offset that puts the world's origin, where its people arrive, where its
// author meant it. After Glow's root sits 59.3 m down so its floor meets the origin; dropped, the floor stood
// 59 m over the anchor. A world keeps it, and its origin goes on the floor of whoever imported it, under the
// dialog, rather than at the height the dialog floats at. -xlinka
internal static class PackageWorldLanding
{
    // A package that brings people in or sets the sky is a world.
    private static readonly HashSet<string> WorldTypes = new(StringComparer.Ordinal)
    {
        "SimpleUserSpawn",
        "Skybox",
        "AmbientLightSH2",
    };

    public static bool IsWorld(IReadOnlyList<string> typeNames)
    {
        foreach (var name in typeNames)
        {
            if (WorldTypes.Contains(PackageTypeMap.SimpleName(name)))
                return true;
        }
        return false;
    }

    public static void Land(Slot root, Slot anchor)
    {
        var feet = anchor.World?.LocalUser?.Root?.Slot.GlobalPosition;
        if (feet is { } floor && !anchor.IsDestroyed)
        {
            var at = anchor.GlobalPosition;
            anchor.GlobalPosition = new float3(at.x, floor.y, at.z);
        }
        Logger.Debug($"Package import: a world, so its root keeps {root.LocalPosition.Value} and its origin lands on the floor under the anchor.");

        // A world's walls and floors can hide what stands behind them (StaticOccluders). It comes switched off: the
        // test cost a four-thread machine 0.45 ms a frame in After Glow for no gain, and gained nothing on a desktop.
        if (!root.IsDestroyed && root.GetComponent<Components.StaticOccluders>() == null)
            root.AttachComponent<Components.StaticOccluders>().Enabled.Value = false;
    }
}
