using Godot;
using Lumora.Core;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// World hook for Godot - creates and manages world root node.
/// Platform world hook for Godot.
/// </summary>
public class WorldHook : IWorldHook
{
    public World Owner { get; private set; }

    public Node3D WorldRoot { get; private set; }

    public static WorldHook Constructor()
    {
        return new WorldHook();
    }

    public void Initialize(World owner)
    {
        Owner = owner;

        // Get WorldManager hook to parent under
        var worldManagerHook = Owner.WorldManager.Hook as WorldManagerHook;
        // Create world root node
        WorldRoot = new Node3D();
        WorldRoot.Name = $"World_{Owner.WorldName.Value}";
        WorldRoot.Visible = false; // Start inactive

        // Parent under WorldManager root
        if (worldManagerHook?.Root != null)
        {
            worldManagerHook.Root.AddChild(WorldRoot);
        }
        else
        {
            GD.Print($"WorldHook.Initialize: WARNING - Could not parent WorldRoot (WorldManagerHook or Root is null)");
        }

        // Reset transform
        WorldRoot.Position = Vector3.Zero;
        WorldRoot.Rotation = Vector3.Zero;
        WorldRoot.Scale = Vector3.One;

        // Store reference in World for hooks to access
        Owner.GodotSceneRoot = WorldRoot;

        // Reparent any existing slot Node3Ds that were created before world root existed
        ReparentExistingSlots(Owner.RootSlot);
    }

    /// <summary>
    /// Recursively reparent existing slot Node3Ds to the world root.
    /// Called after WorldRoot is created to fix slots that were orphaned.
    /// </summary>
    private void ReparentExistingSlots(Lumora.Core.Slot slot)
    {
        if (slot == null) return;

        // If this slot has a hook with a generated Node3D, reparent it
        if (slot.Hook is SlotHook slotHook && slotHook.GeneratedNode3D != null)
        {
            Node3D node3D = slotHook.GeneratedNode3D;
            // For root slot, add directly to WorldRoot
            if (slot.Parent == null || slot.IsRootSlot)
            {
                // If node is orphaned (no parent), add it. Otherwise reparent it.
                if (node3D.GetParent() == null)
                {
                    WorldRoot.AddChild(node3D);
                    GD.Print($"WorldHook: Added orphaned root slot '{slot.SlotName.Value}' to WorldRoot");
                }
                else
                {
                    node3D.Reparent(WorldRoot, false);
                    GD.Print($"WorldHook: Reparented root slot '{slot.SlotName.Value}' to WorldRoot");
                }
            }
            else
            {
                // For child slots, ensure parent has its Node3D first
                if (slot.Parent.Hook is SlotHook parentHook)
                {
                    Node3D parentNode3D = parentHook.GeneratedNode3D;
                    if (parentNode3D == null)
                    {
                        // Parent doesn't have a Node3D yet, create it
                        parentNode3D = parentHook.RequestNode3D();
                    }

                    // If node is orphaned (no parent), add it. Otherwise reparent it.
                    if (node3D.GetParent() == null)
                    {
                        parentNode3D.AddChild(node3D);
                        GD.Print($"WorldHook: Added orphaned slot '{slot.SlotName.Value}' to parent '{slot.Parent.SlotName.Value}'");
                    }
                    else
                    {
                        node3D.Reparent(parentNode3D, false);
                        GD.Print($"WorldHook: Reparented slot '{slot.SlotName.Value}' to parent '{slot.Parent.SlotName.Value}'");
                    }
                }
            }
        }

        // Recursively process children
        foreach (var child in slot.Children)
        {
            ReparentExistingSlots(child);
        }
    }

    public void ChangeFocus(World.WorldFocus focus)
    {
        switch (focus)
        {
            case World.WorldFocus.Focused:
            case World.WorldFocus.Overlay:
                WorldRoot.Visible = true;
                break;

            case World.WorldFocus.PrivateOverlay:
                WorldRoot.Visible = true;
                // TODO: Set layer recursively for private rendering
                // SetLayerRecursively(WorldRoot, RenderHelper.PRIVATE_LAYER);
                break;

            case World.WorldFocus.Background:
                WorldRoot.Visible = false;
                break;
        }
    }

    // TODO: Implement layer system when needed
    // private static void SetLayerRecursively(Node3D node, int layer)
    // {
    //     node.SetLayer(layer);
    //     foreach (Node child in node.GetChildren())
    //     {
    //         if (child is Node3D node3D)
    //             SetLayerRecursively(node3D, layer);
    //     }
    // }

    public void Destroy()
    {
        if (WorldRoot != null && GodotObject.IsInstanceValid(WorldRoot))
        {
            WorldRoot.QueueFree();
        }

        WorldRoot = null;
        Owner = null;
    }
}
