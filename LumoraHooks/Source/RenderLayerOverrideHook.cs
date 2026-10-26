// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Components;

namespace Lumora.Godot.Hooks;

[ImplementableHook(typeof(RenderLayerOverride))]
public class RenderLayerOverrideHook : ComponentHook<RenderLayerOverride>
{
    public static IHook<RenderLayerOverride> Constructor() => new RenderLayerOverrideHook();

    protected override bool ReadsNodePose => false;

    public override void ApplyChanges()
    {
        if (!Owner.Enabled)
            return;

        uint layer = (uint)Owner.Layer.Value;
        if (layer == 0)
            return;
        layer = Helpers.RenderHelper.ForWorld(Owner.World, layer);

        var slotNode = (Owner.Slot.Hook as SlotHook)?.GeneratedNode3D;
        if (slotNode == null || !GodotObject.IsInstanceValid(slotNode))
            return;

        ApplyTo(slotNode, layer);
    }

    private static void ApplyTo(Node3D root, uint layer)
    {
        if (root is VisualInstance3D visual && visual.Layers != layer)
            visual.Layers = layer;

        foreach (var child in root.GetChildren())
        {
            if (child is Node3D child3D)
                ApplyTo(child3D, layer);
        }
    }
}
