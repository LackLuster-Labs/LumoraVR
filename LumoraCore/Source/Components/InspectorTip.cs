// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Interaction;
using Lumora.Core.GodotUI.Inspectors;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

/// <summary>
/// Attach to a controller slot to enable spawning a SceneInspector by pressing
/// the trigger while pointing at an interactive object (RayTarget).
///
/// Primary press  → open inspector for the currently hovered slot.
/// Secondary press (grip) → open inspector at world root.
///
/// </summary>
[ComponentCategory("Interaction")]
[DefaultUpdateOrder(0)]
public class InspectorTip : Component
{
    /// <summary>Which controller this tip lives on.</summary>
    public readonly Sync<Chirality> HandSide;

    /// <summary>
    /// Distance in front of the controller where the inspector panel will be spawned.
    /// </summary>
    public readonly Sync<float> SpawnDistance;

    /// <summary>
    /// If set, opening an inspector will reuse (move) this existing panel instead of
    /// creating a new one. Useful for keeping only one inspector open at a time.
    /// </summary>
    public readonly SyncRef<SceneInspector> PersistentInspector;

    private bool _prevTrigger;
    private bool _prevGrip;

    public override void OnInit()
    {
        base.OnInit();
        HandSide.Value = Chirality.Right;
        SpawnDistance.Value = 0.6f;
    }

    public override void OnUpdate(float delta)
    {
        var input = Engine.Current?.InputInterface;
        if (input == null) return;

        var ctrl = HandSide.Value == Chirality.Left
            ? input.LeftController
            : input.RightController;

        if (ctrl == null) return;

        bool triggerNow = ctrl.TriggerPressed;
        bool gripNow = ctrl.GripValue > 0.7f;

        // Leading-edge trigger → inspect hovered slot
        if (triggerNow && !_prevTrigger)
        {
            var hovered = FindHoveredSlot();
            if (hovered != null)
                OpenInspectorFor(hovered);
        }

        // Leading-edge grip → inspect world root
        if (gripNow && !_prevGrip)
        {
            if (World?.RootSlot != null)
                OpenInspectorFor(World.RootSlot);
        }

        _prevTrigger = triggerNow;
        _prevGrip = gripNow;
    }

    // ── Slot lookup ───────────────────────────────────────────────────────────

    private Slot? FindHoveredSlot()
    {
        if (World == null) return null;

        foreach (var target in World.RootSlot.GetComponentsInChildren<RayTarget>())
        {
            if (target.IsHovered.Value)
                return target.Slot;
        }

        return null;
    }

    // ── Inspector spawning ────────────────────────────────────────────────────

    private void OpenInspectorFor(Slot target)
    {
        // Reuse persistent inspector if available
        var existing = PersistentInspector.Target;
        if (existing != null && !existing.IsDestroyed)
        {
            existing.FocusSlot(target);
            PositionPanelInFront(existing.Slot);
            return;
        }

        // Spawn a new SceneInspector
        var inspectorSlot = World!.RootSlot.AddSlot("SceneInspector");
        var inspector = inspectorSlot.AttachComponent<SceneInspector>();

        PositionPanelInFront(inspectorSlot);
        inspector.InitializeWithWorldRoot();
        inspector.FocusSlot(target);

        // Make it grabable
        if (inspectorSlot.GetComponent<Grabbable>() == null)
            inspectorSlot.AttachComponent<Grabbable>();

        PersistentInspector.Target = inspector;
    }

    private void PositionPanelInFront(Slot panelSlot)
    {
        // Place the inspector SpawnDistance units in front of the controller
        float3 forward = -Slot.Forward; // controller visual faces -Z
        float dist = SpawnDistance.Value;
        panelSlot.GlobalPosition = new float3(
            Slot.GlobalPosition.x + forward.x * dist,
            Slot.GlobalPosition.y + forward.y * dist,
            Slot.GlobalPosition.z + forward.z * dist);

        // Face toward the user (flip 180° around Y)
        panelSlot.GlobalRotation = Slot.GlobalRotation;
    }
}
