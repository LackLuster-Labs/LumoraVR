using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using Lumora.Core.Logging;

namespace Lumora.Core.Templates;

internal sealed class LocalHomeWorldTemplate : WorldTemplateDefinition
{
    public LocalHomeWorldTemplate() : base("LocalHome") { }

    protected override void Build(World world)
    {
        var spawnSlot = world.RootSlot.AddSlot("SpawnArea");
        spawnSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        spawnSlot.Tag.Value = "spawn";
        spawnSlot.AttachComponent<SimpleUserSpawn>();

        var lightSlot = world.RootSlot.AddSlot("DirectionalLight");
        lightSlot.LocalPosition.Value = new float3(0f, 10f, 0f);
        lightSlot.LocalRotation.Value = floatQ.Euler(0.785f, -0.785f, 0f);

        // Circular ground floor - 50 meter diameter
        var groundSlot = world.RootSlot.AddSlot("Ground");
        groundSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        groundSlot.Tag.Value = "floor";

        var groundMesh = groundSlot.AttachComponent<CylinderMesh>();
        groundMesh.Radius.Value = 25f;  // 50m diameter
        float groundHeight = 1.0f;
        groundMesh.Height.Value = groundHeight;
        groundMesh.Segments.Value = 64;
        groundMesh.UVScale.Value = new float2(25f, 25f);

        // Move the mesh down so the top surface sits at y=0
        groundSlot.LocalPosition.Value = new float3(0f, -groundHeight * 0.5f, 0f);

        var groundCollider = groundSlot.AttachComponent<CylinderCollider>();
        groundCollider.Type.Value = ColliderType.Static;
        groundCollider.Radius.Value = groundMesh.Radius.Value;
        groundCollider.Height.Value = groundMesh.Height.Value;
        groundCollider.Offset.Value = float3.Zero;

        // Create box playground area
        CreateBoxPlayground(world);

        var ambientLightSlot = world.RootSlot.AddSlot("AmbientLight");
        ambientLightSlot.LocalPosition.Value = new float3(0f, 5f, 0f);

        var uiPanelsSlot = world.RootSlot.AddSlot("UIPanels");
        uiPanelsSlot.LocalPosition.Value = new float3(0f, 1.4f, -1.2f);

        // Godot-based UI panels
        AttachGodotUserInspectorPanel(uiPanelsSlot, new float3(-0.4f, 0f, 0f));
        AttachGodotEngineDebugPanel(uiPanelsSlot, new float3(0.5f, 0f, 0f));

        // Add ClipboardImporter for paste functionality
        var clipboardSlot = world.RootSlot.AddSlot("ClipboardImporter");
        clipboardSlot.AttachComponent<ClipboardImporter>();
        Logger.Log("WorldTemplates: Added ClipboardImporter for paste functionality");

        // Add respawn plane below the world (larger than ground which is 50m diameter)
        var respawnSlot = world.RootSlot.AddSlot("RespawnPlane");

        var respawnPlane = respawnSlot.AttachComponent<RespawnPlane>();
        respawnPlane.Size.Value = new float2(100f, 100f); // 100m x 100m (bigger than 50m ground)
        respawnPlane.Height.Value = -20f; // 20 meters below ground
        respawnPlane.UseBounds.Value = false;
        respawnPlane.VisualColor.Value = new color(1f, 0.3f, 0.3f, 0.25f);
        respawnPlane.DebugColor.Value = new color(0.9f, 0.2f, 1f, 1f);
        respawnPlane.ShowVisual.Value = true;
        respawnPlane.ShowDebug.Value = true;
        respawnPlane.UserRespawnPosition.Value = new float3(0f, 1f, 0f); // Above spawn
        Logger.Log("WorldTemplates: Added respawn plane at y=-20 with 100m visual");
    }

