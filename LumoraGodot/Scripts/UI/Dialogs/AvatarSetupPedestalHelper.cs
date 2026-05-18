// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Math;
using LumoraMeshes = Lumora.Core.Components.Meshes;

namespace Lumora.Godot.UI;

/// <summary>
/// Static helpers that build the 3D geometry for the avatar setup pedestal.
/// Called by <see cref="ImportDialog"/> (AvatarSetup partial).
/// </summary>
internal static class AvatarSetupPedestalHelper
{
    /// <summary>Create a single cylindrical pedestal section (base, column, or top plate).</summary>
    internal static void CreatePedestalPart(Slot parent, string name, float3 localPosition, float radius, float height, colorHDR color)
    {
        var slot = parent.AddSlot(name);
        slot.LocalPosition.Value = localPosition;
        slot.LocalRotation.Value = floatQ.Identity;

        var mesh = slot.AttachComponent<LumoraMeshes.CylinderMesh>();
        mesh.Radius.Value = radius;
        mesh.Height.Value = height;
        mesh.Segments.Value = 32;

        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;

        var material = slot.AttachComponent<UnlitMaterial>();
        material.Color = color;
        renderer.Material.Target = material;
    }

    /// <summary>
    /// Create a grabbable sphere marker with a direction arrow attached to it.
    /// Returns the marker slot so the caller can align it to a bone.
    /// </summary>
    internal static Slot CreateSetupMarker(Slot parent, string name, colorHDR color, float3 defaultLocalPosition)
    {
        var marker = parent.AddSlot(name);
        marker.LocalPosition.Value = defaultLocalPosition;
        marker.LocalRotation.Value = floatQ.Identity;

        marker.AttachComponent<Grabbable>();

        var collider = marker.AttachComponent<SphereCollider>();
        collider.Radius.Value = 0.065f;

        var sphere = marker.AttachComponent<LumoraMeshes.SphereMesh>();
        sphere.Radius.Value = 0.055f;
        sphere.Segments.Value = 24;
        sphere.Rings.Value = 16;

        var renderer = marker.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = sphere;

        var material = marker.AttachComponent<UnlitMaterial>();
        material.Color = color;
        material.BlendMode.Value = BlendMode.Transparent;
        renderer.Material.Target = material;

        CreateDirectionArrow(marker, color);
        return marker;
    }

    /// <summary>Build the small cylinder+cube direction arrow attached to a marker.</summary>
    internal static void CreateDirectionArrow(Slot markerSlot, colorHDR color)
    {
        var arrowRoot = markerSlot.AddSlot("DirectionArrow");
        arrowRoot.LocalPosition.Value = float3.Zero;
        arrowRoot.LocalRotation.Value = floatQ.Identity;

        // Shaft
        var shaft = arrowRoot.AddSlot("Shaft");
        shaft.LocalPosition.Value = new float3(0f, 0f, 0.08f);
        shaft.LocalRotation.Value = floatQ.FromEuler(new float3(MathF.PI * 0.5f, 0f, 0f));

        var shaftMesh = shaft.AttachComponent<LumoraMeshes.CylinderMesh>();
        shaftMesh.Radius.Value = 0.012f;
        shaftMesh.Height.Value = 0.16f;
        shaftMesh.Segments.Value = 16;

        var shaftRenderer = shaft.AttachComponent<MeshRenderer>();
        shaftRenderer.Mesh.Target = shaftMesh;

        var shaftMat = shaft.AttachComponent<UnlitMaterial>();
        shaftMat.Color = color;
        shaftRenderer.Material.Target = shaftMat;

        // Tip
        var tip = arrowRoot.AddSlot("Tip");
        tip.LocalPosition.Value = new float3(0f, 0f, 0.18f);
        tip.LocalRotation.Value = floatQ.Identity;

        var tipMesh = tip.AttachComponent<LumoraMeshes.BoxMesh>();
        tipMesh.Size.Value = new float3(0.04f, 0.04f, 0.04f);

        var tipRenderer = tip.AttachComponent<MeshRenderer>();
        tipRenderer.Mesh.Target = tipMesh;

        var tipMat = tip.AttachComponent<UnlitMaterial>();
        tipMat.Color = color;
        tipRenderer.Material.Target = tipMat;
    }

    /// <summary>
    /// Snap a marker to a bone's world position/rotation, expressed in avatar-local space.
    /// If <paramref name="bone"/> is null the marker keeps its default position.
    /// </summary>
    internal static void ApplyMarkerFromBone(Slot avatarRoot, Slot marker, Slot bone, float3 fallbackForward, float forwardOffset = 0f)
    {
        if (avatarRoot == null || marker == null || bone == null || bone.IsDestroyed)
            return;

        var boneGlobalPos = bone.GlobalPosition;
        var boneGlobalRot = bone.GlobalRotation;

        if (forwardOffset != 0f)
        {
            var forward = boneGlobalRot * fallbackForward;
            if (forward.LengthSquared > 0.0001f)
                boneGlobalPos += forward.Normalized * forwardOffset;
        }

        marker.LocalPosition.Value = avatarRoot.GlobalPointToLocal(boneGlobalPos);
        marker.LocalRotation.Value = avatarRoot.GlobalRotation.Inverse * boneGlobalRot;
    }
}
