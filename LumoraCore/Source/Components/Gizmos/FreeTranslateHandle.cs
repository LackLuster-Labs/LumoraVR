// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// dragging moves the target in the plane FACING the grabbing beam (frozen at grab), so it follows the
// cursor in all three dimensions without ever jumping toward or away from the viewer
[ComponentCategory("Hidden")]
public class FreeTranslateHandle : TransformHandle
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
        // View-facing plane through the gizmo center, frozen for the whole drag.
        _planeNormal = (rayDirection * -1f).Normalized;
        _planeOrigin = CenterWorld;
        _startPosition = target.GlobalPosition;
        _valid = RayPlane(rayOrigin, rayDirection, _planeOrigin, _planeNormal, out _startHit);
    }

    protected override void UpdateDrag(Slot target, float3 rayOrigin, float3 rayDirection)
    {
        if (!_valid)
            return;
        if (!RayPlane(rayOrigin, rayDirection, _planeOrigin, _planeNormal, out var hit))
            return;
        MoveTarget(target, _startPosition + (hit - _startHit));
    }
}
