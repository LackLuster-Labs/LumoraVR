// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A new subscription, a renewal, or a gifted one, to the channel the source object's stream chat reader
// follows. A gift to many people at once arrives as one of these per person. Heard like On Message.
[Node("Network/Stream Chat/On Subscription", Description = "A new subscription, a renewal, or a gifted one, to the channel the source object's stream chat reader follows.")]
[SourcedEvent]
public sealed class OnStreamSubscription : EventNode<StreamSubscription>
{
}
