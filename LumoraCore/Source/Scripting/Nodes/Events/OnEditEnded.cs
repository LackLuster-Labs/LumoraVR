// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Someone stopped typing into the text field bound as its source, else the one on the object the script
// sits on, however they stopped: submitting, escape, clicking away, or the field going. What they typed
// stays. -xlinka
[Node("UI/Text Input/On Edit Ended", Description = "Someone stopped typing into the text field bound as its source, else the one on the object the script sits on, however they stopped: submitting, escape, clicking away, or the field going.")]
[SourcedEvent]
public sealed class OnEditEnded : EventNode<EditEnded>
{
}
