// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Physics;

namespace Lumora.Core.Components;

// a small grabbable card standing in for a world element (slot, component, or sync member). pulled
// out of inspector rows, carried in the hand, then either dropped onto a reference field to assign
// the element it points at, or activated to open an inspector on that element (and, for asset
// targets, spawn a usable in-world instance beside it). gesture set:
//  - PULL a row's handle (grip) -> a card spawns in the hand pointing at the row's element.
//  - PRIMARY press while held    -> activate: open an inspector on the target (+ spawn an asset
//                                   instance for material/texture/mesh targets), then the card is spent.
//  - PRIMARY press on a resting card (pointed at, not held) -> same activate, no need to grab first.
//  - RELEASE onto a ref field    -> the field assigns the element (the field's receiver removes the card).
//  - RELEASE into empty space    -> the temporary card is discarded.
// Button-created cards expire if left unused; the referenced object is never removed.
// cards pulled from worn-avatar content are inert on activate (they neither open nor spawn).
[ComponentCategory("Utility/Inspectors")]
public class ReferenceProxy : Component, IHeldActivatable
{
    public readonly SyncRef<IWorldElement> Target;

    // When false the card is inert on activate: it neither opens an inspector nor spawns an instance.
    // Set false at spawn for worn-avatar content, so a card pulled off someone's active avatar cannot
    // be used to duplicate it. Synced so every peer agrees whether a shared card is live. -xlinka
    public readonly Sync<bool> SpawnInstanceOnActivate;

    // A card pulled from a row is spent: it goes when dropped, activated, left alone or emptied. One that
    // is part of an object (an imported item that stands for a clip or a source) is not, and stays put
    // through all of that. -xlinka
    public readonly Sync<bool> Temporary;

    // Seconds a card nobody is holding survives before it cleans itself up.
    private const float UnheldLifetime = 10f;

    private RayTarget? _rayTarget;
    private Grabbable? _grabbable;
    private float _idleTime;

    public ReferenceProxy()
    {
        Target = new SyncRef<IWorldElement>(this);
        SpawnInstanceOnActivate = new Sync<bool>(this, true);
        Temporary = new Sync<bool>(this, true);
    }

    public override void OnStart()
    {
        base.OnStart();
        // Resting-card activation: pointing at the set-down card and pressing primary reaches the laser's
        // ray-activation path (the card's ray target outranks its canvas), which fires this. -xlinka
        _rayTarget = Slot?.GetComponent<RayTarget>();
        if (_rayTarget != null)
            _rayTarget.Activated += OnRestingActivated;
        BindRelease();
    }

    public override void OnDestroy()
    {
        if (_rayTarget != null)
            _rayTarget.Activated -= OnRestingActivated;
        if (_grabbable != null)
            _grabbable.OnLocalReleased -= OnReleased;
        base.OnDestroy();
    }

    private void BindRelease()
    {
        if (_grabbable != null) return;
        _grabbable = Slot.GetComponent<Grabbable>();
        if (_grabbable != null) _grabbable.OnLocalReleased += OnReleased;
    }

    private void OnReleased(IGrabbable released)
    {
        // Let reference receivers and held activation finish the release transaction first.
        World.RunInUpdates(1, () =>
        {
            if (!IsDestroyed && !Slot.IsDestroyed && _grabbable?.IsGrabbed != true && Temporary.Value)
                Slot.Destroy();
        });
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        if (!Temporary.Value || _grabbable?.IsGrabbed == true) { _idleTime = 0; return; }
        _idleTime += delta;
        if (World.IsAuthority && (_idleTime >= UnheldLifetime || Target.Target == null || Target.Target.IsDestroyed))
            Slot.Destroy();
    }

    // primary press while carried: activate the card, releasing it from the hand first
    public bool OnHeldActivate(Grabber grabber)
    {
        if (!ShouldActivate())
            return false;

        // Let go of it first so the hand's grab list and holder reset cleanly, then run the activation
        // (which removes the card). -xlinka
        var grabbable = Slot?.GetComponent<Grabbable>();
        if (grabbable != null)
            grabber.Release(grabbable);
        Activate();
        return true;
    }

    // Resting-card activation from the laser's ray target. A protected (inert) card just stays put.
    private void OnRestingActivated(float3 hitPoint)
    {
        if (ShouldActivate())
            Activate();
    }

    private bool ShouldActivate()
    {
        var target = Target.Target;
        return SpawnInstanceOnActivate.Value && target != null && !target.IsDestroyed && World != null;
    }

    // Open the inspector on the target, spawn a usable instance for asset targets, then spend the card.
    private void Activate()
    {
        OpenInspectorForTarget();
        TrySpawnAssetInstance();
        if (Slot != null && !Slot.IsDestroyed && Temporary.Value)
            Slot.Destroy();
    }

    // Open the right inspector for whatever the card points at: a material provider gets the focused
    // material inspector, anything else opens the scene inspector rooted at the element's owning slot
    // (a component target comes pre-expanded). Both panels spawn facing the local user. -xlinka
    private bool OpenInspectorForTarget()
        => ElementInspector.Open(World, Target.Target, Slot.GlobalPosition, Slot.GlobalRotation);

