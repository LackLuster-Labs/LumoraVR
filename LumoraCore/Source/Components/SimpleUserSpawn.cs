using System;
using Lumora.Core;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Input;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Spawns users at this slot's position when they join the world.
/// Creates UserRoot hierarchy and avatar.
///
/// Flow:
/// 1. TrackedDevicePositioner reads device input and creates AvatarObjectSlot
/// 2. AvatarPoseNode on skeleton bones equips to AvatarObjectSlot
/// 3. AvatarPoseNode directly drives bone transforms
/// </summary>
[ComponentCategory("Users")]
public class SimpleUserSpawn : Component, IWorldEventReceiver
{
    public override void OnAwake()
    {
        base.OnAwake();
        World?.RegisterEventReceiver(this);
    }

    public override void OnDestroy()
    {
        World?.UnregisterEventReceiver(this);
        base.OnDestroy();
    }

    public bool HasEventHandler(World.WorldEvent eventType)
    {
        return eventType == World.WorldEvent.OnUserJoined ||
               eventType == World.WorldEvent.OnUserLeft;
    }

    public void OnUserJoined(User user)
    {
        if (user == null) return;

        try
        {
            // === 1. CREATE USER ROOT SLOT ===
            var userSlot = World.RootSlot.AddSlot($"User {user.UserName.Value}");
            userSlot.Persistent.Value = false;
            userSlot.LocalPosition.Value = Slot.LocalPosition.Value;
            userSlot.LocalRotation.Value = Slot.LocalRotation.Value;

            // === 2. ATTACH USERROOT COMPONENT ===
            var userRoot = userSlot.AttachComponent<UserRoot>();
            userRoot.Initialize(user);
            user.UserRootSlot = userSlot;

            // === 3. CREATE TRACKED DEVICE SLOTS (Body Nodes) ===
            // Each TrackedDevicePositioner creates an AvatarObjectSlot that tracking data flows through
            var bodyNodes = userSlot.AddSlot("Body Nodes");

            // Head - this is where VR headset position goes
            var headSlot = bodyNodes.AddSlot("Head");
            headSlot.LocalPosition.Value = new float3(0, 1.7f, 0); // Default standing height
            var headPositioner = headSlot.AttachComponent<TrackedDevicePositioner>();
            headPositioner.AutoBodyNode.Value = BodyNode.Head;

            // Left Hand/Controller
            var leftHandSlot = bodyNodes.AddSlot("LeftHand");
            leftHandSlot.LocalPosition.Value = new float3(-0.3f, 1.0f, 0.3f);
            var leftHandPositioner = leftHandSlot.AttachComponent<TrackedDevicePositioner>();
            leftHandPositioner.AutoBodyNode.Value = BodyNode.LeftController;

            // Right Hand/Controller
            var rightHandSlot = bodyNodes.AddSlot("RightHand");
            rightHandSlot.LocalPosition.Value = new float3(0.3f, 1.0f, 0.3f);
            var rightHandPositioner = rightHandSlot.AttachComponent<TrackedDevicePositioner>();
            rightHandPositioner.AutoBodyNode.Value = BodyNode.RightController;

            // === 4. PHYSICS / MOVEMENT ===
            var collider = userSlot.AttachComponent<CapsuleCollider>();
            collider.Type.Value = ColliderType.CharacterController;
            collider.Height.Value = 1.8f;
            collider.Radius.Value = 0.3f;
            collider.Offset.Value = new float3(0, 0.9f, 0);

            userSlot.AttachComponent<CharacterController>();
            userSlot.AttachComponent<LocomotionController>();

            // === 5. HEAD OUTPUT (Camera) ===
            var headOutput = userSlot.AttachComponent<HeadOutput>();

            // === 6. BUILD SIMPLE AVATAR ===
            // Just head and hands for tracking test
            BuildSimpleAvatar(headSlot, leftHandSlot, rightHandSlot);

            // === 7. CREATE NAMEPLATE ===
            CreateNameplate(headSlot, user);
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"SimpleUserSpawn: Failed to spawn '{user.UserName.Value}': {ex.Message}\n{ex.StackTrace}");
        }
    }

    public void OnUserLeft(User user)
    {
        if (user?.UserRootSlot == null) return;
        user.UserRootSlot.Destroy();
        user.UserRootSlot = null;
    }

    /// <summary>
    /// Build a simple avatar with visual meshes that follow tracking slots directly.
    /// Just head and hands for now - tracking test only.
    /// </summary>
    private void BuildSimpleAvatar(Slot headSlot, Slot leftHandSlot, Slot rightHandSlot)
    {
        // === HEAD VISUAL ===
        // Child of head tracking slot so it follows head movement
        var headVisual = headSlot.AddSlot("HeadVisual");
        headVisual.LocalPosition.Value = float3.Zero;
        headVisual.LocalRotation.Value = floatQ.Identity;
        AddBoxMesh(headVisual, new float3(0.18f, 0.18f, 0.18f));

        // === HAND VISUALS ===
        var leftHandVisual = leftHandSlot.AddSlot("LeftHandVisual");
        leftHandVisual.LocalPosition.Value = float3.Zero;
        AddBoxMesh(leftHandVisual, new float3(0.08f, 0.08f, 0.15f));

        var rightHandVisual = rightHandSlot.AddSlot("RightHandVisual");
        rightHandVisual.LocalPosition.Value = float3.Zero;
        AddBoxMesh(rightHandVisual, new float3(0.08f, 0.08f, 0.15f));
    }

    /// <summary>
    /// Add a box mesh to a slot for visualization.
    /// </summary>
    private void AddBoxMesh(Slot slot, float3 size)
    {
        var renderer = slot.AttachComponent<MeshRenderer>();
        var mesh = slot.AttachComponent<Lumora.Core.Components.Meshes.BoxMesh>();
        mesh.Size.Value = size;
        renderer.Mesh.Value = mesh;
    }

    /// <summary>
    /// Create a nameplate above the user's head.
    /// </summary>
    private void CreateNameplate(Slot headSlot, User user)
    {
        // Create nameplate slot as child of head slot
        var nameplateSlot = headSlot.AddSlot("Nameplate");
        nameplateSlot.LocalPosition.Value = new float3(0, 0.25f, 0); // Above head

        // Attach nameplate component
        var nameplate = nameplateSlot.AttachComponent<Nameplate>();
        nameplate.Initialize(user);

        AquaLogger.Log($"SimpleUserSpawn: Created nameplate for '{user.UserName.Value}'");
    }

    // Unused interface methods
    public void OnFocusChanged(World.WorldFocus focus) { }
    public void OnWorldDestroy() { }
}
