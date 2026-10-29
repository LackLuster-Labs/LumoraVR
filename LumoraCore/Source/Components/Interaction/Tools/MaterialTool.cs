// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Assets;
using Lumora.Core.Components.Magnets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Components.UI;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Physics;

namespace Lumora.Core.Components.Interaction;

// Paints with the material orb loaded in its cartridge.
//
// The material lives in an orb, not in a field on the tool: a sphere drawn in that material, seated in a
// glass socket a hand's width down the tool where anyone can see what it carries, and an object of its
// own that can be pulled out, handed over, copied or dropped into another tool. Secondary with the
// cartridge empty makes an orb of the material under the laser and seats it. Primary paints: the ray
// paints the surface it hits, area paints everything inside the sphere at the tip and keeps painting
// while held, object swaps the hit material across the object it belongs to, scene across the world.
// Pointed at nothing a sky material goes on the world's sky, and secondary picks the sky up as an orb.
//
// Materials are read and written through the renderer's Materials list BY INDEX. MeshRenderer.Material
// is an accessor that ADDS an empty slot-zero entry when the list is empty, so going through it would
// edit the thing it was only supposed to look at. -xlinka
[ComponentCategory("Interaction/Tools")]
public sealed class MaterialTool : EquippableTool
{
    // Along the tool from the hand: the cartridge, the cone, the tip.
    private const float SocketDistance = 0.10f;
    private const float ConeDistance = 0.20f;
    private const float TipDistance = 0.225f;
    private const float SocketRadius = 0.06f;
    private const float SnapReach = 0.05f;
    private const float DefaultAreaRadius = 0.02f;
    private static readonly float[] AreaSizes = { 0.02f, 0.05f, 0.1f, 0.2f };
    private const int MaxParentWalk = 16;

    public readonly Sync<MaterialToolMode> Mode = new();
    public readonly Sync<float> AreaRadius = new();

    // A tool saved before the cartridge kept its material here. It is loaded as an orb on start.
    public readonly AssetRef<MaterialAsset> StoredMaterial = new();

    private readonly SyncRef<MagnetSocket> _cartridge = new();
    private readonly SyncRef<Slot> _areaIndicator = new();

    private UndoBatchScope _stroke;
    private bool _stroking;
    private readonly HashSet<Component> _strokePainted = new();
    private readonly List<Slot> _areaHits = new();

    protected override colorHDR VisualTint => new(1f, 1f, 1f, 1f);

    protected override float3 DefaultLocalTip => float3.Backward * TipDistance;

    public MagnetSocket? Cartridge => _cartridge.Target is { IsDestroyed: false } socket ? socket : null;

    public bool HasOrb => Cartridge?.Attached.Target is { IsDestroyed: false };

    public MaterialOrb? LoadedOrb => Cartridge?.Attached.Target?.Slot?.GetComponent<MaterialOrb>();

    public IAssetProvider<MaterialAsset>? LoadedMaterial => LoadedOrb?.Material;

    public override void OnInit()
    {
        base.OnInit();
        EquipName.Value = "Material Tool";
        AreaRadius.Value = DefaultAreaRadius;
    }

    public override void OnStart()
    {
        base.OnStart();
        LoadStoredMaterial();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        RefreshAreaIndicator();
    }

    // The tool's shape is written by the machines whose writes it is theirs to make: whoever made it,
    // whoever holds it, the authority while nobody does. Everyone else receives it.
    private bool MayShape()
        => World != null && Slot != null && (IsMintedHere || Slot.IsUnderLocalUser || (World.IsAuthority && Slot.ActiveUserRoot == null));

    // VISUAL

