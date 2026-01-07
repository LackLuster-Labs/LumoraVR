using System;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Input;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components.Avatar;

/// <summary>
/// Creates a minimal default avatar (head + hands only).
/// </summary>
public static class DefaultAVI
{
    // Avatar dimensions
    public const float DEFAULT_HEIGHT = 1.7f;
    public const float HEAD_SIZE = 0.2f;
    public const float HAND_SIZE = 0.08f;
    private const float HEAD_FORWARD_OFFSET = 0.25f;
    private const float HAND_FORWARD_OFFSET = 0.18f;

    /// <summary>
    /// Create a default avatar on the given slot.
    /// </summary>
    public static void CreateDefaultAvatar(Slot avatarRoot, UserRoot userRoot)
    {
        if (avatarRoot == null)
        {
            AquaLogger.Error("DefaultAVI: Cannot create avatar - avatarRoot is null");
            return;
        }

        AquaLogger.Log($"DefaultAVI: Creating minimal avatar on '{avatarRoot.SlotName.Value}'");

        CreateHeadAndHands(userRoot);

        AquaLogger.Log("DefaultAVI: Default avatar created (head + hands)");
    }

    private static void CreateHeadAndHands(UserRoot userRoot)
    {
        if (userRoot == null)
        {
            AquaLogger.Warn("DefaultAVI: Cannot create head/hands visuals - UserRoot is null");
            return;
        }

        var tint = GetUserTint(userRoot.ActiveUser);
        CreateHeadVisual(userRoot.HeadSlot, tint);
        CreateHandVisual(userRoot.LeftHandSlot, "DefaultLeftHand", tint);
        CreateHandVisual(userRoot.RightHandSlot, "DefaultRightHand", tint);
    }

    private static void CreateHeadVisual(Slot headSlot, colorHDR tint)
    {
        if (headSlot == null)
        {
            AquaLogger.Warn("DefaultAVI: Head slot not found for head visual");
            return;
        }

        var headVisual = headSlot.FindChild("DefaultHead", recursive: false) ?? headSlot.AddSlot("DefaultHead");
        headVisual.LocalPosition.Value = float3.Zero;
        headVisual.LocalRotation.Value = floatQ.Identity;

        var headMesh = headVisual.GetComponent<SphereMesh>() ?? headVisual.AttachComponent<SphereMesh>();
        headMesh.Radius.Value = HEAD_SIZE * 0.5f;
        headMesh.Segments.Value = 24;
        headMesh.Rings.Value = 16;
        headMesh.DualSided.Value = true;

        ApplyDefaultMaterial(headVisual, headMesh, tint);
    }

    private static void CreateHandVisual(Slot handSlot, string name, colorHDR tint)
    {
        if (handSlot == null)
        {
            AquaLogger.Warn($"DefaultAVI: Hand slot not found for {name}");
            return;
        }

        var handVisual = handSlot.FindChild(name, recursive: false) ?? handSlot.AddSlot(name);
        handVisual.LocalPosition.Value = new float3(0f, -HAND_SIZE * 0.15f, -HAND_FORWARD_OFFSET);
        handVisual.LocalRotation.Value = floatQ.Identity;

        var handMesh = handVisual.GetComponent<BoxMesh>() ?? handVisual.AttachComponent<BoxMesh>();
        handMesh.Size.Value = new float3(HAND_SIZE, HAND_SIZE * 0.5f, HAND_SIZE * 1.2f);
        handMesh.UVScale.Value = new float3(1f, 1f, 1f);

        ApplyDefaultMaterial(handVisual, handMesh, tint);
    }

    private static void ApplyDefaultMaterial(Slot visualSlot, ProceduralMesh mesh, colorHDR tint)
    {
        var meshRenderer = visualSlot.GetComponent<MeshRenderer>() ?? visualSlot.AttachComponent<MeshRenderer>();
        meshRenderer.Mesh.Target = mesh;

        var material = visualSlot.GetComponent<UnlitMaterial>() ?? visualSlot.AttachComponent<UnlitMaterial>();
        material.TintColor.Value = tint;

        meshRenderer.Material.Target = material;
    }

    private static colorHDR GetUserTint(User user)
    {
        if (user == null)
        {
            return new colorHDR(0.8f, 0.8f, 0.8f, 1f);
        }

        var seed = user.UserID.Value;
        if (string.IsNullOrEmpty(seed)) seed = user.MachineID.Value;
        if (string.IsNullOrEmpty(seed)) seed = user.UserName.Value;
        if (string.IsNullOrEmpty(seed)) seed = user.ReferenceID.ToString();

        uint hash = Fnv1a(seed);
        float hue = (hash & 0xFFFF) / 65535f;
        float sat = 0.6f + ((hash >> 16) & 0xFF) / 255f * 0.3f;
        float val = 0.8f + ((hash >> 24) & 0xFF) / 255f * 0.2f;
        return colorHDR.FromHSV(hue, sat, val, 1f);
    }

