namespace Lumora.Core.Components;

/// <summary>
/// Marker component for object root detection.
/// Attach to a slot to mark it as the root of a logical object.
/// </summary>
[ComponentCategory("Internal")]
public class ObjectRoot : Component
{
    /// <summary>
    /// Optional display name for this object.
    /// </summary>
    public Sync<string> ObjectName { get; private set; }

    /// <summary>
    /// Whether this object can be grabbed.
    /// </summary>
    public Sync<bool> Grabbable { get; private set; }

    /// <summary>
    /// Whether this object can be duplicated.
    /// </summary>
    public Sync<bool> Duplicatable { get; private set; }

    /// <summary>
    /// Whether this object can be destroyed by users.
    /// </summary>
    public Sync<bool> Destroyable { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();

        ObjectName = new Sync<string>(this, string.Empty);
        Grabbable = new Sync<bool>(this, true);
        Duplicatable = new Sync<bool>(this, true);
        Destroyable = new Sync<bool>(this, true);
    }

    /// <summary>
    /// Get the display name (falls back to slot name).
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(ObjectName.Value) ? Slot.Name.Value : ObjectName.Value;
}
