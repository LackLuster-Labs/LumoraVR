// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;

namespace Lumora.Godot.Hooks;

public abstract class ComponentHook<D> : ComponentHook<D, IHook> where D : ImplementableComponent<IHook>
{
}

// Automatically requests a Node3D from the Slot when initialized. Component hooks can attach
// Godot child nodes to attachedNode.
public abstract class ComponentHook<D, C> : Hook<D> where D : ImplementableComponent<C> where C : class, IHook
{
    protected SlotHook slotHook { get; private set; } = null!;

    // The slot's Node3D, shared by every component hook on the slot.
    protected Node3D attachedNode { get; private set; } = null!;

    // Whether something this hook hangs under the node follows the node's pose. A hook that places what it makes
    // from the slot itself, or makes nothing that is placed, says no, and a slot only such hooks sit on leaves its
    // node unmoved (see SlotHook).
    protected virtual bool ReadsNodePose => true;

    public override void Initialize()
    {
        slotHook = (SlotHook)Owner.Slot.Hook;
        attachedNode = slotHook.RequestNode3D(ReadsNodePose);
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (slotHook != null && !destroyingWorld)
        {
            slotHook.FreeNode3D(ReadsNodePose);
        }
        slotHook = null!;
        attachedNode = null!;
    }
}

