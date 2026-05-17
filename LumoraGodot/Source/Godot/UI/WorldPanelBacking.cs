// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;

namespace Lumora.Godot.UI;

/// <summary>
/// Shared rendering setup for world-space UI panels that need to hide scene overlays behind them.
/// </summary>
public static class WorldPanelBacking
{
    public const float BackingOffset = -0.004f;
    public const float BackingMargin = 0f;

    public static readonly Color BackingColor = new(0.018f, 0.018f, 0.026f, 1f);

    public static void ConfigureSurfaceMaterial(StandardMaterial3D material)
    {
        material.NoDepthTest = false;
        material.CullMode = BaseMaterial3D.CullModeEnum.Back;
    }

    public static StandardMaterial3D CreateBackingMaterial()
    {
        return new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = BackingColor,
            NoDepthTest = false
        };
    }

    public static MeshInstance3D CreateBackingMesh(string name, QuadMesh mesh, StandardMaterial3D material)
    {
        return new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            Position = new Vector3(0f, 0f, BackingOffset),
            MaterialOverride = material
        };
    }

    public static Vector2 GetBackingSize(Vector2 panelSize)
    {
        return panelSize + new Vector2(BackingMargin, BackingMargin);
    }
}
