using System;

namespace Lumora.Core.Components.Gizmos;

/// <summary>
/// Attribute to register a gizmo type for a specific component type.
/// When the component is inspected, this gizmo will be spawned.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public class GizmoForComponentAttribute : Attribute
{
    /// <summary>
    /// The component type this gizmo is for.
    /// </summary>
    public Type ComponentType { get; }

    /// <summary>
    /// Whether this gizmo should spawn automatically when dev mode is enabled.
    /// </summary>
    public bool SpawnOnDevMode { get; set; } = false;

    /// <summary>
    /// Create a new GizmoForComponent attribute.
    /// </summary>
    /// <param name="componentType">The component type this gizmo handles.</param>
    public GizmoForComponentAttribute(Type componentType)
    {
        ComponentType = componentType ?? throw new ArgumentNullException(nameof(componentType));
    }
}
