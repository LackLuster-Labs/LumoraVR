// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components;

// Leaves undrawn what the walls and floors under this slot stand in front of.
//
// The big solid surfaces under this slot are handed to the renderer as occluders, and anything wholly behind one
// from where the eye stands is not drawn at all. They are picked again whenever something under the slot is added
// or taken away, and follow their surfaces about; nothing here runs per frame. An imported world gets one on its
// root, switched off, and keeps it when saved. Measured in After Glow it halved the draws and gave nothing back on
// a desktop and cost a four-thread machine 0.45 ms a frame, so it is for a world that is mostly walls, on a build
// with the renderer's occlusion culling switched on in the project settings. -xlinka
[ComponentCategory("Rendering")]
public class StaticOccluders : ImplementableComponent
{
    // metres: a surface counts once its largest side reaches this
    [Range(0.5f, 20f, "0.0")]
    public readonly Sync<float> MinimumSize;

    // a mesh with more than this many triangles is left out
    public readonly Sync<int> MaximumTriangles;

    // the hook is the only listener
    public event System.Action? Invalidated;

    private bool _watching;

    public StaticOccluders()
    {
        MinimumSize = new Sync<float>(this, 2.5f);
        MaximumTriangles = new Sync<int>(this, 20000);
    }

    public override void OnStart()
    {
        base.OnStart();
        if (Slot != null)
        {
            Slot.SubtreeStructureChanged += OnStructureChanged;
            _watching = true;
        }
    }

    public override void OnDestroy()
    {
        if (_watching && Slot != null)
            Slot.SubtreeStructureChanged -= OnStructureChanged;
        _watching = false;
        base.OnDestroy();
    }

    private void OnStructureChanged(Slot changed) => Invalidated?.Invoke();
}
