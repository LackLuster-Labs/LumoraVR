// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// The camera's view volume: near and far rectangles joined at the corners, tapering for a perspective
// camera and parallel for an orthographic one. Pads on both clip planes.
//
// Opens along the slot's local -Z, which is where a camera looks. FieldOfView is the full VERTICAL
// angle and OrthographicSize the vertical extent in world units, both matching what the platform
// camera is fed, so the width comes from the camera's own aspect rather than a guess - a frustum drawn
// square when the viewport is not would be worse than drawing nothing.
//
// The far pad refuses to cross the near plane and the near pad refuses to cross the far one, because
// an inverted volume is not a shape and the projection built from it is undefined. -xlinka
[ComponentCategory("Utility/Gizmos")]
[GizmoForComponent(typeof(Camera))]
public class CameraGizmo : ComponentGizmo
{
    private ExtentHandle? _near;
    private ExtentHandle? _far;

    // Cool blue-white, distinct from the light and collider gizmos.
    protected override color BaseTint => new(0.55f, 0.8f, 1f, 0.85f);

    protected override void CollectShapeFields(List<IChangeable> fields)
    {
        if (TargetAs<Camera>() is not { } camera)
            return;
        fields.Add(camera.Projection);
        fields.Add(camera.FieldOfView);
        fields.Add(camera.OrthographicSize);
        fields.Add(camera.NearClip);
        fields.Add(camera.FarClip);
        fields.Add(camera.RotationOffset);
    }

    // The lens's own turn from its slot (see Camera.RotationOffset); the volume and pads follow it.
    private static floatQ Turn(Camera camera)
    {
        var q = camera.RotationOffset.Value;
        return q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f ? floatQ.Identity : q.Normalized;
    }

    protected override void BuildWire(GizmoWireBuilder wire)
    {
        if (TargetAs<Camera>() is not { } camera)
            return;

        float near = MathF.Max(camera.NearClip.Value, MinimumClip);
        float far = MathF.Max(camera.FarClip.Value, near + MinimumClip);
        float aspect = camera.AspectRatio > 0f ? camera.AspectRatio : DefaultAspect;
        var turn = Turn(camera);

        if (camera.Projection.Value == ProjectionType.Orthographic)
        {
            wire.OrthoVolume(float3.Zero, turn * float3.Backward, turn * float3.Up, turn * float3.Right,
                MathF.Max(camera.OrthographicSize.Value, MinimumClip) * 0.5f, aspect, near, far);
        }
        else
        {
            wire.Frustum(float3.Zero, turn * float3.Backward, turn * float3.Up, turn * float3.Right,
                camera.FieldOfView.Value, aspect, near, far);
        }

        // A tick at the eye point, so the apex is findable when the near plane is far enough out that
        // the volume no longer reads as coming from anywhere.
        wire.Cross(float3.Zero, EyeMarkerSize);
    }

    protected override void BuildHandles()
    {
        if (TargetAs<Camera>() is not { } camera)
            return;

        _near = AddHandle("NearClip", float3.Backward, camera.NearClip, 1f, GizmoMaterialKind.AxisZ, MinimumClip);
        _near.ValueName.Value = "Near Clip";
        _far = AddHandle("FarClip", float3.Backward, camera.FarClip, 1f, GizmoMaterialKind.Center, MinimumClip);
        _far.ValueName.Value = "Far Clip";
    }

    protected override void LayoutHandles()
    {
        if (TargetAs<Camera>() is not { } camera)
            return;

        float near = MathF.Max(camera.NearClip.Value, MinimumClip);
        float far = MathF.Max(camera.FarClip.Value, near + MinimumClip);

        // Each plane clamps against the other, re-applied on every layout so the limit tracks whatever
        // the other pad was just dragged to.
        _near!.MaxValue.Value = MathF.Max(far - MinimumClip, MinimumClip);
        _far!.MinValue.Value = near + MinimumClip;

        // On the bottom edge of each rectangle rather than the middle: the centre of a near plane is
        // usually inside the camera's own body.
        var turn = Turn(camera);
        _near.LocalAxis.Value = turn * float3.Backward;
        _far.LocalAxis.Value = turn * float3.Backward;
        PlaceHandle(_near, turn * (float3.Backward * near - new float3(0f, HalfHeight(camera, near), 0f)));
        PlaceHandle(_far, turn * (float3.Backward * far - new float3(0f, HalfHeight(camera, far), 0f)));
    }

    private static float HalfHeight(Camera camera, float distance)
    {
        if (camera.Projection.Value == ProjectionType.Orthographic)
            return MathF.Max(camera.OrthographicSize.Value, MinimumClip) * 0.5f;
        return distance * MathF.Tan(camera.FieldOfView.Value * 0.5f * MathF.PI / 180f);
    }

    private const float MinimumClip = 0.001f;
    private const float DefaultAspect = 16f / 9f;
    private const float EyeMarkerSize = 0.02f;
}
