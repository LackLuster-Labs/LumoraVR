namespace Lumora.Core;

/// <summary>
/// Interface for components that can be implemented by engine-specific hooks.
/// </summary>
public interface IImplementable : IWorldElement
{
    /// <summary>
    /// The hook that implements this component in the engine.
    /// </summary>
    IHook Hook { get; }

    /// <summary>
    /// The world this component belongs to.
    /// </summary>
    World World { get; }

    /// <summary>
    /// The slot this component is attached to.
    /// </summary>
    Slot Slot { get; }
}

/// <summary>
/// Generic implementable interface with typed hook.
/// </summary>
public interface IImplementable<C> : IImplementable where C : class, IHook
{
    /// <summary>
    /// The typed hook that implements this component.
    /// </summary>
    new C Hook { get; }
}
