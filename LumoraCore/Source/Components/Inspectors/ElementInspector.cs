// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components;

// The right inspector for whatever an element is: a material gets the material inspector, anything else the
// scene inspector rooted at its owning slot, with the component it lives on opened. Shared by reference cards
// and by a script node's bound fields. -xlinka
public static class ElementInspector
{
    // The slot an element lives on, and the component when it is one or sits inside one.
    public static (Slot? Slot, Component? Component) OwnerOf(IWorldElement? element)
    {
        while (element != null)
        {
            if (element is Slot slot)
                return (slot, null);
            if (element is Component component)
                return (component.Slot, component);
            element = (element as SyncElement)?.Parent;
        }
        return (null, null);
    }

    public static bool CanOpen(IWorldElement? target)
    {
        var (slot, _) = OwnerOf(target);
        return target != null && !target.IsDestroyed && slot is { IsDestroyed: false };
    }

    public static bool Open(World? world, IWorldElement? target, float3 position, floatQ rotation)
    {
        if (world == null || target == null || target.IsDestroyed)
            return false;

        var (slot, component) = OwnerOf(target);
        if (component is MaterialProvider material && !material.IsDestroyed)
        {
            MaterialInspectorPanel.Spawn(world, material, position, rotation);
            return true;
        }

        if (slot == null || slot.IsDestroyed)
            return false;

        // The component opens once the panel stands, which for a favourite inspector's first fetch is a moment
        // later; false then too, though the panel is on its way.
        var panel = SceneInspectorPanel.OpenOrFocus(world, slot, position, mineOnly: true, opened: shown =>
        {
            if (component is { IsDestroyed: false } && !SceneInspectorPanel.IsExpanded(shown.ExpandedComponents, component.ReferenceID.RawValue))
                shown.ExpandedComponents.Add(component.ReferenceID.RawValue);
        });
        return panel != null;
    }
}
