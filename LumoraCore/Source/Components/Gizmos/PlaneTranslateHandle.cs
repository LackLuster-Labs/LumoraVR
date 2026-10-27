// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// dragging slides the target in the plane PERPENDICULAR to the handle's axis (the small corner squares
// between two arrows); the plane is frozen at grab so it doesn't chase the object it moves
[ComponentCategory("Hidden")]
public class PlaneTranslateHandle : TransformHandle
{
    private float3 _planeNormal;
    private float3 _planeOrigin;
    private float3 _startPosition;
    private float3 _startHit;
    private bool _valid;

    protected override Localization.LocaleText DragDescription => UndoLocale.Move;

    protected override bool PosesTarget => true;

    protected override void BeginDrag(Slot target, float3 rayOrigin, float3 rayDirection)
    {
        _planeNormal = AxisWorld;
        _planeOrigin = CenterWorld;
        _startPosition = target.GlobalPosition;
        _valid = RayPlane(rayOrigin, rayDirection, _planeOrigin, _planeNormal, out _startHit);
    }

    protected override void UpdateDrag(Slot target, float3 rayOrigin, float3 rayDirection)
    {
        if (!_valid)
            return;
        if (!RayPlane(rayOrigin, rayDirection, _planeOrigin, _planeNormal, out var hit))
            return; // beam swung parallel to the plane this frame - hold position
        MoveTarget(target, _startPosition + (hit - _startHit));
    }
}
