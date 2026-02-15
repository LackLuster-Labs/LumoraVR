using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lumora.Core.Logging;

namespace Lumora.Core.Components.Gizmos;

/// <summary>
/// Registry for gizmo types. Maps component types to their associated gizmo types.
/// </summary>
public static class GizmoRegistry
{
    private static readonly Dictionary<Type, Type> _gizmoTypes = new();
    private static readonly Dictionary<Slot, IGizmo> _activeGizmos = new();
    private static bool _initialized = false;

    /// <summary>
    /// Initialize the registry by scanning assemblies for GizmoForComponent attributes.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        _gizmoTypes.Clear();

        // Scan all loaded assemblies for gizmo types
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                ScanAssembly(assembly);
            }
            catch (Exception ex)
            {
                Logger.Warn($"GizmoRegistry: Failed to scan assembly {assembly.FullName}: {ex.Message}");
            }
        }

        _initialized = true;
        Logger.Log($"GizmoRegistry: Initialized with {_gizmoTypes.Count} gizmo types");
    }

    private static void ScanAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            var attr = type.GetCustomAttribute<GizmoForComponentAttribute>();
            if (attr != null)
            {
                RegisterGizmo(attr.ComponentType, type);
            }
        }
    }

    /// <summary>
    /// Register a gizmo type for a component type.
    /// </summary>
    public static void RegisterGizmo(Type componentType, Type gizmoType)
    {
        if (componentType == null)
            throw new ArgumentNullException(nameof(componentType));
        if (gizmoType == null)
            throw new ArgumentNullException(nameof(gizmoType));

        if (_gizmoTypes.ContainsKey(componentType))
        {
            Logger.Warn($"GizmoRegistry: Replacing gizmo for {componentType.Name} with {gizmoType.Name}");
        }

        _gizmoTypes[componentType] = gizmoType;
        Logger.Log($"GizmoRegistry: Registered {gizmoType.Name} for {componentType.Name}");
    }

    /// <summary>
    /// Get the gizmo type for a component type.
    /// </summary>
    public static Type GetGizmoType(Type componentType)
    {
        if (!_initialized) Initialize();

        if (_gizmoTypes.TryGetValue(componentType, out var gizmoType))
            return gizmoType;

        // Check base types
        var baseType = componentType.BaseType;
        while (baseType != null && baseType != typeof(object))
        {
            if (_gizmoTypes.TryGetValue(baseType, out gizmoType))
                return gizmoType;
            baseType = baseType.BaseType;
        }

        return null;
    }

    /// <summary>
    /// Get all registered gizmo types.
    /// </summary>
    public static IEnumerable<(Type ComponentType, Type GizmoType)> GetAllRegistered()
    {
        if (!_initialized) Initialize();
        return _gizmoTypes.Select(kvp => (kvp.Key, kvp.Value));
    }

    /// <summary>
    /// Track an active gizmo for a slot.
    /// </summary>
    public static void TrackGizmo(Slot slot, IGizmo gizmo)
    {
        if (slot == null) return;
        _activeGizmos[slot] = gizmo;
    }

    /// <summary>
    /// Remove tracking for a slot's gizmo.
    /// </summary>
    public static void UntrackGizmo(Slot slot)
    {
        if (slot == null) return;
        _activeGizmos.Remove(slot);
    }

    /// <summary>
    /// Get the active gizmo for a slot, if any.
    /// </summary>
    public static IGizmo GetGizmoForSlot(Slot slot)
    {
        if (slot == null) return null;
        return _activeGizmos.TryGetValue(slot, out var gizmo) ? gizmo : null;
    }

    /// <summary>
    /// Check if a slot has an active gizmo.
    /// </summary>
    public static bool HasGizmo(Slot slot)
    {
        return slot != null && _activeGizmos.ContainsKey(slot);
    }
}