    // A glass socket where the orb sits, a cone beyond it pointing at what gets painted, and the tip at
    // the cone's point.
    protected override void EnsureVisual()
    {
        if (!MayShape())
            return;

        var cone = Slot.FindChild("Visual", recursive: false) ?? Slot.AddSlot("Visual");
        cone.LocalPosition.Value = float3.Backward * ConeDistance;
        // The cone's apex runs along +Y, so it is turned to point down the tip direction (Backward).
        cone.LocalRotation.Value = floatQ.AxisAngleRad(float3.Right, -MathF.PI * 0.5f);
        cone.LocalScale.Value = float3.One;
        // What this tool used to wear: the generic tinted cone and the bead that stood in for the material.
        cone.FindChild("Bead", recursive: false)?.Destroy();
        cone.GetComponent<UnlitMaterial>()?.Destroy();

        var coneMesh = cone.GetComponent<ConeMesh>() ?? cone.AttachComponent<ConeMesh>();
        coneMesh.RadiusBase.Value = 0.03f;
        coneMesh.RadiusTop.Value = 0.005f;
        coneMesh.Height.Value = 0.05f;
        coneMesh.Segments.Value = 24;
        var coneMaterial = cone.GetComponent<PBS_Metallic>() ?? cone.AttachComponent<PBS_Metallic>();
        coneMaterial.AlbedoColor.Value = new colorHDR(1f, 1f, 1f, 1f);
        coneMaterial.Metallic.Value = 0.05f;
        coneMaterial.Smoothness.Value = 0.05f;
        var coneRenderer = cone.GetComponent<MeshRenderer>() ?? cone.AttachComponent<MeshRenderer>();
        coneRenderer.Mesh.Target = coneMesh;
        SetFirstMaterial(coneRenderer, coneMaterial);

        var socketSlot = Slot.FindChild("OrbSlot", recursive: false) ?? Slot.AddSlot("OrbSlot");
        socketSlot.LocalPosition.Value = float3.Backward * SocketDistance;
        var socket = socketSlot.GetComponent<MagnetSocket>() ?? socketSlot.AttachComponent<MagnetSocket>();
        socket.MaxDistance.Value = SnapReach;
        // An orb seats at its own size whatever it was scaled to outside: a video player's 360 orb is the
        // screen's height beside it and a plain 5 cm orb in the cartridge.
        socket.ResetItemScale.Value = true;
        bool tagged = false;
        foreach (var tag in socket.TagWhitelist)
            tagged |= tag == MaterialOrb.MagnetTag;
        if (!tagged)
            socket.TagWhitelist.Add(MaterialOrb.MagnetTag);

        var glass = socketSlot.GetComponent<FresnelMaterial>() ?? socketSlot.AttachComponent<FresnelMaterial>();
        glass.FarColor.Value = new colorHDR(1f, 1f, 1f, 1f);
        glass.NearColor.Value = new colorHDR(1f, 1f, 1f, 0f);
        glass.BlendMode.Value = BlendMode.Alpha;
        var bubble = socketSlot.GetComponent<SphereMesh>() ?? socketSlot.AttachComponent<SphereMesh>();
        bubble.Radius.Value = SocketRadius;
        bubble.Segments.Value = 32;
        bubble.Rings.Value = 16;
        var bubbleRenderer = socketSlot.GetComponent<MeshRenderer>() ?? socketSlot.AttachComponent<MeshRenderer>();
        bubbleRenderer.Mesh.Target = bubble;
        SetFirstMaterial(bubbleRenderer, glass);
        bubbleRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
        _cartridge.Target = socket;

        EnsureAreaIndicator();
    }

    private static void SetFirstMaterial(MeshRenderer renderer, IAssetProvider<MaterialAsset> material)
    {
        if (renderer.Materials.Count == 0)
            renderer.Materials.Add(material);
        else if (!ReferenceEquals(renderer.Materials.GetElement(0).Target, material))
            renderer.Materials.GetElement(0).Target = material;
    }

    // The sphere area mode paints inside, standing just past the tip and shown only in that mode.
    private void EnsureAreaIndicator()
    {
        var slot = _areaIndicator.Target;
        if (slot == null || slot.IsDestroyed)
        {
            slot = Slot.FindChild("AreaIndicator", recursive: false) ?? Slot.AddSlot("AreaIndicator");
            var mesh = slot.GetComponent<SphereMesh>() ?? slot.AttachComponent<SphereMesh>();
            mesh.Radius.Value = 1f;
            var glass = slot.GetComponent<FresnelMaterial>() ?? slot.AttachComponent<FresnelMaterial>();
            glass.FarColor.Value = new colorHDR(0.3f, 0.6f, 1f, 0.9f);
            glass.NearColor.Value = new colorHDR(0.3f, 0.6f, 1f, 0f);
            glass.BlendMode.Value = BlendMode.Alpha;
            var renderer = slot.GetComponent<MeshRenderer>() ?? slot.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = mesh;
            SetFirstMaterial(renderer, glass);
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            _areaIndicator.Target = slot;
        }
        RefreshAreaIndicator();
    }

    private void RefreshAreaIndicator()
    {
        var slot = _areaIndicator.Target;
        if (slot == null || slot.IsDestroyed || !MayShape())
            return;
        float radius = MathF.Max(0.005f, AreaRadius.Value);
        var position = float3.Backward * (TipDistance + radius);
        if (slot.LocalPosition.Value != position)
            slot.LocalPosition.Value = position;
        var scale = float3.One * radius;
        if (slot.LocalScale.Value != scale)
            slot.LocalScale.Value = scale;
        bool shown = Mode.Value == MaterialToolMode.Area;
        if (slot.ActiveSelf.Value != shown)
            slot.ActiveSelf.Value = shown;
    }

