// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// dragging slides the target along the handle's axis by how far the closest point of the laser ray
// moved along that axis - the object tracks the beam without ever leaving the line
[ComponentCategory("Hidden")]
public class TranslateHandle : TransformHandle
{
    private float3 _axis;
    private float3 _lineOrigin;
    private float3 _startPosition;
    private float _startParam;
    private bool _valid;

    protected override Localization.LocaleText DragDescription => UndoLocale.Move;

    protected override bool PosesTarget => true;

    protected override void BeginDrag(Slot target, float3 rayOrigin, float3 rayDirection)
    {
        _axis = AxisWorld;
        _lineOrigin = CenterWorld; // frozen at grab: the axis line must not chase the object it moves
        _startPosition = target.GlobalPosition;
        _startParam = ClosestLineParam(_lineOrigin, _axis, rayOrigin, rayDirection);
        _valid = !float.IsNaN(_startParam);
    }

    protected override void UpdateDrag(Slot target, float3 rayOrigin, float3 rayDirection)
    {
        if (!_valid)
            return;
        float param = ClosestLineParam(_lineOrigin, _axis, rayOrigin, rayDirection);
        if (float.IsNaN(param))
            return; // beam swung parallel to the axis this frame - hold position
        MoveTarget(target, _startPosition + _axis * (param - _startParam));
    }
}
