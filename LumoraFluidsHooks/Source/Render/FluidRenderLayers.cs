// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Godot.Helpers;

namespace Lumora.Fluids.Hooks;

// Render layers for fluid, walked up the slot chain the way ordinary renderers inherit them: the nearest enabled
// RenderLayerOverride wins, so selective cameras and mirror-only bands treat a fluid like any renderer, and a
// private world keeps it private. The wearer's own head spots go on the local head band instead, which a mirror
// draws and the wearer's eyes do not. -xlinka
public static class FluidRenderLayers
{
    public static uint For(Slot? slot, World? world)
    {
        uint layers = RenderHelper.PUBLIC_LAYER;
        for (var s = slot; s != null; s = s.Parent)
        {
            var layerOverride = s.GetComponent<RenderLayerOverride>();
            if (layerOverride != null && layerOverride.Enabled && layerOverride.Layer.Value != 0)
            {
                layers = (uint)layerOverride.Layer.Value;
                break;
            }
        }
        return RenderHelper.ForWorld(world, layers);
    }

    public static uint WearerHead(World? world) => RenderHelper.ForWorld(world, RenderHelper.LOCAL_HEAD_LAYER);

    public static uint Public(World? world) => RenderHelper.ForWorld(world, RenderHelper.PUBLIC_LAYER);
}
