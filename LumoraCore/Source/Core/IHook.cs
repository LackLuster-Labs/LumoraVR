using System;

namespace Lumora.Core;

/// <summary>
/// Base interface for all hooks that bridge components to engine implementations.
/// </summary>
public interface IHook
{
    /// <summary>
    /// The component that owns this hook.
    /// </summary>
    IImplementable Owner { get; }

    /// <summary>
    /// Assign an owner to this hook.
    /// Called when the hook is created.
    /// </summary>
    void AssignOwner(IImplementable owner);

    /// <summary>
    /// Remove the owner from this hook.
    /// Called when the hook is destroyed.
    /// </summary>
    void RemoveOwner();

    /// <summary>
    /// Initialize the hook.
    /// Called after AssignOwner, when the component is starting up.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Apply changes from the owner component to the engine implementation.
    /// Called every frame when the component has changes.
    /// </summary>
    void ApplyChanges();

    /// <summary>
    /// Destroy the hook and clean up engine resources.
    /// </summary>
    /// <param name="destroyingWorld">True if the entire world is being destroyed (skip cleanup in this case)</param>
    void Destroy(bool destroyingWorld);
}

/// <summary>
/// Generic hook interface with typed owner.
/// </summary>
public interface IHook<T> : IHook, IDisposable where T : IImplementable
{
    /// <summary>
    /// The typed component that owns this hook.
    /// </summary>
    new T Owner { get; }
}
