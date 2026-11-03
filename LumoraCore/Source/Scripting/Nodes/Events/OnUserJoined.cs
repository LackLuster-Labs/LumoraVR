// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

[Node("Users/Events/On User Joined", Description = "Starts flow when a user joins the world.",
    Keywords = "player joined, join, connect")]
public sealed class OnUserJoined : EventNode<UserJoined>
{
}
