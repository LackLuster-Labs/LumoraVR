using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;
using Lumora.Core.Physics;

namespace Lumora.Core.Templates;

internal sealed class SocialSpaceWorldTemplate : WorldTemplateDefinition
{
    public SocialSpaceWorldTemplate() : base("SocialSpace") { }

    protected override void Build(World world)
    {
        var spawnSlot = world.RootSlot.AddSlot("SpawnArea");
        spawnSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        spawnSlot.Tag.Value = "spawn";
        spawnSlot.AttachComponent<SimpleUserSpawn>();

        var lightSlot = world.RootSlot.AddSlot("DirectionalLight");
        lightSlot.LocalPosition.Value = new float3(0f, 8f, 0f);
        lightSlot.LocalRotation.Value = floatQ.Euler(0.6f, -0.4f, 0f);

        var ambientLightSlot = world.RootSlot.AddSlot("AmbientLight");
        ambientLightSlot.LocalPosition.Value = new float3(0f, 5f, 0f);

        var groundSlot = world.RootSlot.AddSlot("Ground");
        groundSlot.LocalPosition.Value = new float3(0f, 0f, 0f);
        groundSlot.Tag.Value = "floor";

        var groundMesh = groundSlot.AttachComponent<BoxMesh>();
        groundMesh.Size.Value = new float3(50f, 0.1f, 50f);
        groundMesh.UVScale.Value = new float3(50f, 1f, 50f);

        var groundCollider = groundSlot.AttachComponent<BoxCollider>();
        groundCollider.Type.Value = ColliderType.Static;
        groundCollider.Size.Value = groundMesh.Size.Value;
        groundCollider.Offset.Value = new float3(0f, -groundMesh.Size.Value.y * 0.5f, 0f);

        var uiPanelsSlot = world.RootSlot.AddSlot("UIPanels");
        uiPanelsSlot.LocalPosition.Value = new float3(0f, 1.4f, -2f);

        AttachUserInspectorPanel(uiPanelsSlot, new float3(-1.2f, 0f, 0f));
        AttachEngineDebugPanel(uiPanelsSlot, new float3(1.2f, 0f, 0f));
    }
}
