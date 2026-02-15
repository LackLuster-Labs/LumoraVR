using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Gizmos;
using Lumora.Core.Components.Meshes;
using Lumora.Core.GodotUI;
using Lumora.Core.GodotUI.Inspectors;
using Lumora.Core.GodotUI.Wizards;
using Lumora.Core;

namespace Aquamarine.Source.Godot.Bootstrap;

/// <summary>
/// Centralized registration for all Godot-specific hooks.
/// </summary>
public static class GodotHookRegistry
{
    public static void RegisterAll()
    {
        // Core slot hook (MUST be registered first!)
        World.HookTypes.Register<Slot, Aquamarine.Godot.Hooks.SlotHook>();

        // Mesh hooks
        World.HookTypes.Register<ProceduralMesh, Aquamarine.Godot.Hooks.MeshHook>();
        World.HookTypes.Register<BoxMesh, Aquamarine.Godot.Hooks.MeshHook>();
        World.HookTypes.Register<QuadMesh, Aquamarine.Godot.Hooks.MeshHook>();
        World.HookTypes.Register<CylinderMesh, Aquamarine.Godot.Hooks.MeshHook>();
        World.HookTypes.Register<SphereMesh, Aquamarine.Godot.Hooks.MeshHook>();

        // Renderer hooks
        World.HookTypes.Register<MeshRenderer, Aquamarine.Godot.Hooks.MeshRendererHook>();

        // Physics collider hooks
        World.HookTypes.Register<BoxCollider, Aquamarine.Godot.Hooks.PhysicsColliderHook>();
        World.HookTypes.Register<CapsuleCollider, Aquamarine.Godot.Hooks.PhysicsColliderHook>();
        World.HookTypes.Register<SphereCollider, Aquamarine.Godot.Hooks.PhysicsColliderHook>();
        World.HookTypes.Register<CylinderCollider, Aquamarine.Godot.Hooks.PhysicsColliderHook>();
        World.HookTypes.Register<RigidBody, Aquamarine.Godot.Hooks.RigidBodyHook>();
        World.HookTypes.Register<RespawnPlane, Aquamarine.Godot.Hooks.RespawnPlaneHook>();

        // Specialized hooks
        World.HookTypes.Register<SkeletonBuilder, Aquamarine.Godot.Hooks.SkeletonHook>();
        World.HookTypes.Register<SkinnedMeshRenderer, Aquamarine.Godot.Hooks.SkinnedMeshHook>();
        World.HookTypes.Register<Lumora.Core.Components.HeadOutput, Aquamarine.Godot.Hooks.HeadOutputHook>();
        World.HookTypes.Register<CharacterController, Aquamarine.Godot.Hooks.CharacterControllerHook>();
        World.HookTypes.Register<Lumora.Core.Components.Avatar.GodotIKAvatar, Aquamarine.Godot.Hooks.GodotIKAvatarHook>();

        // Godot UI hooks
        World.HookTypes.Register<GodotUIPanel, Aquamarine.Godot.Hooks.GodotUI.GodotUIPanelHook>();
        World.HookTypes.Register<DashboardPanel, Aquamarine.Godot.Hooks.GodotUI.DashboardPanelHook>();
        World.HookTypes.Register<GodotMaterialInspector, Aquamarine.Godot.Hooks.GodotUI.GodotMaterialInspectorHook>();
        World.HookTypes.Register<Nameplate, Aquamarine.Godot.Hooks.NameplateHook>();

        // Inspector hooks
        World.HookTypes.Register<SlotInspector, Aquamarine.Godot.Hooks.GodotUI.Inspectors.SlotInspectorHook>();
        World.HookTypes.Register<ComponentInspector, Aquamarine.Godot.Hooks.GodotUI.Inspectors.ComponentInspectorHook>();
        World.HookTypes.Register<SceneInspector, Aquamarine.Godot.Hooks.GodotUI.Inspectors.SceneInspectorHook>();
        World.HookTypes.Register<ComponentAttacher, Aquamarine.Godot.Hooks.GodotUI.Inspectors.ComponentAttacherHook>();

        // Gizmo hooks
        World.HookTypes.Register<SlotGizmo, Aquamarine.Godot.Hooks.Gizmos.SlotGizmoHook>();

        // Asset hooks
        AssetHookRegistry.Register<TextureAsset, Aquamarine.Godot.Hooks.TextureAssetHook>();
        AssetHookRegistry.Register<MaterialAsset, Aquamarine.Godot.Hooks.MaterialAssetHook>();
    }
}
