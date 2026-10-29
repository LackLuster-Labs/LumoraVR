// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Interaction;

// Where a tool is pointing, resolved once per press.
//
// The laser knows WHAT it hit and WHERE, but not which way the surface faces - it never asked for a
// normal because nothing before these tools needed one. So the normal comes from a second cast along
// the same ray, and when nothing answers (headless, no physics hook, a hit on something with no
// collider) it falls back to the ray coming back at you, which is the only orientation that is never
// wrong-looking. -xlinka
public struct ToolAim
{
    public Slot? HitSlot;
    public float3 Point;
    public float3 Normal;
    public float3 Origin;
    public float3 Direction;
}

// Shared floor for the equippable tools: aim resolution, the permission posture, undo plumbing, the
// procedural visual and the radial-menu hook. Everything here is additive on ToolItem - the equip
// flow, the touch-to-equip confirm and the hand's press routing are untouched.
//
// Permissions are FAIL CLOSED in two layers. The soft layer is AllowsWorldEditing, which is the UX
// gate: in a Social/Event world the tools simply do nothing rather than placing objects that the
// floor is about to refuse. The hard layer is the datamodel gate itself, which throws on a denied
// write - so every mutation goes through Guarded, which swallows the refusal and logs it. A denied
// press does nothing and takes nobody down with it. -xlinka
[ComponentCategory("Interaction/Tools")]
public abstract class EquippableTool : ToolItem
{
    // Test seam: a headless test has no laser and no physics hook, so there is nothing to aim
    // with. Null in every session.
    public static ToolAim? AimOverride;

    private const float AimRange = 30f;

    private Slot? _visualSlot;
    private UnlitMaterial? _visualMaterial;

    public override bool UsesLaser => true;

    protected override float3 DefaultLocalTip => float3.Backward * 0.075f;

    // The UX half of the permission posture, reading BOTH sides of the floor: the world mode, and the
    // gate's own lock flag (which is what the mode sets, and what a host can have set for other
    // reasons). Asking the gate as well is not belt and braces - Slot.AddSlot attaches the new slot to
    // its parent BEFORE registering it, so a registration the gate refuses leaves an orphan child
    // behind that the caller never gets a handle on. Not trying at all is the only way to be sure
    // nothing is left over. TryAddSlot sweeps up after the cases this cannot see coming. -xlinka
    protected bool EditingBlocked
        => World == null || !World.AllowsWorldEditing || World.DataModelPermissions?.SocialLock == true
           || !ToolUseAllowed;

    // The ToolUse domain, asked of the local user. This is the role's answer rather than the world's,
    // so a Builder world that hands tools to some roles and not others lands here and not in
    // AllowsWorldEditing. Equip asks it too (see AllowsEquip): the tool refuses to be picked up at all
    // rather than sitting in a hand doing nothing when pressed. -xlinka
    protected bool ToolUseAllowed
        => World?.DataModelPermissions?.AllowsDomain(World.LocalUser, DataModelPermissionDomain.ToolUse) != false;

    // The Spawn domain, for the tools that put NEW content into the world. Separate from ToolUse: a
    // role may be trusted to measure and re-material things without being trusted to fill the place up.
    protected bool SpawnBlocked
        => World?.DataModelPermissions?.AllowsDomain(World.LocalUser, DataModelPermissionDomain.Spawn) == false;

    public override bool AllowsEquip(User? user)
        => World?.DataModelPermissions?.AllowsDomain(user, DataModelPermissionDomain.ToolUse) != false;

    protected Slot? VisualSlot => _visualSlot;

    protected UnlitMaterial? VisualMaterial => _visualMaterial;

    protected abstract colorHDR VisualTint { get; }

    public override void OnStart()
    {
        base.OnStart();
        EnsureVisual();

        // Rides the same collector the gizmo mode items use: it only scans the user hierarchy, so an
        // unequipped tool lying on the floor contributes nothing and equip state gates the menu for
        // free. Built by the machine that made the tool, like the visual; the others receive it.
        if (Slot != null && IsMintedHere)
        {
            var source = Slot.GetComponent<ToolActionsMenuSource>() ?? Slot.AttachComponent<ToolActionsMenuSource>();
            if (!ReferenceEquals(source.Tool.Target, this))
                source.Tool.Target = this;
        }
    }

    // Contribute this tool's entries to the root ring of the hand holding it. Called on the local client
    // each time the menu opens, so plain delegates are the right shape here - nothing about the page
    // is stored or replicated.
    public virtual void PopulateToolActions(ContextMenuPage page, ContextMenuContext context)
    {
    }

    protected static readonly float[] ItemFill = { 0.16f, 0.15f, 0.24f, 0.92f };
    protected static readonly float[] ActiveFill = { 0.35f, 0.28f, 0.62f, 0.95f };
    protected static readonly float[] ArmFill = { 0.34f, 0.26f, 0.10f, 0.94f };
    protected static readonly float[] ClearFill = { 0.42f, 0.16f, 0.18f, 0.94f };

