// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Components;

[ComponentCategory("Rendering")]
public class RenderLayerOverride : ImplementableComponent<IHook>
{
    public const int HiddenLayer = 1 << 3;
    // This machine's own interface: drawn for its eyes, filmed only by a camera told to film it.
    public const int PrivateLayer = 1 << 1;
    public const int MirrorLayer = 1 << 16;
    public const int EyesOnlyLayer = 1 << 17;
    // The other side of it: skipped by your own eyes and drawn by every other view.
    public const int LocalHeadLayer = 1 << 18;
    // Bands a camera can leave out: name plates and their badges, and laser pointers.
    public const int NameplateLayer = 1 << 15;
    public const int PointerLayer = 1 << 14;
    // Your own name plate: drawn by mirrors and cameras, skipped by your own eyes.
    public const int OwnNameplateLayer = 1 << 13;
    // The bands cameras borrow to leave objects out or film one alone. The two above them carry the
    // shadows of moving objects, which every camera keeps.
    public const int CameraPoolLayers = 0x7E0;

    public readonly Sync<int> Layer = new();

    public override void OnInit()
    {
        base.OnInit();
        Layer.Value = HiddenLayer;
    }
}
