// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A press on a button or touch control bound as its source, else one on the object the script sits on, coming
// to an end: let go of, or given up, as when a drag passes on to the list the button sits in. -xlinka
[Node("UI/Buttons/On Press End", Description = "A press on a button or touch control bound as its source, else one on the object the script sits on, coming to an end: let go of, or given up, as when a drag passes on to the list the button sits in.",
    Keywords = "click, tap, button, release, press up")]
[SourcedEvent]
public sealed class OnPressEnd : EventNode<PressEnded>
{
}
