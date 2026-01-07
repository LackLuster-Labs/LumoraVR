using Godot;
using Lumora.Core;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Base class for component hooks (non-generic).
/// Platform component hook for Godot.
/// </summary>
public abstract class ComponentHook<D> : ComponentHook<D, IHook> where D : ImplementableComponent<IHook>
{
}

/// <summary>
/// Base class for component hooks (generic).
/// Generic platform component hook.
///
/// Automatically requests a Node3D from the Slot when initialized.
/// Component hooks can attach Godot child nodes to attachedNode.
/// </summary>
public abstract class ComponentHook<D, C> : Hook<D> where D : ImplementableComponent<C> where C : class, IHook
{
    /// <summary>
    /// The SlotHook for the owner's slot.
    /// </summary>
    protected SlotHook slotHook { get; private set; }

    /// <summary>
    /// The Node3D attached to the slot (Godot equivalent of GameObject).
    /// Component hooks can create child nodes under this node.
    /// </summary>
    protected Node3D attachedNode { get; private set; }

    /// <summary>
    /// Initialize the component hook.
    /// Requests a Node3D from the slot hook.
    /// </summary>
    public override void Initialize()
    {
        slotHook = (SlotHook)Owner.Slot.Hook;
        attachedNode = slotHook.RequestNode3D();
    }

    /// <summary>
    /// Destroy the component hook.
    /// Frees the Node3D from the slot hook.
    /// </summary>
    public override void Destroy(bool destroyingWorld)
    {
        if (slotHook != null && !destroyingWorld)
        {
            slotHook.FreeNode3D();
        }
        slotHook = null;
        attachedNode = null;
    }
}
