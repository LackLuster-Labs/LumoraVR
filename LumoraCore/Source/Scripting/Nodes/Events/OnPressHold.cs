// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Every frame a press on a button or touch control bound as its source, else one on the object the script sits
// on, is still held, from the frame after On Press Begin until On Press End. -xlinka
[Node("UI/Buttons/On Press Hold", Description = "Every frame a press on a button or touch control bound as its source, else one on the object the script sits on, is still held, from the frame after On Press Begin until On Press End.",
    Keywords = "click, button, hold, long press")]
[SourcedEvent]
public sealed class OnPressHold : EventNode<PressHeld>
{
}
