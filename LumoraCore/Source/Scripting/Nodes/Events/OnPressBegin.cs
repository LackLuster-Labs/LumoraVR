// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A press landing on a button or touch control bound as its source, else one on the object the script sits on:
// the moment it comes down, where On Pressed waits for a panel button to be let go of. -xlinka
[Node("UI/Buttons/On Press Begin", Description = "A press landing on a button or touch control bound as its source, else one on the object the script sits on: the moment it comes down, where On Pressed waits for a panel button to be let go of.",
    Keywords = "click, tap, button, press, push, press down")]
[SourcedEvent]
public sealed class OnPressBegin : EventNode<PressBegan>
{
}
