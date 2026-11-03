// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

[Node("Users/Events/On User Left", Description = "Starts flow when a user leaves the world.",
    Keywords = "player left, left, leave, disconnect")]
public sealed class OnUserLeft : EventNode<UserLeft>
{
}