    // Spawn a grabbable in-world instance of the target asset at the card's spot, facing the user, for
    // the asset kinds this engine can represent (material orb, texture quad, mesh preview). Other asset
    // kinds (font, audio, video, cubemap) have no simple in-world form here, so they open the inspector
    // only. Returns true when an instance was spawned. -xlinka
    private bool TrySpawnAssetInstance()
    {
        var target = Target.Target;
        if (target == null || World == null)
            return false;

        var (_, component) = ResolveOwner(target);
        var provider = target as IAssetProvider ?? component as IAssetProvider;

        float3 pos = Slot.GlobalPosition;
        floatQ facing = FaceUser(pos);

        if (provider is IAssetProvider<MaterialAsset> material)
        {
            SpawnMaterialOrb(material, pos, facing);
            return true;
        }
        if (provider is IAssetProvider<TextureAsset> texture)
        {
            SpawnTextureQuad(texture, pos, facing);
            return true;
        }
        if (component is ProceduralMesh || provider is IAssetProvider<MeshDataAsset>)
        {
            SpawnMeshPreview(component, pos, facing);
            return true;
        }
        return false;
    }

    // Grabbable sphere carrying the material, so you can drop it onto other objects or into a slot.
    private void SpawnMaterialOrb(IAssetProvider<MaterialAsset> material, float3 pos, floatQ facing)
    {
        var slot = MakeInstanceSlot("Material Orb", pos, facing);

        var mesh = slot.AttachComponent<SphereMesh>();
        mesh.Radius.Value = 0.08f;
        mesh.Segments.Value = 32;
        mesh.Rings.Value = 16;

        var collider = slot.AttachComponent<SphereCollider>();
        collider.Radius.Value = mesh.Radius.Value;
        collider.Type.Value = ColliderType.Trigger;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;

        slot.AttachComponent<Grabbable>();
    }

    // Grabbable double-sided quad showing the texture unlit, so you can hold it up and look at it.
    private void SpawnTextureQuad(IAssetProvider<TextureAsset> texture, float3 pos, floatQ facing)
    {
        var slot = MakeInstanceSlot("Texture", pos, facing);
        slot.LocalScale.Value = float3.One * 0.3f;

        var mesh = slot.AttachComponent<QuadMesh>();
        mesh.Size.Value = float2.One;
        // Two-sided from the material, not from a second coplanar quad. DualSided builds a duplicate
        // rotated 180 degrees at the same depth, which fights with the original over which is in front.
        mesh.DualSided.Value = false;

        var material = slot.AttachComponent<UnlitMaterial>();
        material.BlendMode.Value = BlendMode.Transparent;
        material.Culling.Value = Culling.None;
        material.Texture.Target = texture;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;

        var collider = slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(1f, 1f, 0.02f);
        collider.Type.Value = ColliderType.Trigger;

        slot.AttachComponent<Grabbable>();
    }

    // Grabbable preview of the mesh with a plain material. The mesh keeps its authored size (we have no
    // asset-bounds normalization here), so it is grabbable and scalable rather than fit to a fixed orb.
    private void SpawnMeshPreview(Component? meshComponent, float3 pos, floatQ facing)
    {
        if (meshComponent == null || meshComponent.IsDestroyed)
            return;

        var slot = MakeInstanceSlot("Mesh", pos, facing);

        var material = slot.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = new colorHDR(0.72f, 0.76f, 0.82f, 1f);

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = meshComponent;
        renderer.Material.Target = material;

        slot.AttachComponent<Grabbable>();
    }

    private Slot MakeInstanceSlot(string name, float3 pos, floatQ facing)
    {
        var slot = World.RootSlot.AddSlot(name);
        slot.Persistent.Value = false;
        slot.Tag.Value = "Developer";
        slot.GlobalPosition = pos;
        slot.GlobalRotation = facing;
        // Activating a card is a world edit like any other, so it goes on the undo stack.
        InspectorUndo.Record(this, SlotExistenceUndoBatch.Created(World, new[] { slot }, UndoLocale.SpawnNamed(name)));
        return slot;
    }

