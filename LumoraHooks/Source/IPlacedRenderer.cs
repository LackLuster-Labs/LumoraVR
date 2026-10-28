// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Godot.Hooks;

// A renderer whose instance is placed from its slot's world transform rather than by the slot's node, told to
// take where it is drawn again when something drawn elsewhere above it moved (SlotHook.PlaceRenderersUnder).
internal interface IPlacedRenderer
{
    void Place(bool force);
}
