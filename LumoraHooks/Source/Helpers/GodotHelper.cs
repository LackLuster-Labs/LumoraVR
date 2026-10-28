// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Source.Godot.Extensions;
using Lumora.Godot.Hooks;

namespace Lumora.Godot.Helpers;

public static class GodotHelper
{
    public static Node3D GetGeneratedNode3D(this Slot slot, bool forceGenerate = false)
    {
        if (slot == null)
            return null!;

        var slotHook = slot.Hook as SlotHook;
        if (slotHook == null)
            return null!;

        // Asked for so something can be hung under it, which then follows the node's pose.
        if (forceGenerate)
        {
            var node = slotHook.ForceGetNode3D();
            slotHook.HoldPoseForAttachments();
            return node;
        }

        return slotHook.GeneratedNode3D;
    }

    public static void ConvertSlots(System.Collections.Generic.List<Slot> slots, System.Collections.Generic.List<Node3D> nodes)
    {
        if (slots == null || nodes == null)
            return;

        foreach (var slot in slots)
        {
            var node = slot.GetGeneratedNode3D();
            if (node != null)
                nodes.Add(node);
        }
    }
}

