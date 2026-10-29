// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Components.Gizmos;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Interaction;

[ComponentCategory("Interaction/Tools")]
public sealed class DevToolItem : ToolItem, ILaserHitClassifier
{
    public enum SelectionMode
    {
        Single,
        Multi
    }

    // Identifies developer content; this tag does not grant priority through other objects.
    private const string DeveloperTag = "Developer";

    public readonly Sync<SelectionMode> Selection = new();
    public readonly SyncRef<Slot> SelectedSlot = new();
    public readonly SyncRef<Slot> CurrentGizmoSlot = new();

    private TransformHandle? _activeHandle;
    internal bool IsDraggingGizmo => _activeHandle is { IsDestroyed: false, IsDragging: true };
    private Slot? _visualSlot;
    private OverlayFresnelMaterial? _visualMaterial;

    public override Slot? HeldVisual => _visualSlot;

    public override bool UsesLaser => true;

    public override bool UsesSecondary => Engine.Current?.InputInterface?.IsVRActive != true;

    protected override float3 DefaultLocalTip => float3.Backward * 0.075f;

    public override void OnInit()
    {
        base.OnInit();
        Selection.Value = SelectionMode.Single;
        EquipName.Value = "Dev Tool";
    }

    public override void OnStart()
    {
        base.OnStart();
        // The creator supplies the visual and menu source. A peer can start this component before
        // those children arrive; creating them there adds another replicated source per participant.
        if (!IsMintedHere)
            return;
        // Tag as a developer/editing tool so it's identifiable and excluded from social spaces.
        if (Slot != null && string.IsNullOrEmpty(Slot.Tag.Value))
            Slot.Tag.Value = DeveloperTag;
        EnsureVisual();

        // The source checks equip state and the summoning hand; held and cached tools also live
        // under the user hierarchy and must not contribute their own menu entries.
        if (Slot != null)
        {
            var menuSource = Slot.GetComponent<GizmoModeMenuSource>() ?? Slot.AttachComponent<GizmoModeMenuSource>();
            menuSource.Tool.Target = this;
            if (menuSource.Priority.Value != GizmoModeMenuSource.RingPriority)
                menuSource.Priority.Value = GizmoModeMenuSource.RingPriority;
        }
    }

    // Editing is disabled in Social/Event worlds. This is the UX gate (don't place dead gizmos); the
    // hard lock is the host-authoritative permission floor, which denies the edits regardless.
    private bool EditingDisabled => World != null && !World.AllowsWorldEditing;

    // The dev tool IS the dev gear the ToolUse domain is about, so it answers the same question the
    // build tools do: no right to use tools, no picking this one up and no press once it is in hand.
    public override bool AllowsEquip(User? user)
        => World?.DataModelPermissions?.AllowsDomain(user, DataModelPermissionDomain.ToolUse) != false;

    internal bool IsEquippedInHand(HandTool hand)
        => !IsDestroyed && Enabled.Value && Slot.IsActive && !EditingDisabled
           && hand is { IsDestroyed: false, IsUnderLocalUser: true }
           && ReferenceEquals(ActiveTool, hand) && ReferenceEquals(hand.ActiveToolItem.Target, this)
           && AllowsEquip(World?.LocalUser);

    internal static bool CanManipulateGizmos(InteractionLaser? laser)
    {
        if (laser == null || laser.IsDestroyed)
            return false;
        var hand = laser.Slot.GetComponentInParent<HandTool>(includeSelf: true);
        return hand != null && ReferenceEquals(hand.Laser, laser)
            && hand.ActiveToolItem.Target is DevToolItem tool && tool.IsEquippedInHand(hand);
    }

    // Only gizmo controls may take priority through geometry while this DevTip is equipped. The Developer tag is
    // shared by tools and panels, which must obey normal occlusion or tools behind an inspector
    // steal its clicks and open their equip confirmation.
    public LaserHitClass ClassifyLaserHit(InteractionLaser laser, Slot hitSlot, IInteractionTarget target)
    {
        if (!CanManipulateGizmos(laser) || hitSlot == null || hitSlot.IsDestroyed)
            return LaserHitClass.Allow;
        Slot? annotated;
        if (target is TransformHandle handle)
            annotated = handle.TargetSlot.Target;
        else if (hitSlot.GetComponentInParents<SlotGizmo>() is { } slotGizmo)
            annotated = slotGizmo.TargetSlot;
        else if (hitSlot.GetComponentInParents<ComponentGizmo>() is { } componentGizmo)
            annotated = componentGizmo.TargetComponent?.Slot;
        else
            return LaserHitClass.Allow;
        // A rig on another person is there to be looked at, so it does not reach through what is in front of it.
        return GizmoHelper.InAnotherPerson(annotated) ? LaserHitClass.Allow : LaserHitClass.Prefer;
    }

