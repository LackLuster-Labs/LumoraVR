// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// What a light actually reaches: a sphere at Range for a point light, a cone from Range and SpotAngle
// for a spot, an arrow for a directional. One shape at a time, because the light is one type at a
// time.
//
// The cone and the arrow open along the slot's local -Z, which is the direction a light emits: the
// platform's light node points down its own -Z and our transforms go across unmirrored, so facing
// here is float3.Backward, not Forward. Getting that wrong puts the cone behind the light and everyone
// spends ten minutes wondering why the spot is lighting the wall it is bolted to.
//
// The spot-angle pad drags a DEGREE value through a rim radius, and that relationship is a tangent,
// so its units-per-degree is re-linearised on every layout - which is every frame the value is
// moving, because the write dirties the shape. A single fixed factor made the pad crawl near zero and
// bolt near ninety. -xlinka
[ComponentCategory("Utility/Gizmos")]
[GizmoForComponent(typeof(Light))]
public class LightGizmo : ComponentGizmo
{
    private ExtentHandle? _range;
    private ExtentHandle? _spotAngle;

    // Warm yellow, so a light reads apart from the green colliders around it.
    protected override color BaseTint => new(1f, 0.92f, 0.35f, 0.85f);

    protected override void CollectShapeFields(List<IChangeable> fields)
    {
        if (TargetAs<Light>() is not { } light)
            return;
        fields.Add(light.Type);
        fields.Add(light.Range);
        fields.Add(light.SpotAngle);
        fields.Add(light.RotationOffset);
    }

    // The light's own turn from its slot (see Light.RotationOffset); the shapes and pads follow it.
    private static floatQ Turn(Light light)
    {
        var q = light.RotationOffset.Value;
        return q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f ? floatQ.Identity : q.Normalized;
    }

    protected override void BuildWire(GizmoWireBuilder wire)
    {
        if (TargetAs<Light>() is not { } light)
            return;

        float range = MathF.Max(light.Range.Value, 0f);
        switch (light.Type.Value)
        {
            case LightType.Point:
                wire.Sphere(float3.Zero, range);
                break;

            case LightType.Spot:
            {
                float radius = range * MathF.Tan(ClampAngle(light.SpotAngle.Value) * DegreesToRadians);
                wire.Cone(float3.Zero, Turn(light) * float3.Backward, radius, range, segments: 32, ribs: 4);
                break;
            }

            default:
                // A directional light has no reach to draw, only a direction. Fixed length: it is a
                // pointer, and scaling it by anything would imply a falloff that does not exist.
                wire.Arrow(float3.Zero, Turn(light) * float3.Backward, DirectionalLength, DirectionalLength * 0.2f);
                break;
        }
    }

    protected override void BuildHandles()
    {
        if (TargetAs<Light>() is not { } light)
            return;

        _range = AddHandle("Range", float3.Right, light.Range, 1f, GizmoMaterialKind.Center, MinimumRange);
        _range.ValueName.Value = "Range";

        _spotAngle = AddHandle("SpotAngle", float3.Right, light.SpotAngle, 1f, GizmoMaterialKind.AxisX,
            MinimumAngle, MaximumAngle);
        _spotAngle.ValueName.Value = "Spot Angle";
    }

    protected override void LayoutHandles()
    {
        if (TargetAs<Light>() is not { } light)
            return;

        float range = MathF.Max(light.Range.Value, 0f);
        switch (light.Type.Value)
        {
            case LightType.Point:
                // Sideways, so the pad is not swallowed by whatever the light is mounted on.
                _range!.LocalAxis.Value = float3.Right;
                _range.DistancePerUnit.Value = 1f;
                PlaceHandle(_range, new float3(range, 0f, 0f));
                HideHandle(_spotAngle);
                break;

            case LightType.Spot:
            {
                var turn = Turn(light);
                _range!.LocalAxis.Value = turn * float3.Backward;
                _range.DistancePerUnit.Value = 1f;
                PlaceHandle(_range, turn * (float3.Backward * range));

                float angle = ClampAngle(light.SpotAngle.Value);
                float radians = angle * DegreesToRadians;
                float radius = range * MathF.Tan(radians);

                // d(radius)/d(degrees) at the current angle. Recomputed every layout because the
                // tangent's slope is nothing like constant across the range.
                float cos = MathF.Cos(radians);
                float perDegree = range * DegreesToRadians / MathF.Max(cos * cos, 1e-4f);
                _spotAngle!.LocalAxis.Value = turn * float3.Right;
                _spotAngle.DistancePerUnit.Value = perDegree;
                PlaceHandle(_spotAngle, turn * (new float3(radius, 0f, 0f) + float3.Backward * range));
                break;
            }

            default:
                HideHandle(_range);
                HideHandle(_spotAngle);
                break;
        }
    }

    private static float ClampAngle(float degrees) => System.Math.Clamp(degrees, MinimumAngle, MaximumAngle);

    private const float DegreesToRadians = MathF.PI / 180f;
    private const float DirectionalLength = 0.5f;
    private const float MinimumRange = 0.01f;

    // A zero-width cone draws nothing and a ninety-degree one is a plane; the platform's spot light
    // refuses both anyway.
    private const float MinimumAngle = 0.5f;
    private const float MaximumAngle = 89.5f;
}
