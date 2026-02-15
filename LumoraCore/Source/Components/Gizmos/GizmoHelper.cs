using Lumora.Core.Logging;

namespace Lumora.Core.Components.Gizmos;

/// <summary>
/// Utility class for spawning and managing gizmos.
/// </summary>
public static class GizmoHelper
{
    /// <summary>
    /// Spawn a SlotGizmo for the target slot.
    /// </summary>
    /// <param name="targetSlot">The slot to create a gizmo for.</param>
    /// <returns>The spawned SlotGizmo, or null if failed.</returns>
    public static SlotGizmo SpawnGizmoFor(Slot targetSlot)
    {
        if (targetSlot == null)
        {
            Logger.Warn("GizmoHelper.SpawnGizmoFor: Target slot is null");
            return null;
        }

        if (targetSlot.IsRootSlot)
        {
            Logger.Warn("GizmoHelper.SpawnGizmoFor: Cannot create gizmo for root slot");
            return null;
        }

        // Check if gizmo already exists
        var existingGizmo = GizmoRegistry.GetGizmoForSlot(targetSlot);
        if (existingGizmo is SlotGizmo existing)
        {
            existing.Active.Value = true;
            return existing;
        }

        // Create gizmo slot as sibling to target (not child, to avoid transform issues)
        var gizmoSlot = targetSlot.Parent?.AddSlot($"Gizmo_{targetSlot.Name.Value}")
            ?? targetSlot.World?.RootSlot?.AddSlot($"Gizmo_{targetSlot.Name.Value}");

        if (gizmoSlot == null)
        {
            Logger.Warn("GizmoHelper.SpawnGizmoFor: Failed to create gizmo slot");
            return null;
        }

        // Create the SlotGizmo component
        var gizmo = gizmoSlot.AttachComponent<SlotGizmo>();
        gizmo.Setup(targetSlot);

        Logger.Log($"GizmoHelper: Spawned gizmo for slot '{targetSlot.Name.Value}'");
        return gizmo;
    }

    /// <summary>
    /// Destroy the gizmo for a slot.
    /// </summary>
    /// <param name="targetSlot">The slot whose gizmo should be destroyed.</param>
    public static void DestroyGizmo(Slot targetSlot)
    {
        if (targetSlot == null) return;

        var gizmo = GizmoRegistry.GetGizmoForSlot(targetSlot);
        if (gizmo != null)
        {
            GizmoRegistry.UntrackGizmo(targetSlot);

            // Destroy the gizmo's slot (which destroys the component)
            if (gizmo is Component component && component.Slot != null)
            {
                component.Slot.Destroy();
            }

            Logger.Log($"GizmoHelper: Destroyed gizmo for slot '{targetSlot.Name.Value}'");
        }
    }

    /// <summary>
    /// Check if a slot has an active gizmo.
    /// </summary>
    public static bool HasGizmo(Slot targetSlot)
    {
        return GizmoRegistry.HasGizmo(targetSlot);
    }

    /// <summary>
    /// Get the gizmo for a slot.
    /// </summary>
    public static SlotGizmo GetGizmo(Slot targetSlot)
    {
        return GizmoRegistry.GetGizmoForSlot(targetSlot) as SlotGizmo;
    }

    /// <summary>
    /// Toggle gizmo for a slot (create if doesn't exist, destroy if exists).
    /// </summary>
    public static SlotGizmo ToggleGizmo(Slot targetSlot)
    {
        if (HasGizmo(targetSlot))
        {
            DestroyGizmo(targetSlot);
            return null;
        }
        return SpawnGizmoFor(targetSlot);
    }
}