    public override bool OnPrimaryPress()
    {
        if (EditingDisabled)
        {
            return false;
        }

        var laser = ActiveTool?.Laser;
        var hitSlot = laser?.CurrentHitSlot;
        if (laser == null || hitSlot == null)
        {
            return false;
        }

        // Manipulation handles (arrows/rings/cubes) take the tool's primary: press starts a ray-driven
        // drag session on the handle itself, which then self-updates from the laser every frame. -xlinka
        var handle = hitSlot.GetComponentInParents<TransformHandle>();
        if (handle != null)
        {
            if (handle.BeginToolDrag(laser))
            {
                _activeHandle = handle;
                return true;
            }
            LumoraLogger.Debug($"DevToolItem: handle '{handle.Slot?.SlotName.Value}' refused the drag (dragging={handle.IsDragging}, target={(handle.TargetSlot.Target == null ? "null" : handle.TargetSlot.Target.IsDestroyed ? "destroyed" : "ok")})");
        }

        return false;
    }

    public override bool OnPrimaryHold()
    {
        if (_activeHandle != null)
        {
            // The handle consumes its laser directly in OnUpdate; holding just keeps the claim alive.
            if (_activeHandle.IsDragging && !_activeHandle.IsDestroyed)
            {
                return true;
            }
            _activeHandle = null;
        }

        return false;
    }

    public override bool OnPrimaryRelease()
    {
        if (_activeHandle != null)
        {
            _activeHandle.EndToolDrag();
            _activeHandle = null;
            return true;
        }

        return false;
    }

    public override bool OnSecondaryPress() => SelectTarget();

    public bool SelectTarget()
    {
        if (ActiveTool is not HandTool hand || !IsEquippedInHand(hand))
        {
            return false;
        }

        DropStaleSelection();

        // The slot under the beam, solid or only drawn, not just what the laser can act on: a floor, a wall
        // and a prop nobody made grabbable are all things a person points at to select. In VR the tip pokes too.
        var laser = ActiveTool?.Laser;
        var target = ResolveSelectableSlot(DevToolPick.Pick(laser, Engine.Current?.InputInterface?.IsVRActive == true));
        if (target == null)
        {
            return false;
        }

        if (Selection.Value == SelectionMode.Single && SelectedSlot.Target != null && !ReferenceEquals(SelectedSlot.Target, target))
        {
            GizmoHelper.DestroyGizmo(SelectedSlot.Target);
        }

        var gizmo = GizmoHelper.ToggleGizmo(target);
        SelectedSlot.Target = gizmo == null ? null! : target!;
        CurrentGizmoSlot.Target = (gizmo as Component)?.Slot!;
        // Somebody else's and no yes from them: looking is fine, and the card says where the ask is.
        if (gizmo != null && GizmoHelper.InAnotherPerson(target)
            && Networking.Session.SessionEditConsent.PersonOf(target, World?.LocalUser) is { } person)
        {
            bool rootItself = ReferenceEquals(target, person.Root?.Slot)
                && Networking.Session.SessionEditConsent.Allows(person, World?.LocalUser);
            Lumora.Core.Components.UI.EditConsentNotices.Refused(World, person, rootItself);
        }
        return true;
    }

    // called whenever the gizmo behind it went away, so the tool never holds a selection with nothing
    // on screen to back it
    public void ClearSelection()
    {
        SelectedSlot.Target = null!;
        CurrentGizmoSlot.Target = null!;
    }

    public void DeselectAll()
    {
        GizmoHelper.DeselectAll(World);
        ClearSelection();
    }

    public void DeselectLocal()
    {
        GizmoHelper.DeselectLocal(World);
        ClearSelection();
    }

    // The object one level up from what the gizmo is on: a part picked by pointing at it walks up to the thing it
    // belongs to. The same rig moves up, and this tool follows when it was the one holding that selection. Never
    // the world root, and nothing the selection rules would refuse.
    public static bool CanSelectParent(SlotGizmo? gizmo)
        => gizmo is { IsDestroyed: false } && gizmo.TargetSlot?.Parent is { IsDestroyed: false } parent
           && !RefusesParent(parent);

    public bool SelectParent(SlotGizmo? gizmo)
    {
        if (!CanSelectParent(gizmo))
        {
            return false;
        }

        var child = gizmo!.TargetSlot;
        var parent = child.Parent;
        bool ours = ReferenceEquals(SelectedSlot.Target, child);
        gizmo.OpenParent();
        if (!ReferenceEquals(gizmo.TargetSlot, parent))
        {
            return false;
        }

        if (ours)
        {
            SelectedSlot.Target = parent;
            CurrentGizmoSlot.Target = gizmo.Slot;
        }
        return true;
    }

    // The quiet half of RefusesSelection, for deciding whether to offer the step at all.
    private static bool RefusesParent(Slot parent)
        => parent.IsRootSlot || parent.IsLocalElement || ImmutableComponent.IsProtected(parent)
           || Security.ItemSeals.JudgeInside(parent, Security.ItemSeals.ActingAccount(parent.World), Security.SealRule.Inspect,
               out _, out _) != Security.SealVerdict.Allowed;