    // PAINTING

    public override bool OnPrimaryPress()
    {
        var material = LoadedMaterial;
        if (material == null || EditingBlocked)
            return false;

        _strokePainted.Clear();
        if (Mode.Value == MaterialToolMode.Area)
        {
            // An area stroke keeps painting while held, and the whole stroke is one step back.
            _stroke = BeginUndoBatch(UndoLocale.ApplyMaterial);
            _stroking = true;
            PaintArea(material);
            return true;
        }

        using (BeginUndoBatch(UndoLocale.ApplyMaterial))
            return Paint(material);
    }

    public override bool OnPrimaryHold()
    {
        if (!_stroking)
            return false;
        var material = LoadedMaterial;
        if (material != null && !EditingBlocked)
            PaintArea(material);
        return true;
    }

    public override bool OnPrimaryRelease()
    {
        if (!_stroking)
            return false;
        EndStroke();
        return true;
    }

    public override void OnDequipped()
    {
        base.OnDequipped();
        EndStroke();
    }

    private void EndStroke()
    {
        if (!_stroking)
            return;
        _stroking = false;
        _stroke.Dispose();
        _strokePainted.Clear();
    }

    private bool Paint(IAssetProvider<MaterialAsset> material)
    {
        var hitSlot = TryGetAim(out var aim) ? aim.HitSlot : null;
        if (hitSlot != null && FindRelay(hitSlot) is { } relay)
            return PaintRelay(relay, hitSlot, material);
        var renderer = hitSlot != null ? FindRenderer(hitSlot) : null;
        // Nothing paintable under the beam, the open sky or something with no surface of its own: a sky
        // material goes on the world's sky.
        if (renderer == null)
            return material is ISkyboxMaterial sky && PaintSky(sky);
        if (Mode.Value == MaterialToolMode.Ray && !MaterialPaintPolicy.Allows(renderer.Slot))
            return false;
        int surface = SurfaceUnder(renderer, hitSlot!, aim.Point);

        if (Mode.Value is MaterialToolMode.Object or MaterialToolMode.Scene)
        {
            var scope = Mode.Value == MaterialToolMode.Scene ? World?.RootSlot : ObjectOf(hitSlot!);
            if (scope == null)
                return false;
            // A hit that cannot tell which surface it landed on swaps every material the renderer wears.
            int recorded = 0;
            foreach (var replaced in MaterialsOf(renderer, surface))
            {
                if (!ReferenceEquals(replaced, material))
                    recorded += ReplaceEverywhere(scope, replaced, material);
            }
            return recorded > 0;
        }

        return Apply(renderer, surface, material) > 0;
    }

    // Everything inside the sphere at the tip, every surface of it, once per stroke. Never the painter.
    private void PaintArea(IAssetProvider<MaterialAsset> material)
    {
        var physics = World?.Physics;
        if (physics == null || Slot == null)
            return;
        float radius = MathF.Max(0.005f, AreaRadius.Value);
        var center = Slot.LocalPointToGlobal(float3.Backward * (TipDistance + radius));
        _areaHits.Clear();
        // Sensors are asked for too: a grabbable prop's collider is one. A bare trigger is a zone or a
        // volume, not a surface, and is passed over.
        physics.OverlapSphere(center, Slot.LocalScaleToGlobal(radius), _areaHits, hitTriggers: true);
        var own = Slot.ActiveUserRoot;
        foreach (var hit in _areaHits)
        {
            if (hit == null || hit.IsDestroyed || (own != null && ReferenceEquals(hit.ActiveUserRoot, own)) || IsBareTrigger(hit))
                continue;
            if (FindRelay(hit) is { } relay)
            {
                if (_strokePainted.Add(relay) && MaterialPaintPolicy.Allows(relay.Slot))
                    ApplyRelay(relay, material);
                continue;
            }
            var renderer = FindRenderer(hit);
            if (renderer != null && _strokePainted.Add(renderer) && MaterialPaintPolicy.Allows(renderer.Slot))
                Apply(renderer, -1, material);
        }
    }

