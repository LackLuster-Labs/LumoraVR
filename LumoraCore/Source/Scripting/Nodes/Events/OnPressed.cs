// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A press on a button: the one bound as its source, else one on the object the script sits on.
[Node("UI/Buttons/On Pressed", Description = "A press on a button: the one bound as its source, else one on the object the script sits on.",
    Keywords = "click, tap, button, press, push, submit")]
[SourcedEvent]
public sealed class OnPressed : EventNode<ButtonPressed>
{
}
