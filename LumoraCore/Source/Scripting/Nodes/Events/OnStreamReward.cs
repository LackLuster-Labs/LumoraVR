// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// A viewer redeemed a channel reward that asks them for text, as the reader's chat feed carries it: the
// reward by id, with what they wrote. A reward that asks for nothing never reaches the chat, and a reward's
// title and cost are only told to the channel's own signed in tools, so neither is here. Heard like On
// Message.
[Node("Network/Stream Chat/On Reward Redeemed", Description = "A viewer redeemed a channel reward that asks them for text, as the reader's chat feed carries it: the reward by id, with what they wrote.")]
[SourcedEvent]
public sealed class OnStreamReward : EventNode<StreamRewardRedeemed>
{
}