    private static uint Fnv1a(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        for (int i = 0; i < value.Length; i++)
        {
            hash ^= value[i];
            hash *= prime;
        }
        return hash;
    }

    /// <summary>
    /// Create a complete user with default avatar.
    /// Called by SimpleUserSpawn or other spawn systems.
    /// </summary>
    public static void SpawnWithDefaultAvatar(Slot userSlot, User user)
    {
        if (userSlot == null || user == null)
        {
            AquaLogger.Error("DefaultAVI: Cannot spawn - userSlot or user is null");
            return;
        }

        // Create UserRoot
        var userRoot = userSlot.AttachComponent<UserRoot>();
        userRoot.Initialize(user);

        // Create body tracking slots
        var bodyNodes = userSlot.AddSlot("Body Nodes");

        // Create Head slot - UserRoot auto-resolves from "Body Nodes/Head"
        var headSlot = bodyNodes.AddSlot("Head");
        headSlot.LocalPosition.Value = new float3(0, DEFAULT_HEIGHT - 0.1f, 0);
        var headPositioner = headSlot.AttachComponent<TrackedDevicePositioner>();
        headPositioner.AutoBodyNode.Value = BodyNode.Head;
        var headStreamDriver = headSlot.AttachComponent<TransformStreamDriver>();
        user.GetTrackingStreams(BodyNode.Head, out var headPositionStream, out var headRotationStream);
        headStreamDriver.PositionStream.Target = headPositionStream;
        headStreamDriver.RotationStream.Target = headRotationStream;

        // Create LeftHand slot - UserRoot auto-resolves from "Body Nodes/LeftHand"
        // Default position: at rest by the side (not T-pose)
        var leftHandSlot = bodyNodes.AddSlot("LeftHand");
        leftHandSlot.LocalPosition.Value = new float3(-0.2f, 0.9f, 0.1f); // By left hip, slightly forward
        var leftHandPositioner = leftHandSlot.AttachComponent<TrackedDevicePositioner>();
        leftHandPositioner.AutoBodyNode.Value = BodyNode.LeftController;
        var leftHandStreamDriver = leftHandSlot.AttachComponent<TransformStreamDriver>();
        user.GetTrackingStreams(BodyNode.LeftController, out var leftPositionStream, out var leftRotationStream);
        leftHandStreamDriver.PositionStream.Target = leftPositionStream;
        leftHandStreamDriver.RotationStream.Target = leftRotationStream;

        // Create RightHand slot - UserRoot auto-resolves from "Body Nodes/RightHand"
        // Default position: at rest by the side (not T-pose)
        var rightHandSlot = bodyNodes.AddSlot("RightHand");
        rightHandSlot.LocalPosition.Value = new float3(0.2f, 0.9f, 0.1f); // By right hip, slightly forward
        var rightHandPositioner = rightHandSlot.AttachComponent<TrackedDevicePositioner>();
        rightHandPositioner.AutoBodyNode.Value = BodyNode.RightController;
        var rightHandStreamDriver = rightHandSlot.AttachComponent<TransformStreamDriver>();
        user.GetTrackingStreams(BodyNode.RightController, out var rightPositionStream, out var rightRotationStream);
        rightHandStreamDriver.PositionStream.Target = rightPositionStream;
        rightHandStreamDriver.RotationStream.Target = rightRotationStream;

        // Physics
        var collider = userSlot.AttachComponent<CapsuleCollider>();
        collider.Type.Value = Physics.ColliderType.CharacterController;
        collider.Height.Value = 1.8f;
        collider.Radius.Value = 0.3f;
        collider.Offset.Value = new float3(0, 0.9f, 0);

        userSlot.AttachComponent<CharacterController>();
        userSlot.AttachComponent<LocomotionController>();

        // Head output
        userSlot.AttachComponent<HeadOutput>();

        // Create avatar
        var avatarSlot = userSlot.AddSlot("Avatar");
        CreateDefaultAvatar(avatarSlot, userRoot);

        // Create nameplate
        var nameplateSlot = headSlot.AddSlot("Nameplate");
        nameplateSlot.LocalPosition.Value = float3.Zero;
        nameplateSlot.LocalRotation.Value = floatQ.Identity;
        var nameplate = nameplateSlot.AttachComponent<Nameplate>();
        nameplate.Initialize(user);

        AquaLogger.Log($"DefaultAVI: Spawned user '{user.UserName.Value}' with default avatar");
    }
}
