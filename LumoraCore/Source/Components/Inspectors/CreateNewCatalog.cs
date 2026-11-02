// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Import;

namespace Lumora.Core.Components;

// What "Create New" can put in a world, and the recipe for each.
//
// A flat static registry of (path, name, action) walked as a category tree. The CONTENTS are kept
// deliberately small, because an entry that spawns nothing is worse than an absent entry - the same
// rule the import dialog's dead checkboxes broke.
//
// The rule every recipe here obeys: a bare component is almost never a usable object. A mesh with no
// renderer draws nothing, a text renderer with no font renders zero glyphs, a
// light on an empty slot has no geometry to grab, and nothing is grabbable at all without a collider,
// because the laser resolves a grab target by walking up from a COLLIDER hit. So every entry attaches
// the whole set or it does not ship.
//
// Deliberately absent, each for a checked reason:
//   Camera        - attaching one steals the local view; the hook sets Current on the main viewport
//   Mirror/Portal - no reflected-camera path exists, and both need the camera repair first
//   FogVolume     - no fog volume component of any kind
//   ReverbZone    - a bare zone is an invisible slot, and nothing yet shows or grabs its radius
//   Legacy UI     - no legacy widget family
//   Spawn Area/Point - the user spawn resolves the FIRST area it finds, so a second one is a dead
//                   button in every world that already has one, which is both shipping templates
//   Torus, Grid   - the torus defaults to a 5 mm hairline (it is the rotation gizmo's ring) and the
//                   grid is a one-sided cloth resolution helper with no way to flip it
//   Mesh collider - it needs a mesh to point at, and a fresh slot has none
// -xlinka
public static class CreateNewCatalog
{
    public sealed class Entry
    {
        public string Name = string.Empty;
        public Action<Slot> Build = null!;
        public bool OpensInspector;
    }

    public sealed class Node
    {
        public string Name = string.Empty;
        public string Path = string.Empty;
        public readonly SortedDictionary<string, Node> Subcategories = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Entry> Entries = new();
    }

    public static readonly Node Root = new();

    public static void Add(string path, string name, Action<Slot> build, bool opensInspector = false)
    {
        var node = Root;
        foreach (var part in (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Subcategories.TryGetValue(part, out var child))
            {
                child = new Node
                {
                    Name = part,
                    Path = node.Path.Length == 0 ? part : node.Path + "/" + part,
                };
                node.Subcategories[part] = child;
            }
            node = child;
        }
        node.Entries.Add(new Entry { Name = name, Build = build, OpensInspector = opensInspector });
    }