    // Through a relay: the surfaces it stands for, or in object and scene mode the material on its first one,
    // swapped everywhere it shows. A paint policy is asked for the surfaces painted directly, not for a swap.
    private bool PaintRelay(MeshRendererMaterialRelay relay, Slot hitSlot, IAssetProvider<MaterialAsset> material)
    {
        if (Mode.Value is MaterialToolMode.Object or MaterialToolMode.Scene)
        {
            var replaced = relay.FirstMaterial;
            if (replaced == null || ReferenceEquals(replaced, material))
                return false;
            var scope = Mode.Value == MaterialToolMode.Scene ? World?.RootSlot : ObjectOf(hitSlot);
            return scope != null && ReplaceEverywhere(scope, replaced, material) > 0;
        }
        return MaterialPaintPolicy.Allows(relay.Slot) && ApplyRelay(relay, material) > 0;
    }

    private int ApplyRelay(MeshRendererMaterialRelay relay, IAssetProvider<MaterialAsset> material)
    {
        int recorded = 0;
        Guarded(() =>
        {
            for (int i = 0; i < relay.Surfaces.Count; i++)
            {
                if (relay.Surfaces[i] is not { IsDestroyed: false } surface)
                    continue;
                var batch = AssignAt(surface, material);
                if (batch == null)
                    continue;
                recorded++;
                RecordUndo(batch);
            }
        });
        if (relay.Renderer.Target is { IsDestroyed: false } skinned)
            recorded += Apply(skinned, -1, material);
        return recorded;
    }

    // Puts the material on one surface, or on every surface for -1. Returns the undo steps it recorded.
    private int Apply(Component renderer, int surface, IAssetProvider<MaterialAsset> material)
    {
        int recorded = 0;
        Guarded(() => MaterialSurfaces.Apply(renderer, surface, material, batch =>
        {
            recorded++;
            RecordUndo(batch);
        }));
        return recorded;
    }

    // Every material reference under scope holding the old material gets the new one: renderers, particles,
    // UI, a sky, a switch, whatever wears one. One refusal skips that reference, not the rest.
    private int ReplaceEverywhere(Slot scope, IAssetProvider<MaterialAsset> replaced, IAssetProvider<MaterialAsset> material)
    {
        var references = new List<AssetRef<MaterialAsset>>();
        CollectMaterialReferences(scope, references);
        int recorded = 0;
        foreach (var element in references)
        {
            if (!ReferenceEquals(element.Target, replaced) || element is ILinkable { IsDriven: true })
                continue;
            IUndoBatch? batch = null;
            Guarded(() => batch = AssignAt(element, material));
            if (batch == null)
                continue;
            recorded++;
            RecordUndo(batch);
        }
        return recorded;
    }

    // What a swap must not reach is MaterialSurfaces'; this tool is one of them.
    private void CollectMaterialReferences(Slot scope, List<AssetRef<MaterialAsset>> into)
        => MaterialSurfaces.CollectReferences(scope, Slot, into);

    // The materials a hit on a renderer means: the one surface, or every one it wears when the surface is
    // unknown.
    private static List<IAssetProvider<MaterialAsset>> MaterialsOf(Component renderer, int surface)
    {
        var found = new List<IAssetProvider<MaterialAsset>>();
        void Add(IAssetProvider<MaterialAsset>? material)
        {
            if (material != null && !found.Contains(material))
                found.Add(material);
        }
        if (surface >= 0)
        {
            Add(MaterialAt(renderer, surface));
            return found;
        }
        switch (renderer)
        {
            case MeshRenderer mesh:
                for (int i = 0; i < mesh.Materials.Count; i++)
                    Add(mesh.Materials.GetElement(i).Target);
                break;
            case SkinnedMeshRenderer skinned:
                Add(skinned.Material.Target);
                for (int i = 0; i < skinned.Materials.Count; i++)
                    Add(skinned.Materials.GetElement(i).Target);
                break;
        }
        return found;
    }

    // A zone or a volume: only trigger colliders, and nothing to grab them by.
    private static bool IsBareTrigger(Slot slot)
    {
        bool trigger = false;
        foreach (var component in slot.Components)
        {
            if (component is not Collider { IsDestroyed: false } collider || !collider.Enabled.Value)
                continue;
            if (collider.Type.Value != ColliderType.Trigger)
                return false;
            trigger = true;
        }
        return trigger && slot.GetComponentInParents<Grabbable>() == null;
    }

    // SKY

    // The Skybox showing in this world, if one is.
    private Skybox? ActiveSkybox()
    {
        if (World?.RootSlot is not { } root)
            return null;
        foreach (var skybox in root.GetComponentsInChildren<Skybox>())
        {
            if (!skybox.IsDestroyed && skybox.IsActiveSkybox)
                return skybox;
        }
        return null;
    }

