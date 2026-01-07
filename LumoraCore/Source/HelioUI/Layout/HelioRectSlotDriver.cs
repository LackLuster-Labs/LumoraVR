using Lumora.Core.Math;

namespace Lumora.Core.HelioUI;

/// <summary>
/// Drives a HelioRectTransform's position and size from a Slot's local transform.
/// Useful for creating UI elements that follow 3D slot positions in UI space.
/// </summary>
[ComponentCategory("HelioUI")]
public class HelioRectSlotDriver : Component
{
    /// <summary>
    /// The rect transform to drive.
    /// </summary>
    public SyncRef<HelioRectTransform> TargetRect { get; private set; }

    /// <summary>
    /// The source slot whose local position will drive the rect.
    /// </summary>
    public SyncRef<Slot> SourceSlot { get; private set; }

    /// <summary>
    /// Scale factor for position mapping (slot position to UI space).
    /// </summary>
    public Sync<float2> PositionScale { get; private set; }

    /// <summary>
    /// Offset applied to the mapped position.
    /// </summary>
    public Sync<float2> PositionOffset { get; private set; }

    /// <summary>
    /// Whether to drive the size from the slot's scale.
    /// </summary>
    public Sync<bool> DriveSize { get; private set; }

    /// <summary>
    /// Base size when driving from slot scale.
    /// </summary>
    public Sync<float2> BaseSize { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();

        TargetRect = new SyncRef<HelioRectTransform>(this);
        SourceSlot = new SyncRef<Slot>(this);
        PositionScale = new Sync<float2>(this, float2.One);
        PositionOffset = new Sync<float2>(this, float2.Zero);
        DriveSize = new Sync<bool>(this, false);
        BaseSize = new Sync<float2>(this, new float2(100f, 100f));
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        var rect = TargetRect?.Target;
        var sourceSlot = SourceSlot?.Target;

        if (rect == null || sourceSlot == null)
            return;

        // Get source slot's local position
        var localPos = sourceSlot.LocalPosition;

        // Map to UI space (using X and Y components)
        float2 uiPosition = new float2(localPos.Value.x, localPos.Value.y) * PositionScale.Value + PositionOffset.Value;

        // Get current size or calculate from slot scale
        float2 uiSize = rect.Rect.Size;
        if (DriveSize.Value)
        {
            var localScale = sourceSlot.LocalScale;
            uiSize = BaseSize.Value * new float2(localScale.Value.x, localScale.Value.y);
        }

        // Update the rect by setting offsets
        // Position the rect so its pivot aligns with the calculated UI position
        var pivotOffset = uiSize * rect.Pivot.Value;
        rect.OffsetMin.Value = uiPosition - pivotOffset;
        rect.OffsetMax.Value = rect.OffsetMin.Value + uiSize;

        // Mark rect as layout-driven to prevent anchor calculations from overriding
        rect.IsRectDriven = true;
    }
}