    protected bool TryGetAim(out ToolAim aim)
    {
        if (AimOverride.HasValue)
        {
            aim = AimOverride.Value;
            if (aim.Normal.LengthSquared <= 1e-8f)
                aim.Normal = float3.Up;
            else
                aim.Normal = aim.Normal.Normalized;
            return true;
        }

        aim = default;
        var laser = ActiveTool?.Laser;
        if (laser == null)
            return false;

        var origin = laser.RayOrigin;
        var direction = laser.RayDirection;
        if (direction.LengthSquared <= 1e-8f)
            return false;
        direction = direction.Normalized;

        // The laser's own collider hit stands in when what is under the beam is not something to interact
        // with: a floor, a wall, a world mesh, at any distance the beam reaches. The cast below is for the
        // normal, and for a laser that has hit nothing at all.
        var hitSlot = laser.CurrentHitSlot;
        var hitPoint = laser.CurrentHitPoint;
        if (hitSlot == null && laser.CurrentColliderHitSlot is { IsDestroyed: false } surface)
        {
            hitSlot = surface;
            hitPoint = laser.CurrentColliderHitPoint;
        }
        aim.HitSlot = hitSlot;
        aim.Origin = origin;
        aim.Direction = direction;
        aim.Point = hitSlot != null ? hitPoint : origin + direction * AimRange;
        aim.Normal = -direction;

        // Second cast purely for the surface normal. Skipping our own user root keeps a tool held
        // close to the body from measuring the body.
        var exclude = Slot?.ActiveUserRoot?.Slot;
        var excludeList = exclude != null ? new[] { exclude } : null;
        if (World?.Physics.Raycast(origin, direction, excludeList, AimRange) is { } physicsHit
            && physicsHit.Normal.LengthSquared > 1e-8f)
        {
            aim.Normal = physicsHit.Normal.Normalized;
            if (hitSlot == null)
            {
                aim.HitSlot = physicsHit.Slot;
                aim.Point = physicsHit.Point;
            }
        }

        return hitSlot != null || aim.HitSlot != null;
    }

    // The rotation that stands a shape's +Y on a surface. Not LookRotation at any price: it hands
    // back the INVERSE, and a placed shape built on it lies flat against the wall it was meant to sit
    // on. -xlinka
    protected static floatQ AlignUpTo(float3 normal)
    {
        if (normal.LengthSquared <= 1e-8f)
            return floatQ.Identity;

        normal = normal.Normalized;
        float dot = float3.Dot(float3.Up, normal);
        if (dot > 0.9999f)
            return floatQ.Identity;
        if (dot < -0.9999f)
            return floatQ.AxisAngleRad(float3.Right, MathF.PI);

        var axis = float3.Cross(float3.Up, normal);
        if (axis.LengthSquared <= 1e-8f)
            return floatQ.Identity;
        return floatQ.AxisAngleRad(axis.Normalized, MathF.Acos(System.Math.Clamp(dot, -1f, 1f)));
    }

    // What a click on a piece of a rig actually means: the grabbable the piece belongs to, or the hit
    // slot itself when nothing up the chain is grabbable. A tool that reparented or duplicated the bare
    // mesh child of a prop would tear the prop in half.
    protected static Slot? ResolveObjectRoot(Slot? hitSlot)
    {
        if (hitSlot == null || hitSlot.IsDestroyed)
            return null;
        // Checked on the HIT, not just on the root the walk lands on: a grabbable can sit above the
        // marker, and resolving through it would hand the tool a root that reads as editable.
        if (ImmutableComponent.IsProtected(hitSlot))
            return null;
        var grabbable = hitSlot.GetComponentInParents<Grabbable>();
        var root = grabbable?.Slot ?? hitSlot;
        return root.IsRootSlot ? null : root;
    }

    // Never act on the rig doing the acting, on somebody's body, or on a protected subtree.
    protected bool IsOffLimits(Slot? slot)
    {
        if (slot == null || slot.IsDestroyed || slot.IsRootSlot)
            return true;
        if (ImmutableComponent.IsProtected(slot))
            return true;
        if (Slot != null && (ReferenceEquals(slot, Slot) || Slot.IsDescendantOf(slot) || slot.IsDescendantOf(Slot)))
            return true;
        var toolSlot = ActiveTool?.Slot;
        if (toolSlot != null && (ReferenceEquals(slot, toolSlot) || slot.IsDescendantOf(toolSlot)))
            return true;
        return slot.ActiveUserRoot != null;
    }

    protected UndoManager? UndoManager
        => World?.LocalUser?.Root?.Slot?.GetComponentInChildren<UndoManager>();

    protected void RecordUndo(IUndoBatch? batch)
    {
        if (batch != null)
            UndoManager?.Record(batch);
    }

    // A tool action that mutates several things is still one press to the user, so it is one history
    // entry. Without a manager the scope is inert.
    protected UndoBatchScope BeginUndoBatch(Localization.LocaleText description)
    {
        var manager = UndoManager;
        return manager != null ? manager.BeginBatch(description) : default;
    }