    // The selection is only real while its gizmo is: the gizmo can go without this tool asking (a
    // deselect from the radial menu, the inspector letting go, the slot being deleted). CurrentGizmoSlot
    // is not the test - a dismissed rig keeps its slot so the next selection can reuse it.
    private void DropStaleSelection()
    {
        var selected = SelectedSlot.Target;
        if (selected == null)
            return;
        if (selected.IsDestroyed || !GizmoHelper.HasGizmo(selected))
            ClearSelection();
    }

    public override void OnDequipped()
    {
        _activeHandle?.EndToolDrag();
        _activeHandle = null;
    }

    private Slot? ResolveSelectableSlot(Slot? hitSlot)
    {
        if (hitSlot == null || hitSlot.IsRootSlot)
        {
            return null;
        }

        var slotGizmo = hitSlot.GetComponentInParents<SlotGizmo>();
        if (slotGizmo?.TargetSlot != null)
        {
            return slotGizmo.TargetSlot;
        }

        // A component gizmo's chrome stands for the object it annotates: pointing at a collider's
        // wireframe and pressing select has to pick the collider's slot, not the gizmo's.
        var componentGizmo = hitSlot.GetComponentInParents<ComponentGizmo>();
        if (componentGizmo?.TargetComponent?.Slot is { IsDestroyed: false } annotated)
        {
            return annotated;
        }

        if (IsInActiveToolHierarchy(hitSlot))
        {
            return null;
        }

        // Same answer as pointing at the sky: no selection, so no gizmo is minted over something the
        // person cannot edit anyway.
        if (ImmutableComponent.IsProtected(hitSlot))
        {
            return null;
        }

        // A hitbox an IK solver fitted to a bone is that bone to whoever points at it.
        if (ObjectBoundary.IsBodyHitbox(hitSlot) && hitSlot.Parent is { IsRootSlot: false } bone)
        {
            hitSlot = bone;
        }

        // A card, a cursor or any other local visual stands for the replicated object it belongs to.
        var target = GizmoHelper.ReplicatedOwner(hitSlot);
        return target == null || RefusesSelection(target) ? null : target;
    }

    // Selecting is looking, so another person's body, the avatar they wear and what they hold all select; their
    // gizmo's handles stay inert (TransformHandle). What stays shut: the world root, a marked subtree, and an
    // account's seal to everyone but its owner, as the inspector does.
    private static bool RefusesSelection(Slot target)
        => target.IsRootSlot || target.IsLocalElement || ImmutableComponent.IsProtected(target)
           || Security.ItemSeals.RefusesHere(target, Security.SealRule.Inspect, inside: true);

    private bool IsInActiveToolHierarchy(Slot slot)
    {
        var toolSlot = ActiveTool?.Slot;
        var current = slot;
        while (current != null)
        {
            if (ReferenceEquals(current, toolSlot) || ReferenceEquals(current, Slot))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private void EnsureVisual()
    {
        if (_visualSlot != null && !_visualSlot.IsRemoved)
        {
            return;
        }

        _visualSlot = Slot.FindChild("Visual", recursive: false) ?? Slot.AddSlot("Visual");
        _visualSlot.LocalPosition.Value = float3.Backward * 0.05f;
        // The cone's apex points along +Y; rotate so it points down the tool's tip direction
        // (Backward), so the dev tip reads as a forward-facing pointer. AxisAngle is RADIANS.
        _visualSlot.LocalRotation.Value = floatQ.AxisAngle(float3.Right, -System.MathF.PI * 0.5f);
        _visualSlot.LocalScale.Value = float3.One;

        // A 5cm truncated cone, 5cm ahead of the tool root, drawn in an OVERLAY fresnel so it stays
        // visible through the hand holding it and whatever it is pointed into. A plain unlit alpha cone in
        // the ordinary pass gets covered by the paw wrapped round it: it shows as one pixel-dot at the heel
        // of the palm and reads as "no tool at all". -xlinka
        var cone = _visualSlot.GetComponent<ConeMesh>() ?? _visualSlot.AttachComponent<ConeMesh>();
        cone.RadiusBase.Value = 0.015f;
        cone.RadiusTop.Value = 0.0025f;
        cone.Height.Value = 0.05f;
        cone.Segments.Value = 16;

        _visualMaterial = _visualSlot.GetComponent<OverlayFresnelMaterial>() ?? _visualSlot.AttachComponent<OverlayFresnelMaterial>();
        _visualMaterial.Exponent.Value = 1f;
        _visualMaterial.FrontNearColor.Value = new colorHDR(0f, 0f, 0f, 1f);
        _visualMaterial.FrontFarColor.Value = new colorHDR(1f, 0f, 0f, 1f);
        _visualMaterial.BehindNearColor.Value = new colorHDR(0f, 0f, 0f, 0.25f);
        _visualMaterial.BehindFarColor.Value = new colorHDR(0.8f, 0f, 0f, 0.25f);

        var renderer = _visualSlot.GetComponent<MeshRenderer>() ?? _visualSlot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = cone;
        renderer.Material.Target = _visualMaterial;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }
}
