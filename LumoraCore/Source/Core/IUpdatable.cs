namespace Lumora.Core;

/// <summary>
/// Interface for components that participate in the structured update loop.
/// Provides deterministic update ordering for consistent behavior.
/// </summary>
public interface IUpdatable : IWorldElement
{
    /// <summary>
    /// Whether the component has started.
    /// </summary>
    bool IsStarted { get; }

    /// <summary>
    /// Whether the component has been destroyed.
    /// </summary>
    bool IsDestroyed { get; }

    /// <summary>
    /// Whether the component has pending changes to apply.
    /// </summary>
    bool IsChangeDirty { get; }

    /// <summary>
    /// The last change update index when changes were applied.
    /// </summary>
    int LastChangeUpdateIndex { get; }

    /// <summary>
    /// Update order for this component. Lower values run first.
    /// Standard values:
    ///   -1000000 = TrackedDevicePositioner (runs first for tracking input)
    ///   -5000 = VRIK (runs early for IK solving)
    ///   0 = Default
    ///   1000000 = Late update components
    /// </summary>
    int UpdateOrder { get; }

    /// <summary>
    /// Called during the startup phase before first update.
    /// </summary>
    void InternalRunStartup();

    /// <summary>
    /// Called during the main update phase.
    /// </summary>
    void InternalRunUpdate();

    /// <summary>
    /// Called during the change application phase to process pending changes.
    /// </summary>
    void InternalRunApplyChanges(int changeUpdateIndex);

    /// <summary>
    /// Called during the destruction phase.
    /// </summary>
    void InternalRunDestruction();
}

/// <summary>
/// Attribute to specify default update order for a component type.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Class, Inherited = true)]
public class DefaultUpdateOrderAttribute : System.Attribute
{
    public int Order { get; }

    public DefaultUpdateOrderAttribute(int order)
    {
        Order = order;
    }
}