    // Onto the Skybox showing, else the first one, made the one showing, else a new one. A world on a
    // gradient sky has no Skybox; the new one outranks the gradient, and undo takes it away again.
    private bool PaintSky(ISkyboxMaterial material)
    {
        var root = World?.RootSlot;
        if (root == null)
            return false;
        var sky = ActiveSkybox();
        if (sky == null)
        {
            foreach (var skybox in root.GetComponentsInChildren<Skybox>())
            {
                if (!skybox.IsDestroyed && Paintable(skybox.Slot))
                {
                    sky = skybox;
                    break;
                }
            }
        }
        if (sky == null)
        {
            if (SpawnBlocked)
                return false;
            var slot = TryAddSlot(root, "Skybox");
            if (slot == null)
                return false;
            Guarded(() => sky = slot.AttachComponent<Skybox>());
            if (sky == null)
            {
                Guarded(() => slot.Destroy());
                return false;
            }
            RecordUndo(SlotExistenceUndoBatch.Created(World, new[] { slot }, UndoLocale.ApplyMaterial));
        }
        if (!Paintable(sky.Slot) || !MaterialPaintPolicy.Allows(sky.Slot))
            return false;

        int recorded = 0;
        if (!sky.IsActiveSkybox)
        {
            var priority = (IField)sky.Priority;
            object? before = priority.BoxedValue;
            Guarded(() => sky.MakeActive());
            if (!Equals(before, priority.BoxedValue))
            {
                recorded++;
                RecordUndo(new FieldEditUndoBatch(priority, before, priority.BoxedValue, UndoLocale.ApplyMaterial));
            }
        }
        IUndoBatch? batch = null;
        Guarded(() => batch = AssignAt(sky.Material, material));
        if (batch != null)
        {
            recorded++;
            RecordUndo(batch);
        }
        return recorded > 0;
    }

    // Nothing under the beam: an orb of the sky. A Skybox showing hands over its sky material; a gradient
    // sky has none, so the orb carries a new material that looks the same.
    private bool LoadSky()
    {
        var root = World?.RootSlot;
        if (root == null)
            return false;
        var skybox = ActiveSkybox();
        if (skybox != null)
            return skybox.SkyMaterial is { } worn && Load(worn) != null;
        foreach (var gradient in root.GetComponentsInChildren<GradientSkybox>())
        {
            if (gradient.IsDestroyed || !gradient.Enabled.Value || !gradient.Slot.IsActive)
                continue;
            return Load("Gradient Sky", slot =>
            {
                var copy = slot.AttachComponent<GradientSkyMaterial>();
                copy.CopyFrom(gradient);
                return copy;
            }) != null;
        }
        return false;
    }

    private static IUndoBatch? AssignAt(AssetRef<MaterialAsset> element, IAssetProvider<MaterialAsset> material)
        => MaterialSurfaces.AssignAt(element, material);

    // SAMPLING

    // Secondary loads the cartridge: an orb of the material under the laser, or of a material whose
    // reference card the same hand holds, made and seated in one go. Holding an object's card instead
    // pulls out every material under it as orbs. A loaded cartridge is left alone; Eject empties it.
    public override bool OnSecondaryPress()
    {
        var heldObject = HeldReference<Slot>();
        if (heldObject != null)
            return !Security.ItemSeals.RefusesHere(heldObject, Security.SealRule.Inspect) && ExtractAllMaterials(heldObject) > 0;
        if (HasOrb)
            return false;

        var material = HeldReference<IAssetProvider<MaterialAsset>>();
        // Where the material is taken from: a sealed object gives its materials to nobody but its owner.
        Slot? source = (material as Component)?.Slot;
        if (material == null)
        {
            if (!TryGetAim(out var aim) || aim.HitSlot == null)
                return LoadSky();
            if (FindRelay(aim.HitSlot) is { } relay)
            {
                material = relay.FirstMaterial;
                source = relay.Slot;
            }
            else if (FindRenderer(aim.HitSlot) is { } renderer)
            {
                int surface = SurfaceUnder(renderer, aim.HitSlot, aim.Point);
                material = MaterialAt(renderer, surface < 0 ? 0 : surface);
                source = renderer.Slot;
            }
        }
        if (material != null && Security.ItemSeals.RefusesHere(source, Security.SealRule.Inspect, inside: true))
            return false;
        return material != null && Load(material) != null;
    }

