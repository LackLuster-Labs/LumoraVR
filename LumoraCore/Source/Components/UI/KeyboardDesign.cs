// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;

namespace Lumora.Core.Components.UI;

// A keyboard as an object: the layout the VR keyboard types with, standing in a world where the usual tools
// reach it (keys, rows, colours, sizes, anything added), saved to the inventory like anything else, and made
// the user's keyboard by making it their favourite. Standing in a world it types nothing; its keys only type
// once it is somebody's keyboard, in their own space, where VrKeyboard takes it up. Each key is a VrKey: its
// Glyph and ShiftGlyph are what it types, and its Role says when it is something else (space, shift, enter,
// the arrows). -xlinka
[ComponentCategory("UI")]
public sealed class KeyboardDesign : Component
{
    // The strip that shows what the field being typed into holds. A keyboard without one types blind.
    public readonly SyncRef<Text> Preview;

    public KeyboardDesign()
    {
        Preview = new SyncRef<Text>(this);
    }
}
