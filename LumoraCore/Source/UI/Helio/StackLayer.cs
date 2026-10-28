// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;

namespace Helio.UI;

// A subtree drawn and hit as one sheet that slides and stacks without being laid out again. Offset moves
// every chunk under it across the canvas and takes its hit area along; Lift raises those chunks toward the
// reader. A layer on a chunk root whose first graphic writes depth makes that graphic the sheet's wall: the
// canvas draws every wall before anything else of any sheet, the highest first, so a sheet covers whatever
// of a lower one lies under it, that one's own wall included. A lift step has to clear the push such a wall
// gives its depth (4 mm in view space) at the canvas's scale.
// Neither touches a rect: dragging or raising a whole window is a transform write per chunk, not a relayout
// and a re-mesh of everything in it. Layers nest by adding up. Scale grows or shrinks the drawn sheet about
// Pivot for the moment a window opens, closes or goes to the dock; the hit area does not follow it, so it is
// for motion, not for a size anything is used at. -xlinka
[ComponentCategory("UI/Helio/Layout")]
public sealed class StackLayer : UIComponent
{
    // Canvas units, the same space as the rects underneath.
    public readonly Sync<float2> Offset;
    // Canvas units toward the side the canvas is read from.
    public readonly Sync<float> Lift;
    public readonly Sync<float> Scale;
    // Canvas units, before Offset: the point the sheet scales about.
    public readonly Sync<float2> Pivot;

    public StackLayer()
    {
        Offset = new Sync<float2>(this, float2.Zero);
        Lift = new Sync<float>(this, 0f);
        Scale = new Sync<float>(this, 1f);
        Pivot = new Sync<float2>(this, float2.Zero);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Offset.OnChanged += _ => Notify();
        Lift.OnChanged += _ => Notify();
        Scale.OnChanged += _ => Notify();
        Pivot.OnChanged += _ => Notify();
    }

    public override void OnEnabled()
    {
        base.OnEnabled();
        Notify();
    }

    public override void OnDisabled()
    {
        base.OnDisabled();
        Notify();
    }

    public override void OnDestroy()
    {
        Notify();
        base.OnDestroy();
    }

    private void Notify() => Slot?.GetComponentInParents<Canvas>()?.MarkPlacementDirty();
}
