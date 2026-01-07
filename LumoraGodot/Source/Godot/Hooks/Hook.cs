using Lumora.Core;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Base class for all Godot hooks.
/// Base platform hook for Godot.
///
/// Pattern: LumoraCore Component (data) → Hook (bridge) → Godot Node/Resource (implementation)
/// </summary>
public abstract class Hook<D> : IHook<D> where D : IImplementable
{
    /// <summary>
    /// The component that owns this hook.
    /// </summary>
    public D Owner { get; private set; }

    /// <summary>
    /// The world this hook belongs to.
    /// </summary>
    protected World World => Owner?.World;

    /// <summary>
    /// Explicit interface implementation for non-generic IHook.
    /// </summary>
    IImplementable IHook.Owner => Owner;

    /// <summary>
    /// Assign the owner component to this hook.
    /// </summary>
    public void AssignOwner(IImplementable owner)
    {
        Owner = (D)owner;
    }

    /// <summary>
    /// Remove the owner from this hook.
    /// </summary>
    public void RemoveOwner()
    {
        Owner = default(D);
    }

    /// <summary>
    /// Initialize the hook and create Godot resources.
    /// Override this to set up Godot nodes.
    /// </summary>
    public abstract void Initialize();

    /// <summary>
    /// Apply changes from the owner component to the Godot implementation.
    /// Override this to sync component properties to Godot.
    /// </summary>
    public abstract void ApplyChanges();

    /// <summary>
    /// Destroy the hook and clean up Godot resources.
    /// Override this to free Godot nodes.
    /// </summary>
    public abstract void Destroy(bool destroyingWorld);
}
