namespace Lumora.Core.HelioUI;

/// <summary>
/// Base class for HelioUI controller components that manage UI behavior.
/// Controllers don't render directly but coordinate other components.
/// </summary>
[ComponentCategory("HelioUI")]
public abstract class HelioUIController : HelioUIComputeComponent
{
    /// <summary>
    /// Whether this controller is currently active.
    /// </summary>
    public Sync<bool> Active { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();
        Active = new Sync<bool>(this, true);
        Active.OnChanged += OnActiveChanged;
    }

    /// <summary>
    /// Called when the Active state changes.
    /// </summary>
    protected virtual void OnActiveChanged(bool newValue)
    {
        MarkComputeDirty();
    }

    /// <summary>
    /// Check if this controller should process.
    /// </summary>
    protected bool ShouldProcess()
    {
        return Active?.Value ?? true && Enabled.Value;
    }
}