    // Every kind of material as an orb of its own, under the category its component is filed in, so New and
    // Convert on a material tool can offer each one. -xlinka
    private static void AddMaterialKinds()
    {
        const string root = "Assets/Materials";
        var kinds = typeof(MaterialProvider).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(MaterialProvider).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (Type: t, Category: t.GetCustomAttribute<ComponentCategoryAttribute>()?.Category))
            .Where(k => k.Category != null && k.Category.StartsWith(root, StringComparison.Ordinal))
            .OrderBy(k => k.Category, StringComparer.Ordinal)
            .ThenBy(k => KindName(k.Type), StringComparer.Ordinal)
            .ToList();
        foreach (var (type, category) in kinds)
        {
            var kind = type;
            Add("Materials" + category!.Substring(root.Length), KindName(kind),
                slot => MaterialOrb.Build(slot, (MaterialProvider)slot.AttachComponent(kind)));
        }
    }

    // PBS_Metallic reads "PBS Metallic", XiexeToonMaterial "Xiexe Toon", UIUnlitMaterial "UI Unlit".
    private static string KindName(Type type)
    {
        var name = type.Name;
        if (name.EndsWith("Material", StringComparison.Ordinal) && name.Length > "Material".Length)
            name = name.Substring(0, name.Length - "Material".Length);
        name = name.Replace('_', ' ');
        return Regex.Replace(name, "(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Replace("  ", " ").Trim();
    }

    public static Node? GetNode(string? path)
    {
        var node = Root;
        foreach (var part in (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Subcategories.TryGetValue(part, out var child))
                return null;
            node = child;
        }
        return node;
    }

    static CreateNewCatalog()
    {
        // An empty draws nothing and has nothing for a laser to hit, so an inspector on it is the only
        // handle anyone gets.
        Add("", "Empty Object", _ => { }, opensInspector: true);

        // A bare particle system is NOT an object you spawned. With no emitter attached the system
        // falls back to its built-in disc spray over a 24.5 m half-extent, which is a world-wide
        // drizzle. Attaching an emitter turns that fallback off - and an emitter with no Rate set
        // emits nothing at all, because Rate has no default. Both halves are required.
        Add("", "Particle System", SpawnParticles);

        Add("Text", "Basic", slot => SpawnText(slot, outline: false));
        Add("Text", "Outline", slot => SpawnText(slot, outline: true));

        Add("Object", "Avatar Studio", SpawnAvatarStudio);
        Add("Object", "Seat", SpawnSeat);
        Add("Object", "Glow Marker", SpawnGlowMarker);
        Add("Object", "Reflection Probe", SpawnReflectionProbe);
        Add("Object", "Mirror", SpawnMirror);
        Add("Object", "Keyboard", SpawnKeyboard);

        // A blank material orb with its shader graph open above it, a Material Output ready to wire into.
        // The orb wears whatever the graph makes, and the material tool takes it to anything else.
        Add("Materials", "Shader Graph", slot => Lumora.Core.Scripting.ShaderGraphMaterials.BuildOrb(slot));
        AddMaterialKinds();

        // Editor entries open a wizard panel rather than spawning an object, so the slot the browser
        // made for the entry is dropped first.
        Add("Editor", "Lighting", slot =>
        {
            var world = slot.World;
            slot.Destroy();
            if (world != null)
                LightingWizard.Spawn(world);
        });

        Add("Editor", "Reflection Probes", slot =>
        {
            var world = slot.World;
            slot.Destroy();
            if (world != null)
                ReflectionProbeWizard.Spawn(world);
        });

        Add("Editor", "Loom Diagnostic", slot =>
        {
            var world = slot.World;
            slot.Destroy();
            if (world != null)
                LoomDiagnostic.Spawn(world);
        });

        Add("3D Model", "Box", slot => SpawnPrimitive(slot, ToolShape.Box));
        Add("3D Model", "Sphere", slot => SpawnPrimitive(slot, ToolShape.Sphere));
        Add("3D Model", "Capsule", slot => SpawnPrimitive(slot, ToolShape.Capsule));
        Add("3D Model", "Cone", slot => SpawnPrimitive(slot, ToolShape.Cone));
        Add("3D Model", "Cylinder", slot => SpawnPrimitive(slot, ToolShape.Cylinder));
        // Quad and Circle are single-sided by construction, so they MUST be double-sided here or a
        // flat shape spawned facing away from you renders nothing and reads as a broken button.
        Add("3D Model", "Quad", SpawnQuad);
        Add("3D Model", "Circle", slot => SpawnDisc(slot, segments: 32, name: "Circle"));
        // There is no triangle mesh. A three-segment disc IS one.
        Add("3D Model", "Triangle", slot => SpawnDisc(slot, segments: 3, name: "Triangle"));
        Add("3D Model", "Ring", SpawnRing);

        Add("Collider", "Box", slot => SpawnCollider(slot, ColliderKind.Box));
        Add("Collider", "Sphere", slot => SpawnCollider(slot, ColliderKind.Sphere));
        Add("Collider", "Capsule", slot => SpawnCollider(slot, ColliderKind.Capsule));
        Add("Collider", "Cylinder", slot => SpawnCollider(slot, ColliderKind.Cylinder));
        Add("Collider", "Cone", slot => SpawnCollider(slot, ColliderKind.Cone));

        Add("Light", "Point", slot => SpawnLight(slot, LightType.Point));
        Add("Light", "Spot", slot => SpawnLight(slot, LightType.Spot));
        Add("Light", "Directional", slot => SpawnLight(slot, LightType.Directional));

        // The tools branch is nearly free and it is the answer to "the tools are unreachable outside
        // one demo world": a tool item attaches its own grabbable and builds its own visual, so a bare
        // slot plus the component is already a pick-up-able tool lying in the world.
        Add("Tools", "Dev Tool", slot => SpawnTool<DevToolItem>(slot));
        Add("Tools", "Shape Tool", slot => SpawnTool<ShapeTool>(slot));
        Add("Tools", "Light Tool", slot => SpawnTool<LightTool>(slot));
        Add("Tools", "Material Tool", slot => SpawnTool<MaterialTool>(slot));
        Add("Tools", "Glue Tool", slot => SpawnTool<GlueTool>(slot));
        Add("Tools", "Duplicator", slot => SpawnTool<DuplicatorTool>(slot));
        Add("Tools", "Meter Tool", slot => SpawnTool<MeterTool>(slot));
        Add("Tools", "Script Node Tool", slot => SpawnTool<ScriptNodeTool>(slot));
        Add("Tools", "Camera", SpawnCamera);
        Add("Tools", "Camera Anchor", SpawnCameraAnchor);
        Add("Tools", "Camera Leave-Out Tool", slot => SpawnTool<CameraLeaveOutTool>(slot));
        foreach (var tip in Enum.GetValues<BrushTip>())
        {
            var pick = tip;
            Add("Tools/Brushes", BrushTool.PresetName(pick), slot => slot.AttachComponent<BrushTool>().UsePreset(pick));
        }
    }

    // RECIPES

    private const float DefaultSize = 0.25f;

    // Mirrors the renderer's TEMP_LAYER. Editor chrome lives here and cameras that should not see it
    // mask it out - which now includes reflection probes.
    private const int TempLayer = 1 << 2;

    private static readonly colorHDR DefaultTint = new(0.72f, 0.74f, 0.80f, 1f);

    // The primitive recipe is lifted from ShapeTool rather than re-derived, because that path is
    // already proven to produce a visible, grabbable, correctly-collided object.
    private static void SpawnPrimitive(Slot slot, ToolShape shape)
    {
        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = DefaultTint;
        material.Metallic.Value = 0.1f;
        material.Smoothness.Value = 0.35f;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Material.Target = material;
        renderer.Mesh.Target = BuildPrimitiveMesh(slot, shape, DefaultSize);
        BuildPrimitiveCollider(slot, shape, DefaultSize);
        slot.AttachComponent<Grabbable>();
    }

    private static Component BuildPrimitiveMesh(Slot slot, ToolShape shape, float size)
    {
        switch (shape)
        {
            case ToolShape.Sphere:
            {
                var mesh = slot.AttachComponent<SphereMesh>();
                mesh.Radius.Value = size * 0.5f;
                mesh.Segments.Value = 24;
                mesh.Rings.Value = 14;
                return mesh;
            }
            case ToolShape.Cylinder:
            {
                var mesh = slot.AttachComponent<CylinderMesh>();
                mesh.Radius.Value = size * 0.5f;
                mesh.Height.Value = size;
                mesh.Segments.Value = 24;
                return mesh;
            }
            case ToolShape.Cone:
            {
                var mesh = slot.AttachComponent<ConeMesh>();
                mesh.RadiusBase.Value = size * 0.5f;
                mesh.RadiusTop.Value = 0f;
                mesh.Height.Value = size;
                mesh.Segments.Value = 24;
                return mesh;
            }
            case ToolShape.Capsule:
            {
                var mesh = slot.AttachComponent<CapsuleMesh>();
                mesh.Radius.Value = size * 0.25f;
                mesh.Height.Value = size;
                mesh.Segments.Value = 20;
                return mesh;
            }
            default:
            {
                var mesh = slot.AttachComponent<BoxMesh>();
                mesh.Size.Value = float3.One * size;
                return mesh;
            }
        }
    }

    private static void BuildPrimitiveCollider(Slot slot, ToolShape shape, float size)
    {
        switch (shape)
        {
            case ToolShape.Sphere:
                slot.AttachComponent<SphereCollider>().Radius.Value = size * 0.5f;
                break;
            case ToolShape.Cylinder:
            {
                var collider = slot.AttachComponent<CylinderCollider>();
                collider.Radius.Value = size * 0.5f;
                collider.Height.Value = size;
                break;
            }
            case ToolShape.Cone:
            {
                var collider = slot.AttachComponent<ConeCollider>();
                collider.Radius.Value = size * 0.5f;
                collider.Height.Value = size;
                break;
            }
            case ToolShape.Capsule:
            {
                var collider = slot.AttachComponent<CapsuleCollider>();
                collider.Radius.Value = size * 0.25f;
                collider.Height.Value = size;
                break;
            }
            default:
                slot.AttachComponent<BoxCollider>().Size.Value = float3.One * size;
                break;
        }
    }

    private static void SpawnQuad(Slot slot)
    {
        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = DefaultTint;
        material.Metallic.Value = 0.1f;
        material.Smoothness.Value = 0.35f;
        material.Culling.Value = Culling.None;

        var mesh = slot.AttachComponent<QuadMesh>();
        mesh.Size.Value = new float2(DefaultSize, DefaultSize);
        // Two-sided comes from the material's culling below, NOT from DualSided: that flag builds a
        // second quad rotated 180 degrees, exactly coplanar with this one, and two surfaces at the same
        // depth fight over which is in front.
        mesh.DualSided.Value = false;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Material.Target = material;
        renderer.Mesh.Target = mesh;

        var collider = slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(DefaultSize, DefaultSize, 0.005f);
        // Flat and double-sided, so it must cast from both faces or its shadow vanishes
        // whenever the light is behind it.
        renderer.ShadowCastMode.Value = Components.ShadowCastMode.DoubleSided;
        slot.AttachComponent<Grabbable>();
    }

    private static void SpawnDisc(Slot slot, int segments, string name)
    {
        slot.SlotName.Value = name;

        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = DefaultTint;
        material.Metallic.Value = 0.1f;
        material.Smoothness.Value = 0.35f;
        // A disc is one-sided geometry, so the material has to draw both faces or half of every view
        // of it is empty.
        material.Culling.Value = Culling.None;

        var mesh = slot.AttachComponent<CircleMesh>();
        mesh.Radius.Value = DefaultSize * 0.5f;
        mesh.Segments.Value = segments;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Material.Target = material;
        renderer.Mesh.Target = mesh;

        var collider = slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(DefaultSize, DefaultSize, 0.005f);
        // Flat and double-sided, so it must cast from both faces or its shadow vanishes
        // whenever the light is behind it.
        renderer.ShadowCastMode.Value = Components.ShadowCastMode.DoubleSided;
        slot.AttachComponent<Grabbable>();
    }

    private static void SpawnRing(Slot slot)
    {
        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = DefaultTint;
        material.Metallic.Value = 0.1f;
        material.Smoothness.Value = 0.35f;
        material.Culling.Value = Culling.None;

        var mesh = slot.AttachComponent<RingMesh>();
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Material.Target = material;
        renderer.Mesh.Target = mesh;

        var collider = slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(DefaultSize, DefaultSize, 0.01f);
        // Flat and double-sided, so it must cast from both faces or its shadow vanishes
        // whenever the light is behind it.
        renderer.ShadowCastMode.Value = Components.ShadowCastMode.DoubleSided;
        slot.AttachComponent<Grabbable>();
    }

    private enum ColliderKind { Box, Sphere, Capsule, Cylinder, Cone }

    // A collider on its own is invisible, and an invisible thing you cannot see to grab is a dead
    // button. So it gets a translucent hull of the same shape - the object IS the collider, and you
    // can see and move it.
    private static void SpawnCollider(Slot slot, ColliderKind kind)
    {
        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorHDR(0.35f, 0.85f, 0.55f, 0.30f);
        material.BlendMode.Value = BlendMode.Alpha;
        material.Culling.Value = Culling.None;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Material.Target = material;

        switch (kind)
        {
            case ColliderKind.Sphere:
                renderer.Mesh.Target = BuildPrimitiveMesh(slot, ToolShape.Sphere, DefaultSize);
                slot.AttachComponent<SphereCollider>().Radius.Value = DefaultSize * 0.5f;
                break;
            case ColliderKind.Capsule:
            {
                renderer.Mesh.Target = BuildPrimitiveMesh(slot, ToolShape.Capsule, DefaultSize);
                var collider = slot.AttachComponent<CapsuleCollider>();
                collider.Radius.Value = DefaultSize * 0.25f;
                collider.Height.Value = DefaultSize;
                break;
            }
            case ColliderKind.Cylinder:
            {
                renderer.Mesh.Target = BuildPrimitiveMesh(slot, ToolShape.Cylinder, DefaultSize);
                var collider = slot.AttachComponent<CylinderCollider>();
                collider.Radius.Value = DefaultSize * 0.5f;
                collider.Height.Value = DefaultSize;
                break;
            }
            case ColliderKind.Cone:
            {
                renderer.Mesh.Target = BuildPrimitiveMesh(slot, ToolShape.Cone, DefaultSize);
                var collider = slot.AttachComponent<ConeCollider>();
                collider.Radius.Value = DefaultSize * 0.5f;
                collider.Height.Value = DefaultSize;
                break;
            }
            default:
                renderer.Mesh.Target = BuildPrimitiveMesh(slot, ToolShape.Box, DefaultSize);
                slot.AttachComponent<BoxCollider>().Size.Value = float3.One * DefaultSize;
                break;
        }

        slot.AttachComponent<Grabbable>();
    }

    // A light has no geometry of its own, so it spawns with a small emissive bulb you can actually
    // see and pick up. Directional is deliberately tamed: both shipping world templates already carry
    // a sun, and a second one at stock settings costs four full-map shadow splits out to 60 m.
    private static void SpawnLight(Slot slot, LightType type)
    {
        var light = slot.AttachComponent<Light>();
        light.Type.Value = type;

        if (type == LightType.Directional)
        {
            light.Intensity.Value = 0.35f;
            light.Shadows.Value = ShadowType.None;
        }
        else
        {
            light.Intensity.Value = 1.2f;
            light.Range.Value = 4f;
            // A lamp casts nearby and stops casting across the room. Thirty catalog lamps at stock
            // settings is thirty unbounded cube-shadow lights re-rendered forever; an unshadowed light
            // is close to free while its shadow is six face renders, so the shadow is what fades.
            light.ShadowFadeDistance.Value = 8f;
        }

        var bulb = slot.AddSlot("Bulb");
        var material = bulb.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorHDR(0.1f, 0.1f, 0.1f, 1f);
        material.EmissiveColor.Value = new colorHDR(1.6f, 1.5f, 1.15f, 1f);

        var mesh = bulb.AttachComponent<SphereMesh>();
        mesh.Radius.Value = 0.03f;
        mesh.Segments.Value = 16;
        mesh.Rings.Value = 10;

        var renderer = bulb.AttachComponent<MeshRenderer>();
        renderer.Material.Target = material;
        renderer.Mesh.Target = mesh;

        slot.AttachComponent<SphereCollider>().Radius.Value = 0.05f;
        slot.AttachComponent<Grabbable>();
    }

    private static void SpawnText(Slot slot, bool outline)
    {
        var text = slot.AttachComponent<TextRenderer>();
        text.Text.Value = "Text";
        text.Size.Value = 0.1f;
        text.Color.Value = new color(1f, 1f, 1f, 1f);
        // A text renderer with no font resolves to an empty mesh and renders literally nothing, so
        // the shared world font is not optional here.
        if (SharedFont(slot.World) is { } font)
            text.Font.Target = font;

        if (outline)
        {
            text.OutlineColor.Value = new colorHDR(0f, 0f, 0f, 0.9f);
            text.OutlineThickness.Value = 1f;
        }

        var collider = slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(0.4f, 0.14f, 0.01f);
        var grabbable = slot.AttachComponent<Grabbable>();
        grabbable.Scalable.Value = true;
    }

    // One font provider per world, parked on a shared slot, so twenty labels do not each fetch their
    // own copy of the same face.
    private static FontProvider? SharedFont(World? world)
    {
        if (world?.RootSlot == null)
            return null;

        var shared = world.RootSlot.FindChildOrAdd("Shared Assets");
        var provider = shared.GetComponent<FontProvider>();
        if (provider != null)
            return provider;

        provider = shared.AttachComponent<FontProvider>();
        var url = ImportDialog.ResolveFontUrl(world);
        if (url != null)
            provider.URL.Value = url;
        return provider;
    }

    private static void SpawnParticles(Slot slot)
    {
        var system = slot.AttachComponent<ParticleSystem>();
        system.MaxParticles.Value = 240;

        // The emitter is mandatory. Without one the system runs its built-in disc spray over a 24.5 m
        // half-extent, which is a world-sized drizzle rather than the object you asked for. And an
        // emitter with no Rate emits nothing, because Rate has no default value.
        var nozzle = slot.AddSlot("Nozzle");
        var emitter = nozzle.AttachComponent<ConeEmitter>();
        emitter.System.Target = system;
        emitter.Rate.Value = 110f;
        emitter.Radius.Value = 0.05f;
        emitter.Angle.Value = 13f;

        // Keep a handle for direct grabs, but let pointing rays pass through the effect.
        var handle = slot.AttachComponent<SphereCollider>();
        handle.Radius.Value = 0.1f;
        handle.IgnoreRaycasts.Value = true;
        handle.CharacterCollider.Value = false;
        slot.AttachComponent<Grabbable>();
    }

    private static void SpawnAvatarStudio(Slot slot)
    {
        slot.AttachComponent<Avatar.AvatarStudio>();
    }

    private static void SpawnSeat(Slot slot)
    {
        var material = slot.AttachComponent<PBS_Metallic>();
        material.AlbedoColor.Value = new colorHDR(0.30f, 0.32f, 0.38f, 1f);
        material.Metallic.Value = 0.2f;
        material.Smoothness.Value = 0.4f;

        var mesh = slot.AttachComponent<BoxMesh>();
        mesh.Size.Value = new float3(0.6f, 0.08f, 0.5f);

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Material.Target = material;
        renderer.Mesh.Target = mesh;

        var collider = slot.AttachComponent<BoxCollider>();
        collider.Size.Value = new float3(0.6f, 0.08f, 0.5f);
        collider.Type.Value = ColliderType.Static;

        var seat = slot.AttachComponent<Avatar.Seat>();
        seat.PreserveUpOnExit.Value = true;
    }

    private static void SpawnGlowMarker(Slot slot)
    {
        var glow = slot.AttachComponent<GlowCircle>();
        glow.Setup(0.5f, 0.08f, new colorHDR(0.35f, 0.85f, 1f, 1f));
    }

    private static void SpawnReflectionProbe(Slot slot)
    {
        var probe = slot.AttachComponent<ReflectionProbe>();
        probe.UpdateMode.Value = ProbeUpdateMode.Always;
        probe.DebugVisual.Value = true;

        // ReflectionProbe owns a local chrome preview, excluded from capture, and wire bounds.
        // Grab the probe itself so its capture point follows the sphere.
        slot.AttachComponent<SphereCollider>().Radius.Value = 0.15f;
        slot.AttachComponent<Grabbable>();
    }

    // Turned to whoever made it, so the first camera sent there films them.
    private static void SpawnCameraAnchor(Slot slot)
    {
        var head = slot.World?.LocalUser?.Root?.HeadPosition;
        var toward = head.HasValue ? head.Value - slot.GlobalPosition : float3.Zero;
        if (toward.LengthSquared > 1e-6f)
            slot.GlobalRotation = Lumora.Core.Photos.PhotoShot.Looking(toward.Normalized, float3.Up);
        slot.AttachComponent<CameraAnchor>();
    }

    // The VR keyboard as an object to change with the usual tools: the built-in layout standing here, turned to
    // whoever made it. Saved and made a favourite, it is the keyboard that comes up when they type in VR.
    private static void SpawnKeyboard(Slot slot)
    {
        UI.VrKeyboardLayout.Build(slot);
        slot.LocalScale.Value = float3.One * UI.VrKeyboardLayout.PanelScale;
        var head = slot.World?.LocalUser?.Root?.HeadPosition;
        var toViewer = head.HasValue ? head.Value - slot.GlobalPosition : float3.Zero;
        toViewer.y = 0f;
        if (toViewer.LengthSquared > 1e-6f)
            slot.GlobalRotation = floatQ.AxisAngleRad(float3.Up, MathF.Atan2(toViewer.x, toViewer.z));
    }

    // A tool item attaches its own grabbable and builds its own visual, so this really is one line.
    private static void SpawnTool<T>(Slot slot) where T : ToolItem, new()
    {
        slot.AttachComponent<T>();
    }

    // The maker's favourite camera or mirror when they have one. It can come a moment later, after its
    // fetch, so what is attached then is attached in the maker's own allocation again.
    private static void SpawnCamera(Slot slot) => FavouriteSpawn.Fill<PhotoCamera>(slot, FavouriteKind.Camera, camera =>
    {
        if (slot.IsDestroyed)
            return;
        using var own = slot.World.EnterSpawnAllocation();
        if (camera == null)
            slot.AttachComponent<PhotoCamera>();
        else
            camera.Operator.Target = slot.World.LocalUser!;
    });

    private static void SpawnMirror(Slot slot) => FavouriteSpawn.Fill<Mirror>(slot, FavouriteKind.Mirror, mirror =>
    {
        if (slot.IsDestroyed || mirror != null)
            return;
        using var own = slot.World.EnterSpawnAllocation();
        Mirror.SetUp(slot);
    });
}
