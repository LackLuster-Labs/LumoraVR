using System;
using System.Collections.Generic;

namespace Lumora.Core;

/// <summary>
/// Registry mapping component types to hook types.
/// </summary>
public class HookTypeRegistry
{
    private Dictionary<Type, Type> _componentToHook = new Dictionary<Type, Type>();

    /// <summary>
    /// Register a hook type for a component type.
    /// </summary>
    public void Register<TComponent, THook>()
        where TComponent : IImplementable
        where THook : IHook
    {
        Register(typeof(TComponent), typeof(THook));
    }

    /// <summary>
    /// Register a hook type for a component type.
    /// </summary>
    public void Register(Type componentType, Type hookType)
    {
        if (!typeof(IImplementable).IsAssignableFrom(componentType))
        {
            throw new ArgumentException($"Component type {componentType} must implement IImplementable");
        }

        if (!typeof(IHook).IsAssignableFrom(hookType))
        {
            throw new ArgumentException($"Hook type {hookType} must implement IHook");
        }

        _componentToHook[componentType] = hookType;
    }

    /// <summary>
    /// Get the hook type for a component type.
    /// Returns null if no hook is registered.
    /// </summary>
    public Type GetHookType(Type componentType)
    {
        _componentToHook.TryGetValue(componentType, out Type hookType);
        return hookType;
    }

    /// <summary>
    /// Check if a hook is registered for a component type.
    /// </summary>
    public bool HasHook(Type componentType)
    {
        return _componentToHook.ContainsKey(componentType);
    }

    /// <summary>
    /// Clear all registrations.
    /// </summary>
    public void Clear()
    {
        _componentToHook.Clear();
    }
}
