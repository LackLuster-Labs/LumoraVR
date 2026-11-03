// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone pressed enter in the single line text field bound as its source, else the one on the object the
// script sits on. The field lets go of the keys as it submits, so On Edit Ended comes just before. -xlinka
[Node("UI/Text Input/On Text Submitted", Description = "Someone pressed enter in the single line text field bound as its source, else the one on the object the script sits on.")]
[SourcedEvent]
public sealed class OnTextSubmitted : EventNode<TextSubmitted>
{
}
