// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// A row label copies the whole field. Individual editors copy their own leaf values. -xlinka
[ComponentCategory("Utility/Inspectors")]
public class ValueProxySource : Component, IProxySource
{
    public readonly SyncRef<IWorldElement> Target;

    public ValueProxySource()
    {
        Target = new SyncRef<IWorldElement>(this);
    }

    public IGrabbable? TryCreateProxy(Grabber grabber, in float3 spawnPoint)
    {
        if (World == null || Target.Target is not IField field || field.IsDestroyed)
            return null;
        return ValueProxy.Spawn(World, field, spawnPoint);
    }
}
