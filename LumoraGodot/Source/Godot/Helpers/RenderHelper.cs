// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core;

namespace Lumora.Godot.Helpers;

/// <summary>
/// Rendering helper utilities for layer management and camera registration.
/// </summary>
public static class RenderHelper
{
    /// <summary>
    /// Callback to register cameras with rendering system.
    /// </summary>
    public static Action<Camera3D> RegisterCamera;

    // Render layer bit assignments. Bit 0 (layer 1) is the default "public" layer.
    // Cameras can mask layers via VisualInstance3D.Layers / Camera3D.CullMask.
    public const int PUBLIC_LAYER = 1 << 0;
    public const int PRIVATE_LAYER = 1 << 1;
    public const int TEMP_LAYER = 1 << 2;
    public const int HIDDEN_LAYER = 1 << 3;
    public const int OVERLAY_LAYER = 1 << 4;

    public const int PUBLIC_RENDER_MASK = ~(PRIVATE_LAYER | TEMP_LAYER | HIDDEN_LAYER | OVERLAY_LAYER);
    public const int PRIVATE_RENDER_MASK = ~(TEMP_LAYER | HIDDEN_LAYER | OVERLAY_LAYER);

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
        if (root is VisualInstance3D visual && visual.Layers != (uint)layer)
        {
            if (!previous.ContainsKey(root))
            {
                previous.Add(root, (int)visual.Layers);
            }
            visual.Layers = (uint)layer;
        }

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
        if (root is VisualInstance3D visual && previous.TryGetValue(root, out int previousLayer))
        {
            visual.Layers = (uint)previousLayer;
        }

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
            if (pair.Key != null && GodotObject.IsInstanceValid(pair.Key) && pair.Key is VisualInstance3D visual)
            {
                visual.Layers = (uint)pair.Value;
            }
        }
    }
}