    // What the hand holding this tool also holds a reference card to, if anything.
    private T? HeldReference<T>() where T : class
    {
        var grabbed = ActiveTool?.Grabber?.GrabbedObjects;
        if (grabbed == null)
            return null;
        foreach (var held in grabbed)
        {
            if (held is Component component && component.Slot?.GetComponent<ReferenceProxy>()?.Target.Target is T target)
                return target;
        }
        return null;
    }

    // An orb for every material under the object, laid out in a grid at the tip. The orbs carry the
    // materials themselves, not copies, so editing one edits what the object wears.
    private int ExtractAllMaterials(Slot scope)
    {
        var root = World?.RootSlot;
        if (root == null || SpawnBlocked || ImmutableComponent.IsProtected(scope))
            return 0;
        var made = new List<Slot>();
        foreach (var material in MaterialsUnder(scope))
        {
            var slot = TryAddSlot(root, (material as Component)?.Slot?.Name.Value ?? "Material");
            if (slot != null && Guarded(() => MaterialOrb.Build(slot, material)))
                made.Add(slot);
        }
        if (made.Count > 0)
            RecordUndo(SlotExistenceUndoBatch.Created(World!, made.ToArray(), UndoLocale.ExtractMaterials));
        ArrangeAtTip(made);
        return made.Count;
    }

    // Every material worn under scope, each once, in the order they are met. Orbs are not counted.
    private List<IAssetProvider<MaterialAsset>> MaterialsUnder(Slot scope)
    {
        var found = new List<IAssetProvider<MaterialAsset>>();
        var seen = new HashSet<IAssetProvider<MaterialAsset>>();
        void Take(IAssetProvider<MaterialAsset>? material)
        {
            if (material != null && seen.Add(material))
                found.Add(material);
        }
        foreach (var mesh in scope.GetComponentsInChildren<MeshRenderer>())
        {
            if (!Paintable(mesh.Slot))
                continue;
            for (int i = 0; i < mesh.Materials.Count; i++)
                Take(mesh.Materials.GetElement(i).Target);
        }
        foreach (var skinned in scope.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (!Paintable(skinned.Slot))
                continue;
            Take(skinned.Material.Target);
            for (int i = 0; i < skinned.Materials.Count; i++)
                Take(skinned.Materials.GetElement(i).Target);
        }
        return found;
    }

