namespace Lumora.Core.Input;

/// <summary>
/// Interface for components that need to receive input updates before/after the main input processing.
/// Components implementing this interface will have their BeforeInputUpdate called before regular updates,
/// and AfterInputUpdate called after - allowing them to read raw input data before other components.
/// </summary>
public interface IInputUpdateReceiver
{
    /// <summary>
    /// Called before the main input update cycle.
    /// Use this to read tracking data and update slot transforms before other components read them.
    /// </summary>
    void BeforeInputUpdate();

    /// <summary>
    /// Called after the main input update cycle.
    /// Use this for cleanup or post-processing of input data.
    /// </summary>
    void AfterInputUpdate();
}
