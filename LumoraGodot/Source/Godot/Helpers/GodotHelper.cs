using Godot;
using Lumora.Core;
using Lumora.Core.Math;
using Aquamarine.Source.Godot.Extensions;
using Aquamarine.Godot.Hooks;

namespace Aquamarine.Godot.Helpers;

/// <summary>
/// Helper utilities for Godot interop.
/// Godot platform helper utilities.
/// </summary>
public static class GodotHelper
{
    /// <summary>
    /// Get the generated Godot Node3D for a slot, optionally forcing generation.
    /// </summary>
    public static Node3D GetGeneratedNode3D(this Slot slot, bool forceGenerate = false)
    {
        if (slot == null)
            return null;

        var slotHook = slot.Hook as SlotHook;
        if (slotHook == null)
            return null;

        if (forceGenerate)
            return slotHook.ForceGetNode3D();

        return slotHook.GeneratedNode3D;
    }

    /// <summary>
    /// Convert list of slots to list of GameObjects.
    /// Slot hierarchy conversion utilities.
    /// </summary>
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

    /// <summary>
    /// Get Godot Texture2D from LumoraCore ITexture2D.
    /// TODO: Implement when texture asset system is ready.
    /// </summary>
    // public static Texture2D GetGodot(this ITexture2D texture)
    // {
    //     if (texture == null)
    //         return null;
    //
    //     var hook = texture.Hook as TextureHook;
    //     return hook?.GodotTexture;
    // }

    /// <summary>
    /// Get Godot Mesh from LumoraCore IMesh.
    /// TODO: Implement when mesh asset system is ready.
    /// </summary>
    // public static Mesh GetGodot(this IMesh mesh)
    // {
    //     if (mesh == null)
    //         return null;
    //
    //     var hook = mesh.Hook as MeshHook;
    //     return hook?.GodotMesh;
    // }
}