    // Every world mutation a tool makes runs through here. The gate throws on a refusal and the throw
    // would otherwise escape the press handler and take the hand's whole input pass with it.
    protected bool Guarded(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            LumoraLogger.Warn($"{GetType().Name}: refused: {ex.Message}");
            return false;
        }
    }

    protected Slot? TryAddSlot(Slot parent, string name)
        => TryAddChild(parent, () => parent.AddSlot(name));

    protected Slot? TryDuplicate(Slot source, Slot parent)
        => TryAddChild(parent, () => source.Duplicate(parent, preserveGlobalTransform: true));

    // A refused create can still leave a child attached: Slot.AddSlot sets the parent link BEFORE the
    // registry add that gets denied, and the caller never sees that slot because the call threw. Sweep
    // any newcomer back out rather than leaving an empty ghost in somebody's world. -xlinka
    private Slot? TryAddChild(Slot parent, Func<Slot?> create)
    {
        int before = parent.ChildCount;
        Slot? made = null;
        if (Guarded(() => made = create()) && made != null)
            return made;

        while (parent.ChildCount > before)
        {
            var stray = parent.Children[parent.ChildCount - 1];
            int count = parent.ChildCount;
            Guarded(() => stray.Destroy());
            if (parent.ChildCount >= count)
                break;
        }
        return null;
    }

    // A tool has to be findable in a hand at arm's length, so the visuals are all the same size and
    // the same shape family, and the tint is what tells them apart.
    protected virtual void EnsureVisual()
    {
        if (_visualSlot != null && !_visualSlot.IsRemoved)
        {
            ApplyVisualTint(VisualTint);
            return;
        }
        if (Slot == null)
            return;

        // The machine that made the tool builds its look and the rest receive it, whoever is holding it by the
        // time it gets to them. Here only what is already there is found, for the state tints.
        if (!IsMintedHere)
        {
            _visualSlot = Slot.FindChild("Visual", recursive: false);
            _visualMaterial = _visualSlot?.GetComponent<UnlitMaterial>();
            return;
        }

        _visualSlot = Slot.FindChild("Visual", recursive: false) ?? Slot.AddSlot("Visual");
        _visualSlot.LocalPosition.Value = float3.Backward * 0.05f;
        // The cone's apex runs along +Y, so turn it to point down the tip direction (Backward).
        // AxisAngle takes DEGREES; this one is radians on purpose.
        _visualSlot.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, -MathF.PI * 0.5f);
        _visualSlot.LocalScale.Value = float3.One;

        var cone = _visualSlot.GetComponent<ConeMesh>() ?? _visualSlot.AttachComponent<ConeMesh>();
        cone.RadiusBase.Value = 0.012f;
        cone.RadiusTop.Value = 0.004f;
        cone.Height.Value = 0.045f;
        cone.Segments.Value = 14;

        _visualMaterial = _visualSlot.GetComponent<UnlitMaterial>() ?? _visualSlot.AttachComponent<UnlitMaterial>();
        _visualMaterial.BlendMode.Value = BlendMode.Alpha;
        _visualMaterial.Culling.Value = Culling.None;
        ApplyVisualTint(VisualTint);

        var renderer = _visualSlot.GetComponent<MeshRenderer>() ?? _visualSlot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = cone;
        renderer.Material.Target = _visualMaterial;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;

        BuildVisualExtras(_visualSlot);
    }

    // The one shape per tool that says which tool it is at a glance - a bead, a ring, a pair of cubes.
    protected virtual void BuildVisualExtras(Slot visual)
    {
    }

    protected void ApplyVisualTint(colorHDR tint)
    {
        if (_visualMaterial != null && !_visualMaterial.IsDestroyed)
            _visualMaterial.TintColor.Value = tint;
    }

    // A small unlit bead on the tip, which is what the tools use to show state (armed, holding a pick,
    // holding a material) without needing a second material family.
    protected Slot? EnsureBead(Slot visual, string name, float3 localPosition, float radius, colorHDR tint)
    {
        var slot = visual.FindChild(name, recursive: false) ?? visual.AddSlot(name);
        slot.LocalPosition.Value = localPosition;

        var mesh = slot.GetComponent<SphereMesh>() ?? slot.AttachComponent<SphereMesh>();
        mesh.Radius.Value = radius;
        mesh.Segments.Value = 12;
        mesh.Rings.Value = 8;

        var material = slot.GetComponent<UnlitMaterial>() ?? slot.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = tint;
        material.UseVertexColor.Value = false;

        var renderer = slot.GetComponent<MeshRenderer>() ?? slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        return slot;
    }

    protected static void SetBeadTint(Slot? bead, colorHDR tint)
    {
        var material = bead?.GetComponent<UnlitMaterial>();
        if (material != null && !material.IsDestroyed)
            material.TintColor.Value = tint;
    }
}
