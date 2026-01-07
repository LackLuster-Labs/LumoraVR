namespace Lumora.Core.Assets;

/// <summary>
/// Material types supported by the system.
/// Maps to shader configurations in the Godot hook layer.
/// </summary>
public enum MaterialType
{
    /// <summary>
    /// Physically-Based Rendering with metallic workflow.
    /// Uses StandardMaterial3D in Godot.
    /// </summary>
    PBS_Metallic,

    /// <summary>
    /// Simple unlit material - no lighting calculations.
    /// Uses ShaderMaterial with custom shader in Godot.
    /// </summary>
    Unlit,

    /// <summary>
    /// Custom shader material - uses user-provided .gdshader file.
    /// </summary>
    Custom
}

/// <summary>
/// Blend modes for material transparency.
/// </summary>
public enum BlendMode
{
    /// <summary>
    /// Fully opaque - no transparency.
    /// </summary>
    Opaque,

    /// <summary>
    /// Alpha cutout - pixels are either fully opaque or fully transparent.
    /// Uses alpha threshold (AlphaCutoff).
    /// </summary>
    Cutout,

    /// <summary>
    /// Alpha blending - smooth transparency.
    /// </summary>
    Transparent,

    /// <summary>
    /// Additive blending - adds to background color.
    /// Used for glow effects.
    /// </summary>
    Additive
}

/// <summary>
/// Face culling modes for materials.
/// </summary>
public enum Culling
{
    /// <summary>
    /// Cull back faces (default) - only front faces visible.
    /// </summary>
    Back,

    /// <summary>
    /// Cull front faces - only back faces visible.
    /// </summary>
    Front,

    /// <summary>
    /// No culling - both sides visible (double-sided).
    /// </summary>
    None
}
