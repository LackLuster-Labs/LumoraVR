// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// dragging the hand toward/away from the center shrinks/grows the target proportionally - same feel as
// the two-hand resize gesture, one-handed
[ComponentCategory("Hidden")]
public class ScaleHandle : TransformHandle
{
    private const float MinFactor = 0.02f;
    private const float MaxFactor = 50f;
    private const float MinStartDistance = 0.05f;

    private float3 _center;
    private float3 _startScale;
    private float _startDistance;

    protected override Localization.LocaleText DragDescription => UndoLocale.Scale;

    protected override void BeginDrag(Slot target, float3 rayOrigin, float3 rayDirection)
    {
        _center = CenterWorld;
        _startScale = target.LocalScale.Value;
        _startDistance = MathF.Max(HandDistance(rayOrigin), MinStartDistance);
    }

    protected override void UpdateDrag(Slot target, float3 rayOrigin, float3 rayDirection)
    {
        float factor = HandDistance(rayOrigin) / _startDistance;
        if (factor < MinFactor) factor = MinFactor;
        if (factor > MaxFactor) factor = MaxFactor;
        target.LocalScale.Value = _startScale * factor;
    }

    // Hand position when available (natural push/pull feel); the ray origin is the fallback and on
    // desktop they're the same thing.
    private float HandDistance(float3 rayOrigin)
    {
        var handSlot = ActiveGrabber?.Slot;
        var from = handSlot != null && !handSlot.IsRemoved ? Uncarried(handSlot.GlobalPosition) : rayOrigin;
        var delta = from - _center;
        return MathF.Sqrt(delta.LengthSquared);
    }
}