    // Yaw-only rotation whose readable front points back at the local user's head from the given point.
    private floatQ FaceUser(float3 fromPosition)
    {
        var head = World?.LocalUser?.Root?.HeadSlot;
        if (head == null)
            return Slot.GlobalRotation;
        var toHead = head.GlobalPosition - fromPosition;
        toHead.y = 0f;
        if (toHead.LengthSquared <= 1e-6f)
            return Slot.GlobalRotation;
        return floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toHead.x, toHead.z));
    }

    // Resolve the slot (and owning component, if any) an element lives on: a slot is itself, a
    // component carries its slot, a sync member walks up its parent chain to the component/slot. -xlinka
    private static (Slot? slot, Component? component) ResolveOwner(IWorldElement? element)
        => ElementInspector.OwnerOf(element);

    // Worn-avatar content: the target's owning slot sits under a live user root. A card pulled from it
    // stays inert on activate, so it cannot be used to open or duplicate someone's worn avatar. -xlinka
    private static bool IsProtectedElement(IWorldElement? element)
    {
        var (slot, _) = ResolveOwner(element);
        return slot != null && slot.ActiveUserRoot != null;
    }

    // Spend this card after a receiver accepted what it points at. Let go of it FIRST: destroying a
    // slot the grabber still lists leaves a dead entry behind, and the release pass right after a
    // grip-drop then hands that corpse to every drop receiver within reach. Every receiver that
    // consumes a card goes through here. -xlinka
    public void Consume(IGrabbable? grabbed, Grabber? grabber)
    {
        if (grabbed != null)
            grabber?.Release(grabbed);
        if (Slot != null && !Slot.IsDestroyed && Temporary.Value)
            Slot.Destroy();
    }

    public static IGrabbable? Spawn(World world, IWorldElement? target, float3 position)
    {
        if (world == null || target == null || target.IsDestroyed)
            return null;
        // A card is a handle on the element, so nothing inside a sealed object hands one to anyone but its owner.
        if (Security.ItemSeals.RefusesHere(ObjectBoundary.SlotOf(target), Security.SealRule.Inspect, inside: true))
            return null;

        var slot = world.RootSlot.AddSlot("ReferenceProxy");
        slot.Persistent.Value = false;
        slot.Tag.Value = "Developer";
        slot.GlobalPosition = position;

        var head = world.LocalUser?.Root?.HeadSlot;
        if (head != null)
        {
            float3 toHead = head.GlobalPosition - position;
            toHead.y = 0f;
            if (toHead.LengthSquared > 1e-6f)
                slot.GlobalRotation = floatQ.LookRotation(toHead.Normalized, float3.Up).Inverse;
        }
        float userScale = world.LocalUser?.Root?.Slot?.GlobalScale.y ?? 1f;
        userScale = userScale > 0.0001f ? userScale : 1f;
        slot.LocalScale.Value = float3.One * InspectorUI.PanelScale * userScale;

        var proxy = slot.AttachComponent<ReferenceProxy>();
        proxy.Target.Target = target;
        proxy.SpawnInstanceOnActivate.Value = !IsProtectedElement(target);

        var grabbable = slot.AttachComponent<Grabbable>();
        grabbable.Scalable.Value = false;
        proxy.BindRelease();

        // Ray target so a resting card can be clicked open without grabbing it first. Priority sits above
        // the card's canvas (also an interaction target) so the laser resolves the card to this and the
        // primary press takes the ray-activation path; the hover radius covers the card's world extent. -xlinka
        var ray = slot.AttachComponent<RayTarget>();
        ray.InteractionPriority.Value = 2000;
        ray.HoverRadius.Value = 0.14f * userScale;
        ray.AllowActivation.Value = true;

        BuildCard(slot, Describe(target));
        return grabbable;
    }

    private static void BuildCard(Slot slot, string label)
    {
        var rect = slot.AttachComponent<RectTransform>();
        rect.AnchorMin.Value = new float2(0.5f, 0.5f);
        rect.AnchorMax.Value = new float2(0.5f, 0.5f);
        rect.OffsetMin.Value = new float2(-240f, -26f);
        rect.OffsetMax.Value = new float2(240f, 26f);

        var canvas = slot.AttachComponent<Canvas>();
        canvas.SizeCollider.Value = true;

        slot.GetOrAttachComponent<UITheme>();

        var ui = new UIBuilder(slot);
        InspectorUI.ApplyTheme(ui, slot);

        var backing = ui.Panel(InspectorUI.HeaderColor);
        InspectorUI.FillParent(backing.RectTransform!);

        var accent = ui.Panel(InspectorUI.AccentColor);
        var accentRect = accent.RectTransform!;
        accentRect.AnchorMin.Value = float2.Zero;
        accentRect.AnchorMax.Value = new float2(0f, 1f);
        accentRect.OffsetMin.Value = float2.Zero;
        accentRect.OffsetMax.Value = new float2(8f, 0f);

        var text = ui.Text(label, InspectorUI.FontSize + 2f, InspectorUI.TextColor);
        var textRect = text.RectTransform!;
        textRect.AnchorMin.Value = float2.Zero;
        textRect.AnchorMax.Value = float2.One;
        textRect.OffsetMin.Value = new float2(16f, 0f);
        textRect.OffsetMax.Value = new float2(-8f, 0f);
        text.HorizontalAlignment.Value = TextHorizontalAlignment.Left;
        text.VerticalAlignment.Value = TextVerticalAlignment.Middle;
        // Card labels embed slot names, which are user content with inline style tags.
        text.RichText.Value = true;
    }

    public static string Describe(IWorldElement element)
    {
        return element switch
        {
            Slot slot => $"{slot.SlotName.Value} (slot)",
            Component component => $"{component.GetType().Name} on {component.Slot?.SlotName.Value}",
            ISyncMember member => $"{InspectorUI.MemberDisplayName(member)} ({SyncMemberEditorBuilder.NiceTypeName(element.GetType())})",
            _ => SyncMemberEditorBuilder.NiceTypeName(element.GetType()),
        };
    }
}
