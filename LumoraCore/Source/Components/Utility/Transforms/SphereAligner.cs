// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components.Utility;

// Spreads children evenly over a sphere: each one a step further round a golden-angle spiral that runs from the
// bottom pole to the top, so any count covers the surface with no seams and no bunching at the poles.
//
// Start and End pick the band of the spiral the children fill, as shares of it from the bottom; set the other
// way round, they fill it from the top down. Offset slides everyone along the spiral by that many steps.
// HorizontalStart and HorizontalEnd squeeze the turn round the axis into that share of a full circle, for a
// partial dome; both zero leaves it whole. With FaceOutward each child turns to face away from the middle. -xlinka
[ComponentCategory("Utility/Transforms")]
public class SphereAligner : ChildLayoutBase
{
    private static readonly float GoldenAngle = System.MathF.PI * (3f - System.MathF.Sqrt(5f));

    public readonly Sync<float> Radius;

    public readonly Sync<float> Offset;

    public readonly Sync<bool> FaceOutward;

    // Added to each child's rotation.
    public readonly Sync<floatQ> RotationOffset;

    public readonly Sync<float> Start;

    public readonly Sync<float> End;

    public readonly Sync<float> HorizontalStart;

    public readonly Sync<float> HorizontalEnd;

    public SphereAligner()
    {
        Radius = new Sync<float>(this, 1f);
        Offset = new Sync<float>(this, 0f);
        FaceOutward = new Sync<bool>(this, true);
        RotationOffset = new Sync<floatQ>(this, floatQ.Identity);
        Start = new Sync<float>(this, 0f);
        End = new Sync<float>(this, 1f);
        HorizontalStart = new Sync<float>(this, 0f);
        HorizontalEnd = new Sync<float>(this, 1f);
    }

    public override void OnUpdate(float delta)
    {
        var items = OrderedItems;
        int count = items.Count;
        if (count == 0)
            return;

        float start = System.Math.Clamp(Start.Value, 0f, 1f);
        float end = System.Math.Clamp(End.Value, 0f, 1f);
        float span = System.Math.Abs(end - start);
        if (span <= 0f)
        {
            span = 0.0001f;
            float middle = (start + end) * 0.5f;
            start = middle - span * 0.5f;
            end = middle + span * 0.5f;
        }

        // The whole spiral holds as many points as the band would need to hold this many.
        int total = System.Math.Max(1, (int)System.MathF.Round(count / span));
        float step = 2f / total;
        bool downward = start > end;
        int index = (int)System.MathF.Round(start * total);
        bool squeezed = System.Math.Abs(HorizontalStart.Value) > 1e-6f || System.Math.Abs(HorizontalEnd.Value) > 1e-6f;
        float radius = Radius.Value;
        bool face = FaceOutward.Value;
        var offset = RotationOffset.Value;

        for (int i = 0; i < count; i++)
        {
            var item = items[i];
            float height = index * step - 1f + step * 0.5f;
            float ring = System.MathF.Sqrt(System.Math.Max(0f, 1f - height * height));
            float turn = ((index + Offset.Value) % total) * GoldenAngle;
            if (squeezed)
            {
                float share = (turn % (System.MathF.PI * 2f)) / (System.MathF.PI * 2f);
                turn = (HorizontalStart.Value + (HorizontalEnd.Value - HorizontalStart.Value) * share) * System.MathF.PI * 2f;
            }
            var normal = new float3(System.MathF.Cos(turn) * ring, height, System.MathF.Sin(turn) * ring);

            if (item.Position.IsLinkValid)
                Push(item.Position, normal * radius);

            var root = item.Root.Target;
            if (root != null && !root.IsDestroyed)
            {
                if (!ReferenceEquals(item.Rotation.Target, root.LocalRotation))
                    item.Rotation.DriveTarget(root.LocalRotation);
                if (item.Rotation.IsLinkValid)
                    Push(item.Rotation, (face ? Facing(normal) : floatQ.Identity) * offset);
            }

            index += downward ? -1 : 1;
        }
    }

    // A rotation whose forward points along the normal. LookRotation hands back the inverse of the rotation it
    // names, so it is turned round here.
    private static floatQ Facing(float3 normal)
    {
        if (normal.LengthSquared < 1e-8f)
            return floatQ.Identity;
        var forward = normal.Normalized;
        var reference = System.Math.Abs(forward.y) > 0.999f ? float3.Forward : float3.Up;
        return floatQ.LookRotation(forward, reference).Inverse;
    }
}
