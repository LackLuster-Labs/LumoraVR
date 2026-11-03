// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone started typing into the text field bound as its source, else the one on the object the script
// sits on: they clicked it, or something gave it to them. Typing happens on the typist's machine and
// reaches a Shared script on the host through their relay. -xlinka
[Node("UI/Text Input/On Edit Started", Description = "Someone started typing into the text field bound as its source, else the one on the object the script sits on: they clicked it, or something gave it to them.")]
[SourcedEvent]
public sealed class OnEditStarted : EventNode<EditStarted>
{
}
