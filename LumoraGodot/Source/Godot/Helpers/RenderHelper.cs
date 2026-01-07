using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core;

namespace Aquamarine.Godot.Helpers;

/// <summary>
/// Rendering helper utilities for layer management and camera registration.
/// </summary>
public static class RenderHelper
{
    /// <summary>
    /// Callback to register cameras with rendering system.
    /// </summary>
    public static Action<Camera3D> RegisterCamera;

    // Layer names (Godot render layer system, but we can use these as constants)
    public const string PRIVATE_LAYER = "Private";
    public const string TEMP_LAYER = "Temp";
    public const string HIDDEN_LAYER = "Hidden";
    public const string OVERLAY_LAYER = "Overlay";

    // TODO: Godot uses VisualServer render layers (1-20)
    // These would need to be configured in project settings
    // public static int PUBLIC_RENDER_MASK => ~GetLayerMask("Private", "Temp", "Overlay", "Hidden");
    // public static int PRIVATE_RENDER_MASK => ~GetLayerMask("Temp", "Overlay", "Hidden");

    /// <summary>
    /// Set render layer for slot hierarchy.
    /// </summary>
    public static void SetHierarchyLayer(List<Slot> slots, int layer, Dictionary<Node3D, int> previous)
    {
        var nodes = new List<Node3D>();
        GodotHelper.ConvertSlots(slots, nodes);
        SetHierarchyLayer(nodes, layer, previous);
    }

    /// <summary>
    /// Set render layer for node hierarchy.
    /// </summary>
    public static void SetHierarchyLayer(List<Node3D> nodes, int layer, Dictionary<Node3D, int> previous)
    {
        if (nodes == null)
            return;

        foreach (var node in nodes)
        {
            if (node != null && GodotObject.IsInstanceValid(node))
            {
                SetHierarchyLayer(node, layer, previous);
            }
        }
    }

    /// <summary>
    /// Restore render layers from previous state.
    /// </summary>
    public static void RestoreHierarchyLayer(List<Node3D> nodes, Dictionary<Node3D, int> previous)
    {
        if (nodes == null)
            return;

        foreach (var node in nodes)
        {
            if (node != null && GodotObject.IsInstanceValid(node))
            {
                RestoreHierarchyLayer(node, previous);
            }
        }
    }

    /// <summary>
    /// Set render layer for single node and children.
    /// </summary>
    public static void SetHierarchyLayer(Node3D root, int layer, Dictionary<Node3D, int> previous)
    {
        // TODO: Godot layer system implementation
        // In Godot, layers are set via VisualInstance3D.Layers property
        // if (root is VisualInstance3D visual)
        // {
        //     if (!previous.ContainsKey(root) && visual.Layers != layer)
        //     {
        //         previous.Add(root, (int)visual.Layers);
        //         visual.Layers = (uint)layer;
        //     }
        // }

        // Recurse to children
        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
            {
                SetHierarchyLayer(child3D, layer, previous);
            }
        }
    }

    /// <summary>
    /// Restore render layer for single node and children.
    /// </summary>
    public static void RestoreHierarchyLayer(Node3D root, Dictionary<Node3D, int> previous)
    {
        // TODO: Godot layer system implementation
        // if (previous.TryGetValue(root, out int previousLayer))
        // {
        //     if (root is VisualInstance3D visual && visual.Layers == previousLayer)
        //         return;
        //
        //     if (root is VisualInstance3D vis)
        //         vis.Layers = (uint)previousLayer;
        // }

        // Recurse to children
        foreach (Node child in root.GetChildren())
        {
            if (child is Node3D child3D)
            {
                RestoreHierarchyLayer(child3D, previous);
            }
        }
    }

    /// <summary>
    /// Restore all layers from dictionary.
    /// </summary>
    public static void RestoreLayers(Dictionary<Node3D, int> previous)
    {
        foreach (var pair in previous)
        {
            // TODO: Godot layer system implementation
            // if (pair.Key is VisualInstance3D visual)
            //     visual.Layers = (uint)pair.Value;
        }
    }
}