    // A grid facing the user, centred on the tip, an orb's width and a bit apart.
    private void ArrangeAtTip(List<Slot> slots)
    {
        if (slots.Count == 0)
            return;
        const float spacing = 0.1125f;
        int columns = (int)MathF.Ceiling(MathF.Sqrt(slots.Count));
        int rows = (slots.Count + columns - 1) / columns;
        var head = World?.LocalUser?.Root?.HeadSlot;
        var right = head != null && !head.IsDestroyed ? head.GlobalRotation * float3.Right : float3.Right;
        right = new float3(right.x, 0f, right.z);
        right = right.LengthSquared > 1e-6f ? right.Normalized : float3.Right;
        var tip = Tip;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            float x = (i % columns - (columns - 1) * 0.5f) * spacing;
            float y = ((rows - 1) * 0.5f - i / columns) * spacing;
            Guarded(() => slot.GlobalPosition = tip + right * x + float3.Up * y);
        }
    }

    // CONVERTING

    // Turns materials into another kind. Create New opens on the material kinds; the one picked is made,
    // takes the old material's look, and replaces the old one wherever it was used. With an orb loaded
    // that is its material across the whole world, and the new orb is the converted one; with an object
    // held by its reference card it is every material under that object, each into an orb of its own laid
    // out at the tip.
    private void Convert(Slot? scope)
    {
        if (IsDestroyed || World == null || SpawnBlocked || Security.ItemSeals.RefusesHere(scope, Security.SealRule.Inspect))
            return;
        var single = scope == null ? LoadedMaterial as MaterialProvider : null;
        if (scope == null && single == null)
            return;
        var panel = CreateNewPanel.Spawn(World);
        if (panel == null)
            return;
        panel.CategoryPath.Value = "Materials";
        panel.OnCreated = made => Guarded(() => RunConversion(made, single, scope));
    }

    private void RunConversion(Slot made, MaterialProvider? single, Slot? scope)
    {
        var template = made?.GetComponentInChildren<MaterialProvider>();
        var root = World?.RootSlot;
        if (IsDestroyed || made == null || made.IsDestroyed || template == null || root == null)
            return;

        using (BeginUndoBatch(UndoLocale.ConvertMaterials))
        {
            if (scope == null)
            {
                if (single == null || single.IsDestroyed)
                    return;
                MaterialConversion.CopyProperties(single, template);
                ReplaceEverywhere(root, single, template);
                made.Name.Value = single.Slot?.Name.Value ?? made.Name.Value;
                return;
            }

            if (scope.IsDestroyed)
                return;
            var orbs = new List<Slot>();
            foreach (var old in MaterialsUnder(scope))
            {
                if (old is not MaterialProvider source)
                    continue;
                var copy = TryDuplicate(made, made.Parent ?? root);
                var converted = copy?.GetComponentInChildren<MaterialProvider>();
                if (copy == null || converted == null)
                    continue;
                MaterialConversion.CopyProperties(source, converted);
                ReplaceEverywhere(scope, old, converted);
                copy.Name.Value = source.Slot?.Name.Value ?? copy.Name.Value;
                orbs.Add(copy);
            }
            made.Destroy();
            ArrangeAtTip(orbs);
        }
    }

    // An orb of this material, seated in the cartridge.
    private MaterialOrb? Load(IAssetProvider<MaterialAsset> material)
        => Load((material as Component)?.Slot?.SlotName.Value ?? "Material", _ => material);

    // An orb of the material make gives it, made on the orb's own slot or found elsewhere.
    private MaterialOrb? Load(string name, Func<Slot, IAssetProvider<MaterialAsset>> make)
    {
        var socket = Cartridge;
        var root = World?.RootSlot;
        if (socket == null || root == null || SpawnBlocked)
            return null;

        var orbSlot = TryAddSlot(root, name);
        if (orbSlot == null)
            return null;
        MaterialOrb? orb = null;
        bool seated = false;
        if (!Guarded(() =>
            {
                orbSlot.GlobalPosition = socket.Slot.GlobalPosition;
                orb = MaterialOrb.Build(orbSlot, make(orbSlot));
                seated = orbSlot.GetComponent<Magnet>()?.AttachTo(socket) == true;
            }) || !seated)
        {
            Guarded(() => orbSlot.Destroy());
            return null;
        }
        return orb;
    }

    private void LoadStoredMaterial()
    {
        var stored = StoredMaterial.Target;
        if (stored == null || HasOrb || !MayShape())
            return;
        if (Load(stored) != null)
            Guarded(() => StoredMaterial.Target = null!);
    }

    // Out of the cartridge and left where it was, as an object of its own.
    private void Eject()
    {
        var socket = Cartridge;
        var magnet = socket?.Attached.Target;
        var orbSlot = magnet?.Slot;
        var root = World?.RootSlot;
        if (socket == null || magnet == null || orbSlot == null || root == null)
            return;
        Guarded(() =>
        {
            socket.ReleaseItem(magnet);
            orbSlot.SetParent(root, preserveGlobalTransform: true);
        });
    }

    // TARGETS

    // A relay at or above the hit stands for surfaces kept somewhere else, and is asked before any renderer:
    // a collider tree beside a mesh rather than under it has no renderer above it at all.
    private MeshRendererMaterialRelay? FindRelay(Slot? hitSlot)
    {
        var current = hitSlot;
        for (int depth = 0; current != null && depth < MaxParentWalk; depth++, current = current.Parent)
        {
            if (current.IsDestroyed || !Paintable(current))
                return null;
            var relay = current.GetComponent<MeshRendererMaterialRelay>();
            if (relay != null && !relay.IsDestroyed && relay.Enabled.Value && relay.StandsForAnything)
                return relay;
        }
        return null;
    }

    private Component? FindRenderer(Slot? hitSlot) => MaterialSurfaces.FindRenderer(hitSlot, Paintable);

    // Not an orb (the whole point of one is to keep its material; a shader graph orb is one too), not a
    // tool, this one or any other, not a protected subtree.
    private bool Paintable(Slot slot)
        => MaterialSurfaces.Paintable(slot) && (Slot == null || !(ReferenceEquals(slot, Slot) || slot.IsDescendantOf(Slot)));

    private static IAssetProvider<MaterialAsset>? MaterialAt(Component renderer, int surface) => MaterialSurfaces.MaterialAt(renderer, surface);

    private static int SurfaceUnder(Component renderer, Slot hitSlot, float3 point) => MaterialSurfaces.SurfaceUnder(renderer, hitSlot, point);

    private static Slot? ObjectOf(Slot hit) => MaterialSurfaces.ObjectOf(hit);

    // MENU

    public override void PopulateToolActions(ContextMenuPage page, ContextMenuContext context)
    {
        bool global = Mode.Value is MaterialToolMode.Object or MaterialToolMode.Scene;
        page.AddItem(new ContextMenuItem
        {
            Label = Mode.Value switch
            {
                MaterialToolMode.Area => "Mode: Area",
                MaterialToolMode.Object => "Mode: Object",
                MaterialToolMode.Scene => "Mode: Scene",
                _ => "Mode: Ray",
            },
            FillColor = global ? ClearFill : ActiveFill,
            OnPressed = _ =>
            {
                if (!IsDestroyed)
                    Guarded(() => Mode.Value = (MaterialToolMode)(((int)Mode.Value + 1) % 4));
            },
        });

        if (Mode.Value == MaterialToolMode.Area)
        {
            page.AddItem(new ContextMenuItem
            {
                Label = $"Size {AreaRadius.Value * 100f:0} cm",
                FillColor = ItemFill,
                OnPressed = _ =>
                {
                    if (IsDestroyed)
                        return;
                    int current = 0;
                    for (int i = 1; i < AreaSizes.Length; i++)
                    {
                        if (MathF.Abs(AreaSizes[i] - AreaRadius.Value) < MathF.Abs(AreaSizes[current] - AreaRadius.Value))
                            current = i;
                    }
                    Guarded(() => AreaRadius.Value = AreaSizes[(current + 1) % AreaSizes.Length]);
                },
            });
        }

        if (HasOrb)
        {
            // What can be done with the loaded material, together under its own name.
            var loaded = new ContextMenuPage("Material");
            loaded.AddItem(new ContextMenuItem { Label = "Edit", FillColor = ItemFill, OnPressed = _ => EditMaterial() });
            loaded.AddItem(new ContextMenuItem { Label = "Copy", FillColor = ItemFill, OnPressed = _ => CopyMaterial() });
            loaded.AddItem(new ContextMenuItem { Label = "Convert", FillColor = ArmFill, OnPressed = _ => Convert(null) });
            loaded.AddItem(new ContextMenuItem { Label = "Eject", FillColor = ClearFill, OnPressed = _ => { if (!IsDestroyed) Eject(); } });
            page.AddItem(new ContextMenuItem { Label = "Material", FillColor = ActiveFill, SubPage = loaded });
        }

        if (HeldReference<Slot>() is { } heldObject)
        {
            page.AddItem(new ContextMenuItem { Label = "Convert All", FillColor = ArmFill, OnPressed = _ => Convert(heldObject) });
        }

        page.AddItem(new ContextMenuItem
        {
            Label = "New",
            FillColor = ItemFill,
            OnPressed = _ =>
            {
                if (IsDestroyed || World == null)
                    return;
                var panel = CreateNewPanel.Spawn(World);
                if (panel != null)
                    panel.CategoryPath.Value = "Materials";
            },
        });
    }

    private void EditMaterial()
    {
        if (IsDestroyed || World == null || LoadedMaterial is not MaterialProvider material)
            return;
        InFrontOfUser(out var position, out var rotation);
        MaterialInspectorPanel.Spawn(World, material, position, rotation);
    }

    // A copy of the loaded material in an orb of its own, in front of the user.
    private void CopyMaterial()
    {
        var root = World?.RootSlot;
        if (IsDestroyed || root == null || SpawnBlocked || LoadedMaterial is not MaterialProvider source)
            return;
        var slot = TryAddSlot(root, $"{source.Slot?.SlotName.Value ?? "Material"} - Copy");
        if (slot == null)
            return;
        Guarded(() =>
        {
            var copy = (MaterialProvider)slot.AttachComponent(source.GetType());
            copy.CopyValues(source);
            MaterialOrb.Build(slot, copy);
            InFrontOfUser(out var position, out var rotation);
            slot.GlobalPosition = position;
            slot.GlobalRotation = rotation;
        });
    }

    // Half a metre out along the head's flattened forward, a little below the eyes, turned to face back.
    private void InFrontOfUser(out float3 position, out floatQ rotation)
    {
        var head = World?.LocalUser?.Root?.HeadSlot;
        if (head == null || head.IsDestroyed)
        {
            position = Slot?.GlobalPosition ?? float3.Zero;
            rotation = floatQ.Identity;
            return;
        }
        float3 forward = head.GlobalRotation * float3.Backward;
        forward.y = 0f;
        forward = forward.LengthSquared > 1e-6f ? forward.Normalized : float3.Backward;
        position = head.GlobalPosition + forward * 0.5f + new float3(0f, -0.12f, 0f);
        rotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(-forward.x, -forward.z));
    }
}
