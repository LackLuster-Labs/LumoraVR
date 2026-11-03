// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Every step of someone working a touch control bound as its source, else one on the object the script
// sits on: the press, every frame it is held, and the release, with the fingertip or beam tip and the hand
// it came from. A switch that flips on a press tells the press. Heard on the host, where controls react.
// -xlinka
[Node("UI/Buttons/On Touched", Description = "Reports press, hold and release interactions with a touch control, including the hand and touch position.",
    Keywords = "touch, poke, finger, fingertip, press")]
[SourcedEvent]
public sealed class OnTouched : EventNode<ControlTouched>
{
}