    private static void CreateBoxPlayground(World world)
    {
        var playgroundSlot = world.RootSlot.AddSlot("BoxPlayground");
        playgroundSlot.LocalPosition.Value = new float3(8f, 0f, 8f);

        // Staircase of boxes
        for (int i = 0; i < 5; i++)
        {
            CreateBox(world, playgroundSlot, $"Stair_{i}",
                new float3(i * 0.6f, i * 0.3f + 0.15f, 0f),
                new float3(0.5f, 0.3f, 1.5f));
        }

        // Platform at top
        CreateBox(world, playgroundSlot, "Platform",
            new float3(3f, 1.65f, 0f),
            new float3(2f, 0.3f, 2f));

        // Tall column to jump on
        CreateBox(world, playgroundSlot, "Column1",
            new float3(-2f, 0.5f, 0f),
            new float3(1f, 1f, 1f));

        CreateBox(world, playgroundSlot, "Column2",
            new float3(-2f, 1.5f, 2f),
            new float3(0.8f, 0.8f, 0.8f));

        // Scattered boxes around
        CreateBox(world, playgroundSlot, "Box1", new float3(0f, 0.25f, 3f), new float3(0.5f, 0.5f, 0.5f));
        CreateBox(world, playgroundSlot, "Box2", new float3(1.5f, 0.25f, 4f), new float3(0.5f, 0.5f, 0.5f));
        CreateBox(world, playgroundSlot, "Box3", new float3(-1f, 0.25f, 5f), new float3(0.5f, 0.5f, 0.5f));
        CreateBox(world, playgroundSlot, "Box4", new float3(2f, 0.5f, 5.5f), new float3(1f, 1f, 1f));

        // Wall of boxes (dynamic - can be knocked over)
        // Stack tightly so gravity settles them naturally
        float boxSize = 0.5f;
        for (int x = 0; x < 4; x++)
        {
            for (int y = 0; y < 3; y++)
            {
                CreateDynamicBox(playgroundSlot, $"Wall_{x}_{y}",
                    new float3(-4f + x * boxSize, y * boxSize + boxSize * 0.5f, -2f),
                    new float3(boxSize, boxSize, boxSize), 1f);
            }
        }

        // Nearby cylinder for close-up mesh inspection
        CreateStaticCylinder(playgroundSlot, "DebugCylinder",
            new float3(2.5f, 0.5f, -1.5f),
            0.5f, 1.0f, 48);

        Logger.Log("WorldTemplates: Created box playground area");
    }

    private static void CreateStaticCylinder(Slot parent, string name, float3 position, float radius, float height, int segments)
    {
        var cylSlot = parent.AddSlot(name);
        cylSlot.LocalPosition.Value = position;

        var cylMesh = cylSlot.AttachComponent<CylinderMesh>();
        cylMesh.Radius.Value = radius;
        cylMesh.Height.Value = height;
        cylMesh.Segments.Value = segments;
        cylMesh.UVScale.Value = new float2(1f, 1f);

        var collider = cylSlot.AttachComponent<CylinderCollider>();
        collider.Type.Value = ColliderType.Static;
        collider.Radius.Value = radius;
        collider.Height.Value = height;
    }

    private static void CreateBox(World world, Slot parent, string name, float3 position, float3 size)
    {
        var boxSlot = parent.AddSlot(name);
        boxSlot.LocalPosition.Value = position;

        var boxMesh = boxSlot.AttachComponent<BoxMesh>();
        boxMesh.Size.Value = size;
        boxMesh.UVScale.Value = new float3(1f, 1f, 1f);

        var collider = boxSlot.AttachComponent<BoxCollider>();
        collider.Type.Value = ColliderType.Static;
        collider.Size.Value = size;
    }

    private static void CreateDynamicBox(Slot parent, string name, float3 position, float3 size, float mass)
    {
        var boxSlot = parent.AddSlot(name);
        boxSlot.LocalPosition.Value = position;

        var boxMesh = boxSlot.AttachComponent<BoxMesh>();
        boxMesh.Size.Value = size;
        boxMesh.UVScale.Value = new float3(1f, 1f, 1f);

        // Collider defines the shape (RigidBodyHook will use it)
        var collider = boxSlot.AttachComponent<BoxCollider>();
        collider.Type.Value = ColliderType.NoCollision; // RigidBody handles collision
        collider.Size.Value = size;

        // RigidBody enables physics simulation
        var rigidBody = boxSlot.AttachComponent<Components.RigidBody>();
        rigidBody.Mass.Value = mass;
        rigidBody.UseGravity.Value = true;

        // Make it grabbable
        boxSlot.AttachComponent<Grabbable>();

        // Store original position for respawning
        var respawnData = boxSlot.GetOrAttachComponent<RespawnData>();
        respawnData.OriginalPosition.Value = boxSlot.GlobalPosition;
        respawnData.OriginalRotation.Value = boxSlot.GlobalRotation;
    }
}
