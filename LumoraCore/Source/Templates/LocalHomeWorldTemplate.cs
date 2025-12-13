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
        lightSlot.LocalPosition.Value = new float3(0f, 5f, 0f);
        lightSlot.LocalRotation.Value = floatQ.Euler(0.785f, -0.785f, 0f);

        var groundSlot = world.RootSlot.AddSlot("Ground");
        groundSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        groundSlot.Tag.Value = "floor";

        var groundMesh = groundSlot.AttachComponent<BoxMesh>();
        groundMesh.Size.Value = new float3(20f, 0.1f, 20f);
        groundMesh.UVScale.Value = new float3(20f, 1f, 20f);

        var groundCollider = groundSlot.AttachComponent<BoxCollider>();
        groundCollider.Type.Value = ColliderType.Static;
        groundCollider.Size.Value = groundMesh.Size.Value;
        groundCollider.Offset.Value = new float3(0f, -groundMesh.Size.Value.y * 0.5f, 0f);

        CreateTestCube(world, "TestCube1", new float3(0f, 1f, 2f));
        CreateTestCube(world, "TestCube2", new float3(-2f, 1f, 2f));
        CreateTestCube(world, "TestCube3", new float3(2f, 1f, 2f));

        var ambientLightSlot = world.RootSlot.AddSlot("AmbientLight");
        ambientLightSlot.LocalPosition.Value = new float3(0f, 3f, 0f);

        var uiPanelsSlot = world.RootSlot.AddSlot("UIPanels");
        uiPanelsSlot.LocalPosition.Value = new float3(0f, 1.4f, -1.2f);

        // Godot-based UI panels
        AttachGodotUserInspectorPanel(uiPanelsSlot, new float3(-0.4f, 0f, 0f));
        AttachGodotEngineDebugPanel(uiPanelsSlot, new float3(0.5f, 0f, 0f));
    }

    private static void CreateTestCube(World world, string name, float3 position)
    {
        var cubeSlot = world.RootSlot.AddSlot(name);
        cubeSlot.LocalPosition.Value = position;

        var boxMesh = cubeSlot.AttachComponent<BoxMesh>();
        boxMesh.Size.Value = new float3(0.5f, 0.5f, 0.5f);
        boxMesh.UVScale.Value = new float3(1f, 1f, 1f);

        var collider = cubeSlot.AttachComponent<BoxCollider>();
        collider.Type.Value = ColliderType.Static;
        collider.Size.Value = boxMesh.Size.Value;

        Logger.Log($"WorldTemplates: Created test cube '{name}' at position {position}");
    }
}
